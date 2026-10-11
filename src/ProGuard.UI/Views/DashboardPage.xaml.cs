using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class DashboardPage : Page
{
    public DashboardPage() : this(App.GetService<DashboardViewModel>()) { }

    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (s, e) =>
        {
            if (DataContext is DashboardViewModel vm)
            {
                vm.IsActive = true;
            }
        };

        Unloaded += (s, e) =>
        {
            if (DataContext is DashboardViewModel vm)
            {
                vm.IsActive = false;
            }
        };
    }
}
