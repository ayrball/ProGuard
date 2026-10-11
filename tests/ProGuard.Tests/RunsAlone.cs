namespace ProGuard.Tests;

/// <summary>
/// For tests whose assertions measure CPU time or wall-clock speed, or that load the whole machine. xUnit runs this
/// collection on its own after every parallel one has finished, so those tests neither disturb the others nor get
/// disturbed by them (on a 2-CPU CI runner the rest of the suite is enough to do both).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RunsAlone
{
    public const string Name = "Runs alone";
}
