using System.Windows;
using System.Windows.Input;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => SyncMaximized();
        PreviewKeyDown += OnPreviewKeyDown;
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
