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

    private static ScanComponent CreateComponent(Utf8Slice license, LicenseStatus status, string name = "example")
        => new(name, "1.0.0", license, "npm", DependencyType.Direct, status, $"pkg:npm/{name}@1.0.0", name, default, [], []);
}
