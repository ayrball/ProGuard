using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using ProGuard.Core.Models;

namespace ProGuard.Engine.Services.ProcessRules;

/// <summary>
/// The <c>PerfOptions</c> values Windows reads from a program's Image File Execution Options key when it creates the
/// process. Null leaves that value unset.
/// </summary>
/// <param name="CpuPriorityClass">1 Idle, 2 Normal, 3 High, 5 Below Normal, 6 Above Normal (not the PRIORITY_CLASS flags).</param>
/// <param name="IoPriority">IO_PRIORITY_HINT: 0 Very Low .. 3 High.</param>
/// <param name="PagePriority">MEMORY_PRIORITY: 1 Very Low .. 5 Normal.</param>
public readonly record struct PerfOptions(int? CpuPriorityClass, int? IoPriority, int? PagePriority)
{
    public bool IsEmpty => CpuPriorityClass == null && IoPriority == null && PagePriority == null;
}

/// <summary>Reads and writes the <c>PerfOptions</c> ProGuard manages, keyed by image name ("chrome.exe").</summary>
public interface ILaunchPriorityRegistry
{
    /// <summary>Every image whose PerfOptions ProGuard wrote, with what is there now.</summary>
    IReadOnlyDictionary<string, PerfOptions> ReadManaged();

    /// <summary>Writes the values (removing the ones that are null) and marks the key as ProGuard's.</summary>
    void Write(string image, PerfOptions options);

    /// <summary>Removes ProGuard's values, and the PerfOptions and image keys if that leaves them empty.</summary>
    void Remove(string image);
}

/// <summary>
/// The real registry: <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options</c>, 64-bit view
/// (Windows reads the same key for 32-bit programs). Tests point it at a key under HKCU instead.
/// <para>
/// ProGuard marks the PerfOptions keys it writes with a <see cref="MarkerValue"/> value, which Windows ignores, so it can
/// find and remove its own entries (after a rule is deleted or renamed while the engine was stopped, say) without
/// touching PerfOptions an administrator or another tool set up. A key already holding PerfOptions values that ProGuard
/// didn't write is taken over when a rule asks for it, and those values go when the rule does.
/// </para>
/// </summary>
public sealed class RegistryLaunchPriorityRegistry : ILaunchPriorityRegistry
{
    public const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string MarkerValue = "ProGuardManaged";
    private const string PerfOptionsKey = "PerfOptions";
    private static readonly string[] Values = { "CpuPriorityClass", "IoPriority", "PagePriority" };

    private readonly RegistryKey _root;
    private readonly string _path;

