using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace ProGuard.UI.Services;

/// <param name="ActionText">A button on the toast (null for none) that runs <paramref name="Action"/> and closes it.</param>
public sealed record Toast(string Message, bool IsError, string? ActionText = null, ICommand? Action = null);

/// <summary>Short-lived notices in the corner of the window. Safe to call from any thread.</summary>
public sealed class Toaster
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4.5);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(7);

    private readonly Dispatcher _dispatcher;

    public ObservableCollection<Toast> Items { get; } = new();

    /// <summary>
    /// "Restart as administrator", when this window can offer it. A toast saying something needs administrator rights
    /// (the engine's refusals and the window's own) then carries a button for it.
    /// </summary>
    public Action? Elevate { get; set; }

    public const string ElevateText = "RESTART AS ADMIN";

    public Toaster(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Whether <paramref name="message"/> says the user needs administrator rights for what they tried.</summary>
    public static bool NeedsAdministrator(string message) =>
        message.Contains("as administrator", StringComparison.OrdinalIgnoreCase)
        || message.Contains("needs administrator rights", StringComparison.OrdinalIgnoreCase);

    public void Show(string message, bool isError = false)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Toast toast = new(message, isError);
            if (Elevate is { } elevate && NeedsAdministrator(message))
            {
                toast = toast with
                {
                    ActionText = ElevateText,
                    Action = new RelayCommand(() =>
                    {
                        Dismiss(toast);
                        elevate();
                    }),
                };
            }
            Items.Insert(0, toast);
            while (Items.Count > MaxVisible) Items.RemoveAt(Items.Count - 1);

            var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                // A toast with a button stays long enough to reach it.
                Interval = toast.Action != null ? ErrorLifetime * 1.5 : isError ? ErrorLifetime : Lifetime
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
