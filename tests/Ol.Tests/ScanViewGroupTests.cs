using Ol.Core;
using Ol.Core.Licensing;

namespace Ol.Tests;

/// <summary>
/// Pins what a grouped row is made of, not only how many rows there are.
/// </summary>
/// <remarks>
/// Grouping counts each row before it fills it, so the row's component array is allocated once at its
/// exact size. A row's count and its contents are then produced by two separate passes, and a count that
/// still matches while the wrong components landed in the row is exactly the failure that split makes
/// possible. The CLI-level grouping tests assert counts and row order only.
/// </remarks>
public sealed class ScanViewGroupTests
{
    [Test]
    public async Task Group_MultipleFields_PutsEachComponentInTheRowItsValuesName()
    {
        ScanComponent[] components =
        [
            CreateComponent("a", "MIT", "npm"),
            CreateComponent("b", "MIT", "nuget"),
            CreateComponent("c", "Apache-2.0", "npm"),
            CreateComponent("d", "MIT", "npm"),
            CreateComponent("e", "Apache-2.0", "npm"),
            CreateComponent("f", "MIT", "nuget"),
        ];

        var groups = ScanView.Group(components, "license,ecosystem");

        await Assert.That(groups.Length).IsEqualTo(3);
        await Assert.That(RowNames(groups, "Apache-2.0", "npm")).IsEqualTo("c|e");
        await Assert.That(RowNames(groups, "MIT", "npm")).IsEqualTo("a|d");
        await Assert.That(RowNames(groups, "MIT", "nuget")).IsEqualTo("b|f");
    }

    [Test]
    public async Task Group_EveryComponentAppearsExactlyOnce_AndCountMatchesTheRowContents()
    {
        var components = new ScanComponent[64];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = CreateComponent($"package-{i:D2}", i % 3 == 0 ? "MIT" : "ISC", i % 2 == 0 ? "npm" : "cargo");
        }

        var groups = ScanView.Group(components, "license,ecosystem");

        var total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            await Assert.That(group.Count).IsEqualTo(group.Components.Length);
            total += group.Count;
            foreach (var component in group.Components)
            {
                await Assert.That(seen.Add(component.Name.ToString())).IsTrue();
            }
        }

        await Assert.That(total).IsEqualTo(components.Length);
    }

    [Test]
    public async Task Group_SingleComponent_ProducesOneRowHoldingIt()
    {
        var groups = ScanView.Group([CreateComponent("only", "MIT", "npm")], "license");

        await Assert.That(groups.Length).IsEqualTo(1);
        await Assert.That(groups[0].Count).IsEqualTo(1);
        await Assert.That(groups[0].Components.Length).IsEqualTo(1);
        await Assert.That(groups[0].Components[0].Name.ToString()).IsEqualTo("only");
    }

    [Test]
    public async Task Group_NoComponents_ProducesNoRows()
        => await Assert.That(ScanView.Group([], "license").Length).IsEqualTo(0);

    /// <summary>Renders a row as one ordered string, so an assertion cannot pass on membership alone.</summary>
    private static string RowNames(GroupRow[] groups, string license, string ecosystem)
    {
        foreach (var group in groups)
        {
            if (group.Values[0].ToString() != license || group.Values[1].ToString() != ecosystem)
            {
                continue;
            }

            var names = new string[group.Components.Length];
            for (var i = 0; i < names.Length; i++)
            {
                names[i] = group.Components[i].Name.ToString();
            }

            return string.Join('|', names);
        }

        throw new InvalidOperationException($"No group for {license}/{ecosystem}.");
    }

    private static ScanComponent CreateComponent(string name, string license, string ecosystem)
        => new(name, "1.0.0", license, ecosystem, DependencyType.Direct, LicenseStatus.Matched, default, default, default, []);
}
