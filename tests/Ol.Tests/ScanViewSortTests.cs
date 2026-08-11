using Ol.Core;
using Ol.Core.Licensing;

namespace Ol.Tests;

/// <summary>
/// Pins the order a view produces, and that the parallel usage array follows the components.
/// </summary>
/// <remarks>
/// The view is ordered by sorting component positions and then applying that order to the arrays, so the
/// comparison and the movement are separate steps. An order applied to the components but not to the
/// usages leaves every component labelled with another component's reachability, which no assertion on
/// the components alone can see. The CLI tests cover one ascending two-key sort.
/// </remarks>
public sealed class ScanViewSortTests
{
    [Test]
    public async Task Apply_MultipleKeys_OrdersByEachKeyInTurn()
    {
        ScanComponent[] components =
        [
            CreateComponent("b", "2.0.0", "npm"),
            CreateComponent("a", "2.0.0", "npm"),
            CreateComponent("b", "1.0.0", "npm"),
            CreateComponent("a", "1.0.0", "cargo"),
        ];

        var count = ScanView.Apply(components, dependency: null, "ECOSYSTEM,NAME,VERSION", SortOrder.Asc);

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(Describe(components, count)).IsEqualTo("cargo/a/1.0.0|npm/a/2.0.0|npm/b/1.0.0|npm/b/2.0.0");
    }

    [Test]
    public async Task Apply_Descending_ReversesEveryKey()
    {
        ScanComponent[] components =
        [
            CreateComponent("a", "1.0.0", "npm"),
            CreateComponent("c", "1.0.0", "npm"),
            CreateComponent("b", "1.0.0", "npm"),
        ];

        ScanView.Apply(components, dependency: null, "NAME", SortOrder.Desc);

        await Assert.That(Describe(components, 3)).IsEqualTo("npm/c/1.0.0|npm/b/1.0.0|npm/a/1.0.0");
    }

    [Test]
    public async Task Apply_EqualKeys_KeepsInputOrder()
    {
        // Every component sorts equal on the requested key, so only input order can decide the result.
        var components = new ScanComponent[16];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = CreateComponent("same", $"{i:D2}", "npm");
        }

        ScanView.Apply(components, dependency: null, "NAME", SortOrder.Asc);

        for (var i = 0; i < components.Length; i++)
        {
            await Assert.That(components[i].Version.ToString()).IsEqualTo($"{i:D2}");
        }
    }

    [Test]
    public async Task Apply_WithUsages_MovesEachUsageWithItsComponent()
    {
        ScanComponent[] components =
        [
            CreateComponent("d", "1.0.0", "npm"),
            CreateComponent("b", "1.0.0", "npm"),
            CreateComponent("a", "1.0.0", "npm"),
            CreateComponent("c", "1.0.0", "npm"),
        ];
        DependencyUsage[] usages =
        [
            DependencyUsage.Development,
            DependencyUsage.Runtime,
            DependencyUsage.Unknown,
            DependencyUsage.Runtime,
        ];

        var count = ScanView.Apply(components, usages, dependency: null, "NAME", SortOrder.Asc);

        await Assert.That(count).IsEqualTo(4);
        await Assert.That(Describe(components, count)).IsEqualTo("npm/a/1.0.0|npm/b/1.0.0|npm/c/1.0.0|npm/d/1.0.0");
        // a, b, c, d in the sorted order, each carrying the usage its component arrived with.
        await Assert.That(string.Join('|', usages[..count])).IsEqualTo("Unknown|Runtime|Runtime|Development");
    }

    [Test]
    public async Task Apply_SingleComponent_LeavesItInPlace()
    {
        ScanComponent[] components = [CreateComponent("only", "1.0.0", "npm")];

        await Assert.That(ScanView.Apply(components, dependency: null, "NAME", SortOrder.Asc)).IsEqualTo(1);
        await Assert.That(components[0].Name.ToString()).IsEqualTo("only");
    }

    /// <summary>Renders the view as one ordered string, so an assertion cannot pass on membership alone.</summary>
    private static string Describe(ScanComponent[] components, int count)
    {
        var described = new string[count];
        for (var i = 0; i < count; i++)
        {
            described[i] = $"{components[i].Ecosystem}/{components[i].Name}/{components[i].Version}";
        }

        return string.Join('|', described);
    }

    private static ScanComponent CreateComponent(string name, string version, string ecosystem)
        => new(name, version, "MIT", ecosystem, DependencyType.Direct, LicenseStatus.Matched, default, default, default, []);
}
