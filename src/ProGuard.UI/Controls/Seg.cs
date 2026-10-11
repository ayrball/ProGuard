using System.Windows;

namespace ProGuard.UI.Controls;

/// <summary>
/// Marks a button in a segmented row as the current choice. Unlike a RadioButton, clicking does not set
/// it locally, so a one-way binding (e.g. "is this the process's current priority?") stays in charge.
/// </summary>
public static class Seg
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(Seg), new PropertyMetadata(false));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);
    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
