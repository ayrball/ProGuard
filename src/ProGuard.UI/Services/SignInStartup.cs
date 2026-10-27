using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using ProGuard.UI.Resources;

namespace ProGuard.UI.Services;

/// <summary>Registers, reads and removes Task Scheduler tasks. An interface so tests never touch the real scheduler.</summary>
public interface ITaskScheduler
{
    /// <summary>The task's XML, or null when there is no such task (or it can't be read).</summary>
    string? Query(string name);

    /// <summary>Creates or replaces the task. Throws with Windows' message when it refuses.</summary>
    void Create(string name, string xml);

    /// <summary>Removes the task if it exists. Throws with Windows' message when it refuses.</summary>
    void Delete(string name);
}

/// <summary><see cref="ITaskScheduler"/> through schtasks.exe, so no COM interop is needed.</summary>
public sealed class SchtasksScheduler : ITaskScheduler
{
    public string? Query(string name)
    {
        var (exit, output, _) = Run("/Query", "/TN", name, "/XML");
        return exit == 0 && output.Contains("<Task", StringComparison.Ordinal) ? output : null;
    }

    public void Create(string name, string xml)
    {
        // schtasks reads the definition from a file, and wants it in UTF-16 as the XML declaration says.
        var file = Path.Combine(Path.GetTempPath(), $"ProGuard-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml, Encoding.Unicode);
            var (exit, output, error) = Run("/Create", "/TN", name, "/XML", file, "/F");
            if (exit != 0) throw Refused(error, output);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Delete(string name)
    {
        if (Query(name) == null) return;
        var (exit, output, error) = Run("/Delete", "/TN", name, "/F");
        if (exit != 0) throw Refused(error, output);
    }

    private static Exception Refused(string error, string output)
    {
        var message = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        if (message.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) message = message[6..].Trim();
        return message.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? new UnauthorizedAccessException(message)
            : new InvalidOperationException(message.Length > 0 ? message : Strings.SignIn_Refused);
    }

    private static (int Exit, string Output, string Error) Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            var errorTask = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, errorTask.Result);
        }
        catch (Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}

/// <summary>
/// Start at sign-in without a UAC prompt. A window that always starts as administrator would otherwise ask at every
/// sign-in: the Run key starts it with standard rights and it then asks for more. So while this copy runs elevated
/// and "Always start as administrator" is on, sign-in goes through a per-user scheduled task that runs ProGuard with
/// the user's highest rights (no prompt), and the Run key is removed so it doesn't start twice. Otherwise the Run key
/// is used, as before (<see cref="RunKeyStartup"/>).
/// </summary>
/// <remarks>
/// Creating or removing a task that runs with highest rights takes administrator rights, so only an elevated copy
/// changes it; <see cref="Reconcile"/> runs on every elevated start and puts the two back in line (an installer
/// upgrade, for one, adds the Run key again). Only sign-in is affected: opening ProGuard from the Start menu still asks.
/// </remarks>
public sealed class SignInStartup : IStartupRegistration
{
    /// <summary>The Task Scheduler folder holding one task per user; the installer removes it on uninstall.</summary>
    public const string TaskFolder = @"\ProGuard\";

    private readonly IElevationLauncher _elevation;
    private readonly IStartupRegistration _runKey;
    private readonly ITaskScheduler _tasks;
    private readonly string _exePath;
    private readonly string _userSid;

    public SignInStartup(IElevationLauncher elevation, IStartupRegistration? runKey = null, ITaskScheduler? tasks = null,
        string? exePath = null, string? userSid = null)
    {
        _elevation = elevation;
        _exePath = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("The path of ProGuard.UI.exe is unknown.");
        _runKey = runKey ?? new RunKeyStartup(_exePath);
        _tasks = tasks ?? new SchtasksScheduler();
        _userSid = userSid ?? CurrentUserSid();
    }

    public string TaskName => $"{TaskFolder}Start at sign-in {_userSid}";

    /// <summary>Whether sign-in should go through the task: only an elevated copy may set it up, and only if it's wanted.</summary>
    private bool WantsTask => _elevation.IsElevated && _elevation.AlwaysElevate;

    private bool TaskExists => _tasks.Query(TaskName) != null;

    /// <summary>The task exists and starts this copy of ProGuard.UI.exe (not an older install somewhere else).</summary>
    private bool TaskStartsThisCopy => _tasks.Query(TaskName) is { } xml && string.Equals(CommandIn(xml), _exePath, StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled => _runKey.IsEnabled || TaskStartsThisCopy;

    public void Enable()
    {
        if (WantsTask)
        {
            _tasks.Create(TaskName, TaskXml(_userSid, _exePath));
            _runKey.Disable();
            return;
        }
        if (TaskExists)
        {
            // It already starts ProGuard at sign-in. Only an elevated copy may replace it with the Run key.
            if (!_elevation.IsElevated) return;
            _tasks.Delete(TaskName);
        }
        _runKey.Enable();
    }

    public void Disable()
    {
        _runKey.Disable();
        if (!TaskExists) return;
        if (!_elevation.IsElevated)
            throw new UnauthorizedAccessException(Strings.SignIn_NeedsAdmin);
        _tasks.Delete(TaskName);
    }

    /// <summary>
    /// Puts the task and the Run key in line with "Always start as administrator", keeping start-at-sign-in on or off as
    /// it is. Does nothing in a copy with standard rights, which may not change the task.
    /// </summary>
    public void Reconcile()
    {
        if (!_elevation.IsElevated) return;
        bool task = TaskExists, runKey = _runKey.IsEnabled;
        if (!task && !runKey) return; // start-at-sign-in is off

        if (_elevation.AlwaysElevate)
        {
            if (!TaskStartsThisCopy) _tasks.Create(TaskName, TaskXml(_userSid, _exePath));
            if (runKey) _runKey.Disable();
        }
        else
        {
            if (task) _tasks.Delete(TaskName);
            if (!runKey) _runKey.Enable();
        }
    }

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new SecurityException("Windows didn't say who is signed in.");
    }

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The program a task's XML starts, without quotes; null if it can't be read.</summary>
    public static string? CommandIn(string xml)
    {
        try
        {
            var command = XDocument.Parse(xml).Descendants(Ns + "Command").FirstOrDefault()?.Value;
            return command?.Trim().Trim('"');
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// The task: at this user's sign-in, run <paramref name="exePath"/> --tray with their highest rights. No time limit
    /// (the default would end the tray copy after three days), normal priority (tasks otherwise start below normal),
    /// and it runs on battery too.
    /// </summary>
    public static string TaskXml(string userSid, string exePath)
    {
        var task = new XDocument(new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Author", "ProGuard"),
                    new XElement(Ns + "Description", "Starts ProGuard in the notification area with administrator rights when you sign in, without a UAC prompt. Turn it off on ProGuard's System page.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userSid))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowHardTerminate", "true"),
                    new XElement(Ns + "StartWhenAvailable", "false"),
                    new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(Ns + "IdleSettings",
                        new XElement(Ns + "StopOnIdleEnd", "false"),
                        new XElement(Ns + "RestartOnIdle", "false")),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Hidden", "false"),
                    new XElement(Ns + "RunOnlyIfIdle", "false"),
                    new XElement(Ns + "WakeToRun", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "4")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", $"\"{exePath}\""),
                        new XElement(Ns + "Arguments", RunKeyStartup.TrayArgument),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? string.Empty)))));
        return task.Declaration + Environment.NewLine + task.ToString();
    }
}
