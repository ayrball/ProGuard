using ProGuard.Preview;
using ProGuard.Shared.Client;
using ProGuard.Shared.Contracts;
using ProGuard.Shared.Messages;
using ProGuard.Shared.Profiles;

namespace ProGuard.Tests;

/// <summary>The preview tool's stand-in answers the profile commands the way the engine does.</summary>
public class StandInProfileTests
{
    private static async Task<(StandInEngine Engine, EngineClient Client)> StartAsync(bool administrator = true)
    {
        var engine = new StandInEngine("ProGuard.Tests.StandInProfiles." + Guid.NewGuid().ToString("N"), administrator, readOnly: false).Start();
        await Task.Yield();
        return (engine, new EngineClient(engine.PipeName));
    }

    [Fact]
    public async Task Switch_SavesBeforeSwitch_AndFlagsAnotherMachinesValues()
    {
        var (engine, client) = await StartAsync();
        await using var _ = engine;
        await using var __ = client;

        var profiles = await client.QueryAsync<List<ConfigProfileDto>>(CommandTypes.ListProfiles);
        var desktop = profiles.Single(p => p.Name == "Old desktop");

        var preview = await client.QueryAsync<ConfigPreviewDto>(CommandTypes.SwitchProfile, argument: new SwitchProfileArgument { Id = desktop.Id, Preview = true });
        Assert.False(preview.Applied);
        Assert.Contains(preview.MachineSpecific, f => f.Setting == "Cores (affinity)");
        Assert.Contains(preview.MachineSpecific, f => f.Setting == "CPU Sets");
        Assert.Contains(preview.MachineSpecific, f => f.Setting == "Performance Mode plan");
        Assert.Contains(preview.Changes, c => c.Kind == ConfigChangeKind.Added && c.Name == "render.exe");

        var applied = await client.QueryAsync<ConfigPreviewDto>(CommandTypes.SwitchProfile, argument: new SwitchProfileArgument { Id = desktop.Id });
        Assert.True(applied.Applied);
        var rules = await client.QueryAsync<List<ProcessRuleDto>>(CommandTypes.GetProcessRules);
        Assert.Null(rules.Single(r => r.ProcessName == "render.exe").AffinityMask);
        Assert.Contains(await client.QueryAsync<List<ConfigProfileDto>>(CommandTypes.ListProfiles), p => p.Name == "Before switch" && p.Automatic);
    }

    [Fact]
    public async Task ExportThenImport_RoundTrips_AndANewerFileIsRefused()
    {
        var (engine, client) = await StartAsync();
        await using var _ = engine;
        await using var __ = client;

        var exported = await client.QueryAsync<ConfigDocument>(CommandTypes.ExportConfig);
        var file = ConfigDocuments.Serialize(exported);
        var preview = await client.QueryAsync<ConfigPreviewDto>(CommandTypes.ImportConfig, argument: new ImportConfigArgument { Document = file, Preview = true });
        Assert.Empty(preview.Changes);
        Assert.Empty(preview.MachineSpecific);

        var newer = file.Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 9");
        var refused = await Assert.ThrowsAsync<EngineCommandException>(() =>
            client.QueryAsync<ConfigPreviewDto>(CommandTypes.ImportConfig, argument: new ImportConfigArgument { Document = newer, Preview = true }));
        Assert.Contains("newer version", refused.Message);
    }

    [Fact]
    public async Task StandardUser_CantImportARealtimeRule()
    {
        var (engine, client) = await StartAsync(administrator: false);
        await using var _ = engine;
        await using var __ = client;

        var doc = await client.QueryAsync<ConfigDocument>(CommandTypes.ExportConfig);
        doc.ProcessRules.Add(new ProcessRuleDto { ProcessName = "turbo.exe", PriorityClass = 256 });
        var argument = new ImportConfigArgument { Document = ConfigDocuments.Serialize(doc) };

        var refused = await Assert.ThrowsAsync<EngineCommandException>(() => client.QueryAsync<ConfigPreviewDto>(CommandTypes.ImportConfig, argument: argument));
        Assert.True(refused.IsAccessDenied);
    }
}
