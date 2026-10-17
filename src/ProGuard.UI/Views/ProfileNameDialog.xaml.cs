using System.Windows.Controls;

namespace ProGuard.UI.Views;

public partial class ProfileNameDialog : UserControl
{
    public ProfileNameDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }
}
