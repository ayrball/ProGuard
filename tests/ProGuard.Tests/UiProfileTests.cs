using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProGuard.Preview;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;
using ProGuard.UI.Services;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>The System page's PROFILES &amp; BACKUP section, driven against the preview tool's stand-in engine.</summary>
public class UiProfileTests
{
    private sealed class FakeFiles : IConfigFiles
    {
        public string? SavePath { get; set; }
        public string? OpenPath { get; set; }
        public string? Suggested { get; private set; }

        public string? PickSavePath(string suggestedName)
        {
            Suggested = suggestedName;
            return SavePath;
        }

        public string? PickOpenPath() => OpenPath;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public StandInEngine Engine { get; }
        public EngineClient Client { get; }
        public ShellViewModel Shell { get; }
        public FakeFiles Files { get; } = new();
        public ProfilesViewModel Profiles { get; }
        public int Reloads { get; private set; }

        public Rig(bool administrator = true)
        {
            Engine = new StandInEngine("ProGuard.Tests.UiProfiles." + Guid.NewGuid().ToString("N"), administrator).Start();
            Client = new EngineClient(Engine.PipeName);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var toaster = new Toaster(dispatcher);
            var hub = new EngineHub(new EngineClient(Engine.PipeName), dispatcher, toaster);
            Shell = new ShellViewModel(hub, toaster, new IconCache(dispatcher));
            Profiles = new ProfilesViewModel(hub, Shell, () => { Reloads++; return Task.CompletedTask; }, Files);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Engine.DisposeAsync();
        }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"proguard_ui_{Guid.NewGuid():N}{ConfigDocuments.FileExtension}");

    [Fact]
    public async Task Load_ListsNamedProfilesBeforeAutomaticOnes()
    {
        await using var rig = new Rig();
        await rig.Profiles.LoadAsync();
        Assert.True(rig.Profiles.HasProfiles);
        Assert.Equal(new[] { "GAMING", "OLD DESKTOP", "QUIET WORK" }, rig.Profiles.Profiles.Select(p => p.Name));

        var gaming = rig.Profiles.Profiles[0];
        await rig.Profiles.SwitchProfileCommand.ExecuteAsync(gaming);
        await ((ConfigPreviewDialogViewModel)rig.Shell.Dialog!).ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("BEFORE SWITCH", rig.Profiles.Profiles.Last().Name);
        Assert.True(rig.Profiles.Profiles.Last().Automatic);
        Assert.Contains("KEPT AUTOMATICALLY", rig.Profiles.Profiles.Last().Detail);
        Assert.Equal(1, rig.Reloads); // the page reloads the settings the switch replaced
    }

