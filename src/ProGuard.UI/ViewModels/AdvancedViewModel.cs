using CommunityToolkit.Mvvm.ComponentModel;
using ProGuard.UI.Services;
using ProGuard.UI.Resources;

namespace ProGuard.UI.ViewModels;

/// <summary>One sub-section of the ADVANCED page: a chip in its row and the page shown under it.</summary>
/// <param name="Page">The section's view model. Give it a DataTemplate in AdvancedView.xaml; if it is an <see cref="IPage"/>
/// it is told when it is on screen.</param>
public sealed record AdvancedSection(string Label, string Blurb, object Page);

/// <summary>
/// 07 ADVANCED: tools most people never need, one sub-section at a time.
/// <para>
/// To add a sub-section, add one <see cref="AdvancedSection"/> to the list in the constructor and one DataTemplate for its
/// view model in AdvancedView.xaml. Nothing in the shell changes.
/// </para>
/// </summary>
public sealed partial class AdvancedViewModel : ObservableObject, IPage
{
    public CpuTuningViewModel CpuTuning { get; }
    public LogsViewModel Logs { get; }

    public IReadOnlyList<AdvancedSection> Sections { get; }

    [ObservableProperty] private AdvancedSection currentSection;

    private bool _shown;

    public AdvancedViewModel(EngineHub hub, ShellViewModel? shell = null)
    {
        CpuTuning = new CpuTuningViewModel(hub, shell);
        Logs = new LogsViewModel(hub, shell);

        Sections = new[]
        {
            // Sub-sections:
            new AdvancedSection(Strings.Advanced_Logs, Strings.Advanced_LogsBlurb, Logs),
            new AdvancedSection(Strings.Advanced_CpuTuning, Strings.Advanced_CpuTuningBlurb, CpuTuning),
        };
        currentSection = Sections[0];
    }

    partial void OnCurrentSectionChanged(AdvancedSection? oldValue, AdvancedSection newValue)
    {
        if (!_shown) return;
        (oldValue?.Page as IPage)?.OnHidden();
        (newValue.Page as IPage)?.OnShown();
    }

    public void OnShown()
    {
        _shown = true;
        (CurrentSection.Page as IPage)?.OnShown();
    }

    public void OnHidden()
    {
        _shown = false;
        (CurrentSection.Page as IPage)?.OnHidden();
    }
}
