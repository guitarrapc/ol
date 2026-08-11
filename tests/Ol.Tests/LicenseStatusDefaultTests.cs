using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards the safe default for the license classification. A component built without an explicit status
/// must never read as resolved: for a compliance tool, an absent license has to surface as unresolved.
/// </summary>
public sealed class LicenseStatusDefaultTests
{
    private static readonly SpdxLicenseIndex Spdx = new(["MIT", "Apache-2.0"], []);

    [Test]
    public async Task DefaultLicenseStatus_IsUnknownRatherThanMatched()
    {
        var statuses = new[] { default(LicenseStatus), default(LicenseCandidate).Status, default(ScanComponent).Status };
        var resolved = statuses.Count(status => status == LicenseStatus.Matched);

        await Assert.That(resolved).IsEqualTo(0);
    }

    // Equivalence classes: a package-manager input declares a valid license, an unnormalizable license,
    // or none at all. The "none at all" class is the one a zero-valued Matched silently mislabels.

    [Test]
    [Arguments("npm-none", null, LicenseStatus.Unknown)]
    [Arguments("npm-valid", "MIT", LicenseStatus.Matched)]
    [Arguments("npm-ambiguous", "BSD", LicenseStatus.Ambiguous)]
    public async Task Scan_NpmPackageLock_ClassifiesLicensePresence(string label, string? license, LicenseStatus expected)
    {
        var declaration = license is null ? string.Empty : $", \"license\": \"{license}\"";
        var json = $$"""
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "app", "dependencies": { "target": "^1.0.0" } },
            "node_modules/target": { "name": "target", "version": "1.0.0"{{declaration}} }
          }
        }
        """;

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx, expectedFormat: ScanInputFormat.NpmPackageLock);
        var target = inventory.Components.Single(c => c.Name.ToString() == "target");

        await Assert.That(target.Status).IsEqualTo(expected);
        await Assert.That(label).IsNotEmpty();
    }

    [Test]
    public async Task Scan_NpmPackageLockWithoutLicense_ProducesNoLicenseValue()
    {
        var json = """
        {
          "lockfileVersion": 3,
          "packages": {
            "": { "name": "app", "dependencies": { "target": "^1.0.0" } },
            "node_modules/target": { "name": "target", "version": "1.0.0" }
          }
        }
        """;

        var inventory = DependencyInputScanner.Scan(Encoding.UTF8.GetBytes(json), Spdx, expectedFormat: ScanInputFormat.NpmPackageLock);
        var target = inventory.Components.Single(c => c.Name.ToString() == "target");

        await Assert.That(target.Status).IsEqualTo(LicenseStatus.Unknown);
        await Assert.That(target.License.IsEmpty).IsTrue();
        await Assert.That(target.CandidateCount).IsEqualTo(0);
    }

    /// <summary>
    /// Cross-parser invariant. Every registered input builds components itself, so the same
    /// zero-valued-status mistake can reappear in any new parser. A component can only be
    /// <see cref="LicenseStatus.Matched"/> when it actually carries a license expression.
    /// </summary>
    [Test]
    [Arguments("package-lock.json")]
    [Arguments("cargo-metadata.json")]
    [Arguments("pip-inspect.json")]
    [Arguments("pnpm-lock.yaml")]
    [Arguments("yarn-classic.lock")]
    [Arguments("yarn-berry.lock")]
    [Arguments("nuget-project.assets.json")]
    [Arguments("Gemfile.lock")]
    public async Task Scan_AnyRegisteredInput_NeverReportsMatchedWithoutALicense(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var inventory = DependencyInputScanner.Scan(await File.ReadAllBytesAsync(path), Spdx);

        var mislabeled = inventory.Components
            .Where(component => component.Status == LicenseStatus.Matched && component.License.IsEmpty)
            .Select(component => component.Name.ToString())
            .ToArray();

        await Assert.That(mislabeled).IsEmpty();
    }

    [Test]
    public async Task Reconcile_WithNoCandidates_ReportsUnknown()
    {
        var component = new ScanComponent("x", "1.0.0", default, "npm", DependencyType.Direct, LicenseStatus.Matched, default, default, default, []);

        var reconciled = LicenseReconciler.Reconcile(component);

        await Assert.That(reconciled.Status).IsEqualTo(LicenseStatus.Unknown);
    }
}
