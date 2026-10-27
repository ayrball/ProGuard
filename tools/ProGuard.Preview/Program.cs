using System.Globalization;
using System.IO;

namespace ProGuard.Preview;

/// <summary>
/// ProGuard.Preview: renders the UI against a stand-in engine, so the design can be reviewed without the real engine
/// or administrator rights.
/// </summary>
internal static class Program
{
    private const string Usage = """
        ProGuard.Preview [outputDir] [--mode admin,standard,offline|all] [--size 1440x900] [--scale 1] [--culture en]
            Opens the real main window off-screen against a stand-in engine, visits every page of the shell's
            navigation, opens every dialog and editor it can, and writes a PNG of each to
            <outputDir>/<mode>/ (default: artifacts/preview in the repository).
              admin     the caller is an administrator (editing is allowed)
              standard  the caller is a standard user (the engine answers "Access denied" where it would)
              offline   no engine at all (default modes: admin and standard)
            --culture renders the window in that language (es, de, fr, pt-BR...); English by default, whatever
            the PC's own language is, so screenshots stay comparable.

        ProGuard.Preview --serve <pipeName> [--standard]
            Runs only the stand-in engine. Start the real UI against it with PROGUARD_PIPE=<pipeName>.

        ProGuard.Preview --probe <pipeName>
            Against a running engine (the real one, or --serve): measures UI-thread stalls and right-click latency
            on the Processes page. Briefly turns the engine's ProBalance/Foreground Boost/SmartTrim off, then restores them.
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        string? outDir = null, servePipe = null, probePipe = null;
        var modes = new List<RenderMode> { RenderMode.Admin, RenderMode.Standard };
        int width = 1440, height = 900;
        double scale = 1;
        bool standard = false;
        var culture = "en";

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h" or "--help" or "/?":
                        Console.WriteLine(Usage);
                        return 0;
                    case "--serve": servePipe = Next(args, ref i); break;
                    case "--probe": probePipe = Next(args, ref i); break;
                    case "--standard": standard = true; break;
                    case "--mode": modes = ParseModes(Next(args, ref i)); break;
                    case "--scale": scale = double.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--culture": culture = Next(args, ref i); break;
                    case "--size":
                        var parts = Next(args, ref i).Split('x', 'X');
                        width = int.Parse(parts[0], CultureInfo.InvariantCulture);
                        height = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}.");
                        outDir = args[i];
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 64;
        }

        // The UI's text (Strings.resx and its translations) follows the current UI culture.
        try
        {
            var ui = CultureInfo.GetCultureInfo(culture);
            CultureInfo.DefaultThreadCurrentUICulture = ui;
            CultureInfo.CurrentUICulture = ui;
        }
        catch (CultureNotFoundException)
        {
            Console.Error.WriteLine($"Unknown culture '{culture}'.");
            return 64;
        }

        if (servePipe != null) return Serve(servePipe, standard);
        if (probePipe != null) return Probe.Run(probePipe);

        var options = new RenderOptions(ResolveOutputDirectory(outDir), modes, width, height, scale);
        return new Renderer(options).Run();
    }

    private static string Next(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value.");

    private static List<RenderMode> ParseModes(string text)
    {
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return Enum.GetValues<RenderMode>().ToList();
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => Enum.TryParse<RenderMode>(m, ignoreCase: true, out var mode) ? mode : throw new ArgumentException($"Unknown mode '{m}'."))
            .Distinct().ToList();
    }

    /// <summary>A path given on the command line is used as it is; otherwise artifacts/preview in the repository the tool is run from.</summary>
    private static string ResolveOutputDirectory(string? given)
    {
        if (given != null) return Path.GetFullPath(given);
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ProGuard.sln"))) return Path.Combine(dir.FullName, "artifacts", "preview");
            }
        }
        return Path.GetFullPath(Path.Combine("artifacts", "preview"));
    }

    private static int Serve(string pipe, bool standard)
    {
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };

        var engine = new StandInEngine(pipe, callerIsAdministrator: !standard).Start();
        Console.WriteLine($"Stand-in engine ({(standard ? "standard user" : "administrator")}) listening on pipe '{pipe}'. Ctrl+C to stop.");
        Console.WriteLine($"Run the UI against it:  $env:PROGUARD_PIPE='{pipe}'; .\\src\\ProGuard.UI\\bin\\Debug\\net10.0-windows\\ProGuard.UI.exe");
        done.Wait();
        engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        return 0;
    }
}
