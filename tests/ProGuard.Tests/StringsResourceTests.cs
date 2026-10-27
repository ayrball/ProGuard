using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ProGuard.UI.Resources;
using ProGuard.UI.ViewModels;

namespace ProGuard.Tests;

/// <summary>
/// The UI's text: Strings.resx (English) and one Strings.&lt;culture&gt;.resx per translation, with Strings.cs generated from
/// the English file by tools/i18n/Update-Strings.ps1. These keep the three in step, so a translation can't drop a key,
/// break a placeholder or crash string.Format at run time.
/// </summary>
public class StringsResourceTests
{
    private static readonly string ResourcesDir = Path.Combine(RepoRoot(), "src", "ProGuard.UI", "Resources");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProGuard.sln"))) return dir.FullName;
        }
        throw new InvalidOperationException("ProGuard.sln not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(ResourcesDir, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    private static readonly Dictionary<string, string> English = Load("Strings.resx");

    /// <summary>Each translation file beside Strings.resx, by culture name.</summary>
    public static TheoryData<string> Translations()
    {
        var data = new TheoryData<string>();
        var files = Directory.GetFiles(ResourcesDir, "Strings.*.resx");
        foreach (var file in files)
            data.Add(Path.GetFileName(file)["Strings.".Length..^".resx".Length]);
        if (files.Length == 0) data.Add(string.Empty); // nothing to check yet; the theory still needs a row
        return data;
    }

    private static SortedSet<int> Placeholders(string text) =>
        new(Placeholder.Matches(text.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

    [Fact]
    public void TheGeneratedClass_HasExactlyTheKeysOfStringsResx()
    {
        var generated = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToHashSet();

        // Out of step means someone changed Strings.resx without running tools/i18n/Update-Strings.ps1.
        Assert.Empty(English.Keys.Except(generated));
        Assert.Empty(generated.Except(English.Keys));
    }

    [Fact]
    public void EveryEnglishString_IsThere_AndFormatsWithItsPlaceholders()
    {
        Assert.NotEmpty(English);
        foreach (var (key, text) in English)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            var args = Enumerable.Range(0, Placeholders(text).DefaultIfEmpty(-1).Max() + 1).Select(_ => (object)1).ToArray();
            var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, args));
            Assert.True(ex == null, $"{key}: {ex?.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslation_HasEveryKey_TheSamePlaceholders_AndNoEmptyText(string culture)
    {
        if (culture.Length == 0) return;
        var translated = Load($"Strings.{culture}.resx");

        Assert.Empty(English.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(English.Keys));
        foreach (var (key, english) in English)
        {
            var text = translated[key];
            Assert.False(string.IsNullOrWhiteSpace(text), $"{culture} {key} is empty");
            Assert.True(Placeholders(english).SetEquals(Placeholders(text)), $"{culture} {key}: placeholders {string.Join(",", Placeholders(text))}, English has {string.Join(",", Placeholders(english))}");
            var args = Enumerable.Range(0, Placeholders(text).DefaultIfEmpty(-1).Max() + 1).Select(_ => (object)1).ToArray();
            var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, args));
            Assert.True(ex == null, $"{culture} {key}: {ex?.Message}");
        }
    }

    [Fact]
    public void EveryOfferedLanguage_HasATranslationFile_AndShipsItsSatellite()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ProGuard.UI", "ProGuard.UI.csproj"));
        var shipped = Regex.Match(project, "<SatelliteResourceLanguages>([^<]*)</SatelliteResourceLanguages>").Groups[1].Value.Split(';');

        foreach (var (culture, _) in ShellViewModel.SupportedLanguages)
        {
            Assert.Contains(culture, shipped);
            if (culture != "en") Assert.True(File.Exists(Path.Combine(ResourcesDir, $"Strings.{culture}.resx")), $"no Strings.{culture}.resx");
        }
    }

    [Fact]
    public void TheLanguagePicker_OffersWindowsDefaultThenEachLanguage_AndRemembersTheChoice()
    {
        var preferences = new ProGuard.UI.Services.MemoryUiPreferences { Language = "en" };
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var toaster = new ProGuard.UI.Services.Toaster(dispatcher);
        var hub = new ProGuard.UI.Services.EngineHub(new ProGuard.Shared.Client.EngineClient("ProGuard.Tests.NoEngine." + Guid.NewGuid().ToString("N")), dispatcher, toaster);
        var shell = new ShellViewModel(hub, toaster, new ProGuard.UI.Services.IconCache(dispatcher), preferences: preferences);

        Assert.Equal(string.Empty, shell.Languages[0].Value); // Windows' own
        Assert.Equal(ShellViewModel.SupportedLanguages.Count + 1, shell.Languages.Count);
        Assert.Single(shell.Languages, l => l.IsOn);
        Assert.True(shell.Languages.Single(l => l.Value == "en").IsOn);

        shell.PickLanguageCommand.Execute(shell.Languages[0]);
        Assert.Equal(string.Empty, preferences.Language);
        Assert.True(shell.Languages[0].IsOn);
        Assert.Single(shell.Languages, l => l.IsOn);
    }
}
