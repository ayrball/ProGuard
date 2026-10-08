using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class ProcessListPage : Page
{
    public ProcessListPage() : this(App.GetService<ProcessListViewModel>()) { }

    public ProcessListPage(ProcessListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (s, e) =>
        {
            if (DataContext is ProcessListViewModel vm)
            {
                vm.IsActive = true;
            }
        };

        Unloaded += (s, e) =>
        {
            if (DataContext is ProcessListViewModel vm)
            {
                vm.IsActive = false;
            }
        };
    }

    private void OnRowRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }
}
