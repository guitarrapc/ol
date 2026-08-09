using System.Text;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Spdx;

namespace Ol.Tests;

/// <summary>
/// Guards how reconciliation compares two valid SPDX expressions.
/// </summary>
/// <remarks>
/// A disjunction states a choice the publisher offers. An evidence source that reports one of those
/// options is agreeing with the offer, not contradicting it: repository license detection answers with
/// one file at the repository root, so it names one option out of several by construction. Treating
/// that as disagreement turns dual licensing, the norm in Rust, into a conflict on every component and
/// makes collecting more evidence produce a worse result than collecting none.
/// </remarks>
public sealed class LicenseReconciliationTests
{
    private static readonly SpdxLicenseIndex Spdx = new(
        ["MIT", "Apache-2.0", "GPL-2.0-only", "GPL-3.0-only", "Unicode-3.0", "BSL-1.0", "Unlicense"],
        ["LLVM-exception"]);

    // Equivalence classes for the relation between two valid expressions: identical, subset either way,
    // equal sets in a different order, disjoint, overlapping but neither subset, one license named
    // inside a conjunction, one license named inside a nested conjunction, a license absent from a
    // conjunction, two conjunctions that differ, and a disjunct carrying WITH that is present or absent.

    [Test]
    [Arguments("MIT", "MIT", LicenseStatus.Matched)]
    [Arguments("MIT OR Apache-2.0", "Apache-2.0", LicenseStatus.Matched)]
    [Arguments("Apache-2.0", "MIT OR Apache-2.0", LicenseStatus.Matched)]
    [Arguments("MIT OR Apache-2.0", "Apache-2.0 OR MIT", LicenseStatus.Matched)]
    [Arguments("Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT", "Apache-2.0", LicenseStatus.Matched)]
    [Arguments("(MIT OR Apache-2.0) AND Unicode-3.0", "Apache-2.0", LicenseStatus.Matched)]
    [Arguments("Apache-2.0", "(MIT OR Apache-2.0) AND Unicode-3.0", LicenseStatus.Matched)]
    [Arguments("(MIT OR Apache-2.0) AND Unicode-3.0", "Unicode-3.0", LicenseStatus.Matched)]
    [Arguments("MIT AND Unicode-3.0", "Unicode-3.0", LicenseStatus.Matched)]
    [Arguments("MIT", "Apache-2.0", LicenseStatus.Conflict)]
    [Arguments("MIT OR GPL-2.0-only", "MIT OR Apache-2.0", LicenseStatus.Conflict)]
    [Arguments("(MIT OR Apache-2.0) AND Unicode-3.0", "GPL-3.0-only", LicenseStatus.Conflict)]
    [Arguments("MIT AND Unicode-3.0", "MIT AND BSL-1.0", LicenseStatus.Conflict)]
    [Arguments("Apache-2.0 WITH LLVM-exception OR MIT", "Apache-2.0", LicenseStatus.Conflict)]
    public async Task Reconcile_TwoValidExpressions_AgreeWhenNeitherWithdrawsWhatTheOtherStates(string declared, string observed, LicenseStatus expected)
    {
        var component = Reconcile(declared, observed);

        await Assert.That(component.Status).IsEqualTo(expected);
    }

    // A conjunction states terms that all apply. A source naming one of them has observed part of it,
    // not contradicted it: the GitHub License API answers with the single license it detects at the
    // repository root and cannot express `AND` at all. The reconciled value stays the conjunction, so
    // every term it requires is still evaluated by an allow-list.

    [Test]
    [Arguments("(MIT OR Apache-2.0) AND Unicode-3.0", "Apache-2.0", "(MIT OR Apache-2.0) AND Unicode-3.0")]
    [Arguments("Apache-2.0", "(MIT OR Apache-2.0) AND Unicode-3.0", "(MIT OR Apache-2.0) AND Unicode-3.0")]
    [Arguments("MIT AND Unicode-3.0", "MIT", "MIT AND Unicode-3.0")]
    public async Task Reconcile_OneLicenseNamedInsideAConjunction_KeepsEveryTermTheConjunctionRequires(string declared, string observed, string expected)
    {
        var component = Reconcile(declared, observed);

        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task Reconcile_ConjunctionAndItsObservedTerm_ResolvesTheSameWayInEitherEvidenceOrder()
    {
        var forward = Reconcile("(MIT OR Apache-2.0) AND Unicode-3.0", "Apache-2.0");
        var reverse = Reconcile("Apache-2.0", "(MIT OR Apache-2.0) AND Unicode-3.0");

        await Assert.That(forward.License.ToString()).IsEqualTo(reverse.License.ToString());
        await Assert.That(forward.Status).IsEqualTo(reverse.Status);
    }

    // A satisfied disjunction keeps every option the publisher offered. Narrowing to the observed
    // option would drop a choice the evidence never withdrew, and would fail an allow-list that
    // permits only the other option.

    [Test]
    [Arguments("MIT OR Apache-2.0", "Apache-2.0", "MIT OR Apache-2.0")]
    [Arguments("Apache-2.0", "MIT OR Apache-2.0", "MIT OR Apache-2.0")]
    [Arguments("Apache-2.0 OR MIT", "Apache-2.0", "Apache-2.0 OR MIT")]
    [Arguments("Unlicense OR MIT", "Unlicense", "Unlicense OR MIT")]
    [Arguments("MIT", "MIT", "MIT")]
    public async Task Reconcile_SatisfiedDisjunction_ReportsEveryOfferedOption(string declared, string observed, string expected)
    {
        var component = Reconcile(declared, observed);

        await Assert.That(component.License.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task Reconcile_EqualDisjunctSetsInDifferentOrder_ReportsTheFirstObservedSpelling()
    {
        var component = Reconcile("MIT OR Apache-2.0", "Apache-2.0 OR MIT");

        await Assert.That(component.License.ToString()).IsEqualTo("MIT OR Apache-2.0");
    }

    [Test]
    public async Task Reconcile_ThreeCandidatesAllSatisfyingOneDisjunction_ResolvesWithoutConflict()
    {
        var component = Reconcile("MIT OR Apache-2.0", "MIT OR Apache-2.0", "Apache-2.0");

        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Matched);
        await Assert.That(component.License.ToString()).IsEqualTo("MIT OR Apache-2.0");
    }

    [Test]
    public async Task Reconcile_ThreeCandidatesWithOneIncompatible_RemainsConflict()
    {
        var component = Reconcile("MIT OR Apache-2.0", "Apache-2.0", "GPL-3.0-only");

        await Assert.That(component.Status).IsEqualTo(LicenseStatus.Conflict);
    }

    [Test]
    public async Task Reconcile_ConflictingExpressions_ReportsBothClaims()
    {
        var component = Reconcile("MIT", "Apache-2.0");

        await Assert.That(component.License.ToString()).Contains("MIT");
        await Assert.That(component.License.ToString()).Contains("Apache-2.0");
    }

    private static ScanComponent Reconcile(params string[] expressions)
    {
        var component = new ScanComponent(
            "example",
            "1.0.0",
            default,
            "cargo",
            DependencyType.Unknown,
            LicenseStatus.Unknown,
            "pkg:cargo/example@1.0.0",
            default,
            default,
            [],
            []);
        foreach (var expression in expressions)
        {
            component = LicenseReconciler.AddCandidate(
                component,
                LicenseCandidateFactory.Create(
                    LicenseCandidateSource.DependencyInput,
                    LicenseCandidateKind.Expression,
                    Encoding.UTF8.GetBytes(expression),
                    Spdx));
        }

        return component;
    }
}
