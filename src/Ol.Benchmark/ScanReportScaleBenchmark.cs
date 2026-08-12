using BenchmarkDotNet.Attributes;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Internals;
using System.Buffers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Measures the view and report stages at an inventory size a real scan reaches.
/// </summary>
/// <remarks>
/// The other report benchmarks render a single component, which measures fixed cost only: an allocation
/// paid once per component is invisible at that size, and a per-component allocation is the one that
/// decides what a scan of a real dependency graph costs. Reconciliation is measured here rather than in
/// the enrichment benchmarks because it runs once per evidence source per component, so its cost scales
/// with both and neither fixed-cost benchmark can show it.
/// </remarks>
[MemoryDiagnoser]
public class ScanReportScaleBenchmark
{
    private readonly ArrayBufferWriter<byte> buffer = new(1024 * 1024);
    private readonly PackageArtifactCollectionSummary packageArtifactSummary = new(1, 1, 1);
    private readonly DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary = new(1, 0, 1, 0, 1, 1, 0);
    private readonly PackageMetadataSummary packageMetadataSummary = new(0, 0, 0, 0, 0, 0, 0, 1, 0);
    private readonly SourceRepositorySummary sourceRepositorySummary = new(0, 0, 0, 0, 0, 0, "none", 1, 0);
    private readonly ScanReportScope scope = new(ExternalEvidenceCollected: true, DependencyFilter: null, ExcludedCount: 0, ExcludedUnknownCount: 0);
    private ScanComponent[] components = [];
    private ScanComponent[] enriched = [];
    private GroupRow[] groups = [];
    private DependencyInventory inventory;
    private LicenseCandidate registryCandidate;
    private LicenseCandidate sourceCandidate;
    private SpdxData spdx;
    private ScanComponent[] reconcileScratch = [];

    // Grouping moves the view into group order, so it gets its own copy. Sharing one array would leave the
    // report benchmarks rendering a view an earlier benchmark permuted, and the unresolved section costs
    // more on a view whose positions no longer line up with the inventory.
    private ScanComponent[] groupScratch = [];
    private Utf8JsonWriter writer = null!;

    [Params(1024)]
    public int ComponentCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        spdx = SpdxData.Load(null);
        writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        inventory = DependencyInputScanner.Scan(CreateCycloneDx(ComponentCount), spdx.Index);
        components = inventory.Components;
        reconcileScratch = new ScanComponent[components.Length];

        // The two candidates a collecting scan adds to every component, so reconciliation is measured
        // doing the work it does in a real run rather than on a component that was never enriched.
        registryCandidate = new LicenseCandidate(
            LicenseCandidateSource.NpmRegistry,
            LicenseCandidateKind.License,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.PackageMetadataNotFound,
            new LicenseEvidence(LicenseEvidenceKind.PackageRegistry));
        sourceCandidate = new LicenseCandidate(
            LicenseCandidateSource.SourceRepository,
            LicenseCandidateKind.Unavailable,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.SourceRepositoryUnavailable,
            new LicenseEvidence(LicenseEvidenceKind.SourceRepository));

        // The report stages render what enrichment produced, not what the parser produced. Rendering the
        // parsed components instead would skip the unresolved section entirely, because the reasons it
        // states come from the collection warnings only an enriched component carries.
        enriched = new ScanComponent[components.Length];
        for (var i = 0; i < components.Length; i++)
        {
            enriched[i] = LicenseReconciler.AddCandidate(LicenseReconciler.AddCandidate(components[i], registryCandidate), sourceCandidate);
        }

        // The report renders a sorted view, and the unresolved section resolves each row's introducer by
        // locating it in the inventory. Rendering an inventory-ordered view instead measures a positional
        // hit rate no scan produces, because every scan sorts before it renders.
        ScanView.Apply(enriched, dependency: null, "ecosystem,name,version", SortOrder.Asc);

