using System.Windows;
using System.Windows.Input;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => { SyncMaximized(); HideToTrayIfMinimized(); };
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>True once there is a tray icon to fall back on. Without one the window always minimises and closes normally.</summary>
    public bool HasTray { get; set; }

    /// <summary>The app is quitting (tray menu, Windows shutting down): let the window close instead of hiding.</summary>
    public bool IsQuitting { get; set; }

    private bool KeepInTray => !IsQuitting && HasTray && (DataContext as ShellViewModel)?.MinimizeToTray == true;

    // MinimizeToTray: minimising and closing both send the window to the tray; "Exit" in the tray menu quits.
    private void HideToTrayIfMinimized()
    {
        if (WindowState == WindowState.Minimized && KeepInTray)
        {
            WindowState = WindowState.Normal; // so Show() brings it back as a normal window, not a minimised one
            Hide();
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!KeepInTray) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>Shows the window, restored and in front.</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ToggleVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else BringToFront();
    }

    private void SyncMaximized()
    {
        bool max = WindowState == WindowState.Maximized;
        // A borderless window maximises past the screen edge by the resize border; pull the content back in.
        var edge = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = max ? new Thickness(edge.Left + 1, edge.Top + 1, edge.Right + 1, edge.Bottom + 1) : new Thickness(0);
        Frame.BorderThickness = new Thickness(max ? 0 : 1);
        MaxButton.Content = max ? "" : "";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ShellViewModel { Dialog: not null } shell)
        {
            shell.CloseDialog();
            e.Handled = true;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
