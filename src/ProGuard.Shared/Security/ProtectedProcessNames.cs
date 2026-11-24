using System;
using System.Collections.Generic;
using System.Linq;

namespace ProGuard.Shared.Security;

/// <summary>
/// The programs ProGuard leaves alone, by file name, in one place for the engine and the UI:
/// <list type="bullet">
/// <item><see cref="CriticalNames"/>: session, security and ProGuard processes no rule may change or end
/// (the engine's <c>CriticalProcesses</c>);</item>
/// <item><see cref="SecurityProductNames"/>: antivirus and EDR programs, which no rule may give launch-time priorities.</item>
/// </list>
/// Names are compared without ".exe" and whatever their case ("MsMpEng.exe", "msmpeng" alike).
/// </summary>
public static class ProtectedProcessNames
{
    /// <summary>
    /// Session and security processes, and ProGuard itself: ending, starving or re-prioritising them takes Windows (or
    /// ProGuard) down with them, so no rule acts on them, for administrators either.
    /// </summary>
    public static readonly IReadOnlySet<string> CriticalNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "system", "idle", "registry", "memory compression", "secure system", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "dwm", "fontdrvhost", "proguard.engine", "proguard.ui", "proguard.sessionagent",
        // Sign-in, UAC and the session shell host: ending them logs the user off or breaks elevation prompts.
        "consent", "logonui", "userinit", "sihost",
        // Hosts many services at once: ending one by name takes unrelated services down.
        "svchost",
        // Windows Security's service.
        "securityhealthservice",
    };

    /// <summary>
    /// Antivirus and EDR programs: Microsoft Defender and Windows Security, and the common third-party products.
    /// <para>
    /// Why: launch-time priorities are written to the program's Image File Execution Options key under HKLM, which
    /// Windows reads every time the program starts. Security products watch their own IFEO keys, because malware uses
    /// them to hijack or slow down a scanner, and a write there by another program is exactly what they flag. Lowering
    /// a scanner's priority also weakens the machine's protection, and raising it buys nothing. Most of these run as
    /// protected processes, so ProGuard couldn't change them while they run anyway.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> SecurityProductNames = new HashSet<string>(StringComparer.Ordinal)
    {
        // Microsoft Defender Antivirus, Defender for Endpoint, Windows Security and SmartScreen.
        "msmpeng", "mpdefendercoreservice", "nissrv", "mpcmdrun", "mssense", "senseir", "sensecncproxy", "sensendr",
        "securityhealthservice", "securityhealthsystray", "smartscreen",
        // Kaspersky.
        "avp", "avpui", "kavfs",
        // Bitdefender.
        "bdservicehost", "bdagent", "vsserv", "epsecurityservice",
        // ESET.
        "ekrn", "egui",
        // AVG and Avast.
        "avgsvc", "avgui", "avastsvc", "avastui", "aswidsagent",
        // McAfee / Trellix.
        "mcshield", "mfemms", "mfevtps", "mfetp", "masvc",
        // Sophos.
        "sophoshealth", "sophosfilescanner", "sophosntpservice", "savservice", "sedservice",
        // CrowdStrike Falcon.
        "csfalconservice", "csfalconcontainer",
        // SentinelOne.
        "sentinelagent", "sentinelservicehost", "sentinelstaticengine",
        // VMware Carbon Black.
        "cb", "repmgr", "repux",
        // Malwarebytes.
        "mbamservice", "mbamtray",
        // Norton and Symantec Endpoint Protection.
        "nortonsecurity", "ccsvchst", "sepwscsvc",
        // Trend Micro.
        "ntrtscan", "tmbmsrv", "pccntmon",
        // Palo Alto Cortex XDR, Cylance, Webroot, Trellix/FireEye HX, Elastic.
        "cyserver", "cytray", "cylancesvc", "wrsa", "xagt", "elastic-endpoint",
        // F-Secure / WithSecure, G DATA, Panda / WatchGuard, Comodo.
        "fshoster32", "fsulprothoster", "avkservice", "psanhost", "cmdagent", "cavwp",
    };

    /// <summary>The name as rules compare it: trimmed, without ".exe", lower case.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];
        return trimmed.ToLowerInvariant();
    }

    /// <summary>True for a session, security or ProGuard process (<see cref="CriticalNames"/>).</summary>
    public static bool IsCritical(string? processName) => CriticalNames.Contains(Normalize(processName));

    /// <summary>
    /// True when ProGuard must never write launch-time priorities (Image File Execution Options PerfOptions) for this
    /// program: a security product (<see cref="SecurityProductNames"/>) or a core system program (<see cref="CriticalNames"/>).
    /// </summary>
    public static bool IsLaunchPriorityBlocked(string? processName)
    {
        var name = Normalize(processName);
        return SecurityProductNames.Contains(name) || CriticalNames.Contains(name);
    }

    /// <summary>Every name <see cref="IsLaunchPriorityBlocked"/> refuses, as "name.exe", for documentation and tests.</summary>
    public static IEnumerable<string> LaunchPriorityBlockList =>
        SecurityProductNames.Union(CriticalNames).OrderBy(n => n, StringComparer.Ordinal).Select(n => n + ".exe");
}
