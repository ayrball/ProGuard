using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProGuard.Shared.Contracts;

namespace ProGuard.UI.ViewModels;

/// <summary>
/// A number typed into a text box. Keeps the engine's stored value until the user actually edits it, so a value
/// the page would display rounded (900 s as "15 min") or clamped is never rewritten just because something else on
/// the page was saved.
/// </summary>
public sealed partial class NumberField : ObservableObject
{
    private readonly double _scale;
    private readonly bool _integer;
    private bool _setting;

    public string Label { get; }
    public string Unit { get; }
    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>"1 – 1440 MIN": shown beside the box, and highlighted while the text isn't acceptable.</summary>
    public string Range { get; }

    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private bool isValid = true;

    /// <summary>The user changed the text since the last load.</summary>
    public bool IsEdited { get; private set; }

    /// <summary>The number the text holds, in display units. Meaningful while <see cref="IsValid"/>.</summary>
    public double Value { get; private set; }

    /// <summary>Raised when the user types a number that is acceptable.</summary>
    public event Action? Committed;

    /// <summary>Raised on every edit by the user, acceptable or not (after <see cref="IsValid"/> is up to date).</summary>
    public event Action? Edited;

    /// <param name="scale">Stored value ÷ scale = displayed value (60 shows stored seconds as minutes).</param>
    public NumberField(string label, string unit, double minimum, double maximum, bool integer = true, double scale = 1)
    {
        Label = label;
        Unit = unit;
        Minimum = minimum;
        Maximum = maximum;
        _integer = integer;
        _scale = scale;
        Range = $"{Format(minimum)} – {Format(maximum)} {unit}".TrimEnd();
    }

    /// <summary>Shows a stored value. Not an edit.</summary>
    public void Load(double stored)
    {
        _setting = true;
        try
        {
            Value = stored / _scale;
            Text = Format(Value);
            IsValid = true;
            IsEdited = false;
        }
        finally
        {
            _setting = false;
        }
    }

    /// <summary>The value to store: the engine's own when the user hasn't edited (or the text is unacceptable), else the typed one.</summary>
    public double ToStored(double current) => IsEdited && IsValid ? Value * _scale : current;

    public long ToStored(long current) => IsEdited && IsValid ? (long)Math.Round(Value * _scale) : current;

    public int ToStored(int current) => IsEdited && IsValid ? (int)Math.Round(Value * _scale) : current;

    partial void OnTextChanged(string value)
    {
        if (_setting) return;
        IsEdited = true;
        if (TryParse(value, out var number) && number >= Minimum && number <= Maximum && (!_integer || number == Math.Floor(number)))
        {
            Value = number;
            IsValid = true;
            Committed?.Invoke();
        }
        else
        {
            IsValid = false;
        }
        Edited?.Invoke();
    }

    private static bool TryParse(string text, out double number) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out number)
        || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>A multi-line list kept as the engine stores it (one string with separators) until the user edits it.</summary>
public sealed partial class ListField : ObservableObject
{
    private readonly char[] _separators;
    private string? _original;
    private bool _setting;

    public string Label { get; }

    /// <summary>One entry per line.</summary>
    [ObservableProperty] private string text = string.Empty;

    public event Action? Committed;

    public ListField(string label, params char[] separators)
    {
        Label = label;
        _separators = separators;
    }

    public void Load(string? stored)
    {
        _setting = true;
        try
        {
            _original = stored;
            Text = string.Join(Environment.NewLine, Split(stored));
        }
        finally
        {
            _setting = false;
        }
    }

    /// <summary>The stored text, unchanged when the entries are the same as when it was loaded; else the entries joined with ';'.</summary>
    public string? ToStored()
    {
        var entries = Split(Text);
        if (entries.SequenceEqual(Split(_original), StringComparer.Ordinal)) return _original;
        return entries.Count == 0 ? null : string.Join(";", entries);
    }

    partial void OnTextChanged(string value)
    {
        if (!_setting) Committed?.Invoke();
    }

    private List<string> Split(string? value) => (value ?? string.Empty)
        .Split(_separators.Concat(new[] { '\r', '\n' }).ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}

public sealed partial class PlanOption : ObservableObject
{
    public string Label { get; }

    /// <summary>The plan's GUID as the engine stores it; null is the setting's documented default.</summary>
    public string? Value { get; }

    [ObservableProperty] private bool isOn;

    public PlanOption(string label, string? value, bool isOn)
    {
        Label = label;
        Value = value;
        this.isOn = isOn;
    }
}

/// <summary>
/// Choose one of the installed power plans, or "the default" (stored as null). A plan the machine no longer has stays
/// listed as unknown rather than being dropped, so loading and saving never silently changes the setting.
/// </summary>
public sealed partial class PlanPicker : ObservableObject
{
    private readonly string _defaultLabel;

    public string Label { get; }
    public ObservableCollection<PlanOption> Options { get; } = new();

    /// <summary>The stored value as loaded, or the plan the user picked since.</summary>
    public string? Value { get; private set; }

    public bool IsEdited { get; private set; }

    public event Action? Committed;

    /// <param name="defaultLabel">What a null value means, e.g. "DEFAULT · POWER SAVER".</param>
    public PlanPicker(string label, string defaultLabel)
    {
        Label = label;
        _defaultLabel = defaultLabel;
    }

    public void Load(IReadOnlyList<PowerPlanDto> plans, string? stored)
    {
        Value = stored;
        IsEdited = false;
        Rebuild(plans);
    }

    /// <summary>The plans changed (or arrived after the settings did); the stored value stays.</summary>
    public void SetPlans(IReadOnlyList<PowerPlanDto> plans) => Rebuild(plans);

    private void Rebuild(IReadOnlyList<PowerPlanDto> plans)
    {
        Options.Clear();
        Options.Add(new PlanOption(_defaultLabel, null, Value == null));

        bool known = Value == null;
        foreach (var plan in plans)
        {
            bool on = Matches(Value, plan.Id);
            known |= on;
            Options.Add(new PlanOption(plan.Name.ToUpperInvariant(), plan.Id.ToString(), on));
        }

        if (!known) Options.Add(new PlanOption($"MISSING PLAN {Value}", Value, true));
    }

    private static bool Matches(string? stored, Guid id) => Guid.TryParse(stored, out var guid) && guid == id;

    [RelayCommand]
    private void Pick(PlanOption option)
    {
        if (option.IsOn) return;
        Value = option.Value;
        IsEdited = true;
        foreach (var o in Options) o.IsOn = ReferenceEquals(o, option);
        Committed?.Invoke();
    }

    /// <summary>The value to store.</summary>
    public string? ToStored(string? current) => IsEdited ? Value : current;
}
