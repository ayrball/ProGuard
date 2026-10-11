using System.Linq;
using System.Reflection;
using ProGuard.Core.Models;
using ProGuard.Engine.IPC;
using ProGuard.Shared.Contracts;

namespace ProGuard.Tests;

/// <summary>
/// The UI has no reference to ProGuard.Core, so the engine copies between the stored models and their wire mirrors by
/// property name. These tests fail when someone adds a setting to a model and forgets its mirror (the UI would then
/// silently never see or save it), and when an enum drifts from its mirror.
/// </summary>
public class RuleDtoMapperTests
{
    public static TheoryData<System.Type, System.Type> ModelsAndMirrors() => new()
    {
        { typeof(ProcessRule), typeof(ProcessRuleDto) },
        { typeof(WatchdogRule), typeof(WatchdogRuleDto) },
        { typeof(AppSettings), typeof(EngineSettingsDto) },
        { typeof(MemoryPurgeReport), typeof(MemoryPurgeResultDto) },
    };

    [Theory]
    [MemberData(nameof(ModelsAndMirrors))]
    public void EveryModelProperty_HasADtoCounterpart(System.Type model, System.Type dto)
    {
        // The database key is the one thing a client must not choose; it travels as Id only where a rule can be named.
        var missing = DtoMapper.Unmapped(model, dto).Where(n => !(n == "Id" && dto == typeof(EngineSettingsDto)));
        // A report's computed totals have no DTO field of their own.
        if (model == typeof(MemoryPurgeReport)) missing = missing.Where(n => n is not (nameof(MemoryPurgeReport.TotalBytesReclaimed) or nameof(MemoryPurgeReport.AnyAction)));

        Assert.True(!missing.Any(), $"{dto.Name} lacks: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(ModelsAndMirrors))]
    public void EveryDtoProperty_HasAModelCounterpart(System.Type model, System.Type dto)
    {
        var missing = DtoMapper.Unmapped(dto, model).Where(n => n != nameof(MemoryPurgeResultDto.Summary));
        Assert.True(!missing.Any(), $"{model.Name} lacks: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData(typeof(InstanceBalancerMode), typeof(BalancerMode))]
    [InlineData(typeof(WatchdogCondition), typeof(WatchdogTrigger))]
    [InlineData(typeof(WatchdogAction), typeof(WatchdogResponse))]
    [InlineData(typeof(WatchdogMemoryMetric), typeof(MemoryMetric))]
    public void EnumsMirrorTheirCoreCounterpartValueForValue(System.Type core, System.Type mirror)
    {
        static (string Name, long Value)[] Members(System.Type e) =>
            System.Enum.GetNames(e).Select(n => (n, System.Convert.ToInt64(System.Enum.Parse(e, n)))).OrderBy(m => m.Item2).ToArray();

        // Rules are stored by the numeric value, so values must match; names are checked too so a swap can't hide.
        Assert.Equal(Members(core).Select(m => m.Value), Members(mirror).Select(m => m.Value));
        Assert.Equal(Members(core).Select(m => m.Name), Members(mirror).Select(m => m.Name));
    }

    /// <summary>A value for <paramref name="type"/> that differs from the type's default and from <paramref name="seed"/>'s neighbours.</summary>
    private static object NonDefault(System.Type type, int seed)
    {
        type = System.Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 10 + seed;
        if (type == typeof(long)) return 1000L + seed;
        if (type == typeof(double)) return 2.5 + seed;
        if (type == typeof(string)) return $"value{seed}";
        if (type.IsEnum) return System.Enum.GetValues(type).Cast<object>().Last();
        if (type == typeof(System.Collections.Generic.List<string>)) return new System.Collections.Generic.List<string> { "an error" };
        throw new System.NotSupportedException($"Teach NonDefault about {type}");
    }

    private static T Filled<T>(params string[] except) where T : new()
    {
        var value = new T();
        var seed = 0;
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && !except.Contains(p.Name)))
            p.SetValue(value, NonDefault(p.PropertyType, seed++));
        return value;
    }

    private static void AssertSameValues<T>(T expected, T actual, params string[] except)
    {
        foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && !except.Contains(p.Name)))
        {
            var want = p.GetValue(expected);
            var got = p.GetValue(actual);
            if (want is System.Collections.IEnumerable l && want is not string)
                Assert.Equal(l.Cast<object>(), ((System.Collections.IEnumerable)got!).Cast<object>());
            else
                Assert.True(Equals(want, got), $"{typeof(T).Name}.{p.Name}: expected {want}, got {got}");
        }
    }

    [Fact]
    public void RoundTrip_KeepsEveryFieldTheDtoCarries()
    {
        var rule = Filled<ProcessRule>();
        rule.Id = 42;

        var copy = new ProcessRule();
        RuleDtoMapper.CopyTo(RuleDtoMapper.ToDto(rule), copy);

        AssertSameValues(rule, copy);
    }

    [Fact]
    public void RoundTrip_KeepsEverythingForWatchdogRulesAndSettings()
    {
        var watchdog = Filled<WatchdogRule>();
        watchdog.Id = 7;
        AssertSameValues(watchdog, DtoMapper.Map<WatchdogRuleDto, WatchdogRule>(DtoMapper.Map<WatchdogRule, WatchdogRuleDto>(watchdog)));

        var settings = Filled<AppSettings>(nameof(AppSettings.Id));
        var back = DtoMapper.Map<EngineSettingsDto, AppSettings>(DtoMapper.Map<AppSettings, EngineSettingsDto>(settings));
        AssertSameValues(settings, back, nameof(AppSettings.Id));
    }

    [Fact]
    public void SettingsDto_NeverChangesTheStoredId()
    {
        var stored = new AppSettings { Id = 5 };
        DtoMapper.CopyTo(Filled<EngineSettingsDto>(), stored);
        Assert.Equal(5, stored.Id);
    }

    [Fact]
    public void ToDto_CopiesNullableValuesAsNull()
    {
        var dto = RuleDtoMapper.ToDto(new ProcessRule { ProcessName = "x.exe" });
        Assert.Null(dto.PriorityClass);
        Assert.Null(dto.InstanceBalancerMode);
        Assert.Null(dto.EfficiencyMode);
    }

    [Fact]
    public void UnknownBalancerMode_IsStoredAsOff()
    {
        var copy = new ProcessRule { InstanceBalancerMode = InstanceBalancerMode.FixedCoresPerInstance };
        RuleDtoMapper.CopyTo(new ProcessRuleDto { ProcessName = "x.exe", InstanceBalancerMode = (BalancerMode)99 }, copy);
        Assert.Null(copy.InstanceBalancerMode);
    }

    [Fact]
    public void UndefinedRequiredEnum_IsRejectedRatherThanStored()
    {
        var dto = new WatchdogRuleDto { TargetProcessName = "x.exe", ConditionType = (WatchdogTrigger)99 };
        Assert.Throws<System.ArgumentException>(() => DtoMapper.Map<WatchdogRuleDto, WatchdogRule>(dto));

        dto = new WatchdogRuleDto { TargetProcessName = "x.exe", ActionType = (WatchdogResponse)99 };
        Assert.Throws<System.ArgumentException>(() => DtoMapper.Map<WatchdogRuleDto, WatchdogRule>(dto));
    }
}
