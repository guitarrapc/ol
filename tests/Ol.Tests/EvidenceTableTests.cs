using Ol.Core.GitHub;
using Ol.Core.PackageManagers;
using Ol.Internals;

namespace Ol.Tests;

/// <summary>
/// Covers the scan summary's evidence collection breakdown.
/// </summary>
public sealed class EvidenceTableTests
{
    [Test]
    public async Task WriteEvidenceSummary_WithNothingToCollect_IsCompact()
    {
        var rendered = Render(
            componentCount: 1,
            new PackageArtifactCollectionSummary(0, 0, 0),
            new DeclaredGitHubFileArtifactCollectionSummary(0, 0, 0, 0, 0, 0, 0),
            new PackageMetadataSummary(0, 0, 0, 0, 0, 0, 0, 1, 8, 1),
            new SourceRepositorySummary(0, 0, 0, 0, 0, 1, "none", 8, 1));

        await Assert.That(rendered).IsEqualTo(
            """
              Evidence (full scan)
                Package artifacts: 0 targets; 0 documents; 0 SPDX matches
                Declared GitHub files: 0 targets; 0 cache misses; 0 fetch errors
                Package metadata: 0 targets; 0 component cache misses; 0 fetch errors
                Source repositories: 0 targets; 0 cache misses; 0 fetch errors

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task WriteEvidenceSummary_WithCollectedEvidence_IsCompactAndNamesUnits()
    {
        var rendered = Render(
            componentCount: 93,
            new PackageArtifactCollectionSummary(63, 16, 14),
            new DeclaredGitHubFileArtifactCollectionSummary(1, 0, 1, 0, 1, 4, 0),
            new PackageMetadataSummary(81, 60, 19, 0, 0, 2, 0, 12, 8, 1, 77),
            new SourceRepositorySummary(38, 0, 38, 0, 0, 54, "none", 8, 1));

        await Assert.That(rendered).IsEqualTo(
            """
              Evidence (full scan)
                Package artifacts: 63 targets; 16 documents; 14 SPDX matches
                Declared GitHub files: 1 target; 0 cache misses; 0 fetch errors
                Package metadata: 77 targets; 19 component cache misses; 0 fetch errors
                Source repositories: 38 targets; 0 cache misses; 0 fetch errors

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task WriteEvidenceSummary_WhenVerbose_StatesDetailedPartitionsAndSettings()
    {
        var rendered = Render(
            componentCount: 2,
            new PackageArtifactCollectionSummary(0, 0, 0),
            new DeclaredGitHubFileArtifactCollectionSummary(0, 0, 0, 0, 0, 0, 0),
            new PackageMetadataSummary(1, 1, 0, 0, 0, 0, 0, 0, 8, 1, 1),
            new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", 8, 1),
            verbose: true);

        await Assert.That(rendered).IsEqualTo(
            """
              Evidence (full scan)
                Package artifacts: 0 targets; 0 documents; 0 SPDX matches
                          Details: 0 documents = 0 SPDX matches + 0 unmatched
                Declared GitHub files: 0 targets; 0 cache misses; 0 fetch errors
                              Details: 0 cache hits; 0 GitHub requests; 0 documents; 0 component matches
                Package metadata: 1 target; 0 component cache misses; 0 fetch errors
                         Details: 2 components = 1 lookup eligible + 0 in unsupported ecosystems + 0 with unversioned purl + 0 without purl + 1 skipped; 1 component cache hits; 0 refreshed
                Source repositories: 0 targets; 0 cache misses; 0 fetch errors
                            Details: 0 cache hits; 0 GitHub requests; 0 components with unknown source-license outcomes
              External evidence configuration: concurrency 8; configured retry limit 1 per request; GitHub auth none

            """.ReplaceLineEndings("\n"));
    }

    private static string Render(
        int componentCount,
        PackageArtifactCollectionSummary packageArtifacts,
        DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFiles,
        PackageMetadataSummary packageMetadata,
        SourceRepositorySummary source,
        bool verbose = false)
    {
        var writer = new StringWriter { NewLine = "\n" };
        ScanCommands.WriteEvidenceSummary(componentCount, packageArtifacts, declaredGitHubFiles, packageMetadata, source, verbose, writer);
        return writer.ToString();
    }
}
