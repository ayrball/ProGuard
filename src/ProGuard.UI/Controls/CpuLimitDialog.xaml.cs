using System;
using System.Windows;
using Wpf.Ui.Controls;

namespace ProGuard.UI.Controls;

public partial class CpuLimitDialog : FluentWindow
{
    public int SelectedLimitPercent => (int)LimitSlider.Value;
    public bool RemoveLimitRequested { get; private set; }

    public CpuLimitDialog(string processName, int pid, int? currentLimitPercent = null)
    {
        InitializeComponent();

        ProcessInfoText.Text = $"Target: {processName} (PID: {pid})";
        if (currentLimitPercent.HasValue && currentLimitPercent.Value > 0)
        {
            LimitSlider.Value = currentLimitPercent.Value;
        }
        else
        {
            LimitSlider.Value = 50;
        }

        UpdateValueDisplay();
    }

    private void LimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueDisplay();
    }

    private void UpdateValueDisplay()
    {
        if (LimitValueText != null)
        {
            LimitValueText.Text = $"{(int)LimitSlider.Value}%";
        }
    }

    private void Preset25_Click(object sender, RoutedEventArgs e) => LimitSlider.Value = 25;
    private void Preset50_Click(object sender, RoutedEventArgs e) => LimitSlider.Value = 50;
    private void Preset75_Click(object sender, RoutedEventArgs e) => LimitSlider.Value = 75;
    private void Preset90_Click(object sender, RoutedEventArgs e) => LimitSlider.Value = 90;

    private void RemoveLimit_Click(object sender, RoutedEventArgs e)
    {
        RemoveLimitRequested = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