    public RegistryLaunchPriorityRegistry()
        : this(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), IfeoPath)
    {
    }

    public RegistryLaunchPriorityRegistry(RegistryKey root, string path)
    {
        _root = root;
        _path = path;
    }

    public IReadOnlyDictionary<string, PerfOptions> ReadManaged()
    {
        var managed = new Dictionary<string, PerfOptions>(StringComparer.OrdinalIgnoreCase);
        using var ifeo = _root.OpenSubKey(_path);
        if (ifeo == null) return managed;
        foreach (var image in ifeo.GetSubKeyNames())
        {
            using var perf = ifeo.OpenSubKey(image + "\\" + PerfOptionsKey);
            if (perf?.GetValue(MarkerValue) == null) continue;
            managed[image] = new PerfOptions(ReadInt(perf, Values[0]), ReadInt(perf, Values[1]), ReadInt(perf, Values[2]));
        }
        return managed;
    }

    public void Write(string image, PerfOptions options)
    {
        using var perf = _root.CreateSubKey(_path + "\\" + image + "\\" + PerfOptionsKey, writable: true);
        WriteInt(perf, Values[0], options.CpuPriorityClass);
        WriteInt(perf, Values[1], options.IoPriority);
        WriteInt(perf, Values[2], options.PagePriority);
        perf.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
    }

    public void Remove(string image)
    {
        using var ifeo = _root.OpenSubKey(_path, writable: true);
        if (ifeo == null) return;
        using (var perf = ifeo.OpenSubKey(image + "\\" + PerfOptionsKey, writable: true))
        {
            if (perf == null) return;
            foreach (var name in Values.Append(MarkerValue)) perf.DeleteValue(name, throwOnMissingValue: false);
        }
        DeleteIfEmpty(ifeo, image + "\\" + PerfOptionsKey);
        DeleteIfEmpty(ifeo, image);
    }

    private static void DeleteIfEmpty(RegistryKey parent, string path)
    {
        using (var key = parent.OpenSubKey(path))
        {
            if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
        }
        parent.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static int? ReadInt(RegistryKey key, string name) => key.GetValue(name) is int value ? value : null;

    private static void WriteInt(RegistryKey key, string name, int? value)
    {
        if (value is int v) key.SetValue(name, v, RegistryValueKind.DWord);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Keeps the Image File Execution Options <c>PerfOptions</c> in line with the process rules that ask for
/// <see cref="ProcessRule.ApplyPrioritiesAtCreation"/>: Windows then starts those programs with the rule's priority
/// class, I/O priority and memory (page) priority, where the engine's poll would apply them up to a second later.
/// <see cref="Sync"/> writes what the rules want and removes every entry it wrote that no rule wants any more, so a
/// rule deleted, renamed or with the option cleared takes its registry entry with it.
/// </summary>
public sealed class LaunchPriorityService
{
    private readonly ILogger<LaunchPriorityService> _logger;
    private readonly ILaunchPriorityRegistry _registry;
    private readonly object _sync = new();
    private Dictionary<string, PerfOptions>? _lastSynced;

    public LaunchPriorityService(ILogger<LaunchPriorityService>? logger = null, ILaunchPriorityRegistry? registry = null)
    {
        _logger = logger ?? NullLogger<LaunchPriorityService>.Instance;
        _registry = registry ?? new RegistryLaunchPriorityRegistry();
    }

    /// <summary>
    /// The image name Windows looks up for a rule's process name ("chrome" → "chrome.exe"), or null when the name
    /// isn't a plain file name: wildcards and paths can't be expressed in Image File Execution Options.
    /// </summary>
    public static string? ImageName(string? processName)
    {
        var name = processName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.IndexOfAny(new[] { '*', '?', '\\', '/', ':' }) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (name is "." or "..") return null;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>The IFEO CpuPriorityClass value for a priority class; null for Realtime, which Windows doesn't apply from there.</summary>
    public static int? CpuPriorityClassValue(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => 1,
        ProcessPriorityClass.Normal => 2,
        ProcessPriorityClass.High => 3,
        ProcessPriorityClass.BelowNormal => 5,
        ProcessPriorityClass.AboveNormal => 6,
        _ => null,
    };

    /// <summary>What a rule writes: its priority class, I/O priority and memory priority, where set.</summary>
    public static PerfOptions ToPerfOptions(ProcessRule rule) => new(
        rule.PriorityClass is int pc ? CpuPriorityClassValue((ProcessPriorityClass)pc) : null,
        rule.IoPriority,
        rule.MemoryPriority);

    /// <summary>The entries the rules ask for, by image name. Where two rules name the same image, the first one wins.</summary>
    public static Dictionary<string, PerfOptions> Desired(IEnumerable<ProcessRule> rules)
    {
        var desired = new Dictionary<string, PerfOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules.Where(r => r.ApplyPrioritiesAtCreation && !r.DisallowExecution))
        {
            if (ImageName(rule.ProcessName) is not { } image) continue;
            var options = ToPerfOptions(rule);
            if (!options.IsEmpty) desired.TryAdd(image, options);
        }
        return desired;
    }

    /// <summary>
    /// Brings the registry in line with <paramref name="rules"/>. The registry is only read when what the rules ask for
    /// changed since the last call, unless <paramref name="force"/> (at startup, to catch up on changes made while the
    /// engine was stopped). Never throws: without administrator rights (an engine run by hand, unelevated) the entries
    /// just aren't written, and the rules still apply on the next poll. Returns the number of entries written or removed.
    /// </summary>
    public int Sync(IEnumerable<ProcessRule> rules, bool force = false)
    {
        lock (_sync)
        {
            var desired = Desired(rules);
            if (!force && _lastSynced != null && SameEntries(_lastSynced, desired)) return 0;
            // Failures aren't retried until the rules change or the engine restarts: they mean no rights, which a retry
            // every reload wouldn't fix, only log again.
            _lastSynced = desired;
            try
            {
                var current = _registry.ReadManaged();
                int changes = 0;

                foreach (var (image, options) in desired)
                {
                    if (current.TryGetValue(image, out var existing) && existing == options) continue;
                    if (Try(() => _registry.Write(image, options), "write", image))
                    {
                        changes++;
                        _logger.LogInformation("Launch priorities for {Image} written to Image File Execution Options: {Options}", image, options);
                    }
                }

                foreach (var image in current.Keys.Where(i => !desired.ContainsKey(i)).ToList())
                {
                    if (Try(() => _registry.Remove(image), "remove", image))
                    {
                        changes++;
                        _logger.LogInformation("Launch priorities for {Image} removed from Image File Execution Options", image);
                    }
                }
                return changes;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                _logger.LogWarning(ex, "Can't read Image File Execution Options; launch priorities aren't kept in step");
                return 0;
            }
        }
    }

    private static bool SameEntries(Dictionary<string, PerfOptions> a, Dictionary<string, PerfOptions> b) =>
        a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var other) && other == e.Value);

    private bool Try(Action action, string what, string image)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Can't {What} the launch priorities for {Image} in Image File Execution Options", what, image);
            return false;
        }
    }
}
