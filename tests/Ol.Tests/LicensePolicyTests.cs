using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

public sealed class LicensePolicyTests
{
    private static readonly SpdxLicenseIndex Spdx = new(
        ["MIT", "Apache-2.0", "BSD-3-Clause", "GPL-2.0-only", "GPL-3.0-only"],
        ["Classpath-exception-2.0"]);

    [Test]
    [Arguments("MIT", "MIT", true)]
    [Arguments("GPL-3.0-only", "MIT", false)]
    [Arguments("MIT AND Apache-2.0", "MIT,Apache-2.0", true)]
    [Arguments("MIT AND GPL-3.0-only", "MIT", false)]
    [Arguments("GPL-3.0-only AND MIT", "MIT", false)]
    [Arguments("GPL-3.0-only AND Apache-2.0", "MIT", false)]
    [Arguments("MIT OR Apache-2.0", "MIT,Apache-2.0", true)]
    [Arguments("MIT OR GPL-3.0-only", "MIT", true)]
    [Arguments("GPL-3.0-only OR MIT", "MIT", true)]
    [Arguments("GPL-3.0-only OR Apache-2.0", "MIT", false)]
    [Arguments("GPL-2.0-only WITH Classpath-exception-2.0", "GPL-2.0-only", true)]
    [Arguments("GPL-2.0-only WITH Classpath-exception-2.0", "MIT", false)]
    [Arguments("MIT AND (Apache-2.0 OR GPL-3.0-only)", "MIT,Apache-2.0", true)]
    [Arguments("(MIT OR Apache-2.0) AND GPL-3.0-only", "MIT,Apache-2.0", false)]
    public async Task Evaluate_MatchedExpression_FollowsSpdxBooleanSemantics(string expression, string allowed, bool expected)
    {
        var allowedIds = allowed.Split(',');
        var created = LicenseAllowPolicy.TryCreate(allowedIds, Spdx, out var policy, out var error);
        var component = CreateComponent(expression, LicenseStatus.Matched);

        var violations = policy.Evaluate([component]);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(violations.Length == 0).IsEqualTo(expected);
    }

    [Test]
    [Arguments(LicenseStatus.Conflict, LicensePolicyViolationKind.Conflict)]
    [Arguments(LicenseStatus.Unknown, LicensePolicyViolationKind.Unknown)]
    [Arguments(LicenseStatus.Ambiguous, LicensePolicyViolationKind.Ambiguous)]
    [Arguments(LicenseStatus.Invalid, LicensePolicyViolationKind.Invalid)]
    [Arguments(LicenseStatus.Error, LicensePolicyViolationKind.Error)]
    public async Task Evaluate_UnresolvedStatus_FailsClosed(LicenseStatus status, LicensePolicyViolationKind expectedKind)
    {
        LicenseAllowPolicy.TryCreate(["MIT"], Spdx, out var policy, out _);

        var violations = policy.Evaluate([CreateComponent("MIT", status)]);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(violations[0].ComponentIndex).IsEqualTo(0);
        await Assert.That(violations[0].Kind).IsEqualTo(expectedKind);
    }

