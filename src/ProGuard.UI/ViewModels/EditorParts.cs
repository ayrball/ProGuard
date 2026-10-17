using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ProGuard.UI.ViewModels;

/// <summary>Number parsing for the editors' text boxes. Accepts both "40.5" and the user's own decimal separator.</summary>
internal static class EditorNumbers
{
    public static bool TryDouble(string? text, out double value)
    {
        var trimmed = (text ?? string.Empty).Replace("%", string.Empty).Trim();
        var ok = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                 || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return ok && double.IsFinite(value);
    }

    public static bool TryInt(string? text, out int value) =>
        int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static string Text(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Display text shared by the rule and watchdog pages: terse, uppercase where it is a label.</summary>
internal static class RuleText
{
    // What the engine does when a setting is left empty. Shown as the hint in the empty box.
    public const int DefaultLimiterOverSeconds = 5;
    public const int DefaultLimiterCores = 1;
    public const int DefaultLimiterDurationSeconds = 30;
    public const int DefaultBoostSeconds = 10;
    public const int DefaultCoresPerInstance = 1;

    /// <summary>The longest duration an editor accepts (90 days): keeps typos from overflowing a stored int.</summary>
    public const int MaxSeconds = 90 * 24 * 3600;

    /// <summary>
    /// "chrome" and "chrome.exe" are the same rule, so a plain name is shown with .exe. Wildcard patterns, full paths
    /// and the System process are left exactly as written.
    /// </summary>
    public static string Display(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.IndexOfAny(new[] { '*', '?', '\\', '/' }) >= 0 || trimmed.Equals("System", StringComparison.OrdinalIgnoreCase)) return trimmed;
        return Fmt.Exe(trimmed);
    }

    /// <summary>The rule written for exactly this program name ("chrome" and "chrome.exe" alike), if there is one.</summary>
    public static ProGuard.Shared.Contracts.ProcessRuleDto? RuleFor(IEnumerable<ProGuard.Shared.Contracts.ProcessRuleDto> rules, string processName) =>
        rules.FirstOrDefault(r => string.Equals(Display(r.ProcessName), Display(processName), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A name Windows can look up in Image File Execution Options: a file name, without wildcards or a path. The engine
    /// checks the same (<c>LaunchPriorityService.ImageName</c>).
    /// </summary>
    public static bool IsPlainProgramName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 && trimmed is not ("." or "..")
               && trimmed.IndexOfAny(new[] { '*', '?', '\\', '/', ':' }) < 0
               && trimmed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>30s, 90s, 5m, 1m 30s... Minutes only from two minutes up, so "60s" stays "60s".</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 120) return $"{seconds}s";
        if (seconds % 3600 == 0) return $"{seconds / 3600}h";
        if (seconds % 60 == 0) return $"{seconds / 60}m";
        return $"{seconds / 60}m {seconds % 60}s";
    }

    public static string PriorityName(int priorityClass) =>
        Enum.IsDefined(typeof(ProcessPriorityClass), priorityClass) ? Fmt.Priority((ProcessPriorityClass)priorityClass) : $"CLASS {priorityClass}";

    /// <summary>The cores in a mask as ranges: 0-3, 0,2,4-5.</summary>
    public static string CoreRange(long mask)
    {
        if (mask == 0) return "NONE";
        var parts = new List<string>();
        int i = 0;
        while (i < 64)
        {
            if (((mask >> i) & 1) == 0) { i++; continue; }
            int j = i;
            while (j + 1 < 64 && ((mask >> (j + 1)) & 1) != 0) j++;
            parts.Add(j == i ? i.ToString(CultureInfo.InvariantCulture) : $"{i}-{j}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }

    public static string IoPriorityName(int value) => value switch
    {
        0 => "VERY LOW",
        1 => "LOW",
        2 => "NORMAL",
        3 => "HIGH",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    public static string MemoryPriorityName(int value) => value switch
    {
        1 => "VERY LOW",
        2 => "LOW",
        3 => "MEDIUM",
        4 => "BELOW NORMAL",
        5 => "NORMAL",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    public static string GpuPriorityName(int value) => value switch
    {
        0 => "IDLE",
        1 => "LOW",
        2 => "NORMAL",
        3 => "ABOVE",
        4 => "HIGH",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    public static string GpuPreferenceName(int value) => value switch
    {
        0 => "AUTO",
        1 => "POWER SAVING",
        2 => "HIGH PERF",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>A grid of logical processors to switch on and off, and the affinity mask it stands for.</summary>
public sealed partial class CorePicker : ObservableObject
{
    private readonly long _shown;

    public ObservableCollection<CoreToggle> Cores { get; } = new();

    [ObservableProperty] private string summary = string.Empty;

    /// <summary>Raised whenever a core is switched on or off.</summary>
    public event Action? Changed;

    /// <param name="logical">How many logical processors the machine has (shown up to 64).</param>
    /// <param name="mask">The cores that start lit; null lights them all.</param>
    public CorePicker(int logical, long? mask = null)
    {
        int count = Math.Clamp(logical, 1, 64);
        _shown = count >= 64 ? -1L : (1L << count) - 1;
        long lit = mask ?? -1L;
        for (int i = 0; i < count; i++)
        {
            var core = new CoreToggle(i, (lit & (1L << i)) != 0);
            core.PropertyChanged += (_, _) => OnCoreChanged();
            Cores.Add(core);
        }
        Refresh();
    }

    /// <summary>Bits of the cores that are on.</summary>
    public long Mask => Cores.Where(c => c.IsOn).Aggregate(0L, (m, c) => m | (1L << c.Index));

    /// <summary>
    /// The mask to store: the lit cores plus any bits of <paramref name="original"/> for cores this machine doesn't
    /// show, so editing a rule never drops cores the picker has no button for.
    /// </summary>
    public long MaskKeeping(long? original) => Mask | ((original ?? 0) & ~_shown);

    public bool AnyOn => Cores.Any(c => c.IsOn);

    public void SetMask(long mask)
    {
        foreach (var core in Cores) core.IsOn = (mask & (1L << core.Index)) != 0;
    }

    private void OnCoreChanged()
    {
        Refresh();
        Changed?.Invoke();
    }

    private void Refresh()
    {
        int on = Cores.Count(c => c.IsOn);
        Summary = on == 0 ? "PICK AT LEAST ONE" : $"{on} OF {Cores.Count} THREADS";
    }

    private void Set(Func<int, bool> on)
    {
        foreach (var core in Cores) core.IsOn = on(core.Index);
    }

    [RelayCommand] private void All() => Set(_ => true);
    [RelayCommand] private void FirstHalf() => Set(i => i < Math.Max(1, Cores.Count / 2));
    [RelayCommand] private void SecondHalf() => Set(i => i >= Cores.Count / 2);
    /// <summary>Every other thread: on SMT machines that is one thread per physical core.</summary>
    [RelayCommand] private void EveryOther() => Set(i => i % 2 == 0);
}

/// <summary>A length of time typed as a number of seconds or minutes, with a row of common values to pick from.</summary>
public sealed partial class DurationInput : ObservableObject
{
    private bool _busy;

    public IReadOnlyList<Choice<bool>> Units { get; } = new[] { new Choice<bool>("SEC", false), new Choice<bool>("MIN", true) };
    public IReadOnlyList<Choice<int>> Presets { get; }

    [ObservableProperty] private string text = "0";
    [ObservableProperty] private Choice<bool> unit;

    public DurationInput(IReadOnlyList<Choice<int>> presets, int seconds = 0)
    {
        Presets = presets;
        unit = Units[0];
        Seconds = seconds;
    }

    /// <summary>Sets the value, showing it in minutes when that is exact.</summary>
    public int Seconds
    {
        set
        {
            _busy = true;
            bool minutes = value >= 60 && value % 60 == 0;
            Unit = Units[minutes ? 1 : 0];
            Text = EditorNumbers.Text(minutes ? value / 60.0 : value);
            _busy = false;
            OnPropertyChanged(nameof(Preset));
        }
    }

    /// <summary>The preset that matches the value, if any. Picking one sets the value.</summary>
    public Choice<int>? Preset
    {
        get => TryGetSeconds(out var s) ? Presets.FirstOrDefault(p => p.Value == s) : null;
        set
        {
            if (value != null) Seconds = value.Value;
        }
    }

    /// <summary>False when the text isn't a number, or is negative or longer than <see cref="RuleText.MaxSeconds"/>.</summary>
    public bool TryGetSeconds(out int seconds)
    {
        seconds = 0;
        if (!EditorNumbers.TryDouble(Text, out var number) || number < 0) return false;
        var total = Math.Round(number * (Unit.Value ? 60 : 1));
        if (total > RuleText.MaxSeconds) return false;
        seconds = (int)total;
        return true;
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(Preset));

    partial void OnUnitChanged(Choice<bool> value)
    {
        if (_busy)
        {
            OnPropertyChanged(nameof(Preset));
            return;
        }

        // Switching SEC/MIN keeps the same length of time instead of the same number.
        if (EditorNumbers.TryDouble(Text, out var number) && number >= 0)
        {
            _busy = true;
            Text = EditorNumbers.Text(value.Value ? number / 60 : number * 60);
            _busy = false;
        }
        OnPropertyChanged(nameof(Preset));
    }
}
