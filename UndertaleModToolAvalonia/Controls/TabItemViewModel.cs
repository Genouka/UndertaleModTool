using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UndertaleModToolAvalonia;

public interface ITabContent
{
    /// <summary>
    /// Runs after the tab content is attached to a tab, i.e. when it becomes a tab's content.
    /// </summary>
    void OnAttached() { }

    /// <summary>
    /// Runs before the tab content is detached from a tab, i.e. when it stops being a tab's content.
    /// </summary>
    void OnDetached() { }

    /// <summary>
    /// Runs before <see cref="OnDetached"/>, when the data file is not closing. Use it to save the tab's temporary contents to the data file.
    /// Return true if saving was successful; if false is returned, detaching will not continue.
    /// </summary>
    async Task<bool> OnSave() => true;
}

public partial class TabItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial ITabContent Content { get; set; } = null!;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = false;

    [ObservableProperty]
    public partial bool CanGoBack { get; set; } = false;

    [ObservableProperty]
    public partial bool CanGoForward { get; set; } = false;

    /// <summary>
    /// The content currently realized in the tab's view. Lags behind <see cref="Content"/> while the
    /// new content is being loaded, so a progress bar can be shown instead of a frozen window.
    /// </summary>
    [ObservableProperty]
    public partial ITabContent? DisplayedContent { get; set; }

    [ObservableProperty]
    public partial bool IsContentLoading { get; set; } = false;

    private int contentLoadVersion = 0;

    private readonly List<ITabContent> history = [];
    private int historyPosition = -1;

    public TabItemViewModel(ITabContent content, bool isSelected = false)
    {
        Content = content;
        IsSelected = isSelected;

        history.Add(Content);
        historyPosition = 0;
    }

    /// <summary>
    /// Starts realizing <see cref="Content"/> if it is not being displayed yet. The expensive part
    /// (creating the content's control tree and laying it out) is deferred to a dispatcher pass
    /// that runs after the frame with the progress bar has been rendered.
    /// </summary>
    private void BeginContentLoad()
    {
        if (ReferenceEquals(DisplayedContent, Content))
            return;

        int version = ++contentLoadVersion;

        // Show the progress bar (and drop the previous content) before the UI thread is blocked.
        DisplayedContent = null;
        IsContentLoading = true;

        // DispatcherPriority.Loaded runs after layout and render, so the progress bar has been
        // painted by the time this callback starts blocking the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (version != contentLoadVersion)
                return;

            // Instantiates the content's data template synchronously and lays it out in the
            // following layout pass.
            DisplayedContent = Content;

            // Runs after that pass too, so the progress bar is hidden only once the content is up.
            Dispatcher.UIThread.Post(() =>
            {
                if (version != contentLoadVersion)
                    return;

                IsContentLoading = false;
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    partial void OnContentChanged(ITabContent value)
    {
        if (IsSelected)
            BeginContentLoad();
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            BeginContentLoad();
        }
        else
        {
            // Cancel a pending load; it is restarted when the tab is selected again.
            // DisplayedContent is kept so switching back doesn't rebuild the view.
            contentLoadVersion++;
            IsContentLoading = false;
        }
    }

    /// <summary>
    /// Drops the realized view and the navigation history. Called when the tab is closed, so a
    /// closed tab that is still referenced (for example by a stale selection) cannot keep its
    /// control tree and every previously opened content alive.
    /// </summary>
    private void ReleaseContent()
    {
        contentLoadVersion++;
        IsContentLoading = false;
        DisplayedContent = null;

        history.Clear();
        historyPosition = -1;
        CanGoBack = false;
        CanGoForward = false;
    }

    public void OnOpen()
    {
        Content.OnAttached();
    }

    public void OnClose()
    {
        Content.OnDetached();
        ReleaseContent();
    }

    public async Task<bool> Save()
    {
        return await Content.OnSave();
    }

    public async Task<bool> GoTo(ITabContent content)
    {
        if (content == Content)
            return true;

        if (!await Content.OnSave())
            return false;

        Content.OnDetached();

        Content = content;

        history.RemoveRange(historyPosition + 1, history.Count - (historyPosition + 1));

        history.Add(content);
        historyPosition++;

        CanGoBack = true;
        CanGoForward = false;

        Content.OnAttached();

        return true;
    }

    public async Task<bool> GoBack()
    {
        if (!await Content.OnSave())
            return false;

        Content.OnDetached();

        historyPosition--;
        Content = history[historyPosition];

        CanGoBack = (historyPosition != 0);
        CanGoForward = true;

        Content.OnAttached();

        return true;
    }

    public async Task<bool> GoForward()
    {
        if (!await Content.OnSave())
            return false;

        Content.OnDetached();

        historyPosition++;
        Content = history[historyPosition];

        CanGoBack = true;
        CanGoForward = (historyPosition != history.Count - 1);

        Content.OnAttached();

        return true;
    }
}