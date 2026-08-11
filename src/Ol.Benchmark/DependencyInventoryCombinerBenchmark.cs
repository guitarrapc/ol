using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;
using Ol.Internals;

/// <summary>
/// Measures combining several resolved inventories into one collection, including folding an SBOM into
/// package-manager rows by package URL identity. The fold must stay linear in component count: a linear scan for each
/// SBOM component would be quadratic and would only show up at a realistic inventory size.
/// </summary>
public class DependencyInventoryCombinerBenchmark
{
    /// <summary>
    /// Two sizes, so the cost of folding is read as a slope rather than a single number: a linear fold roughly
    /// quadruples between them, a quadratic one grows about sixteenfold.
    /// </summary>
    [Params(1024, 4096)]
    public int ComponentCount { get; set; }

    private readonly SpdxLicenseIndex spdx = new(["MIT", "Apache-2.0"], []);
    private DependencyInventory[] packageManagerOnly = [];
    private DependencyInventory[] mixed = [];
    private DependencyInputHandler[] packageManagerOnlyHandlers = [];
    private DependencyInputHandler[] mixedHandlers = [];

    [GlobalSetup]
    public void Setup()
    {
        var registry = DependencyInputRegistry.Default;
        registry.TryGetInputFormat(ScanInputFormat.NpmPackageLock.Name, out var npmHandler);
        registry.TryGetInputFormat(ScanInputFormat.CycloneDx.Name, out var sbomHandler);

        var first = CreatePackageManagerInventory(0, ComponentCount / 2);
        var second = CreatePackageManagerInventory(ComponentCount / 2, ComponentCount / 2);
        var sbom = CreateSbomInventory(ComponentCount);

        packageManagerOnly = [first, second];
        packageManagerOnlyHandlers = [npmHandler, npmHandler];
        mixed = [first, second, sbom];
        mixedHandlers = [npmHandler, npmHandler, sbomHandler];
    }

    [Benchmark(Baseline = true)]
    public int CombinePackageManagerInputs()
        => DependencyInventoryCombiner.Combine(packageManagerOnly, packageManagerOnlyHandlers, Descriptor()).Components.Length;

    [Benchmark]
    public int CombineSbomWithPackageManagerInputs()
        => DependencyInventoryCombiner.Combine(mixed, mixedHandlers, Descriptor()).Components.Length;

    private static ScanInputDescriptor Descriptor()
        => new(ScanInputKind.Collection, ScanInputFormat.Collection, "benchmark", string.Empty, default);

    private DependencyInventory CreatePackageManagerInventory(int startIndex, int count)
    {
        var components = new ScanComponent[count];
        var occurrences = new DependencyOccurrence[count];
        for (var i = 0; i < count; i++)
        {
            var purl = Utf8Slice.FromOwnedBytes(System.Text.Encoding.UTF8.GetBytes($"pkg:npm/package-{startIndex + i}@1.0.0"));
            var sourceId = Utf8Slice.FromOwnedBytes(System.Text.Encoding.UTF8.GetBytes($"node_modules/package-{startIndex + i}"));
            components[i] = new ScanComponent(
                purl,
                default,
                default,
                "npm",
                DependencyType.Transitive,
                LicenseStatus.Matched,
                purl,
                sourceId,
                LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.Id, "MIT"u8, spdx),
                [],
                LicenseCandidateWarnings.None,
                default,
                ComponentSupply.PackageManager);
            occurrences[i] = new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, i);
        }

        return new DependencyInventory(default, [], components, occurrences, []);
    }

    private DependencyInventory CreateSbomInventory(int count)
    {
        var components = new ScanComponent[count];
        var occurrences = new DependencyOccurrence[count];
        for (var i = 0; i < count; i++)
        {
            var purl = Utf8Slice.FromOwnedBytes(System.Text.Encoding.UTF8.GetBytes($"pkg:npm/package-{i}@1.0.0"));
            components[i] = new ScanComponent(
                purl,
                default,
                default,
                "npm",
                DependencyType.Direct,
                LicenseStatus.Matched,
                purl,
                Utf8Slice.FromOwnedBytes(System.Text.Encoding.UTF8.GetBytes($"ref-{i}")),
                LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, LicenseCandidateKind.Id, "MIT"u8, spdx),
                [],
                LicenseCandidateWarnings.None,
                default,
                ComponentSupply.Sbom);
            occurrences[i] = new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, i);
        }

        return new DependencyInventory(default, [], components, occurrences, []);
    }
}
