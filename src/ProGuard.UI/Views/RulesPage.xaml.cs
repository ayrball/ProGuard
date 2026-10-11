using System.Windows.Controls;
using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Views;

public partial class RulesPage : Page
{
    public RulesPage() : this(App.GetService<RulesViewModel>()) { }

    public RulesPage(RulesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
