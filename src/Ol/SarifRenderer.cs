using System.Buffers;
using System.Text;
using System.Text.Json;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;

namespace Ol.Internals;

/// <summary>
/// Renders policy violations as SARIF so CI can annotate a pull request.
/// </summary>
/// <remarks>
/// Ol consumes resolved graphs, not manifests, so a violation has no trustworthy file position. Rather
/// than invent one, results carry a logical location and the shortest root-to-component dependency path,
/// which is what tells a reviewer which direct dependency to change.
/// </remarks>
internal static class SarifRenderer
{
    private const string SchemaUri = "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json";

    public static byte[] Render(
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<LicensePolicyViolation> violations,
        string toolVersion)
        => Render(inventory, components, violations, [], toolVersion);

    public static byte[] Render(
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<LicensePolicyViolation> violations,
        ReadOnlySpan<int> developmentAllowedComponents,
        string toolVersion,
        ScanReportViewScope view = default)
    {
        var buffer = new ArrayBufferWriter<byte>(512 + (violations.Length * 320) + (developmentAllowedComponents.Length * 160));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema"u8, SchemaUri);
            writer.WriteString("version"u8, "2.1.0");
            writer.WriteStartArray("runs"u8);
            writer.WriteStartObject();

            writer.WriteStartObject("tool"u8);
            writer.WriteStartObject("driver"u8);
            writer.WriteString("name"u8, "ol");
            writer.WriteString("informationUri"u8, "https://github.com/guitarrapc/ol");
            writer.WriteString("version"u8, toolVersion);
            WriteRules(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartArray("results"u8);
            using var rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
            for (var i = 0; i < violations.Length; i++)
            {
                WriteResult(writer, inventory, rootPaths, components, violations[i]);
            }

            writer.WriteEndArray();
            WriteRunProperties(writer, components, developmentAllowedComponents, view);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    // What the run covered rather than what it found. A run has one properties bag, so everything run-level is
    // written from here: a second bag would be a duplicate key that a reader resolves by keeping one of the two,
    // silently dropping the other.
    private static void WriteRunProperties(
        Utf8JsonWriter writer,
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<int> developmentAllowedComponents,
        in ScanReportViewScope view)
    {
        if (developmentAllowedComponents.Length == 0 && !view.IsFiltered)
        {
            return;
        }

        writer.WriteStartObject("properties"u8);
        WriteEvaluatedView(writer, view);
        WriteDevelopmentAllowances(writer, components, developmentAllowedComponents);
        writer.WriteEndObject();
    }

    // A complete result set over a narrowed population reads exactly like a complete one, and a CI job that consumes
    // only the SARIF file has nothing else to learn the difference from.
    private static void WriteEvaluatedView(Utf8JsonWriter writer, in ScanReportViewScope view)
    {
        if (!view.IsFiltered)
        {
            return;
        }

        writer.WriteStartObject("evaluatedView"u8);
        writer.WriteString("dependencyFilter"u8, view.DependencyFilter);
        writer.WriteNumber("excludedCount"u8, view.ExcludedCount);
        writer.WriteNumber("excludedUnknownCount"u8, view.ExcludedUnknownCount);
        writer.WriteEndObject();
    }

    // Components admitted by the development allow-list are not violations, so they are not SARIF results. They are
    // recorded as run-level tool metadata so a reviewer can audit which packages passed only under the development
    // policy, and under which license, without treating them as findings.
    private static void WriteDevelopmentAllowances(Utf8JsonWriter writer, ReadOnlySpan<ScanComponent> components, ReadOnlySpan<int> developmentAllowedComponents)
    {
        if (developmentAllowedComponents.Length == 0)
        {
            return;
        }

        writer.WriteStartArray("developmentPolicyAllowances"u8);
        for (var i = 0; i < developmentAllowedComponents.Length; i++)
        {
            var component = components[developmentAllowedComponents[i]];
            writer.WriteStartObject();
            writer.WriteString("name"u8, component.Name.Span);
            writer.WriteString("version"u8, component.Version.Span);
            writer.WriteString("ecosystem"u8, component.Ecosystem ?? string.Empty);
            if (!component.Purl.IsEmpty) writer.WriteString("purl"u8, component.Purl.Span);
            if (!component.SourceId.IsEmpty) writer.WriteString("sourceId"u8, component.SourceId.Span);
            if (!component.License.IsEmpty) writer.WriteString("license"u8, component.License.Span);
            writer.WriteString("policySource"u8, "allow-dev-licenses");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteRules(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("rules"u8);
        foreach (var kind in Enum.GetValues<LicensePolicyViolationKind>())
        {
            writer.WriteStartObject();
            writer.WriteString("id"u8, RuleId(kind));
            writer.WriteString("name"u8, RuleName(kind));
            writer.WriteStartObject("shortDescription"u8);
            writer.WriteString("text"u8, RuleDescription(kind));
            writer.WriteEndObject();
            writer.WriteStartObject("defaultConfiguration"u8);
            writer.WriteString("level"u8, "error");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteResult(
        Utf8JsonWriter writer,
        in DependencyInventory inventory,
        in DependencyRootPaths rootPaths,
        ReadOnlySpan<ScanComponent> components,
        in LicensePolicyViolation violation)
    {
        var component = components[violation.ComponentIndex];
        var identity = Identity(component);
        var inventoryComponentIndex = rootPaths.FindComponentIndex(component, violation.ComponentIndex);
        var path = rootPaths.GetPath(inventoryComponentIndex);

        writer.WriteStartObject();
        writer.WriteString("ruleId"u8, RuleId(violation.Kind));
        writer.WriteString("level"u8, "error");

        writer.WriteStartObject("message"u8);
        writer.WriteString("text"u8, BuildMessage(component, violation.Kind, inventory, path));
        writer.WriteEndObject();

        writer.WriteStartArray("locations"u8);
        writer.WriteStartObject();
        writer.WriteStartArray("logicalLocations"u8);
        writer.WriteStartObject();
        writer.WriteString("name"u8, component.Name.Span);
        writer.WriteString("fullyQualifiedName"u8, identity);
        writer.WriteString("kind"u8, "package");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();

        writer.WriteStartObject("properties"u8);
        if (!component.Purl.IsEmpty) writer.WriteString("purl"u8, component.Purl.Span);
        writer.WriteString("ecosystem"u8, component.Ecosystem ?? string.Empty);
        writer.WriteString("status"u8, component.Status.ToUtf8());
        if (!component.License.IsEmpty) writer.WriteString("license"u8, component.License.Span);
        writer.WriteString("dependency"u8, DependencyToken(component.DependencyType));
        if (path.Length != 0)
        {
            writer.WriteStartArray("dependencyPath"u8);
            for (var i = 0; i < path.Length; i++)
            {
                writer.WriteStringValue(Identity(inventory.Components[path[i]]));
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string BuildMessage(
        in ScanComponent component,
        LicensePolicyViolationKind kind,
        in DependencyInventory inventory,
        ReadOnlySpan<int> path)
    {
        var builder = new StringBuilder();
        builder.Append(Identity(component));
        builder.Append(": ");
        builder.Append(Reason(kind));
        if (!component.License.IsEmpty && kind == LicensePolicyViolationKind.NotAllowed)
        {
            builder.Append(" (");
            builder.Append(component.License.ToString());
            builder.Append(')');
        }

        // Naming the introducing direct dependency is the actionable part when the violation is transitive.
        if (path.Length > 1)
        {
            builder.Append(". Introduced through ");
            builder.Append(DependencyPathText.Format(inventory, path));
        }

        return builder.ToString();
    }

    private static string Identity(in ScanComponent component) => DependencyPathText.Identity(component);

    private static string DependencyToken(DependencyType value) => value switch
    {
        DependencyType.Root => "root",
        DependencyType.Direct => "direct",
        DependencyType.Transitive => "transitive",
        _ => "unknown",
    };

    private static string RuleId(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "OL0001",
        LicensePolicyViolationKind.Conflict => "OL0002",
        LicensePolicyViolationKind.Unknown => "OL0003",
        LicensePolicyViolationKind.Ambiguous => "OL0004",
        LicensePolicyViolationKind.Invalid => "OL0005",
        _ => "OL0006",
    };

    private static string RuleName(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "LicenseNotAllowed",
        LicensePolicyViolationKind.Conflict => "LicenseEvidenceConflict",
        LicensePolicyViolationKind.Unknown => "LicenseUnresolved",
        LicensePolicyViolationKind.Ambiguous => "LicenseAmbiguous",
        LicensePolicyViolationKind.Invalid => "LicenseExpressionInvalid",
        _ => "LicenseEvidenceError",
    };

    private static string RuleDescription(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "The resolved license is not on the allow-list.",
        LicensePolicyViolationKind.Conflict => "Evidence sources disagree about the license.",
        LicensePolicyViolationKind.Unknown => "No usable license evidence was found.",
        LicensePolicyViolationKind.Ambiguous => "License text exists but cannot be normalized without guessing.",
        LicensePolicyViolationKind.Invalid => "A claimed SPDX expression is invalid.",
        _ => "License evidence could not be collected or processed.",
    };

    private static string Reason(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "license is not allowed",
        LicensePolicyViolationKind.Conflict => "license evidence conflicts",
        LicensePolicyViolationKind.Unknown => "license is unresolved",
        LicensePolicyViolationKind.Ambiguous => "license is ambiguous",
        LicensePolicyViolationKind.Invalid => "license expression is invalid",
        _ => "license evidence could not be completed",
    };
}