        groupScratch = new ScanComponent[components.Length];
        var groupedComponents = (ScanComponent[])enriched.Clone();
        groups = ScanView.Group(groupedComponents, null, groupedComponents.Length, "license,ecosystem");
    }

    [GlobalCleanup]
    public void Cleanup() => writer.Dispose();

    /// <summary>Adds the registry and source candidates every collecting scan adds, once per component.</summary>
    [Benchmark]
    public int ReconcileEnrichmentCandidates()
    {
        components.CopyTo(reconcileScratch, 0);
        var warned = 0;
        for (var i = 0; i < reconcileScratch.Length; i++)
        {
            var component = LicenseReconciler.AddCandidate(reconcileScratch[i], registryCandidate);
            component = LicenseReconciler.AddCandidate(component, sourceCandidate);
            reconcileScratch[i] = component;
            warned += System.Numerics.BitOperations.PopCount((uint)component.Warnings);
        }

        return warned;
    }

    /// <summary>
    /// Parses one registry request per distinct purl, as enrichment planning does.
    /// </summary>
    /// <remarks>
    /// The enrichment benchmarks either plan a single component or repeat one purl, so both measure a
    /// deduplicated plan. A real inventory names a different package in nearly every component, which is
    /// the case where this parse runs once per component rather than once per scan.
    /// </remarks>
    [Benchmark]
    public int ParseMetadataRequests()
    {
        var parsed = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (OlDefaults.TryCreatePackageMetadataRequest(components[i].Purl, out _, out _))
            {
                parsed++;
            }
        }

        return parsed;
    }

    [Benchmark]
    public int GroupByLicenseAndEcosystem()
    {
        enriched.CopyTo(groupScratch, 0);
        return ScanView.Group(groupScratch, null, groupScratch.Length, "license,ecosystem").Length;
    }

    [Benchmark]
    public int WriteText()
    {
        buffer.Clear();
        ReportRenderer.WriteText(buffer, inventory, enriched, verbose: false);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteVerboseText()
    {
        buffer.Clear();
        ReportRenderer.WriteText(buffer, inventory, enriched, verbose: true);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int RenderMarkdown()
    {
        buffer.Clear();
        ReportRenderer.WriteMarkdown(buffer, inventory, enriched, verbose: true);
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteJson()
    {
        buffer.Clear();
        writer.Reset(buffer);
        ReportRenderer.WriteJson(writer, inventory, enriched, spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
        writer.Flush();
        return buffer.WrittenCount;
    }

    [Benchmark]
    public int WriteGroupedJson()
    {
        buffer.Clear();
        writer.Reset(buffer);
        ReportRenderer.WriteJson(writer, inventory, groups, "license,ecosystem", spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
        writer.Flush();
        return buffer.WrittenCount;
    }

    /// <summary>
    /// Builds an inventory whose components differ the way a real one does.
    /// </summary>
    /// <remarks>
    /// Three ecosystems and a repeating set of licenses, so grouping produces many groups rather than one,
    /// and a share of components resolve to no license so the unresolved section is rendered too. A single
    /// repeated component would let a per-component cost hide behind one warmed lookup.
    /// </remarks>
    private static byte[] CreateCycloneDx(int componentCount)
    {
        string[] ecosystems = ["npm", "nuget", "cargo"];
        string[] licenses = ["MIT", "Apache-2.0", "BSD-3-Clause", "ISC", "NOASSERTION"];
        var builder = new StringBuilder();
        builder.Append("""{"bomFormat":"CycloneDX","specVersion":"1.6","components":[""");
        for (var i = 0; i < componentCount; i++)
        {
            if (i != 0)
            {
                builder.Append(',');
            }

            var ecosystem = ecosystems[i % ecosystems.Length];
            var version = $"1.0.{i % 10}";
            var name = $"package-{i:D5}";
            var purl = $"pkg:{ecosystem}/{name}@{version}";
            builder.Append("{\"bom-ref\":\"").Append(purl)
                .Append("\",\"name\":\"").Append(name)
                .Append("\",\"version\":\"").Append(version)
                .Append("\",\"purl\":\"").Append(purl)
                .Append("\",\"licenses\":[{\"license\":{\"id\":\"").Append(licenses[i % licenses.Length])
                .Append("\"}}]}");
        }

        builder.Append("]}");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
