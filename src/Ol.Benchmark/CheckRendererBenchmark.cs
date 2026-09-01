using System.Buffers;
using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;

/// <summary>Measures the CI-facing projection of a large unresolved policy result.</summary>
[MemoryDiagnoser]
public class CheckRendererBenchmark
{
    private const int ComponentCount = 1_024;
    private readonly ArrayBufferWriter<byte> buffer = new(128 * 1024);
    private readonly ScanComponent[] components = new ScanComponent[ComponentCount];
    private readonly LicensePolicyViolation[] violations = new LicensePolicyViolation[ComponentCount];
    private readonly DependencyInventory inventory;

    private readonly ScanComponent[] linkedComponents = new ScanComponent[ComponentCount];
    private readonly LicensePolicyViolation[] linkedViolations = new LicensePolicyViolation[ComponentCount];
    private readonly DependencyInventory linkedInventory;
    private readonly ScanReport linkedReport;

    public CheckRendererBenchmark()
    {
        var candidate = new LicenseCandidate(
            LicenseCandidateSource.PackageRegistry,
            LicenseCandidateKind.Fetch,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.PackageMetadataNotFound);

        for (var i = 0; i < ComponentCount; i++)
        {
            components[i] = new ScanComponent(
                "example",
                "1.0.0",
                default,
                "npm",
                DependencyType.Transitive,
                LicenseStatus.Unknown,
                "pkg:npm/example@1.0.0",
                default,
                candidate,
                []);
            violations[i] = new LicensePolicyViolation(i, LicensePolicyViolationKind.Unknown);
        }

        inventory = new DependencyInventory(default, [], components, [], []);

        // The same table over an inventory that actually proves a path, so the Path column reaches
        // DependencyPathText.Introducer instead of short-circuiting on an empty graph. Every row here
        // builds a string, which is the cost the column-width pass must not pay a second time.
        var occurrences = new DependencyOccurrence[ComponentCount];
        var edges = new DependencyEdge[ComponentCount];
        for (var i = 0; i < ComponentCount; i++)
        {
            linkedComponents[i] = new ScanComponent(
                $"example-{i}",
                "1.0.0",
                default,
                "npm",
                i == 0 ? DependencyType.Direct : DependencyType.Transitive,
                LicenseStatus.Unknown,
                $"pkg:npm/example-{i}@1.0.0",
                default,
                candidate,
                []);
            linkedViolations[i] = new LicensePolicyViolation(i, LicensePolicyViolationKind.Unknown);
            occurrences[i] = new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, i);
            edges[i] = i == 0
                ? new DependencyEdge(0, DependencyOccurrence.ContextRoot, 0)
                : new DependencyEdge(0, 0, i);
        }

        linkedInventory = new DependencyInventory(default, [], linkedComponents, occurrences, edges);
        linkedReport = new ScanReport(1, "benchmark", "3.0.0", linkedInventory, linkedComponents, [], []);
    }

    [Benchmark]
    public int WriteUnresolvedViolations()
    {
        buffer.Clear();
        CheckRenderer.Write(buffer, inventory, components, violations, ComponentCount);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteViolationsWithPaths()
    {
        buffer.Clear();
        CheckRenderer.Write(buffer, linkedInventory, linkedComponents, linkedViolations, ComponentCount);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteMarkdownReport()
    {
        buffer.Clear();
        CheckRenderer.WriteMarkdown(buffer, linkedReport, linkedViolations, ComponentCount, allowLicenses: "MIT");
        return buffer.WrittenCount;
    }
}
