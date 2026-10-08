using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class PowerPage : Page
{
    public PowerPage() : this(App.GetService<PowerViewModel>()) { }

    public PowerPage(PowerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
