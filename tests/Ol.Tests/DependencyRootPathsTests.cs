using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;

namespace Ol.Tests;

/// <summary>
/// Pins the ownership contract of the one type in the repository that holds pooled buffers.
/// </summary>
/// <remarks>
/// The buffers go back to the pool at the end of the scope that built them, so an access afterwards would
/// read storage the pool has already handed to someone else. It has to fail loudly instead. The type is a
/// <see langword="ref"/> <see langword="struct"/>, so the checks run in synchronous helpers: an instance
/// of one cannot live in an async method.
/// </remarks>
public sealed class DependencyRootPathsTests
{
    [Test]
    public async Task RootPaths_AfterDisposal_RejectsPathAccessInsteadOfReadingReturnedStorage()
        => await Assert.That(DisposedPathAccessThrows()).IsTrue();

    [Test]
    public async Task RootPaths_AfterDisposal_RejectsComponentLookupInsteadOfReadingReturnedStorage()
        => await Assert.That(DisposedLookupThrows()).IsTrue();

    [Test]
    public async Task RootPaths_BeforeDisposal_ResolvesTheIntroducingPath()
        => await Assert.That(ResolvedPathLength()).IsEqualTo(2);

    /// <summary>A component the inventory does not hold resolves to no position rather than to a wrong one.</summary>
    [Test]
    public async Task RootPaths_UnknownComponent_ResolvesToNoPosition()
        => await Assert.That(UnknownComponentIndex()).IsEqualTo(-1);

    private static bool DisposedPathAccessThrows()
    {
        var inventory = CreateInventory();
        var paths = DependencyPathResolver.BuildRootPaths(inventory);
        paths.Dispose();
        paths.Dispose();

        try
        {
            paths.GetPath(0);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool DisposedLookupThrows()
    {
        var inventory = CreateInventory();
        var paths = DependencyPathResolver.BuildRootPaths(inventory);
        paths.Dispose();

        try
        {
            paths.FindComponentIndex(inventory.Components[1]);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static int ResolvedPathLength()
    {
        var inventory = CreateInventory();
        using var paths = DependencyPathResolver.BuildRootPaths(inventory);

        // Located by identity with no positional hint, which is what a sorted view leaves the report with.
        var index = paths.FindComponentIndex(inventory.Components[2]);
        return paths.GetPath(index).Length;
    }

    private static int UnknownComponentIndex()
    {
        var inventory = CreateInventory();
        using var paths = DependencyPathResolver.BuildRootPaths(inventory);
        return paths.FindComponentIndex(CreateComponent("Absent", "9.9.9", DependencyType.Transitive));
    }

    /// <summary>Root -> Direct -> Transitive, so the transitive component has a two-hop path.</summary>
    private static DependencyInventory CreateInventory()
    {
        var components = new[]
        {
            CreateComponent("App", "1.0.0", DependencyType.Root),
            CreateComponent("Direct", "1.0.0", DependencyType.Direct),
            CreateComponent("Transitive", "2.0.0", DependencyType.Transitive),
        };
        var occurrences = new[]
        {
            new DependencyOccurrence(0, 0),
            new DependencyOccurrence(0, 1),
            new DependencyOccurrence(0, 2),
        };
        var edges = new[]
        {
            new DependencyEdge(0, DependencyOccurrence.ContextRoot, 1),
            new DependencyEdge(0, 1, 2),
        };
        var contexts = new[] { new DependencyResolutionContext("app", "net10.0", "", "", "", "runtime", "project.assets.json") };
        return new DependencyInventory(default, contexts, components, occurrences, edges);
    }

    private static ScanComponent CreateComponent(string name, string version, DependencyType dependencyType)
        => new(name, version, default, "nuget", dependencyType, LicenseStatus.Unknown, $"pkg:nuget/{name}@{version}", $"{name}/{version}", default, []);
}
