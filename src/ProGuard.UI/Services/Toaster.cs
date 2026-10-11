using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ProGuard.UI.Services;

public sealed record Toast(string Message, bool IsError);

/// <summary>Short-lived notices in the corner of the window. Safe to call from any thread.</summary>
public sealed class Toaster
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(7);

    private readonly Dispatcher _dispatcher;

    public ObservableCollection<Toast> Items { get; } = new();

    public Toaster(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Show(string message, bool isError = false)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var toast = new Toast(message, isError);
            Items.Insert(0, toast);
            while (Items.Count > MaxVisible) Items.RemoveAt(Items.Count - 1);

            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = isError ? ErrorLifetime : Lifetime
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Items.Remove(toast);
            };
            timer.Start();
        });
    }

    public void Dismiss(Toast toast) => Items.Remove(toast);
}