    [Fact]
    public async Task Switch_ShowsThePreviewFirst_AndOnlySwitchesOnConfirm()
    {
        await using var rig = new Rig();
        await rig.Profiles.LoadAsync();
        var desktop = rig.Profiles.Profiles.Single(p => p.Name == "OLD DESKTOP");

        await rig.Profiles.SwitchProfileCommand.ExecuteAsync(desktop);

        var dialog = Assert.IsType<ConfigPreviewDialogViewModel>(rig.Shell.Dialog);
        Assert.Equal("SWITCH TO OLD DESKTOP?", dialog.Title);
        Assert.True(dialog.HasMachineSpecific);
        Assert.Contains("MACHINE-SPECIFIC VALUES LEFT OUT", dialog.Summary);
        Assert.Contains(dialog.Changes, c => c.Sign == "+" && c.Name == "render.exe");
        Assert.DoesNotContain(await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules), r => r.ProcessName == "render.exe");

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(rig.Shell.Dialog);
        Assert.Contains(await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules), r => r.ProcessName == "render.exe");
    }

    [Fact]
    public async Task ExportThenImport_GoesThroughAFile_AndPreviewsTheChanges()
    {
        await using var rig = new Rig();
        var path = TempFile();
        try
        {
            rig.Files.SavePath = path;
            await rig.Profiles.ExportCommand.ExecuteAsync(null);
            Assert.EndsWith(ConfigDocuments.FileExtension, rig.Files.Suggested);
            var exported = ConfigDocuments.Parse(File.ReadAllText(path), out var error);
            Assert.Null(error);
            Assert.NotEmpty(exported!.ProcessRules);

            // Change the file: one rule more, from a machine with 64 threads.
            exported.ProcessRules.Add(new ProcessRuleDto { ProcessName = "encoder.exe", PriorityClass = 16384, AffinityMask = unchecked((long)0xFF00000000000000UL) });
            File.WriteAllText(path, ConfigDocuments.Serialize(exported));

            rig.Files.OpenPath = path;
            await rig.Profiles.ImportCommand.ExecuteAsync(null);

            var dialog = Assert.IsType<ConfigPreviewDialogViewModel>(rig.Shell.Dialog);
            Assert.Equal("IMPORT CURRENT CONFIGURATION?", dialog.Title);
            var change = Assert.Single(dialog.Changes);
            Assert.Equal(("+", "PROCESS RULE", "encoder.exe"), (change.Sign, change.Area, change.Name));
            var flag = Assert.Single(dialog.MachineSpecific);
            Assert.Equal("Cores (affinity)", flag.Setting);
            Assert.True(dialog.CanApply);

            await dialog.ConfirmCommand.ExecuteAsync(null);
            var rule = (await rig.Client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules)).Single(r => r.ProcessName == "encoder.exe");
            Assert.Null(rule.AffinityMask);
            Assert.Contains(rig.Profiles.Profiles, p => p.Name == "BEFORE IMPORT");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_OfAFileFromANewerVersion_OpensNothing()
    {
        await using var rig = new Rig();
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{ "Format": "ProGuard.Config", "SchemaVersion": 7 }""");
            rig.Files.OpenPath = path;

            await rig.Profiles.ImportCommand.ExecuteAsync(null);

            // The engine's refusal ("made by a newer version of ProGuard") is shown as a toast; nothing opens.
            Assert.Null(rig.Shell.Dialog);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StandardUser_SeesWhyAnImportCantBeApplied()
    {
        await using var rig = new Rig(administrator: false);
        var doc = await rig.Client.QueryAsync<ConfigDocument>(CommandTypes.ExportConfig);
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "turbo.exe", PriorityClass = 256 });
        var path = TempFile();
        try
        {
            File.WriteAllText(path, ConfigDocuments.Serialize(doc));
            rig.Files.OpenPath = path;

            await rig.Profiles.ImportCommand.ExecuteAsync(null);

            var dialog = Assert.IsType<ConfigPreviewDialogViewModel>(rig.Shell.Dialog);
            Assert.False(dialog.CanApply);
            Assert.False(dialog.ConfirmCommand.CanExecute(null));
            Assert.Contains(dialog.Blockers, b => b.StartsWith("Access denied") && b.Contains("Realtime"));
            Assert.Contains("CAN'T BE APPLIED", dialog.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAs_AsksForAName_AndRefusesTheAutomaticOnes()
    {
        await using var rig = new Rig();
        await rig.Profiles.LoadAsync();

        rig.Profiles.NewProfileCommand.Execute(null);
        var dialog = Assert.IsType<ProfileNameDialogViewModel>(rig.Shell.Dialog);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        dialog.Name = "before switch";
        Assert.False(dialog.ConfirmCommand.CanExecute(null));
        dialog.Name = "gaming";
        Assert.Equal("REPLACES THE PROFILE OF THAT NAME", dialog.Hint);
        dialog.Name = " Evening ";
        Assert.True(dialog.ConfirmCommand.CanExecute(null));

        await dialog.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(rig.Profiles.Profiles, p => p.Name == "EVENING");
    }

    [Fact]
    public async Task Rename_RefusesANameInUse()
    {
        await using var rig = new Rig();
        await rig.Profiles.LoadAsync();
        rig.Profiles.RenameProfileCommand.Execute(rig.Profiles.Profiles.Single(p => p.Name == "GAMING"));
        var dialog = Assert.IsType<ProfileNameDialogViewModel>(rig.Shell.Dialog);

        dialog.Name = "Quiet work";
        Assert.Equal("ANOTHER PROFILE HAS THAT NAME", dialog.Hint);
        Assert.False(dialog.ConfirmCommand.CanExecute(null));

        dialog.Name = "Games";
        await dialog.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(rig.Profiles.Profiles, p => p.Name == "GAMES");
    }

    // ------------------------------------------------------------------ rendering

    private static readonly ConfigPreviewDto FlaggedPreview = new()
    {
        Name = "Old desktop",
        Changes =
        {
            new ConfigChangeDto { Kind = ConfigChangeKind.Added, Area = "Process rule", Name = "render.exe", Detail = "priority Below normal" },
            new ConfigChangeDto { Kind = ConfigChangeKind.Removed, Area = "Watchdog rule", Name = "chrome.exe", Detail = "memory above 4000 MB → Trim working set" },
            new ConfigChangeDto { Kind = ConfigChangeKind.Changed, Area = "Setting", Name = "ProBalance sensitivity", Detail = "5 → 8" },
        },
        MachineSpecific =
        {
            new MachineSpecificValueDto { Where = "Process rule render.exe", Setting = "Cores (affinity)", Value = "0xFFFF0000", Reason = "names processors this PC doesn't have (it has 8)", Outcome = "Left out: the rule doesn't change the cores" },
            new MachineSpecificValueDto { Where = "Settings", Setting = "Performance Mode plan", Value = "Studio render (5c1f0e9a-7d2b-4c8e-9f3a-1b6d2e4a8c71)", Reason = "isn't installed on this PC", Outcome = "Left out: uses the default (High performance)" },
        },
    };

    [Fact]
    public void PreviewDialog_And_ProfilesSection_Render()
    {
        Exception? failure = null;
        UiTestApp.Run(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var toaster = new Toaster(dispatcher);
                var hub = new EngineHub(new EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
                var shell = new ShellViewModel(hub, toaster, new IconCache(dispatcher));

                var dialog = new ProGuard.UI.Views.ConfigPreviewDialog
                {
                    DataContext = new ConfigPreviewDialogViewModel(shell, FlaggedPreview, "SWITCH PROFILE", "SWITCH TO OLD DESKTOP?", "SWITCH", "Kept as Before switch.", () => Task.CompletedTask),
                };
                var dialogPixels = Render(dialog, 780, 820, "config-preview-dialog");
                Assert.True(Distinct(dialogPixels) > 2, "the preview dialog rendered blank");
                var texts = Texts(dialog);
                Assert.Contains("ONLY FOR THE MACHINE IT WAS SAVED ON", texts);
                Assert.Contains("render.exe", texts);
                Assert.Contains("WHAT CHANGES", texts);

                shell.System.Profiles.Show(new[]
                {
                    new ConfigProfileDto { Id = 1, Name = "Gaming", ProcessRuleCount = 4, WatchdogRuleCount = 2, SavedUtc = DateTime.UtcNow },
                    new ConfigProfileDto { Id = 2, Name = "Before switch", Automatic = true },
                });
                var page = new ProGuard.UI.Views.SystemView { DataContext = shell.System };
                Render(page, 1100, 5200, "system-page");
                var pageTexts = Texts(page);
                Assert.Contains("PROFILES & BACKUP", pageTexts);
                Assert.Contains("GAMING", pageTexts);
                Assert.Contains("BEFORE SWITCH", pageTexts);
                Assert.Contains("4 RULES · 2 WATCHDOG · SAVED", string.Join("\n", pageTexts));

                var nameDialog = new ProGuard.UI.Views.ProfileNameDialog
                {
                    DataContext = new ProfileNameDialogViewModel(shell, "SAVE CURRENT AS", "NEW PROFILE", "SAVE", "Gaming", new[] { "Gaming" }, _ => Task.CompletedTask),
                };
                Render(nameDialog, 580, 400, "profile-name-dialog");
                Assert.Contains("REPLACES THE PROFILE OF THAT NAME", Texts(nameDialog));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        Assert.Null(failure);
    }

    /// <summary>
    /// Lays the view out in a host styled as the main window (ink background, volt text) and renders it. With
    /// PROGUARD_RENDER_DIR set, also saves it there as <paramref name="name"/>.png, for looking at.
    /// </summary>
    private static uint[] Render(FrameworkElement view, int width, int height, string name)
    {
        var resources = Application.Current.Resources;
        var host = new System.Windows.Controls.Border
        {
            Background = (Brush)resources["InkBrush"],
            Padding = new Thickness(24),
            Child = view,
        };
        System.Windows.Documents.TextElement.SetForeground(host, (Brush)resources["VoltBrush"]);
        System.Windows.Documents.TextElement.SetFontFamily(host, (FontFamily)resources["Display"]);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        if (Environment.GetEnvironmentVariable("PROGUARD_RENDER_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(dir, name + ".png"));
            encoder.Save(file);
        }

        var pixels = new uint[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static int Distinct(uint[] pixels) => pixels.Distinct().Take(10).Count();

    /// <summary>Every piece of text the view shows (TextBlocks and their runs, and button captions).</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var found = new List<string>();
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case System.Windows.Controls.TextBlock t:
                    if (!string.IsNullOrEmpty(t.Text)) found.Add(t.Text);
                    break;
                case System.Windows.Controls.ContentControl c when c.Content is string s:
                    found.Add(s);
                    break;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return found;
    }
}
