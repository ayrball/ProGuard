using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using System.Collections.ObjectModel;
using System.Windows.Controls;

namespace ProGuard.UI.Controls;

public partial class CpuGraphControl : UserControl
{
    public ObservableCollection<ISeries> ChartSeries { get; set; }
    public Axis[] ChartXAxes { get; set; }
    public Axis[] ChartYAxes { get; set; }

    public CpuGraphControl()
    {
        InitializeComponent();
        
        ChartSeries = new ObservableCollection<ISeries>
        {
            new LineSeries<ObservableValue>
            {
                Values = new ObservableCollection<ObservableValue>(),
                Fill = null,
                GeometrySize = 0,
                LineSmoothness = 0.5
            }
        };

        ChartXAxes = new Axis[] { new Axis { IsVisible = false } };
        ChartYAxes = new Axis[] { new Axis { MinLimit = 0, MaxLimit = 100 } };
        
        DataContext = this;
    }
}
