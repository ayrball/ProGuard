using System;
using System.Diagnostics;
using System.Globalization;

namespace ProGuard.Core.Helpers;

/// <summary>
/// Parses <c>WatchdogRule.ActionArgument</c>. Shared by the engine, which acts on it, and the UI, which checks it
/// before saving a rule.
/// </summary>
public static class WatchdogArguments
{
    /// <summary>The priority a SetPriority rule without an argument uses (what it did before the argument was honoured).</summary>
    public const ProcessPriorityClass DefaultPriority = ProcessPriorityClass.Idle;

    /// <summary>IO_PRIORITY_HINT values accepted by NtSetInformationProcess(ProcessIoPriority). Critical (4) is kernel-only.</summary>
    public const int IoVeryLow = 0, IoLow = 1, IoNormal = 2, IoHigh = 3;
    public const int DefaultIoPriority = IoLow;

    public const string ContinuousTrim = "continuous";

    /// <summary>Accepts a ProcessPriorityClass name ("BelowNormal", "Below Normal", "Realtime") or its numeric value ("16384").</summary>
    public static bool TryParsePriority(string? text, out ProcessPriorityClass priority)
    {
        priority = DefaultPriority;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var compact = text.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (compact.Equals("Realtime", StringComparison.OrdinalIgnoreCase)) compact = nameof(ProcessPriorityClass.RealTime);
        if (Enum.TryParse(compact, ignoreCase: true, out ProcessPriorityClass parsed) && Enum.IsDefined(parsed))
        {
            priority = parsed;
            return true;
        }
        return false;
    }

    /// <summary>Accepts 0-3 or VeryLow / Low / Normal / High.</summary>
    public static bool TryParseIoPriority(string? text, out int ioPriority)
    {
        ioPriority = DefaultIoPriority;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var compact = text.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (int.TryParse(compact, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            if (number < IoVeryLow || number > IoHigh) return false;
            ioPriority = number;
            return true;
        }

        int? named = compact.ToLowerInvariant() switch
        {
            "verylow" => IoVeryLow,
            "low" => IoLow,
            "normal" => IoNormal,
            "high" => IoHigh,
            _ => null
        };
        if (named == null) return false;
        ioPriority = named.Value;
        return true;
    }

    public static string IoPriorityName(int ioPriority) => ioPriority switch
    {
        IoVeryLow => "Very Low",
        IoLow => "Low",
        IoNormal => "Normal",
        IoHigh => "High",
        _ => ioPriority.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Accepts a non-zero mask in decimal or 0x-prefixed hex.</summary>
    public static bool TryParseAffinity(string? text, out long mask)
    {
        mask = 0;
        var trimmed = text?.Trim() ?? "";
        var parsed = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask)
            : long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask);
        return parsed && mask != 0;
    }

    public static bool IsContinuousTrim(string? text) =>
        string.Equals(text?.Trim(), ContinuousTrim, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a command line into the program (unquoted) and the rest, the way CreateProcess reads its first token:
    /// up to the closing quote if it starts with one, else up to the first space. Returns false for a blank line.
    /// </summary>
    public static bool TrySplitCommandLine(string? commandLine, out string program, out string arguments)
    {
        program = arguments = string.Empty;
        var text = commandLine?.Trim() ?? "";
        if (text.Length == 0) return false;

        int end;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            if (close < 0) { program = text[1..]; return program.Length > 0; }
            program = text[1..close];
            end = close + 1;
        }
        else
        {
            end = text.IndexOf(' ');
            if (end < 0) end = text.Length;
            program = text[..end];
        }

        arguments = end < text.Length ? text[end..].Trim() : string.Empty;
        return program.Length > 0;
    }

    /// <summary>A command line that starts with <paramref name="program"/>, quoted, followed by <paramref name="arguments"/>.</summary>
    public static string BuildCommandLine(string program, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? $"\"{program}\"" : $"\"{program}\" {arguments.Trim()}";
}
