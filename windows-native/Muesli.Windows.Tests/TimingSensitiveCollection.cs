namespace Muesli.Windows.Tests;

/// <summary>
/// Tests that time real work (query plans against a large history, spawning helper processes
/// under an attempt timeout). xUnit runs a collection with parallelization disabled on its own,
/// after the parallel collections, so the budgets measure the code rather than CPU contention
/// from the rest of the suite. Without this they failed whenever the machine was busy and passed
/// when run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
