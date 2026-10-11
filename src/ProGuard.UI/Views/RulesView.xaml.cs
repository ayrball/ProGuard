using System.Windows.Controls;
using ProGuard.UI.ViewModels;

namespace ProGuard.UI.Views;

public partial class RulesView : UserControl
{
    public RulesView()
    {
        // The CPU Set picker opens from this page. Register its view here so the window can show it without a
        // line in MainWindow.xaml (which, once the dialogs are listed there, makes this call redundant).
        DialogTemplates.Register<CpuSetDialogViewModel, CpuSetDialog>();
        InitializeComponent();
    }
}
