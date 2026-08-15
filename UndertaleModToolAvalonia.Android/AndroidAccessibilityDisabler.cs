using System;
using System.Collections;
using System.Reflection;
using Android.Views;
using AndroidX.Core.View;
using Avalonia.Android;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using AndroidView = Android.Views.View;

namespace UndertaleModToolAvalonia.Android;

/// <summary>
/// Disables Avalonia's Android accessibility/automation bridge.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia.Android wires an <c>ExploreByTouchHelper</c>-derived access helper
/// (<c>AvaloniaAccessHelper</c>) onto the <see cref="AvaloniaView"/> unconditionally. That helper
/// subscribes to every (re)created <see cref="AutomationPeer"/>'s
/// <see cref="AutomationPeer.PropertyChanged"/> and, on each change, synchronously repopulates the
/// Android virtual view by calling every cached <c>NodeInfoProvider</c>.
/// </para>
/// <para>
/// Some peers expose a provider only conditionally (for example
/// <c>TreeDataGridRowAutomationPeer</c> returns <see cref="IToggleProvider"/> only while the row
/// currently has an expander). Once the helper has cached such a provider, a later layout pass can
/// leave the peer without the provider, and the repopulation throws
/// <see cref="System.InvalidOperationException"/> ("Peer instance does not implement T"), which
/// crosses the JNI boundary and crashes the app as soon as any accessibility service is connected.
/// </para>
/// <para>
/// This helper neutralises the bridge as completely as possible from the outside:
/// <list type="bullet">
///   <item>detaches the access helper as the view's accessibility delegate,</item>
///   <item>marks the view as not important for accessibility, and</item>
///   <item>unsubscribes the helper's peer/property wiring and clears its peer/provider caches so the
///   internal <c>InvalidateVirtualView()</c> repopulation path can never run again.</item>
/// </list>
/// </para>
/// <para>
/// This means screen readers (TalkBack, OEM accessibility services, <c>uiautomator</c>) will no longer
/// read this app's UI, but those are precisely the clients whose tree walks trigger the crash above.
/// </para>
/// </remarks>
public static class AndroidAccessibilityDisabler
{
    /// <summary>
    /// Detaches and neutralises Avalonia.Android's accessibility helper on the activity's
    /// <see cref="AvaloniaView"/>. Best-effort: failures are swallowed so this never crashes the app.
    /// </summary>
    /// <param name="decorView">The activity's <c>Window.DecorView</c> (or any root view).</param>
    public static void Disable(AndroidView? decorView)
    {
        try
        {
            AvaloniaView? view = FindViewOfType<AvaloniaView>(decorView);
            if (view is null)
                return;

            // Stop the framework from querying the automation tree (and thus from registering any
            // new automation peers through the access helper).
            ViewCompat.SetAccessibilityDelegate(view, null);
            view.ImportantForAccessibility = ImportantForAccessibility.No;

            // Break the helper's internal (re)registration wiring. The helper keeps itself alive by
            // subscribing peer.PropertyChanged/ChildrenChanged and populating virtual views whenever
            // a peer's bounds change; without a disconnected delegate that is the crash path, so
            // clearing these events guarantees no automated repopulation can fire.
            object? helper = GetFieldValue(view, "_accessHelper");
            if (helper is null)
                return;

            // Peers the helper has already registered.
            IDictionary? peerMap = GetFieldValue(helper, "_peerNodeInfoProviders") as IDictionary;
            if (peerMap is not null)
            {
                foreach (object? key in new ArrayList(peerMap.Keys))
                {
                    if (key is AutomationPeer peer)
                        UnsubscribePeerEvents(peer);
                }
            }

            // Clear the caches so any late framework access lands on empty state (safe no-ops).
            (GetFieldValue(helper, "_peers") as IDictionary)?.Clear();
            (GetFieldValue(helper, "_peerNodeInfoProviders") as IDictionary)?.Clear();
        }
        catch
        {
            // Best effort only: if internal Avalonia.Android structure changes, we simply fall back to
            // the framework-level importance/delegate disables above.
        }
    }

    /// <summary>Removes all <c>PropertyChanged</c>/<c>ChildrenChanged</c> subscribers of a peer.</summary>
    static void UnsubscribePeerEvents(AutomationPeer peer)
    {
        // The events are declared on the AutomationPeer base class as auto-implemented events, so
        // their backing delegate fields live on that exact type.
        SetFieldValue(peer, "PropertyChanged", null, typeof(AutomationPeer));
        SetFieldValue(peer, "ChildrenChanged", null, typeof(AutomationPeer));
    }

    static object? GetFieldValue(object instance, string fieldName)
    {
        return instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);
    }

    static void SetFieldValue(object instance, string fieldName, object? value, Type? declaringType = null)
    {
        FieldInfo? field = (declaringType ?? instance.GetType())
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(instance, value);
    }

    static T? FindViewOfType<T>(AndroidView? root) where T : AndroidView
    {
        switch (root)
        {
            case null:
                return null;
            case T match:
                return match;
            case ViewGroup group:
                for (int i = 0; i < group.ChildCount; i++)
                {
                    if (FindViewOfType<T>(group.GetChildAt(i)) is { } found)
                        return found;
                }
                return null;
            default:
                return null;
        }
    }
}