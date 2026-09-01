using Ol.Core;
using Ol.Core.Licensing;

namespace Ol.Tests;

public sealed class DependencyUsageResolverTests
{
    [Test]
    public async Task Resolve_WithInterleavedDevelopmentOccurrences_ClassifiesEveryComponent()
    {
        // Occurrence order: 0 dev, 1 runtime, 2 dev, 3 runtime, 4 dev, 5 runtime.
        // Development indices are non-contiguous, and component 4 is reached by both occurrence 4 (dev) and
        // occurrence 5 (runtime), so it must stay runtime.
        var inventory = CreateInventory(
            componentCount: 5,
            occurrenceComponents: [0, 1, 2, 3, 4, 4],
            developmentOccurrences: [0, 2, 4]);

        var usages = new DependencyUsage[5];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[1]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[2]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[3]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[4]).IsEqualTo(DependencyUsage.Runtime);
    }

    /// <summary>
    /// An occurrence whose input determines no usage abstains instead of vetoing. It carries no observation
    /// about reachability — its input kind has no vocabulary for one — so treating it as a competing claim
    /// let an input that cannot speak overrule one that did.
    /// </summary>
    [Test]
    public async Task Resolve_WithUndeterminedOccurrenceBesideDevelopment_KeepsDevelopment()
    {
        // Occurrence 0 is a lockfile entry classified development; occurrence 1 is the same component seen
        // by an input that determines nothing, such as an SBOM folded onto the row.
        var inventory = CreateInventory(
            componentCount: 1,
            occurrenceComponents: [0, 0],
            developmentOccurrences: [0],
            ranges: [new DependencyUsageRange(0, 1)]);

        var usages = new DependencyUsage[1];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Development);
    }

    /// <summary>A determination that the component is reachable at runtime still wins; only silence abstains.</summary>
    [Test]
    public async Task Resolve_WithUndeterminedOccurrenceBesideRuntime_KeepsRuntime()
    {
        var inventory = CreateInventory(
            componentCount: 1,
            occurrenceComponents: [0, 0],
            developmentOccurrences: [],
            ranges: [new DependencyUsageRange(0, 1)]);

        var usages = new DependencyUsage[1];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Runtime);
    }

    /// <summary>
    /// Two determining inputs that resolved to one component still contradict each other, and runtime wins.
    /// Abstention is about inputs that said nothing, never about inputs that disagreed.
    /// </summary>
    [Test]
    public async Task Resolve_WithDevelopmentAndRuntimeFromDeterminingInputs_KeepsRuntime()
    {
        var inventory = CreateInventory(
            componentCount: 1,
            occurrenceComponents: [0, 0],
            developmentOccurrences: [0],
            ranges: [new DependencyUsageRange(0, 2)]);

        var usages = new DependencyUsage[1];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Runtime);
    }

    /// <summary>A component only undetermined inputs saw has nothing to preserve and stays unknown.</summary>
    [Test]
    public async Task Resolve_WithOnlyUndeterminedOccurrences_LeavesComponentUnknown()
    {
        var inventory = CreateInventory(
            componentCount: 1,
            occurrenceComponents: [0, 0],
            developmentOccurrences: [],
            ranges: [new DependencyUsageRange(5, 1)]);

        var usages = new DependencyUsage[1];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Unknown);
    }

    [Test]
    public async Task Resolve_WithOccurrenceOutsideEveryDeterminedRange_LeavesComponentUnknown()
    {
        // Only occurrences 0..1 are determined; occurrence 2 belongs to an input that reported no usage.
        var inventory = CreateInventory(
            componentCount: 3,
            occurrenceComponents: [0, 1, 2],
            developmentOccurrences: [0],
            ranges: [new DependencyUsageRange(0, 2)]);

        var usages = new DependencyUsage[3];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[1]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[2]).IsEqualTo(DependencyUsage.Unknown);
    }

    [Test]
    public async Task Resolve_WithMultipleDeterminedRanges_AppliesEachRangeIndependently()
    {
        // Two combined inputs: occurrences 0..1 and 3..4 determined, occurrence 2 from an unsupported input.
        var inventory = CreateInventory(
            componentCount: 5,
            occurrenceComponents: [0, 1, 2, 3, 4],
            developmentOccurrences: [1, 3],
            ranges: [new DependencyUsageRange(0, 2), new DependencyUsageRange(3, 2)]);

        var usages = new DependencyUsage[5];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Runtime);
        await Assert.That(usages[1]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[2]).IsEqualTo(DependencyUsage.Unknown);
        await Assert.That(usages[3]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[4]).IsEqualTo(DependencyUsage.Runtime);
    }

    [Test]
    public async Task Resolve_WithoutUsageInformation_LeavesEveryComponentUnknown()
    {
        var inventory = CreateInventory(componentCount: 2, occurrenceComponents: [0, 1], developmentOccurrences: null, ranges: null);

        var usages = new DependencyUsage[2];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Unknown);
        await Assert.That(usages[1]).IsEqualTo(DependencyUsage.Unknown);
    }

    [Test]
    public async Task Resolve_WithComponentThatHasNoOccurrence_LeavesItUnknown()
    {
        var inventory = CreateInventory(componentCount: 2, occurrenceComponents: [0], developmentOccurrences: [0]);

        var usages = new DependencyUsage[2];
        DependencyUsageResolver.Resolve(inventory, usages);

        await Assert.That(usages[0]).IsEqualTo(DependencyUsage.Development);
        await Assert.That(usages[1]).IsEqualTo(DependencyUsage.Unknown);
    }

    private static DependencyInventory CreateInventory(
        int componentCount,
        int[] occurrenceComponents,
        int[]? developmentOccurrences,
        DependencyUsageRange[]? ranges = null)
    {
        var components = new ScanComponent[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            components[i] = new ScanComponent($"pkg{i}", "1.0.0", "MIT", "npm", DependencyType.Direct, LicenseStatus.Matched, $"pkg:npm/pkg{i}@1.0.0", $"pkg{i}", default, []);
        }

        var occurrences = new DependencyOccurrence[occurrenceComponents.Length];
        for (var i = 0; i < occurrenceComponents.Length; i++)
        {
            occurrences[i] = new DependencyOccurrence(0, occurrenceComponents[i]);
        }

        return new DependencyInventory(
            default,
            [new DependencyResolutionContext("app", default, default, default, default, default, "project.assets.json")],
            components,
            occurrences,
            [],
            [],
            ranges ?? (developmentOccurrences is null ? null : [new DependencyUsageRange(0, occurrences.Length)]),
            developmentOccurrences);
    }
}
