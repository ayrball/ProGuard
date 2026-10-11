using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class WatchdogPage : Page
{
    public WatchdogPage() : this(App.GetService<WatchdogViewModel>()) { }

    public WatchdogPage(WatchdogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
