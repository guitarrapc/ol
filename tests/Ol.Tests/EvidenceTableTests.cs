using Ol.Core.GitHub;
using Ol.Core.PackageManagers;
using Ol.Internals;

namespace Ol.Tests;

/// <summary>
/// Covers the alignment of the scan summary's evidence table.
/// </summary>
/// <remarks>
/// No scan can reach a counter wide enough to widen a column — the narrowest header is four characters —
/// so the width computation the alignment depends on is only observable here.
/// </remarks>
public sealed class EvidenceTableTests
{
    /// <summary>Every counter narrower than its header, which is what an ordinary scan produces.</summary>
    [Test]
    public async Task WriteEvidenceTable_WithCountersNarrowerThanTheirHeaders_AlignsEveryCellUnderItsHeader()
    {
        var rendered = Render(
            new PackageArtifactCollectionSummary(0, 0, 0),
            new DeclaredGitHubFileArtifactCollectionSummary(0, 0, 0, 0, 0, 0, 0),
            new PackageMetadataSummary(0, 0, 0, 0, 0, 0, 0, 0, 8, 1),
            new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", 8, 1));

        await Assert.That(rendered).IsEqualTo(
            """
              Evidence (full scan)     targets  requests  cache hits  cache misses  docs  matched  errors
                Package artifacts            0         -           -             -     0        0       -
                Declared GitHub files        0         0           0             0     0        0       0
                Package metadata             0         -           0             0     -        -       0
                Source repositories          0         0           0             0     -        -       0
                Package metadata: 0 refreshed; 0 unsupported ecosystems; 0 unversioned purls; 0 without purl
                Source repositories: 0 components without source license

            """.ReplaceLineEndings("\n"));
    }

    /// <summary>A counter wider than its header widens that column for the header and for every other row.</summary>
    [Test]
    public async Task WriteEvidenceTable_WithCounterWiderThanItsHeader_WidensThatColumnForEveryRow()
    {
        var rendered = Render(
            new PackageArtifactCollectionSummary(0, 0, 0),
            new DeclaredGitHubFileArtifactCollectionSummary(2, 3, 4, 5, 12345, 6, 1234567),
            new PackageMetadataSummary(7, 8, 9, 11, 10, 12, 13, 14, 8, 1),
            new SourceRepositorySummary(15, 16, 17, 18, 19, 20, "token", 8, 1));

        await Assert.That(rendered).IsEqualTo(
            """
              Evidence (full scan)     targets  requests  cache hits  cache misses   docs  matched   errors
                Package artifacts            0         -           -             -      0        0        -
                Declared GitHub files        2         3           4             5  12345        6  1234567
                Package metadata             7         -           8             9      -        -       10
                Source repositories         15        16          17            18      -        -       19
                Package metadata: 11 refreshed; 12 unsupported ecosystems; 13 unversioned purls; 14 without purl
                Source repositories: 20 components without source license

            """.ReplaceLineEndings("\n"));
    }

    private static string Render(
        PackageArtifactCollectionSummary packageArtifacts,
        DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFiles,
        PackageMetadataSummary packageMetadata,
        SourceRepositorySummary source)
    {
        var writer = new StringWriter { NewLine = "\n" };
        ScanCommands.WriteEvidenceTable(packageArtifacts, declaredGitHubFiles, packageMetadata, source, writer);
        return writer.ToString();
    }
}
