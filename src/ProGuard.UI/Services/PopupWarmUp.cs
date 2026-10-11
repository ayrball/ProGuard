using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProGuard.UI.Services;

/// <summary>
/// The first popup a WPF process opens (a context menu, a drop-down, a tooltip) takes a few hundred
/// milliseconds to create its window and render pipeline. Left alone, that cost lands on the user's first
/// right-click, so it is paid here instead, with an invisible menu opened and closed once the app is idle.
/// </summary>
public static class PopupWarmUp
{
    public static void Schedule(FrameworkElement owner)
    {
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                var menu = new ContextMenu { Opacity = 0, PlacementTarget = owner };
                var submenu = new MenuItem { Header = "Warm-up" };
                submenu.Items.Add(new MenuItem { Header = "Warm-up" });
                menu.Items.Add(submenu);
                menu.IsOpen = true;

                // Idle again means the popup has been laid out and rendered, so it can go.
                owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => menu.IsOpen = false));
            }
            catch { }
        }));
    }
}
