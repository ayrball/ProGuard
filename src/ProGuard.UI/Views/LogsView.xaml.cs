using System.Windows.Controls;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI.Views;

public partial class LogsView : UserControl
{
    /// <summary>How close to the bottom of the table (in pixels) the next older page is fetched.</summary>
    private const double LoadOlderMargin = 400;

    public LogsView() => InitializeComponent();

    // Scrolling near the end pages older entries in, so the table reads as one long log.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || DataContext is not LogsViewModel vm) return;
        if (!vm.HasOlder || viewer.ExtentHeight <= 0) return;
        if (viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - LoadOlderMargin)
            vm.LoadOlderCommand.Execute(null);
    }
}
