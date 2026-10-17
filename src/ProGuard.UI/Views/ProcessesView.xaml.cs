using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI.Views;

public partial class ProcessesView : UserControl
{
    private bool _pointerOver;
    private bool _menuOpen;

    public ProcessesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window != null) window.PreviewKeyDown += OnWindowKeyDown;
            WarmUpMenu();
        };
        Unloaded += (_, _) =>
        {
            var window = Window.GetWindow(this);
            if (window != null) window.PreviewKeyDown -= OnWindowKeyDown;
            _pointerOver = _menuOpen = false;
            SyncFreeze();
        };
    }

    private ProcessesViewModel? Vm => DataContext as ProcessesViewModel;

    private static bool _menuWarmed;

    /// <summary>
    /// WPF builds a popup's window and visual tree the first time it opens, which costs a noticeable fraction
    /// of a second (measured ~170 ms here). Pay it once, invisibly, when the app is idle, rather than on the
    /// user's first right-click.
    /// </summary>
    private void WarmUpMenu()
    {
        if (_menuWarmed || Table.ContextMenu is not { } menu) return;
        _menuWarmed = true;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (menu.IsOpen) return;
            menu.Opacity = 0;
            menu.PlacementTarget = Table;
            menu.IsOpen = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                menu.IsOpen = false;
                menu.Opacity = 1;
            });
        });
    }

    // The order holds still while the pointer is over the table or its menu is open, so rows can't jump
    // between aiming and clicking.
    private void SyncFreeze()
    {
        if (Vm is { } vm) vm.FreezeOrder = _pointerOver || _menuOpen;
    }

    private void OnTableMouseEnter(object sender, MouseEventArgs e) { _pointerOver = true; SyncFreeze(); }
    private void OnTableMouseLeave(object sender, MouseEventArgs e) { _pointerOver = false; SyncFreeze(); }
    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        SyncFreeze();
        // So the Always submenus tick what the program's rule keeps.
        if (Vm is { } vm) _ = vm.LoadSelectedRuleAsync();
    }
    private void OnMenuClosed(object sender, RoutedEventArgs e) { _menuOpen = false; SyncFreeze(); }

    private void OnTableRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Select the row under the pointer before the menu opens, so the menu acts on what was clicked.
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
        else
        {
            e.Handled = true; // right-click on empty space: no menu, nothing to act on
        }
    }

    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Vm?.EndCommand.CanExecute(null) == true)
        {
            Vm.EndCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Apps && Table.ContextMenu != null && Vm?.Selected != null)
        {
            Table.ContextMenu.PlacementTarget = Table;
            Table.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return node as T;
    }
}
