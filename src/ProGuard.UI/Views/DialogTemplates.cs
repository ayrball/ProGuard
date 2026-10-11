using System.Windows;

namespace ProGuard.UI.Views;

/// <summary>Lets a page add the view for a dialog view model of its own, instead of every dialog being listed in MainWindow.xaml.</summary>
internal static class DialogTemplates
{
    /// <summary>Makes <typeparamref name="TView"/> the view for <typeparamref name="TViewModel"/>, once per application.</summary>
    public static void Register<TViewModel, TView>() where TView : FrameworkElement, new()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        var key = new DataTemplateKey(typeof(TViewModel));
        if (resources.Contains(key)) return;
        resources.Add(key, new DataTemplate(typeof(TViewModel)) { VisualTree = new FrameworkElementFactory(typeof(TView)) });
    }
}
