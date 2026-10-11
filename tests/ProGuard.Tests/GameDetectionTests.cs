using System.IO;
using Moq;
using ProGuard.Core.Models;
using ProGuard.Core.Services;
using ProGuard.Engine.Services;

namespace ProGuard.Tests;

/// <summary>
/// Automatic Performance Mode's game detection: the launcher path heuristics, the user's list and exclusions, Steam's
/// libraryfolders.vdf / appmanifest_*.acf on sample files, and the service that turns it on and off.
/// </summary>
public class GameDetectionTests
{
    // ---------- path heuristics ----------

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Hades\x64\Hades.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe")]
    [InlineData(@"C:\GOG Games\The Witcher 3\bin\x64\witcher3.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Far Cry 6\bin\FarCry6.exe")]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Games\Overwatch\Overwatch.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 2042\BF2042.exe")]
    [InlineData(@"c:/program files/epic games/game/game.exe")]
    public void LauncherFolders_AreGames(string path)
    {
        Assert.True(GameDetector.LooksLikeGame(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]                                          // not in steamapps\common
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Steamworks Shared\_CommonRedist\vcredist\2019\VC_redist.x64.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Hades\x64\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\upc.exe")]
    [InlineData(@"C:\Program Files\EA Games\Battlefield 2042\__Installer\Touchup.exe")]
    [InlineData(@"C:\GOG Games\The Witcher 3\unins000.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void LaunchersHelpersAndOtherApps_AreNot(string? path)
    {
        Assert.False(GameDetector.LooksLikeGame(path));
    }

    // ---------- the user's list and exclusions ----------

    [Theory]
    [InlineData("game.exe", "game", @"C:\x\game.exe", true)]
    [InlineData("GAME", "game", @"C:\x\game.exe", true)]
    [InlineData("game.exe", "gamer", @"C:\x\gamer.exe", false)]
    [InlineData("gam*.exe", "gamer", @"C:\x\gamer.exe", true)]
    [InlineData(@"C:\Games\Foo\foo.exe", "foo", @"c:\games\foo\foo.exe", true)]
    [InlineData(@"C:\Games\Foo\foo.exe", "foo", @"D:\games\foo\foo.exe", false)]
    [InlineData(@"C:\Games\", "anything", @"C:\Games\Bar\bin\bar.exe", true)]           // a folder covers what's inside
    [InlineData(@"C:\Games", "anything", @"C:\GamesBackup\bar.exe", false)]            // ...but not a sibling with the same prefix
    [InlineData(@"C:\Games\*\bin\*.exe", "bar", @"C:\Games\Bar\bin\bar.exe", true)]
    [InlineData(@"C:\Games\Foo\foo.exe", "foo", null, false)]                           // path unknown (access denied)
    [InlineData("  \"game.exe\" ", "game", null, true)]
    public void ProcessPattern_Matches(string pattern, string name, string? path, bool expected)
    {
        Assert.Equal(expected, ProcessPattern.Matches(pattern, name, path));
    }

    private static ProcessInfo Process(string path, int pid = 100) =>
        new() { Pid = pid, Name = Path.GetFileNameWithoutExtension(path), FilePath = path };

    private static readonly GameDetector NoSteam = new(() => SteamLibraryIndex.Empty);

    [Fact]
    public void ListedProcess_TurnsItOn_EvenOutsideGameFolders()
    {
        var options = new AutoPerformanceModeOptions { Processes = new[] { "blender.exe" } };
        Assert.Equal("blender.exe is running (listed)", NoSteam.Match(Process(@"C:\Program Files\Blender\blender.exe"), options));
        Assert.Null(NoSteam.Match(Process(@"C:\Program Files\Epic Games\Game\game.exe"), options));   // detection is off
    }

    [Fact]
    public void Exclusions_WinOverEverything()
    {
        var options = new AutoPerformanceModeOptions
        {
            Processes = new[] { "blender.exe" },
            Exclusions = new[] { "blender.exe", @"C:\Program Files\Epic Games\Tool\" },
            DetectGames = true,
        };
        Assert.Null(NoSteam.Match(Process(@"C:\Program Files\Blender\blender.exe"), options));
        Assert.Null(NoSteam.Match(Process(@"C:\Program Files\Epic Games\Tool\tool.exe"), options));
        Assert.NotNull(NoSteam.Match(Process(@"C:\Program Files\Epic Games\Game\game.exe"), options));
    }

    [Fact]
    public void ParseList_SplitsOnSemicolonsAndLines_ButNotCommas()
    {
        Assert.Equal(new[] { "a.exe", @"C:\Games, Old\b.exe", "c.exe" },
            AutoPerformanceModeOptions.ParseList("a.exe; C:\\Games, Old\\b.exe\r\n\r\nc.exe;A.EXE"));
        Assert.Empty(AutoPerformanceModeOptions.ParseList(null));
        Assert.Empty(AutoPerformanceModeOptions.ParseList("  "));
    }

    // ---------- Steam libraries ----------

    private const string LibraryFoldersVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"5288817296340126013"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"1290158730"
        			"1145360"		"15430328410"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Games \"SSD\""
        		"apps"
        		{
        			"413150"		"1103346563"
        		}
        	}
        }
        """;

    // The format before 2021: library paths directly under numbered keys.
    private const string OldLibraryFoldersVdf = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1612345678"
        	"ContentStatsID"		"-123456789"
        	"1"		"E:\\Games\\Steam"
        	// a comment
        	"2"		"F:\\SteamLibrary"
        }
        """;

    private static string Manifest(string appId, string name, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"universe"		"1"
        	"name"		"{{name}}"
        	"StateFlags"		"4"
        	"installdir"		"{{installDir}}"
        	"UserConfig"
        	{
        		"language"		"english"
        	}
        }
        """;

    [Fact]
    public void Vdf_ParsesNestedKeysEscapesAndComments()
    {
        var root = VdfNode.Parse(LibraryFoldersVdf);
        var library1 = root.GetNode("libraryfolders")!.GetNode("1")!;
        Assert.Equal(@"D:\SteamLibrary", library1.GetString("PATH"));   // keys are case-insensitive
        Assert.Equal("Games \"SSD\"", library1.GetString("label"));
        Assert.Equal("1103346563", library1.GetNode("apps")!.GetString("413150"));
    }

    [Fact]
    public void Vdf_IsLenientAboutBrokenFiles()
    {
        Assert.Empty(VdfNode.Parse("").Entries);
        var root = VdfNode.Parse("\"a\" { \"b\" \"c\"");       // truncated
        Assert.Equal("c", root.GetNode("a")!.GetString("b"));
        Assert.Null(VdfNode.Parse("\"a\"").GetString("a"));
        Assert.Equal("x", VdfNode.Parse("\"k\" \"x\" [$WIN32] \"j\" \"y\"").GetString("k"));
    }

    [Fact]
    public void LibraryFolders_CurrentFormat()
    {
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLibraryIndex.ParseLibraryFolders(LibraryFoldersVdf));
    }

    [Fact]
    public void LibraryFolders_OldFormat()
    {
        Assert.Equal(new[] { @"E:\Games\Steam", @"F:\SteamLibrary" }, SteamLibraryIndex.ParseLibraryFolders(OldLibraryFoldersVdf));
    }

    [Fact]
    public void AppManifest_GivesTheGameAndItsFolder()
    {
        Assert.Equal(("413150", "Stardew Valley", "Stardew Valley"),
            SteamLibraryIndex.ParseAppManifest(Manifest("413150", "Stardew Valley", "Stardew Valley")));
    }

    [Theory]
    [InlineData("228980", "Steamworks Common Redistributables", "Steamworks Shared")]
    [InlineData("1493710", "Proton Experimental", "Proton - Experimental")]
    [InlineData("1", "Escape", @"..\..\Windows")]                   // installdir must be a plain folder name
    [InlineData("2", "Absolute", @"C:\Windows")]
    [InlineData("3", "Empty", "")]
    public void AppManifest_SkipsToolsAndBadFolders(string appId, string name, string installDir)
    {
        Assert.Null(SteamLibraryIndex.ParseAppManifest(Manifest(appId, name, installDir.Replace(@"\", @"\\"))));
    }

    [Fact]
    public void Load_ReadsEveryLibraryOnDisk_AndFindsTheGameOfAnExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProGuard.Tests", "steam-" + Guid.NewGuid().ToString("N"));
        try
        {
            var steam = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "Library2");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(library, "steamapps"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{steam.Replace(@"\", @"\\")}\" }} \"1\" {{ \"path\" \"{library.Replace(@"\", @"\\")}\" }} }}");
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1145360.acf"), Manifest("1145360", "Hades", "Hades"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"), Manifest("228980", "Steamworks Common Redistributables", "Steamworks Shared"));
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_413150.acf"), Manifest("413150", "Stardew Valley", "Stardew Valley"));
            File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_999.acf"), "garbage {{{");

            var index = SteamLibraryIndex.Load(steam);

            Assert.Equal(new[] { "Hades", "Stardew Valley" }, index.Games.Select(g => g.Name).OrderBy(n => n));
            Assert.Equal("Stardew Valley", index.FindGame(Path.Combine(library, "steamapps", "common", "Stardew Valley", "Stardew Valley.exe"))?.Name);
            Assert.Null(index.FindGame(Path.Combine(library, "steamapps", "common", "Stardew Valley Mods", "x.exe")));
            Assert.Null(index.FindGame(Path.Combine(steam, "steamapps", "common", "Steamworks Shared", "x.exe")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Load_WithoutSteam_IsEmpty()
    {
        Assert.Empty(SteamLibraryIndex.Load(null).Games);
        Assert.Empty(SteamLibraryIndex.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Games);
    }

    [Fact]
    public void EngageForSteam_MatchesOnlyGamesInTheLibraries()
    {
        var index = new SteamLibraryIndex(new[] { new SteamGame("413150", "Stardew Valley", @"D:\SteamLibrary\steamapps\common\Stardew Valley") });
        var detector = new GameDetector(() => index);
        var options = new AutoPerformanceModeOptions { EngageForSteam = true };

        Assert.Equal("Stardew Valley.exe is running (Steam game \"Stardew Valley\")",
            detector.Match(Process(@"D:\SteamLibrary\steamapps\common\Stardew Valley\Stardew Valley.exe"), options));
        Assert.Null(detector.Match(Process(@"D:\SteamLibrary\steamapps\common\Other\other.exe"), options));   // detection is off
        Assert.Null(detector.Match(Process(@"D:\SteamLibrary\steamapps\common\Stardew Valley\UnityCrashHandler64.exe"), options));
    }

    // ---------- the service ----------

    private sealed class Harness
    {
        public List<ProcessInfo> Processes { get; } = new();
        public Mock<IProcessMonitor> Monitor { get; } = new();
        public Mock<IPowerManager> Power { get; } = new();
        public List<(bool Active, string? Reason)> Calls { get; } = new();
        public int SteamLoads { get; private set; }
        public TimeSpan Now { get; set; }
        public AutoPerformanceModeService Service { get; }

        public Harness()
        {
            Monitor.Setup(m => m.GetAllProcesses()).Returns(() => Processes.ToList());
            Power.Setup(p => p.SetAutomaticPerformanceMode(It.IsAny<bool>(), It.IsAny<string?>()))
                 .Callback<bool, string?>((a, r) => Calls.Add((a, r)));
            Service = new AutoPerformanceModeService(null, Monitor.Object, Power.Object,
                () => { SteamLoads++; return SteamLibraryIndex.Empty; }, () => Now);
        }
    }

    [Fact]
    public void Service_TurnsItOnWhileAGameRuns_AndOffWhenNoneRemain()
    {
        var h = new Harness();
        h.Service.Configure(new AutoPerformanceModeOptions { DetectGames = true });
        h.Service.Check();
        Assert.Empty(h.Calls);          // nothing running: nothing to say

        h.Processes.Add(Process(@"C:\Program Files\Epic Games\A\a.exe", pid: 1));
        h.Service.Check();
        h.Processes.Add(Process(@"C:\GOG Games\B\b.exe", pid: 2));
        h.Service.Check();              // still crediting the first game: no new call
        h.Processes.RemoveAt(0);
        h.Service.Check();              // now the second one
        h.Processes.Clear();
        h.Service.Check();

        Assert.Equal(new (bool, string?)[]
        {
            (true, "a.exe is running (detected game)"),
            (true, "b.exe is running (detected game)"),
            (false, "b.exe exited (detected game)"),
        }, h.Calls);
    }

    [Fact]
    public void Service_TurningAutomaticModeOff_ReleasesAtOnce()
    {
        var h = new Harness();
        h.Processes.Add(Process(@"C:\Tools\render.exe"));
        h.Service.Configure(new AutoPerformanceModeOptions { Processes = new[] { "render.exe" } });
        h.Service.Configure(new AutoPerformanceModeOptions());
        Assert.Equal(new (bool, string?)[]
        {
            (true, "render.exe is running (listed)"),
            (false, "automatic Performance Mode turned off"),
        }, h.Calls);
    }

    // Seen live: switching game detection off while a game ran logged "WardogsClient-Win64-Shipping.exe exited".
    [Fact]
    public void Service_ReleasedBecauseTheSettingsChanged_DoesNotSayTheProcessExited()
    {
        var h = new Harness();
        h.Processes.Add(Process(@"C:\Tools\render.exe", pid: 1));
        h.Processes.Add(Process(@"C:\Tools\other.exe", pid: 2));
        h.Service.Configure(new AutoPerformanceModeOptions { Processes = new[] { "render.exe", "other.exe" } });
        h.Service.Configure(new AutoPerformanceModeOptions { Processes = new[] { "other.exe" } });
        h.Service.Configure(new AutoPerformanceModeOptions { Processes = new[] { "nothing.exe" } });

        Assert.Equal(new (bool, string?)[]
        {
            (true, "render.exe is running (listed)"),
            (true, "other.exe is running (listed)"),
            (false, "other.exe no longer turns it on (listed)"),
        }, h.Calls);
    }

    [Fact]
    public void Service_ReadsTheSteamLibrariesOnlyWhenNeeded_AndRereadsThemNowAndThen()
    {
        var h = new Harness();
        h.Processes.Add(Process(@"D:\SteamLibrary\steamapps\common\Game\game.exe"));
        h.Processes.Add(Process(@"C:\Windows\explorer.exe"));
        h.Service.Configure(new AutoPerformanceModeOptions { DetectGames = true });
        h.Service.Check();
        Assert.Equal(0, h.SteamLoads);

        h.Service.Configure(new AutoPerformanceModeOptions { EngageForSteam = true });
        h.Service.Check();
        h.Service.Check();
        Assert.Equal(1, h.SteamLoads);

        h.Now += AutoPerformanceModeService.SteamRescanInterval;
        h.Service.Check();
        Assert.Equal(2, h.SteamLoads);
    }

    [Fact]
    public void Options_FromSettings_AndEquality()
    {
        var settings = new AppSettings { AutoPerformanceModeProcesses = "a.exe;b.exe", PerformanceModeEngageForSteam = true };
        var a = AutoPerformanceModeOptions.FromSettings(settings);
        var b = AutoPerformanceModeOptions.FromSettings(settings.Clone());
        Assert.Equal(a, b);
        Assert.True(a.AnyEnabled);
        Assert.False(AutoPerformanceModeOptions.FromSettings(new AppSettings()).AnyEnabled);
        Assert.NotEqual(a, a with { DetectGames = true });
    }
}
