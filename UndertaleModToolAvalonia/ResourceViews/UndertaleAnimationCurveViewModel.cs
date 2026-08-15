using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UndertaleModLib;
using UndertaleModLib.Models;

namespace UndertaleModToolAvalonia;

public partial class UndertaleAnimationCurveViewModel : ObservableObject, IUndertaleResourceViewModel
{
    bool isSyncing;

    public UndertaleResource Resource => AnimationCurve;
    public UndertaleAnimationCurve AnimationCurve { get; }

    [ObservableProperty]
    public partial UndertaleAnimationCurve.Channel? ChannelSelected { get; set; }

    /// <summary>
    /// Editable view over the selected channel's points. The wrappers write back into the
    /// underlying point fields (which cannot be used with bindings directly).
    /// </summary>
    public ObservableCollection<ChannelPointWrapper> ChannelPoints { get; } = [];

    public UndertaleAnimationCurveViewModel(UndertaleAnimationCurve animationCurve)
    {
        AnimationCurve = animationCurve;

        ChannelPoints.CollectionChanged += (_, _) =>
        {
            if (isSyncing || ChannelSelected?.Points is not { } points)
                return;

            isSyncing = true;
            try
            {
                points.Clear();
                foreach (var wrapper in ChannelPoints)
                    points.Add(wrapper.Point);
            }
            finally
            {
                isSyncing = false;
            }
        };
    }

    partial void OnChannelSelectedChanged(UndertaleAnimationCurve.Channel? value)
    {
        isSyncing = true;
        try
        {
            ChannelPoints.Clear();
            if (value?.Points is not null)
            {
                foreach (var point in value.Points)
                    ChannelPoints.Add(new ChannelPointWrapper(point));
            }
        }
        finally
        {
            isSyncing = false;
        }
    }

    public static UndertaleAnimationCurve.Channel CreateChannel() => new();
    public static ChannelPointWrapper CreateWrapperPoint() => new(new UndertaleAnimationCurve.Channel.Point());

    public void ChannelSelectedChanged(object? item)
    {
        ChannelSelected = (UndertaleAnimationCurve.Channel?)item;
    }
}