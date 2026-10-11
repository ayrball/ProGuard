using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class LogsPage : Page
{
    public LogsPage() : this(App.GetService<LogsViewModel>()) { }

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
