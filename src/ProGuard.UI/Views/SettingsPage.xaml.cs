using ProGuard.UI.ViewModels;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using System.Windows.Controls;

namespace ProGuard.UI.Views;

public partial class SettingsPage : Page
{
    public SettingsPage() : this(App.GetService<SettingsViewModel>()) { }

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            switch (comboBox.SelectedIndex)
            {
                case 0:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    break;
                case 1:
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    break;
                case 2:
                    ApplicationThemeManager.ApplySystemTheme();
                    break;
            }
        }
    }
}
