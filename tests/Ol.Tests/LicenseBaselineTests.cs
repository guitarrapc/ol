using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class LicenseBaselineTests
{
    private static readonly SpdxLicenseIndex Spdx = new(
        ["MIT", "Apache-2.0", "BSD-3-Clause", "GPL-3.0-only"],
        ["Classpath-exception-2.0"]);

    private static LicenseAllowPolicy CreatePolicy(params string[] allowed)
    {
        LicenseAllowPolicy.TryCreate(allowed.Length == 0 ? ["MIT"] : allowed, Spdx, out var policy, out _);
        return policy;
    }

    // Acknowledgeability: status class x forbidden-candidate class.

    [Test]
    [Arguments(LicenseStatus.Unknown, true)]
    [Arguments(LicenseStatus.Ambiguous, true)]
    [Arguments(LicenseStatus.Conflict, true)]
    [Arguments(LicenseStatus.Invalid, true)]
    [Arguments(LicenseStatus.Error, false)]
    [Arguments(LicenseStatus.Matched, false)]
    public async Task CanAcknowledge_ByStatus_AllowsOnlyUnresolvedExceptError(LicenseStatus status, bool expected)
    {
        var policy = CreatePolicy("MIT");
        // A value the allow-list can neither admit nor reject, so status alone decides every row. A candidate
        // the allow-list admits would settle the ambiguous row on its own merits instead.
        var component = CreateComponent(status, Candidate(LicenseCandidateSource.Sbom, "Custom License", "Custom License"));

        await Assert.That(policy.CanAcknowledge(component)).IsEqualTo(expected);
    }

    [Test]
    public async Task CanAcknowledge_ResolvedListingTheAllowListAdmits_IsNotAcknowledgeable()
    {
        var policy = CreatePolicy("MIT", "Apache-2.0");
        var component = CreateComponent(LicenseStatus.Ambiguous, ListingCandidate("MIT; Apache-2.0"));

        await Assert.That(policy.CanAcknowledge(component)).IsFalse();
    }

    [Test]
    public async Task CanAcknowledge_ResolvedListingNamingAForbiddenLicense_StaysAcknowledgeable()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Ambiguous, ListingCandidate("MIT; Apache-2.0"));

        await Assert.That(policy.CanAcknowledge(component)).IsTrue();
    }

    // Publisher punctuation is not a listing, so it stays a violation and therefore stays acknowledgeable.
    [Test]
    public async Task CanAcknowledge_PublisherTextContainingASemicolon_StaysAcknowledgeable()
    {
        var policy = CreatePolicy("MIT", "Apache-2.0");
        var component = CreateComponent(LicenseStatus.Ambiguous, Candidate(LicenseCandidateSource.NpmRegistry, "MIT; Apache-2.0", "MIT; Apache-2.0"));

        await Assert.That(policy.CanAcknowledge(component)).IsTrue();
    }

    [Test]
    [Arguments("MIT", true)]
    [Arguments("Apache-2.0", true)]
    [Arguments("GPL-3.0-only", false)]
    public async Task CanAcknowledge_WithNormalizedCandidate_RejectsForbiddenLicense(string normalized, bool expected)
    {
        var policy = CreatePolicy("MIT", "Apache-2.0");
        var component = CreateComponent(
            LicenseStatus.Conflict,
            Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT"),
            Candidate(LicenseCandidateSource.NpmRegistry, normalized, normalized));

        await Assert.That(policy.CanAcknowledge(component)).IsEqualTo(expected);
    }

    [Test]
    public async Task CanAcknowledge_WithUnnormalizableCandidate_RemainsAcknowledgeable()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Invalid, Candidate(LicenseCandidateSource.Sbom, "GPLv3", string.Empty));

        await Assert.That(policy.CanAcknowledge(component)).IsTrue();
    }

    [Test]
    public async Task CanAcknowledge_WithNoCandidates_AllowsUnknown()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Unknown);

        await Assert.That(policy.CanAcknowledge(component)).IsTrue();
    }

    [Test]
    public async Task CanAcknowledge_WithRoot_ReturnsFalse()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Unknown) with { DependencyType = DependencyType.Root };

        await Assert.That(policy.CanAcknowledge(component)).IsFalse();
    }

    [Test]
    public async Task CanAcknowledge_AfterTighteningAllowList_RejectsPreviouslyAcknowledgeableComponent()
    {
        var component = CreateComponent(
            LicenseStatus.Conflict,
            Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT"),
            Candidate(LicenseCandidateSource.NpmRegistry, "Apache-2.0", "Apache-2.0"));

        await Assert.That(CreatePolicy("MIT", "Apache-2.0").CanAcknowledge(component)).IsTrue();
        await Assert.That(CreatePolicy("MIT").CanAcknowledge(component)).IsFalse();
    }

    // Fingerprint.

    [Test]
    public async Task ComputeFingerprint_WithSameEvidence_IsStable()
    {
        var first = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.Sbom, "BSD", string.Empty));
        var second = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.Sbom, "BSD", string.Empty));

        await Assert.That(LicenseBaseline.ComputeFingerprint(first)).IsEqualTo(LicenseBaseline.ComputeFingerprint(second));
        await Assert.That(LicenseBaseline.ComputeFingerprint(first).Length).IsEqualTo(64);
    }

    [Test]
    public async Task ComputeFingerprint_WithDifferentCandidateOrder_IsStable()
    {
        var first = CreateComponent(
            LicenseStatus.Conflict,
            Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT"),
            Candidate(LicenseCandidateSource.NpmRegistry, "Apache-2.0", "Apache-2.0"));
        var second = CreateComponent(
            LicenseStatus.Conflict,
            Candidate(LicenseCandidateSource.NpmRegistry, "Apache-2.0", "Apache-2.0"),
            Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT"));

        await Assert.That(LicenseBaseline.ComputeFingerprint(first)).IsEqualTo(LicenseBaseline.ComputeFingerprint(second));
    }

    [Test]
    public async Task ComputeFingerprint_WhenRawValueChanges_Changes()
    {
        var before = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.NpmRegistry, string.Empty, string.Empty));
        var after = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.NpmRegistry, "MIT-0", string.Empty));

        await Assert.That(LicenseBaseline.ComputeFingerprint(before)).IsNotEqualTo(LicenseBaseline.ComputeFingerprint(after));
    }

    [Test]
    public async Task ComputeFingerprint_WhenStatusChanges_Changes()
    {
        var candidate = Candidate(LicenseCandidateSource.Sbom, "BSD", string.Empty);
        var unknown = CreateComponent(LicenseStatus.Unknown, candidate);
        var ambiguous = CreateComponent(LicenseStatus.Ambiguous, candidate);

        await Assert.That(LicenseBaseline.ComputeFingerprint(unknown)).IsNotEqualTo(LicenseBaseline.ComputeFingerprint(ambiguous));
    }

    // Snapshot and serialization.

    [Test]
    public async Task CreateEntries_ExcludesComponentsThatCannotBeAcknowledged()
    {
        var policy = CreatePolicy("MIT");
        ScanComponent[] components =
        [
            CreateComponent(LicenseStatus.Unknown, name: "acknowledgeable"),
            CreateComponent(LicenseStatus.Error, name: "collection-failure"),
            CreateComponent(LicenseStatus.Matched, name: "resolved", candidates: [Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT")]),
            CreateComponent(LicenseStatus.Conflict, name: "forbidden", candidates: [Candidate(LicenseCandidateSource.Sbom, "GPL-3.0-only", "GPL-3.0-only")]),
        ];

        var entries = LicenseBaseline.CreateEntries(components, policy);

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("acknowledgeable");
    }

    [Test]
    public async Task CreateEntries_WithExcludedPackage_OmitsThatComponent()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], ["pkg:nuget/MyCompany."], Spdx, out var policy, out _);
        ScanComponent[] components =
        [
            CreateComponent(LicenseStatus.Unknown, name: "MyCompany.Core", ecosystem: "nuget"),
            CreateComponent(LicenseStatus.Unknown, name: "third-party"),
        ];

        var entries = LicenseBaseline.CreateEntries(components, policy);

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("third-party");
    }

    [Test]
    public async Task CreateEntries_WithUnknownRootAndDependency_RecordsOnlyDependency()
    {
        var policy = CreatePolicy("MIT");
        ScanComponent[] components =
        [
            CreateComponent(LicenseStatus.Unknown, name: "application") with { DependencyType = DependencyType.Root },
            CreateComponent(LicenseStatus.Unknown, name: "dependency"),
        ];

        var entries = LicenseBaseline.CreateEntries(components, policy);

        await Assert.That(entries).Count().IsEqualTo(1);
        await Assert.That(entries[0].Name).IsEqualTo("dependency");
    }

    [Test]
    public async Task CreateEntries_OrdersByEcosystemNameVersionAndDeduplicates()
    {
        var policy = CreatePolicy("MIT");
        ScanComponent[] components =
        [
            CreateComponent(LicenseStatus.Unknown, name: "zeta", ecosystem: "npm"),
            CreateComponent(LicenseStatus.Unknown, name: "alpha", ecosystem: "nuget"),
            CreateComponent(LicenseStatus.Unknown, name: "alpha", ecosystem: "npm"),
            CreateComponent(LicenseStatus.Unknown, name: "alpha", ecosystem: "npm"),
        ];

        var entries = LicenseBaseline.CreateEntries(components, policy);

        await Assert.That(entries).Count().IsEqualTo(3);
        await Assert.That(entries[0].Ecosystem).IsEqualTo("npm");
        await Assert.That(entries[0].Name).IsEqualTo("alpha");
        await Assert.That(entries[1].Name).IsEqualTo("zeta");
        await Assert.That(entries[2].Ecosystem).IsEqualTo("nuget");
    }

    [Test]
    public async Task Serialize_IsByteStableAndCarriesNoTimestamp()
    {
        var policy = CreatePolicy("MIT");
        var entries = LicenseBaseline.CreateEntries([CreateComponent(LicenseStatus.Unknown)], policy);

        var first = LicenseBaseline.Serialize(entries, "1.2.3", "abc1234");
        var second = LicenseBaseline.Serialize(entries, "1.2.3", "abc1234");
        var text = Encoding.UTF8.GetString(first);

        await Assert.That(first).IsEquivalentTo(second);
        await Assert.That(text).Contains("\"schemaVersion\": 1");
        await Assert.That(text).Contains("\"licenseListVersion\": \"abc1234\"");
        await Assert.That(text.Contains("timestamp", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(text.Contains("generatedAt", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Serialize_RetainsRawClaimsForReview()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Ambiguous, Candidate(LicenseCandidateSource.Sbom, "BSD", string.Empty));

        var text = Encoding.UTF8.GetString(LicenseBaseline.Serialize(LicenseBaseline.CreateEntries([component], policy), "1.0.0", "x"));

        await Assert.That(text).Contains("\"raw\": \"BSD\"");
        await Assert.That(text).Contains("\"source\": \"sbom\"");
        await Assert.That(text).Contains("\"status\": \"ambiguous\"");
    }

    [Test]
    public async Task Serialize_WithOverlongRawValue_TruncatesInFileButNotInFingerprint()
    {
        var policy = CreatePolicy("MIT");
        var longRaw = new string('a', LicenseBaseline.MaxRawLength + 50);
        var shortRaw = new string('a', LicenseBaseline.MaxRawLength);
        var longComponent = CreateComponent(LicenseStatus.Ambiguous, Candidate(LicenseCandidateSource.Sbom, longRaw, string.Empty));
        var shortComponent = CreateComponent(LicenseStatus.Ambiguous, Candidate(LicenseCandidateSource.Sbom, shortRaw, string.Empty));

        var entries = LicenseBaseline.CreateEntries([longComponent], policy);
        var text = Encoding.UTF8.GetString(LicenseBaseline.Serialize(entries, "1.0.0", "x"));

        await Assert.That(entries[0].Evidence[0].Raw.Length).IsEqualTo(LicenseBaseline.MaxRawLength);
        await Assert.That(entries[0].Evidence[0].Truncated).IsTrue();
        await Assert.That(text).Contains("\"truncated\": true");
        await Assert.That(LicenseBaseline.ComputeFingerprint(longComponent)).IsNotEqualTo(LicenseBaseline.ComputeFingerprint(shortComponent));
    }

    // Round-trip and matching.

    [Test]
    public async Task IsAcknowledged_WithMatchingPurlAndFingerprint_ReturnsTrue()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Unknown);
        var baseline = RoundTrip(LicenseBaseline.CreateEntries([component], policy));

        await Assert.That(baseline.IsAcknowledged(component)).IsTrue();
    }

    [Test]
    public async Task IsAcknowledged_WhenEvidenceChanged_ReturnsFalse()
    {
        var policy = CreatePolicy("MIT");
        var before = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.NpmRegistry, string.Empty, string.Empty));
        var after = CreateComponent(LicenseStatus.Unknown, Candidate(LicenseCandidateSource.NpmRegistry, "Unlicense", string.Empty));
        var baseline = RoundTrip(LicenseBaseline.CreateEntries([before], policy));

        await Assert.That(baseline.IsAcknowledged(before)).IsTrue();
        await Assert.That(baseline.IsAcknowledged(after)).IsFalse();
    }

    [Test]
    public async Task IsAcknowledged_WhenVersionChanged_ReturnsFalse()
    {
        var policy = CreatePolicy("MIT");
        var before = CreateComponent(LicenseStatus.Unknown, version: "1.0.0");
        var after = CreateComponent(LicenseStatus.Unknown, version: "1.1.0");
        var baseline = RoundTrip(LicenseBaseline.CreateEntries([before], policy));

        await Assert.That(baseline.IsAcknowledged(after)).IsFalse();
    }

    [Test]
    public async Task IsAcknowledged_WithoutPurl_MatchesOnEcosystemNameAndVersion()
    {
        var policy = CreatePolicy("MIT");
        var component = CreateComponent(LicenseStatus.Unknown, purl: new Utf8Slice());
        var entries = LicenseBaseline.CreateEntries([component], policy);
        var baseline = RoundTrip(entries);

        await Assert.That(entries[0].Purl).IsEmpty();
        await Assert.That(baseline.IsAcknowledged(component)).IsTrue();
    }

    // Parsing failures.

    [Test]
    [Arguments("{ malformed")]
    [Arguments("[]")]
    [Arguments("{ \"schemaVersion\": 99, \"acknowledged\": [] }")]
    [Arguments("{ \"acknowledged\": [] }")]
    public async Task TryParse_WithUnusableDocument_Fails(string json)
    {
        var parsed = LicenseBaseline.TryParse(Encoding.UTF8.GetBytes(json), out _, out var error);

        await Assert.That(parsed).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task TryParse_WithEmptyAcknowledgedList_Succeeds()
    {
        var parsed = LicenseBaseline.TryParse(
            Encoding.UTF8.GetBytes($"{{ \"schemaVersion\": {LicenseBaseline.SchemaVersion}, \"acknowledged\": [] }}"),
            out var baseline,
            out var error);

        await Assert.That(parsed).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(baseline.Count).IsEqualTo(0);
    }

    // Policy integration.

    [Test]
    public async Task Evaluate_WithBaseline_RemovesAcknowledgedViolationsAndCountsThem()
    {
        var policy = CreatePolicy("MIT");
        ScanComponent[] components =
        [
            CreateComponent(LicenseStatus.Unknown, name: "acknowledged"),
            CreateComponent(LicenseStatus.Matched, name: "forbidden", candidates: [Candidate(LicenseCandidateSource.Sbom, "GPL-3.0-only", "GPL-3.0-only")]),
        ];
        components[1] = components[1] with { License = "GPL-3.0-only" };
        var baseline = RoundTrip(LicenseBaseline.CreateEntries(components, policy));

        var violations = policy.Evaluate(components, baseline, out var acknowledgedCount);

        await Assert.That(acknowledgedCount).IsEqualTo(1);
        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(violations[0].ComponentIndex).IsEqualTo(1);
        await Assert.That(violations[0].Kind).IsEqualTo(LicensePolicyViolationKind.NotAllowed);
    }

    [Test]
    public async Task Evaluate_WithBaselineWrittenUnderWiderAllowList_ReappliesForbiddenLicenseRule()
    {
        var component = CreateComponent(
            LicenseStatus.Conflict,
            Candidate(LicenseCandidateSource.Sbom, "MIT", "MIT"),
            Candidate(LicenseCandidateSource.NpmRegistry, "Apache-2.0", "Apache-2.0"));
        var wide = CreatePolicy("MIT", "Apache-2.0");
        var baseline = RoundTrip(LicenseBaseline.CreateEntries([component], wide));

        var wideViolations = wide.Evaluate([component], baseline, out var wideAcknowledged);
        var narrowViolations = CreatePolicy("MIT").Evaluate([component], baseline, out var narrowAcknowledged);

        await Assert.That(wideViolations).IsEmpty();
        await Assert.That(wideAcknowledged).IsEqualTo(1);
        await Assert.That(narrowViolations).Count().IsEqualTo(1);
        await Assert.That(narrowAcknowledged).IsEqualTo(0);
    }

    [Test]
    public async Task Evaluate_WithoutBaseline_MatchesExistingBehavior()
    {
        var policy = CreatePolicy("MIT");
        ScanComponent[] components = [CreateComponent(LicenseStatus.Unknown)];

        var violations = policy.Evaluate(components, null, out var acknowledgedCount);

        await Assert.That(acknowledgedCount).IsEqualTo(0);
        await Assert.That(violations).Count().IsEqualTo(1);
    }

    private static LicenseBaseline RoundTrip(LicenseBaselineEntry[] entries)
    {
        var utf8 = LicenseBaseline.Serialize(entries, "1.0.0", "test");
        if (!LicenseBaseline.TryParse(utf8, out var baseline, out var error)) throw new InvalidOperationException(error);
        return baseline;
    }

    private static LicenseCandidate Candidate(LicenseCandidateSource source, string raw, string normalized)
        => new(source, LicenseCandidateKind.License, raw, normalized, LicenseStatus.Unknown, false, LicenseCandidateWarnings.None);

    private static LicenseCandidate ListingCandidate(string raw)
        => LicenseCandidateFactory.CreateLicenseSet(LicenseCandidateSource.DepsDev, Utf8Slice.FromString(raw), Spdx);

    private static ScanComponent CreateComponent(
        LicenseStatus status,
        params LicenseCandidate[] candidates)
        => CreateComponent(status, "example", "npm", "1.0.0", null, candidates);

    private static ScanComponent CreateComponent(
        LicenseStatus status,
        string name = "example",
        string ecosystem = "npm",
        string version = "1.0.0",
        Utf8Slice? purl = null,
        LicenseCandidate[]? candidates = null)
    {
        candidates ??= [];
        var resolvedPurl = purl ?? (Utf8Slice)$"pkg:{ecosystem}/{name}@{version}";
        return new ScanComponent(
            name,
            version,
            default,
            ecosystem,
            DependencyType.Direct,
            status,
            resolvedPurl,
            name,
            candidates.Length == 0 ? default : candidates[0],
            candidates.Length <= 1 ? [] : candidates[1..]);
    }
}