    [Test]
    public async Task Evaluate_WithRootAndUnknownDependency_IgnoresOnlyRootAndPreservesComponentIndex()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], Spdx, out var policy, out _);
        ScanComponent[] components =
        [
            CreateComponent("GPL-3.0-only", LicenseStatus.Matched, "application") with { DependencyType = DependencyType.Root },
            CreateComponent(default, LicenseStatus.Unknown, "dependency"),
        ];

        var violations = policy.Evaluate(components, null, out _, out var evaluatedCount);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(evaluatedCount).IsEqualTo(1);
        await Assert.That(violations[0].ComponentIndex).IsEqualTo(1);
        await Assert.That(violations[0].Kind).IsEqualTo(LicensePolicyViolationKind.Unknown);
    }

    [Test]
    public async Task Evaluate_MultipleComponents_ReturnsEveryViolationInComponentOrder()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], Spdx, out var policy, out _);
        ScanComponent[] components =
        [
            CreateComponent("GPL-3.0-only", LicenseStatus.Matched, "first"),
            CreateComponent("MIT", LicenseStatus.Matched, "allowed"),
            CreateComponent(default, LicenseStatus.Unknown, "third"),
        ];

        var violations = policy.Evaluate(components);

        await Assert.That(violations).Count().IsEqualTo(2);
        await Assert.That(violations[0].ComponentIndex).IsEqualTo(0);
        await Assert.That(violations[1].ComponentIndex).IsEqualTo(2);
    }

    [Test]
    public async Task Evaluate_WithNoComponents_ReturnsNoViolations()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], Spdx, out var policy, out _);

        var violations = policy.Evaluate([]);

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task TryCreate_NormalizesCaseAndDeduplicatesIdentifiers()
    {
        var created = LicenseAllowPolicy.TryCreate(["mit", "MIT"], Spdx, out var policy, out var error);

        var violations = policy.Evaluate([CreateComponent("MIT", LicenseStatus.Matched)]);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments("")]
    [Arguments("Unknown-License")]
    [Arguments("MIT OR Apache-2.0")]
    [Arguments("Classpath-exception-2.0")]
    [Arguments("Apache License")]
    public async Task TryCreate_WithInvalidEntry_RejectsPolicy(string value)
    {
        var created = LicenseAllowPolicy.TryCreate([value], Spdx, out _, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task Evaluate_DevelopmentOnlyComponent_AllowedByDevelopmentList()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], ["GPL-3.0-only"], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent("GPL-3.0-only", LicenseStatus.Matched)],
            [DependencyUsage.Development],
            null,
            out _,
            out var evaluatedCount,
            out var developmentAllowedComponents);

        await Assert.That(violations).IsEmpty();
        await Assert.That(evaluatedCount).IsEqualTo(1);
        await Assert.That(developmentAllowedComponents.Length).IsEqualTo(1);
    }

    [Test]
    [Arguments(DependencyUsage.Runtime)]
    [Arguments(DependencyUsage.Unknown)]
    public async Task Evaluate_NonDevelopmentUsage_NotAllowedByDevelopmentList(DependencyUsage usage)
    {
        LicenseAllowPolicy.TryCreate(["MIT"], ["GPL-3.0-only"], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent("GPL-3.0-only", LicenseStatus.Matched)],
            [usage],
            null,
            out _,
            out _,
            out var developmentAllowedComponents);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(violations[0].Kind).IsEqualTo(LicensePolicyViolationKind.NotAllowed);
        await Assert.That(developmentAllowedComponents.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Evaluate_DevelopmentOnlyComponent_LicenseOutsideDevelopmentList_Fails()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], ["GPL-3.0-only"], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent("GPL-2.0-only", LicenseStatus.Matched)],
            [DependencyUsage.Development],
            null,
            out _,
            out _,
            out var developmentAllowedComponents);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(developmentAllowedComponents.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments("MIT AND GPL-3.0-only", true)]
    [Arguments("MIT OR GPL-3.0-only", true)]
    [Arguments("GPL-3.0-only AND GPL-2.0-only", false)]
    [Arguments("GPL-2.0-only WITH Classpath-exception-2.0", false)]
    public async Task Evaluate_DevelopmentUnion_FollowsSpdxBooleanSemantics(string expression, bool expectedPass)
    {
        LicenseAllowPolicy.TryCreate(["MIT"], ["GPL-3.0-only"], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent(expression, LicenseStatus.Matched)],
            [DependencyUsage.Development],
            null,
            out _,
            out _,
            out _);

        await Assert.That(violations.Length == 0).IsEqualTo(expectedPass);
    }

    [Test]
    public async Task Evaluate_WithoutDevelopmentList_IgnoresUsageAndFailsClosed()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent("GPL-3.0-only", LicenseStatus.Matched)],
            [DependencyUsage.Development],
            null,
            out _,
            out _,
            out var developmentAllowedComponents);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(developmentAllowedComponents.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Evaluate_DevelopmentUsage_DoesNotBypassUnresolvedStatus()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], ["GPL-3.0-only"], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponent(default, LicenseStatus.Unknown)],
            [DependencyUsage.Development],
            null,
            out _,
            out _,
            out var developmentAllowedComponents);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(violations[0].Kind).IsEqualTo(LicensePolicyViolationKind.Unknown);
        await Assert.That(developmentAllowedComponents.Length).IsEqualTo(0);
    }

    [Test]
    public async Task TryCreate_WithInvalidDevelopmentEntry_RejectsPolicy()
    {
        var created = LicenseAllowPolicy.TryCreate(["MIT"], ["Unknown-License"], Spdx, out _, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task Evaluate_WithExcludedPurlPrefix_SkipsComponentAndCountsIt()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], ["pkg:nuget/MyCompany."], Spdx, out var policy, out _);
        ScanComponent[] components =
        [
            CreateComponentWithPurl("pkg:nuget/MyCompany.Core@1.0.0", LicenseStatus.Unknown),
            CreateComponentWithPurl("pkg:npm/example@1.0.0", LicenseStatus.Unknown),
        ];

        var violations = policy.Evaluate(components, default, null, out _, out var evaluatedCount, out _, out var excludedCount);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(violations[0].ComponentIndex).IsEqualTo(1);
        await Assert.That(evaluatedCount).IsEqualTo(1);
        await Assert.That(excludedCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("pkg:npm/left-pad@1.3.0", "pkg:npm/left-pad@1.3.0", true)]
    [Arguments("pkg:npm/@acme/", "pkg:npm/@acme/util@1.0.0", true)]
    [Arguments("pkg:nuget/MyCompany.", "pkg:nuget/MyCompany.Core@1.0.0", true)]
    [Arguments("pkg:nuget/MyCompany", "pkg:nuget/MyCompany@1.0.0", true)]
    [Arguments("pkg:npm/@acme", "pkg:npm/@acme/util@1.0.0", true)]
    [Arguments("pkg:nuget/MyCompany", "pkg:nuget/MyCompanyEvil@1.0.0", false)]
    [Arguments("pkg:npm/lodash", "pkg:npm/lodash.merge@1.0.0", false)]
    [Arguments("pkg:npm/example", "pkg:nuget/example@1.0.0", false)]
    [Arguments("pkg:nuget/mycompany.", "pkg:nuget/MyCompany.Core@1.0.0", false)]
    public async Task Evaluate_ExcludedPrefix_MatchesOnlyAtPurlBoundary(string prefix, string purl, bool excluded)
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], [prefix], Spdx, out var policy, out _);

        var violations = policy.Evaluate([CreateComponentWithPurl(purl, LicenseStatus.Unknown)], default, null, out _, out _, out _, out var excludedCount);

        await Assert.That(violations.Length == 0).IsEqualTo(excluded);
        await Assert.That(excludedCount).IsEqualTo(excluded ? 1 : 0);
    }

    [Test]
    public async Task Evaluate_ComponentWithoutPurl_IsNeverExcluded()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], ["pkg:npm/example"], Spdx, out var policy, out _);

        var violations = policy.Evaluate([CreateComponentWithPurl(default, LicenseStatus.Unknown)], default, null, out _, out _, out _, out var excludedCount);

        await Assert.That(violations).Count().IsEqualTo(1);
        await Assert.That(excludedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Evaluate_ExcludedComponentWithForbiddenLicense_IsNotAViolation()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], ["pkg:nuget/MyCompany."], Spdx, out var policy, out _);

        var violations = policy.Evaluate(
            [CreateComponentWithPurl("pkg:nuget/MyCompany.Core@1.0.0", LicenseStatus.Matched, "GPL-3.0-only")],
            default,
            null,
            out _,
            out var evaluatedCount,
            out _,
            out var excludedCount);

        await Assert.That(violations).IsEmpty();
        await Assert.That(evaluatedCount).IsEqualTo(0);
        await Assert.That(excludedCount).IsEqualTo(1);
    }

    [Test]
    public async Task CanAcknowledge_ExcludedComponent_ReturnsFalse()
    {
        LicenseAllowPolicy.TryCreate(["MIT"], [], ["pkg:nuget/MyCompany."], Spdx, out var policy, out _);

        var excluded = policy.CanAcknowledge(CreateComponentWithPurl("pkg:nuget/MyCompany.Core@1.0.0", LicenseStatus.Unknown));
        var evaluated = policy.CanAcknowledge(CreateComponentWithPurl("pkg:npm/example@1.0.0", LicenseStatus.Unknown));

        await Assert.That(excluded).IsFalse();
        await Assert.That(evaluated).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("npm/example")]
    [Arguments("pkg:")]
    [Arguments("pkg:npm")]
    [Arguments("pkg:npm/")]
    [Arguments("pkg:npm/@")]
    public async Task TryCreate_WithInvalidExcludedPackage_RejectsPolicy(string value)
    {
        var created = LicenseAllowPolicy.TryCreate(["MIT"], [], [value], Spdx, out _, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task TryCreate_WithSurroundingWhitespaceAndDuplicateExclusions_AppliesOnce()
    {
        var created = LicenseAllowPolicy.TryCreate(["MIT"], [], [" pkg:npm/example ", "pkg:npm/example"], Spdx, out var policy, out var error);

        var violations = policy.Evaluate([CreateComponentWithPurl("pkg:npm/example@1.0.0", LicenseStatus.Unknown)], default, null, out _, out _, out _, out var excludedCount);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(violations).IsEmpty();
        await Assert.That(excludedCount).IsEqualTo(1);
    }

    private static ScanComponent CreateComponent(Utf8Slice license, LicenseStatus status, string name = "example")
        => new(name, "1.0.0", license, "npm", DependencyType.Direct, status, $"pkg:npm/{name}@1.0.0", name, default, [], []);

    private static ScanComponent CreateComponentWithPurl(Utf8Slice purl, LicenseStatus status, Utf8Slice license = default)
        => new("example", "1.0.0", license, "npm", DependencyType.Direct, status, purl, "example", default, [], []);
}
