using System.Buffers;
using System.Numerics;
using System.Buffers.Text;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Reporting;
using Ol.Internals;

/// <summary>What the run did and what the rendered view left out, as the canonical JSON report states it.</summary>
/// <remarks>
/// The text and Markdown views state both facts in the stderr summary, and the JSON view emits no stderr
/// summary because the document is supposed to carry them instead. Neither is derivable from the counters:
/// a run that never collected external evidence and a run that collected and found nothing to do produce
/// the same zeroed metadata, and a filtered report is indistinguishable from a complete one once the
/// excluded components are gone. Read as a complete report, a filtered one is a false pass.
/// </remarks>
/// <param name="ExternalEvidenceCollected">Whether package registries and source repositories were read at all.</param>
/// <param name="DependencyFilter">The <c>--dependency</c> filter applied to the view, or null when unfiltered.</param>
/// <param name="ExcludedCount">Components the filter removed from the view.</param>
/// <param name="ExcludedUnknownCount">Removed components whose dependency relationship is unknown.</param>
/// <param name="ExcludedInputPaths">Logical paths omitted from directory input discovery.</param>
/// <param name="Discovery">What input discovery found, ignored, and skipped.</param>
internal readonly record struct ScanReportScope(
    bool ExternalEvidenceCollected,
    string? DependencyFilter,
    int ExcludedCount,
    int ExcludedUnknownCount,
    string[]? ExcludedInputPaths = null,
    ScanInputDiscovery Discovery = default);

/// <summary>What input discovery observed, as distinct from what the invocation asked it to exclude.</summary>
/// <remarks>
/// These are the facts a reader needs to tell a scan that read every input from one that skipped an ecosystem,
/// and no counter elsewhere in the report implies them: an ignored candidate and a skipped companion set both
/// leave the report smaller without leaving any trace in the components it does contain. They lived only in the
/// stderr summary, which <c>--format json</c> does not write, so the recommended CI path produced a document
/// that could not state whether it was complete.
/// </remarks>
/// <param name="DetectedFileCount">Physical input files discovery detected, including ones it then skipped.</param>
/// <param name="IgnoredCandidates">
/// Known dependency inputs Ol cannot consume, named by the directory pattern that detected them. The values are
/// a closed vocabulary Ol owns rather than anything read from the file system, so the field carries no path.
/// </param>
/// <param name="IncompleteInputSetCount">Companion sets discovery found incomplete and therefore skipped.</param>
internal readonly record struct ScanInputDiscovery(
    int DetectedFileCount,
    string[]? IgnoredCandidates,
    int IncompleteInputSetCount);

internal static class ReportRenderer
{
    private const int JsonSchemaVersion = 1;
    private const string ToolName = "ol";
    private const string ToolInformationUri = "https://github.com/guitarrapc/ol";
    private static readonly string ToolVersion =
        typeof(ReportRenderer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReportRenderer).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>The identifier and sentence that state an input contributed no dependency inventory.</summary>
    /// <remarks>
    /// A recognized input that resolves nothing produces a report where every count is zero, and a
    /// zero-violation policy result follows from it. Read as a pass, that is the one false negative a
    /// compliance gate cannot recover from, and the causes are ordinary: an unrestored project, an obj
    /// directory from a different build, an SBOM generated before install. So it is stated in the primary
    /// result, which <c>--quiet</c> does not suppress, and retained in the JSON report's warnings. It is
    /// not a command failure: the input was read and the report is complete, and only the reader knows
    /// whether "no dependencies" is the expected answer.
    /// </remarks>
    private const string EmptyInventoryWarning = "input_declares_no_components";
    private static ReadOnlySpan<byte> EmptyInventoryHeadingUtf8 => "No components"u8;
    private static ReadOnlySpan<byte> EmptyInventorySentenceUtf8 => "The input declares no resolved dependencies, so this report states nothing about licenses."u8;

    public static void WriteText(
        IBufferWriter<byte> writer,
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        bool verbose,
        bool emptyInventory = false)
    {
        WriteInputHeader(writer, inventory.Input);
        Span<int> widths = stackalloc int[verbose ? 8 : 7];
        widths[0] = "NAME"u8.Length;
        widths[1] = "VERSION"u8.Length;
        widths[2] = "LICENSE"u8.Length;
        widths[3] = "ECOSYSTEM"u8.Length;
        widths[4] = "DEPENDENCY"u8.Length;
        widths[5] = "STATUS"u8.Length;
        widths[6] = "SUPPLIED"u8.Length;
        if (verbose) widths[7] = "PURL"u8.Length;

        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            TextTable.Include(ref widths[0], Display(component.Name));
            TextTable.Include(ref widths[1], Display(component.Version));
            TextTable.Include(ref widths[2], Display(component.License));
            TextTable.Include(ref widths[3], component.Ecosystem);
            TextTable.Include(ref widths[4], GetDependencyTypeUtf8(component.DependencyType));
            TextTable.Include(ref widths[5], component.Status.ToUtf8());
            TextTable.Include(ref widths[6], GetSuppliedByUtf8(component.SuppliedBy));
            if (verbose) TextTable.Include(ref widths[7], Display(component.Purl));
        }

        TextTable.WriteCell(writer, "NAME"u8, widths[0]);
        TextTable.WriteCell(writer, "VERSION"u8, widths[1]);
        TextTable.WriteCell(writer, "LICENSE"u8, widths[2]);
        TextTable.WriteCell(writer, "ECOSYSTEM"u8, widths[3]);
        TextTable.WriteCell(writer, "DEPENDENCY"u8, widths[4]);
        TextTable.WriteCell(writer, "STATUS"u8, widths[5]);
        TextTable.WriteCell(writer, "SUPPLIED"u8, widths[6], last: !verbose);
        if (verbose) TextTable.WriteCell(writer, "PURL"u8, widths[7], last: true);
        TextTable.WriteNewLine(writer);
        TextTable.WriteSeparator(writer, widths);
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            TextTable.WriteCell(writer, Display(component.Name), widths[0]);
            TextTable.WriteCell(writer, Display(component.Version), widths[1]);
            TextTable.WriteCell(writer, Display(component.License), widths[2]);
            TextTable.WriteCell(writer, component.Ecosystem, widths[3]);
            TextTable.WriteCell(writer, GetDependencyTypeUtf8(component.DependencyType), widths[4]);
            TextTable.WriteCell(writer, component.Status.ToUtf8(), widths[5]);
            TextTable.WriteCell(writer, GetSuppliedByUtf8(component.SuppliedBy), widths[6], last: !verbose);
            if (verbose) TextTable.WriteCell(writer, Display(component.Purl), widths[7], last: true);
            TextTable.WriteNewLine(writer);
        }

        WriteEmptyInventoryText(writer, emptyInventory);
        WriteUnresolvedText(writer, inventory, components);
    }

    private static void WriteEmptyInventoryText(IBufferWriter<byte> writer, bool emptyInventory)
    {
        if (!emptyInventory)
        {
            return;
        }

        WriteNewLine(writer);
        WriteUtf8(writer, EmptyInventoryHeadingUtf8);
        WriteNewLine(writer);
        WriteUtf8(writer, "  "u8);
        WriteUtf8(writer, EmptyInventorySentenceUtf8);
        WriteNewLine(writer);
    }

    /// <summary>
    /// Explains every displayed component the scan did not resolve to one license.
    /// </summary>
    /// <remarks>
    /// The table alone cannot answer "why", and the answer decides what a reviewer does next: wait for
    /// Ol, open a document, or ask the publisher. The reason uses the same identifiers as the JSON
    /// report so one vocabulary describes both, and the reference is a location Ol actually observed
    /// rather than one it constructed. The section is omitted when there is nothing to explain.
    /// </remarks>
    private static void WriteUnresolvedText(IBufferWriter<byte> writer, in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components)
    {
        var unresolvedCount = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (!IsExplainedElsewhere(components[i]) && UnresolvedMechanism.TryGetReason(components[i], out _)) unresolvedCount++;
        }

        if (unresolvedCount == 0) return;

        WriteNewLine(writer);
        WriteUtf8(writer, "Unresolved components"u8);
        WriteNewLine(writer);

        Span<int> widths = stackalloc int[]
        {
            "NAME"u8.Length,
            "VERSION"u8.Length,
            "REASON"u8.Length,
            "REFERENCE"u8.Length,
            "PATH"u8.Length,
        };
        // The reference and the path are built strings, so the width pass keeps what it derived and the
        // write pass replays it. Resolve first, so nothing sits between the rental and its try.
        using var rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
        var rows = ArrayPool<UnresolvedRow>.Shared.Rent(unresolvedCount);
        try
        {
            var count = 0;
            for (var i = 0; i < components.Length; i++)
            {
                ref readonly var component = ref components[i];
                if (IsExplainedElsewhere(component) || !UnresolvedMechanism.TryGetReason(component, out var reason)) continue;
                var reference = UnresolvedMechanism.GetReference(component, reason);
                var path = DependencyPathText.Introducer(inventory, rootPaths, component, i);
                rows[count++] = new UnresolvedRow(i, reason, reference, path);
                TextTable.Include(ref widths[0], Display(component.Name));
                TextTable.Include(ref widths[1], Display(component.Version));
                TextTable.Include(ref widths[2], UnresolvedMechanism.GetNameUtf8(reason));
                TextTable.Include(ref widths[3], reference);
                TextTable.Include(ref widths[4], path);
            }

            TextTable.WriteCell(writer, "NAME"u8, widths[0]);
            TextTable.WriteCell(writer, "VERSION"u8, widths[1]);
            TextTable.WriteCell(writer, "REASON"u8, widths[2]);
            TextTable.WriteCell(writer, "REFERENCE"u8, widths[3]);
            TextTable.WriteCell(writer, "PATH"u8, widths[4], last: true);
            TextTable.WriteNewLine(writer);
            TextTable.WriteSeparator(writer, widths);
            for (var i = 0; i < count; i++)
            {
                var row = rows[i];
                ref readonly var component = ref components[row.ComponentIndex];
                TextTable.WriteCell(writer, Display(component.Name), widths[0]);
                TextTable.WriteCell(writer, Display(component.Version), widths[1]);
                TextTable.WriteCell(writer, UnresolvedMechanism.GetNameUtf8(row.Reason), widths[2]);
                TextTable.WriteCell(writer, row.Reference, widths[3]);
                TextTable.WriteCell(writer, row.Path, widths[4], last: true);
                TextTable.WriteNewLine(writer);
            }
        }
        finally
        {
            ArrayPool<UnresolvedRow>.Shared.Return(rows, clearArray: true);
        }
    }

    /// <summary>One unresolved row's derived text, kept between the width pass and the write pass.</summary>
    private readonly record struct UnresolvedRow(int ComponentIndex, UnresolvedMechanismKind Reason, string Reference, string Path);

    /// <summary>
    /// Reports whether a component needs no entry in the unresolved section.
    /// </summary>
    /// <remarks>
    /// A resolved component has nothing to explain. Neither does a root: it is the subject of the scan rather than a
    /// dependency of it, and <see href="cli.md#contract-policy-checks">policy evaluates all non-root components</see>,
    /// so an entry would ask a reviewer for work that no check will ever require. It stays in the table, because the
    /// report must not stop saying what the input described.
    /// </remarks>
    private static bool IsExplainedElsewhere(in ScanComponent component)
        => component.Status == LicenseStatus.Matched || component.DependencyType == DependencyType.Root;

    /// <summary>
    /// Writes the Markdown report as UTF-8, the encoding it is read in.
    /// </summary>
    /// <remarks>
    /// The report used to be assembled as text and handed over as one string, which cost the document
    /// twice — once in the builder's chunks and once in the string it produced — and decoded every
    /// source-backed value on the way in only for the encoder to undo it on the way out. Written as bytes
    /// the values are copied, not translated, and nothing holds the document but the output buffer.
    /// </remarks>
    public static void WriteMarkdown(IBufferWriter<byte> writer, in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, bool verbose, bool emptyInventory = false)
    {
        WriteUtf8(writer, verbose
            ? "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | SUPPLIED | PURL |"u8
            : "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | SUPPLIED |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, verbose ? "|---|---|---|---|---|---|---|---|"u8 : "|---|---|---|---|---|---|---|"u8);
        WriteNewLine(writer);
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            WriteUtf8(writer, "| "u8);
            WriteMarkdownValue(writer, component.Name);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Version);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.License);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Ecosystem);
            WriteUtf8(writer, " | "u8);
            WriteUtf8(writer, GetDependencyTypeUtf8(component.DependencyType));
            WriteUtf8(writer, " | "u8);
            WriteUtf8(writer, component.Status.ToUtf8());
            WriteUtf8(writer, " | "u8);
            WriteUtf8(writer, GetSuppliedByUtf8(component.SuppliedBy));
            if (verbose)
            {
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, component.Purl);
            }

            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
        }

        WriteEmptyInventoryMarkdown(writer, emptyInventory);
        WriteUnresolvedMarkdown(writer, inventory, components);
    }

    /// <summary>Writes the report's input line. The text view states the same thing without the code span.</summary>
    public static void WriteMarkdownInputHeader(IBufferWriter<byte> writer, ScanInputDescriptor input)
    {
        WriteUtf8(writer, "Input: `"u8);
        WriteUtf8(writer, input.Kind.Name);
        WriteUtf8(writer, "/"u8);
        WriteUtf8(writer, input.Format.Name);
        WriteUtf8(writer, "`"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
    }

    /// <summary>Renders the same statement as the text report. See <see cref="EmptyInventoryWarning"/>.</summary>
    private static void WriteEmptyInventoryMarkdown(IBufferWriter<byte> writer, bool emptyInventory)
    {
        if (!emptyInventory)
        {
            return;
        }

        WriteNewLine(writer);
        WriteUtf8(writer, "## "u8);
        WriteUtf8(writer, EmptyInventoryHeadingUtf8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteUtf8(writer, EmptyInventorySentenceUtf8);
        WriteNewLine(writer);
    }

    /// <summary>Renders the same explanation as the text report. See <see cref="WriteUnresolvedText"/>.</summary>
    private static void WriteUnresolvedMarkdown(IBufferWriter<byte> writer, in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components)
    {
        var first = true;
        var rootPaths = default(DependencyRootPaths);
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (IsExplainedElsewhere(component) || !UnresolvedMechanism.TryGetReason(component, out var reason))
                {
                    continue;
                }

                if (first)
                {
                    WriteNewLine(writer);
                    WriteUtf8(writer, "## Unresolved components"u8);
                    WriteNewLine(writer);
                    WriteNewLine(writer);
                    WriteUtf8(writer, "| NAME | VERSION | REASON | REFERENCE | PATH |"u8);
                    WriteNewLine(writer);
                    WriteUtf8(writer, "|---|---|---|---|---|"u8);
                    WriteNewLine(writer);
                    rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
                    first = false;
                }

                WriteUtf8(writer, "| "u8);
                WriteMarkdownValue(writer, component.Name);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, component.Version);
                WriteUtf8(writer, " | "u8);
                WriteUtf8(writer, UnresolvedMechanism.GetNameUtf8(reason));
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, UnresolvedMechanism.GetReference(component, reason));
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, DependencyPathText.Introducer(inventory, rootPaths, component, i));
                WriteUtf8(writer, " |"u8);
                WriteNewLine(writer);
            }
        }
        finally
        {
            rootPaths.Dispose();
        }
    }

    public static void WriteText(
        IBufferWriter<byte> writer,
        ScanInputDescriptor input,
        ReadOnlySpan<GroupRow> groups,
        string groupBy,
        bool emptyInventory = false)
    {
        WriteInputHeader(writer, input);
        var headerCount = GetGroupFieldCount(groupBy);
        Span<int> widths = stackalloc int[headerCount + 1];
        for (var i = 0; i < headerCount; i++)
        {
            widths[i] = GetGroupHeaderUtf8(groupBy, i).Length;
        }

        widths[headerCount] = "COUNT"u8.Length;
        for (var i = 0; i < groups.Length; i++)
        {
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                TextTable.Include(ref widths[valueIndex], Display(groups[i].Values[valueIndex]));
            }

            TextTable.Include(ref widths[headerCount], groups[i].Count);
        }

        for (var i = 0; i < headerCount; i++)
        {
            TextTable.WriteCell(writer, GetGroupHeaderUtf8(groupBy, i), widths[i]);
        }

        TextTable.WriteCell(writer, "COUNT"u8, widths[headerCount], last: true);
        TextTable.WriteNewLine(writer);
        TextTable.WriteSeparator(writer, widths);
        for (var i = 0; i < groups.Length; i++)
        {
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                TextTable.WriteCell(writer, Display(groups[i].Values[valueIndex]), widths[valueIndex]);
            }

            TextTable.WriteCell(writer, groups[i].Count, widths[headerCount], last: true);
            TextTable.WriteNewLine(writer);
        }

        WriteEmptyInventoryText(writer, emptyInventory);
    }

    /// <summary>
    /// Writes the grouped Markdown report. Headers come from the same table the text view reads, so the
    /// two views cannot disagree about what a column is called.
    /// </summary>
    public static void WriteMarkdown(IBufferWriter<byte> writer, ReadOnlySpan<GroupRow> groups, string groupBy, bool emptyInventory = false)
    {
        var headerCount = GetGroupFieldCount(groupBy);
        WriteUtf8(writer, "| "u8);
        for (var i = 0; i < headerCount; i++)
        {
            if (i != 0)
            {
                WriteUtf8(writer, " | "u8);
            }

            WriteUtf8(writer, GetGroupHeaderUtf8(groupBy, i));
        }

        WriteUtf8(writer, " | COUNT |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|"u8);
        for (var i = 0; i < headerCount + 1; i++)
        {
            WriteUtf8(writer, "---|"u8);
        }

        WriteNewLine(writer);
        for (var i = 0; i < groups.Length; i++)
        {
            WriteUtf8(writer, "| "u8);
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                WriteMarkdownValue(writer, groups[i].Values[valueIndex]);
                WriteUtf8(writer, " | "u8);
            }

            WriteCount(writer, groups[i].Count);
            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
        }

        WriteEmptyInventoryMarkdown(writer, emptyInventory);
    }

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, SpdxData spdx, PackageArtifactCollectionSummary packageArtifactSummary, DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
        => WriteJson(writer, inventory, components, default, spdx, packageArtifactSummary, declaredGitHubFileSummary, metadataSummary, sourceSummary, scope);

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, ReadOnlySpan<DependencyUsage> componentUsages, SpdxData spdx, PackageArtifactCollectionSummary packageArtifactSummary, DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", JsonSchemaVersion);
        writer.WriteStartObject("metadata");
        WriteToolMetadata(writer);
        WriteInputMetadata(writer, inventory.Input);
        WriteSpdxMetadata(writer, spdx);
        WritePackageArtifactMetadata(writer, packageArtifactSummary);
        WriteDeclaredGitHubFileMetadata(writer, declaredGitHubFileSummary);
        WritePackageMetadata(writer, metadataSummary);
        WriteSourceRepositoryMetadata(writer, sourceSummary);
        WriteScopeMetadata(writer, scope);
        writer.WriteEndObject();

        WriteInventory(writer, inventory);

        writer.WriteStartArray("components");
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            writer.WriteStartObject();
            writer.WriteString("name"u8, component.Name.Span);
            writer.WriteString("version"u8, component.Version.Span);
            writer.WriteString("license"u8, component.License.IsEmpty ? "-"u8 : component.License.Span);
            writer.WriteString("ecosystem", component.Ecosystem);
            writer.WriteString("dependency"u8, GetDependencyTypeUtf8(component.DependencyType));
            writer.WriteString("status"u8, component.Status.ToUtf8());
            writer.WriteString("purl"u8, component.Purl.Span);
            writer.WriteString("sourceId"u8, component.SourceId.Span);
            WriteSuppliedBy(writer, component.SuppliedBy);
            if (i < componentUsages.Length && componentUsages[i] != DependencyUsage.Unknown)
            {
                writer.WriteString("usage"u8, componentUsages[i] == DependencyUsage.Development ? "development"u8 : "runtime"u8);
            }

            WriteLicenseCandidates(writer, component);
            WriteCandidateWarnings(writer, component.Warnings);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteSummary(writer, ScanSummary.Create(components));
        WriteWarnings(writer, components, inventory.Components.Length == 0);
        writer.WriteEndObject();
    }

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, GroupRow[] groups, string groupBy, SpdxData spdx, PackageArtifactCollectionSummary packageArtifactSummary, DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", JsonSchemaVersion);
        writer.WriteStartObject("metadata");
        WriteToolMetadata(writer);
        WriteInputMetadata(writer, inventory.Input);
        WriteSpdxMetadata(writer, spdx);
        WritePackageArtifactMetadata(writer, packageArtifactSummary);
        WriteDeclaredGitHubFileMetadata(writer, declaredGitHubFileSummary);
        WritePackageMetadata(writer, metadataSummary);
        WriteSourceRepositoryMetadata(writer, sourceSummary);
        WriteScopeMetadata(writer, scope);
        writer.WriteEndObject();

        WriteInventory(writer, inventory);

        writer.WriteStartArray("groups");
        for (var i = 0; i < groups.Length; i++)
        {
            writer.WriteStartObject();
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                writer.WriteString(GetGroupPropertyNameUtf8(groupBy, valueIndex), groups[i].Values[valueIndex].Span);
            }

            writer.WriteNumber("count", groups[i].Count);
            writer.WriteStartArray("components");
            var rowComponents = groups[i].Components.Span;
            for (var componentIndex = 0; componentIndex < rowComponents.Length; componentIndex++)
            {
                var component = rowComponents[componentIndex];
                writer.WriteStartObject();
                writer.WriteString("name"u8, component.Name.Span);
                writer.WriteString("version"u8, component.Version.Span);
                writer.WriteString("ecosystem", component.Ecosystem);
                writer.WriteString("purl"u8, component.Purl.Span);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteSummary(writer, ScanSummary.Create(groups));
        WriteWarnings(writer, groups, inventory.Components.Length == 0);
        writer.WriteEndObject();
    }

    private static void WriteToolMetadata(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("tool");
        writer.WriteString("name", ToolName);
        writer.WriteString("version", ToolVersion);
        writer.WriteString("informationUri", ToolInformationUri);
        writer.WriteEndObject();
    }

    private static void WriteInputHeader(IBufferWriter<byte> writer, ScanInputDescriptor input)
    {
        WriteUtf8(writer, "Input: "u8);
        WriteUtf8(writer, input.Kind.Name);
        WriteUtf8(writer, "/"u8);
        WriteUtf8(writer, input.Format.Name);
        WriteNewLine(writer);
        WriteNewLine(writer);
    }

    private static void WriteDisplay(IBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
        {
            WriteUtf8(writer, "-"u8);
        }
        else
        {
            WriteUtf8(writer, value);
        }
    }

    private static void WriteDisplay(IBufferWriter<byte> writer, Utf8Slice value)
    {
        WriteUtf8(writer, value.IsEmpty ? "-"u8 : value.Span);
    }

    private static ReadOnlySpan<byte> Display(Utf8Slice value)
        => value.IsEmpty ? "-"u8 : value.Span;

    private static void WriteNewLine(IBufferWriter<byte> writer)
        => WriteUtf8(writer, Environment.NewLine);

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string value) => WriteUtf8(writer, value.AsSpan());

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
    }

    private static int GetGroupFieldCount(string groupBy)
    {
        var value = groupBy.AsSpan();
        var count = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != ',')
            {
                continue;
            }

            if (!TrimAsciiWhitespace(value[start..i]).IsEmpty)
            {
                count++;
            }

            start = i + 1;
        }

        return count;
    }

    private static ReadOnlySpan<byte> GetGroupHeaderUtf8(string groupBy, int targetIndex)
    {
        var propertyName = GetGroupPropertyNameUtf8(groupBy, targetIndex);
        if (propertyName.SequenceEqual("name"u8)) return "NAME"u8;
        if (propertyName.SequenceEqual("version"u8)) return "VERSION"u8;
        if (propertyName.SequenceEqual("license"u8)) return "LICENSE"u8;
        if (propertyName.SequenceEqual("ecosystem"u8)) return "ECOSYSTEM"u8;
        if (propertyName.SequenceEqual("dependency"u8)) return "DEPENDENCY"u8;
        if (propertyName.SequenceEqual("status"u8)) return "STATUS"u8;
        throw new ArgumentOutOfRangeException(nameof(targetIndex));
    }

    /// <summary>
    /// Writes a source-backed value into a table cell without decoding it.
    /// </summary>
    /// <remarks>
    /// The only character a cell has to escape is the one that would end it, and in UTF-8 that byte never
    /// occurs inside a multi-byte sequence, so the scan is safe on bytes and the value is copied rather
    /// than translated.
    /// </remarks>
    private static void WriteMarkdownValue(IBufferWriter<byte> writer, Utf8Slice value)
    {
        var remaining = value.Span;
        if (remaining.IsEmpty)
        {
            WriteUtf8(writer, "-"u8);
            return;
        }

        while (true)
        {
            var index = remaining.IndexOf((byte)'|');
            if (index < 0)
            {
                WriteUtf8(writer, remaining);
                return;
            }

            WriteUtf8(writer, remaining[..index]);
            WriteUtf8(writer, "\\|"u8);
            remaining = remaining[(index + 1)..];
        }
    }

    /// <summary>Writes a value the report owns as text, such as an ecosystem name or a resolved path.</summary>
    private static void WriteMarkdownValue(IBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
        {
            WriteUtf8(writer, "-"u8);
            return;
        }

        var remaining = value.AsSpan();
        while (true)
        {
            var index = remaining.IndexOf('|');
            if (index < 0)
            {
                WriteUtf8(writer, remaining);
                return;
            }

            WriteUtf8(writer, remaining[..index]);
            WriteUtf8(writer, "\\|"u8);
            remaining = remaining[(index + 1)..];
        }
    }

    private static void WriteCount(IBufferWriter<byte> writer, int count)
    {
        var destination = writer.GetSpan(11);
        if (!Utf8Formatter.TryFormat(count, destination, out var bytesWritten))
        {
            throw new InvalidOperationException("Unable to format group count.");
        }

        writer.Advance(bytesWritten);
    }


    private static ReadOnlySpan<byte> GetDependencyTypeUtf8(DependencyType value) => value switch
    {
        DependencyType.Unknown => "unknown"u8,
        DependencyType.Root => "root"u8,
        DependencyType.Direct => "direct"u8,
        DependencyType.Transitive => "transitive"u8,
        _ => default,
    };

    // Human output keeps the same tokens the canonical JSON uses so one vocabulary describes both views.
    private static ReadOnlySpan<byte> GetSuppliedByUtf8(ComponentSupply value) => value switch
    {
        ComponentSupply.Sbom => "sbom"u8,
        ComponentSupply.PackageManager => "package-manager"u8,
        ComponentSupply.Sbom | ComponentSupply.PackageManager => "sbom,package-manager"u8,
        _ => "-"u8,
    };

    private static ReadOnlySpan<byte> GetGroupPropertyNameUtf8(string groupBy, int targetIndex)
    {
        var value = groupBy.AsSpan();
        var fieldIndex = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != ',')
            {
                continue;
            }

            var field = TrimAsciiWhitespace(value[start..i]);
            start = i + 1;
            if (field.IsEmpty)
            {
                continue;
            }

            if (fieldIndex++ != targetIndex)
            {
                continue;
            }

            if (field.Equals("name", StringComparison.OrdinalIgnoreCase)) return "name"u8;
            if (field.Equals("version", StringComparison.OrdinalIgnoreCase)) return "version"u8;
            if (field.Equals("license", StringComparison.OrdinalIgnoreCase)) return "license"u8;
            if (field.Equals("ecosystem", StringComparison.OrdinalIgnoreCase)) return "ecosystem"u8;
            if (field.Equals("dependency", StringComparison.OrdinalIgnoreCase)) return "dependency"u8;
            if (field.Equals("status", StringComparison.OrdinalIgnoreCase)) return "status"u8;
            break;
        }

        throw new ArgumentOutOfRangeException(nameof(targetIndex));
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is ' ' or '\t' or '\r' or '\n') start++;
        var end = value.Length;
        while (end > start && value[end - 1] is ' ' or '\t' or '\r' or '\n') end--;
        return value[start..end];
    }

    private static void WriteSpdxMetadata(Utf8JsonWriter writer, SpdxData spdx)
    {
        writer.WriteStartObject("spdx");
        writer.WriteString("source", spdx.Source);
        writer.WriteString("licenseListVersion", spdx.LicenseListVersion);
        writer.WriteString("dataRef", spdx.DataRef);
        writer.WriteString("licensesSha256", spdx.GetLicensesSha256());
        writer.WriteString("exceptionsSha256", spdx.GetExceptionsSha256());
        writer.WriteEndObject();
    }

    private static void WritePackageMetadata(Utf8JsonWriter writer, PackageMetadataSummary summary)
    {
        writer.WriteStartObject("packageMetadata");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("supportedComponentCount", summary.SupportedComponentCount);
        writer.WriteNumber("cacheHitCount", summary.CacheHitCount);
        writer.WriteNumber("cacheMissCount", summary.CacheMissCount);
        writer.WriteNumber("refreshedCount", summary.RefreshedCount);
        writer.WriteNumber("fetchErrorCount", summary.FetchErrorCount);
        writer.WriteNumber("unsupportedEcosystemCount", summary.UnsupportedEcosystemCount);
        writer.WriteNumber("unversionedPurlCount", summary.UnversionedPurlCount);
        writer.WriteNumber("noPurlCount", summary.NoPurlCount);
        writer.WriteNumber("concurrency", summary.Concurrency);
        writer.WriteNumber("retryCount", summary.RetryCount);
        writer.WriteEndObject();
    }

    private static void WritePackageArtifactMetadata(Utf8JsonWriter writer, PackageArtifactCollectionSummary summary)
    {
        writer.WriteStartObject("packageArtifacts");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("documentCount", summary.DocumentCount);
        writer.WriteNumber("matchedCount", summary.MatchedCount);
        writer.WriteEndObject();
    }

    private static void WriteDeclaredGitHubFileMetadata(Utf8JsonWriter writer, DeclaredGitHubFileArtifactCollectionSummary summary)
    {
        writer.WriteStartObject("declaredGitHubFiles");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("githubRequestCount", summary.GitHubRequestCount);
        writer.WriteNumber("cacheHitCount", summary.CacheHitCount);
        writer.WriteNumber("cacheMissCount", summary.CacheMissCount);
        writer.WriteNumber("documentCount", summary.DocumentCount);
        writer.WriteNumber("matchedCount", summary.MatchedCount);
        writer.WriteNumber("fetchErrorCount", summary.FetchErrorCount);
        writer.WriteEndObject();
    }

    private static void WriteSourceRepositoryMetadata(Utf8JsonWriter writer, SourceRepositorySummary summary)
    {
        writer.WriteStartObject("sourceRepository");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("githubLicenseRequestCount", summary.GitHubRequestCount);
        writer.WriteNumber("cacheHitCount", summary.CacheHitCount);
        writer.WriteNumber("cacheMissCount", summary.CacheMissCount);
        writer.WriteNumber("fetchErrorCount", summary.FetchErrorCount);
        writer.WriteNumber("unknownCount", summary.UnknownCount);
        writer.WriteEndObject();
        writer.WriteStartObject("network");
        writer.WriteString("githubAuth", summary.AuthMode);
        writer.WriteEndObject();
    }

    // Stated rather than implied, because the counters above are zero both when collection was disabled and
    // when it was enabled with nothing to do, and because a filtered view is otherwise indistinguishable
    // from a complete report once the excluded components are gone.
    private static void WriteScopeMetadata(Utf8JsonWriter writer, ScanReportScope scope)
    {
        writer.WriteStartObject("collection");
        writer.WriteString("externalEvidence", scope.ExternalEvidenceCollected ? "collected" : "not-collected");
        writer.WriteEndObject();
        writer.WriteStartObject("view");
        if (scope.DependencyFilter is { } dependencyFilter) writer.WriteString("dependencyFilter", dependencyFilter);
        else writer.WriteNull("dependencyFilter");
        writer.WriteNumber("excludedCount", scope.ExcludedCount);
        writer.WriteNumber("excludedUnknownCount", scope.ExcludedUnknownCount);
        writer.WriteEndObject();
        writer.WriteStartObject("inputScope");
        var excludedInputPaths = scope.ExcludedInputPaths;
        writer.WriteNumber("excludedPathCount", excludedInputPaths?.Length ?? 0);
        writer.WriteStartArray("excludedPaths");
        if (excludedInputPaths is not null)
        {
            for (var excludedIndex = 0; excludedIndex < excludedInputPaths.Length; excludedIndex++)
            {
                writer.WriteStringValue(excludedInputPaths[excludedIndex]);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        WriteInputDiscoveryMetadata(writer, scope.Discovery);
    }

    // Written unconditionally and with every count, for the reason inputScope is: a field that appeared only when
    // it had something to say would leave "discovery ignored nothing" indistinguishable from "an older Ol wrote
    // this report", and a reader would have to determine the document's shape before it could read the field.
    private static void WriteInputDiscoveryMetadata(Utf8JsonWriter writer, in ScanInputDiscovery discovery)
    {
        writer.WriteStartObject("inputDiscovery");
        writer.WriteNumber("detectedFileCount", discovery.DetectedFileCount);
        var ignoredCandidates = discovery.IgnoredCandidates;
        writer.WriteNumber("ignoredCandidateCount", ignoredCandidates?.Length ?? 0);
        writer.WriteStartArray("ignoredCandidates");
        if (ignoredCandidates is not null)
        {
            for (var candidateIndex = 0; candidateIndex < ignoredCandidates.Length; candidateIndex++)
            {
                writer.WriteStringValue(ignoredCandidates[candidateIndex]);
            }
        }

        writer.WriteEndArray();
        writer.WriteNumber("incompleteInputSetCount", discovery.IncompleteInputSetCount);
        writer.WriteEndObject();
    }

    // Which inputs supplied a component is a list rather than a single token so that a collection needs no combined
    // vocabulary such as "both", and so a reader parses the same shape whatever the input was.
    private static void WriteSuppliedBy(Utf8JsonWriter writer, ComponentSupply supply)
    {
        writer.WriteStartArray("suppliedBy"u8);
        if ((supply & ComponentSupply.Sbom) != 0) writer.WriteStringValue("sbom"u8);
        if ((supply & ComponentSupply.PackageManager) != 0) writer.WriteStringValue("package-manager"u8);
        writer.WriteEndArray();
    }

    private static void WriteLicenseCandidates(Utf8JsonWriter writer, ScanComponent component)
    {
        writer.WriteStartArray("licenseCandidates");
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            writer.WriteStartObject();
            writer.WriteString("source"u8, candidate.Source.ToUtf8());
            writer.WriteString("kind"u8, candidate.Kind.ToUtf8());
            writer.WriteString("raw"u8, candidate.Raw.Span);
            writer.WriteString("normalized"u8, candidate.Normalized.Span);
            writer.WriteString("status"u8, candidate.Status.ToUtf8());
            writer.WriteBoolean("deprecated", candidate.Deprecated);
            WriteCandidateWarnings(writer, candidate.Warnings);
            WriteLicenseEvidence(writer, candidate.Evidence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteLicenseEvidence(Utf8JsonWriter writer, LicenseEvidence evidence)
    {
        if (evidence.Kind == LicenseEvidenceKind.None)
        {
            return;
        }

        writer.WriteStartObject("evidence");
        // The location a publisher declared is provenance for every source that can state one, so it is
        // written once here rather than inside each source's own shape.
        if (evidence.DeclaredReference is { } declaredReference)
        {
            writer.WriteString("declaredLicenseReferenceKind", declaredReference.Kind switch
            {
                DeclaredLicenseReferenceKind.Location => "location",
                DeclaredLicenseReferenceKind.InlineText => "inline-text",
                _ => "artifact-path",
            });
            writer.WriteString("declaredLicenseReference", declaredReference.Value.Span);
        }

        switch (evidence.Kind)
        {
            case LicenseEvidenceKind.Sbom:
                writer.WriteString("type", "sbom");
                var field = evidence.SbomField switch
                {
                    SbomLicenseField.CycloneDxLicenses => "licenses",
                    SbomLicenseField.CycloneDxEvidenceLicenses => "evidence.licenses",
                    SbomLicenseField.SpdxLicenseDeclared => "licenseDeclared",
                    SbomLicenseField.SpdxLicenseConcluded => "licenseConcluded",
                    _ => null,
                };
                if (field is not null)
                {
                    writer.WriteString("field", field);
                }

                if (evidence.Acknowledgement != LicenseAcknowledgement.None)
                {
                    writer.WriteString("acknowledgement", evidence.Acknowledgement == LicenseAcknowledgement.Declared ? "declared" : "concluded");
                }

                break;
            case LicenseEvidenceKind.DependencyInput:
                writer.WriteString("type", "dependency-input");
                if (evidence.DependencyInput is { } input)
                {
                    writer.WriteString("format", input.Format);
                    writer.WriteString("field", input.Field);
                }

                break;
            case LicenseEvidenceKind.PackageRegistry:
                writer.WriteString("type", "package-registry");
                if (evidence.PackageRegistry?.CacheKeySha256 is { Length: > 0 } cacheKeySha256)
                {
                    writer.WriteString("cacheKeySha256", cacheKeySha256);
                }

                if (evidence.PackageRegistry is { } packageDetails && packageDetails.CollectedAt != default)
                {
                    writer.WriteString("collectedAt", packageDetails.CollectedAt);
                }

                break;
            case LicenseEvidenceKind.SourceRepository:
                writer.WriteString("type", "source-repository");
                if (evidence.SourceRepository is { } sourceRepository)
                {
                    WriteSourceRepositoryEvidence(writer, sourceRepository);
                }

                break;
            case LicenseEvidenceKind.PackageArtifact:
                writer.WriteString("type", "package-artifact");
                if (evidence.PackageArtifact is { } artifact)
                {
                    writer.WriteString("artifact", artifact.Artifact);
                    writer.WriteString("path", artifact.Path);
                    writer.WriteString("contentSha256", artifact.ContentSha256);
                    writer.WriteString("matcher", artifact.Matcher);
                    writer.WriteString("corpusVersion", artifact.CorpusVersion);
                }

                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteSourceRepositoryEvidence(Utf8JsonWriter writer, SourceRepositoryEvidence value)
    {
        writer.WriteString("repository", value.Repository);
        writer.WriteString("ref", value.Ref);
        if (value.HttpStatus is { } status) writer.WriteNumber("httpStatus", status);
        else writer.WriteNull("httpStatus");
        writer.WriteString("cacheKeySha256", value.CacheKeySha256);
        writer.WriteString("licensePath", value.LicensePath);
        writer.WriteString("licenseSha", value.LicenseSha);
        writer.WriteString("licenseKey", value.LicenseKey);
        writer.WriteString("licenseName", value.LicenseName);
        writer.WriteString("licenseUrl", value.LicenseUrl);
    }

    /// <summary>Writes a warning set as identifiers. Used for both a component and each of its candidates.</summary>
    private static void WriteCandidateWarnings(Utf8JsonWriter writer, LicenseCandidateWarnings warnings)
    {
        writer.WriteStartArray("warnings");
        if ((warnings & LicenseCandidateWarnings.DeprecatedSpdxIdentifier) != 0) writer.WriteStringValue("deprecated_spdx_identifier"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataFetchFailed) != 0) writer.WriteStringValue("package_metadata_fetch_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryCacheInvalid) != 0) writer.WriteStringValue("source_repository_cache_invalid"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryCacheWriteFailed) != 0) writer.WriteStringValue("source_repository_cache_write_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryFetchFailed) != 0) writer.WriteStringValue("source_repository_fetch_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryUnavailable) != 0) writer.WriteStringValue("source_repository_unavailable"u8);
        if ((warnings & LicenseCandidateWarnings.UnsupportedPackageMetadata) != 0) writer.WriteStringValue("unsupported_package_metadata"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataUnversionedPurl) != 0) writer.WriteStringValue("package_metadata_unversioned_purl"u8);
        if ((warnings & LicenseCandidateWarnings.UnsupportedSourceRepository) != 0) writer.WriteStringValue("unsupported_source_repository"u8);
        if ((warnings & LicenseCandidateWarnings.ExternalEvidenceNotCollected) != 0) writer.WriteStringValue("external_evidence_not_collected"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataNotFound) != 0) writer.WriteStringValue("package_metadata_not_found"u8);
        if ((warnings & LicenseCandidateWarnings.SourceLicenseNotDetected) != 0) writer.WriteStringValue("license_not_detected"u8);
        if ((warnings & LicenseCandidateWarnings.SourceLicenseNotRecognized) != 0) writer.WriteStringValue("license_not_recognized"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositorySubdirectory) != 0) writer.WriteStringValue("source_repository_subdirectory"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryRefNotFound) != 0) writer.WriteStringValue("source_repository_ref_not_found"u8);
        writer.WriteEndArray();
    }

    private static void WriteSummary(Utf8JsonWriter writer, ScanSummary summary)
    {
        writer.WriteStartObject("summary");
        writer.WriteNumber("matched", summary.Matched);
        writer.WriteNumber("conflict", summary.Conflict);
        writer.WriteNumber("unknown", summary.Unknown);
        writer.WriteNumber("ambiguous", summary.Ambiguous);
        writer.WriteNumber("invalid", summary.Invalid);
        writer.WriteNumber("error", summary.Error);
        // Present in a single-input report too, for the reason the per-component field is: a tally that
        // appeared only when inputs were mixed would leave "this scan had one input" indistinguishable
        // from "an older Ol wrote this report".
        writer.WriteStartObject("supply");
        writer.WriteNumber("sbomOnly", summary.SbomOnlyCount);
        writer.WriteNumber("packageManagerOnly", summary.PackageManagerOnlyCount);
        writer.WriteNumber("both", summary.BothSuppliedCount);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteWarnings(Utf8JsonWriter writer, ReadOnlySpan<ScanComponent> components, bool emptyInventory)
    {
        writer.WriteStartArray("warnings");
        if (HasDeprecatedWarning(components))
        {
            writer.WriteStringValue("deprecated_spdx_identifier");
        }

        if (emptyInventory)
        {
            writer.WriteStringValue(EmptyInventoryWarning);
        }

        writer.WriteEndArray();
    }

    private static void WriteWarnings(Utf8JsonWriter writer, ReadOnlySpan<GroupRow> groups, bool emptyInventory)
    {
        writer.WriteStartArray("warnings");
        for (var i = 0; i < groups.Length; i++)
        {
            if (HasDeprecatedWarning(groups[i].Components.Span))
            {
                writer.WriteStringValue("deprecated_spdx_identifier");
                break;
            }
        }

        if (emptyInventory)
        {
            writer.WriteStringValue(EmptyInventoryWarning);
        }

        writer.WriteEndArray();
    }

    private static bool HasDeprecatedWarning(ReadOnlySpan<ScanComponent> components)
    {
        for (var i = 0; i < components.Length; i++)
        {
            if ((components[i].Warnings & LicenseCandidateWarnings.DeprecatedSpdxIdentifier) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteInputMetadata(Utf8JsonWriter writer, ScanInputDescriptor input)
    {
        writer.WriteStartObject("input");
        writer.WriteString("kind", input.Kind.Name);
        writer.WriteString("format", input.Format.Name);
        writer.WriteString("sourceRef", input.SourceReference);
        writer.WriteString("sourceSha256", input.SourceSha256);
        writer.WriteString("parser", input.Format.Parser);
        writer.WriteString("specificationVersion"u8, input.SpecificationVersion.Span);
        if (input.Kind == ScanInputKind.Sbom)
        {
            writer.WriteString("sbomRef", input.SourceReference);
            writer.WriteString("sbomFormat", input.Format.DisplayName);
            writer.WriteString("sbomSpecVersion"u8, input.SpecificationVersion.Span);
            writer.WriteString("sbomSha256", input.SourceSha256);
        }

        writer.WriteEndObject();
    }

    private static void WriteInventory(Utf8JsonWriter writer, DependencyInventory inventory)
    {
        writer.WriteStartObject("inventory");
        writer.WriteStartArray("contexts");
        for (var i = 0; i < inventory.Contexts.Length; i++)
        {
            var context = inventory.Contexts[i];
            writer.WriteStartObject();
            WriteLogicalPath(writer, "projectOrigin"u8, context.ProjectOrigin);
            writer.WriteString("target"u8, context.Target.Span);
            writer.WriteString("runtime"u8, context.Runtime.Span);
            writer.WriteString("platform"u8, context.Platform.Span);
            writer.WriteString("architecture"u8, context.Architecture.Span);
            writer.WriteString("variant"u8, context.Variant.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("components");
        for (var i = 0; i < inventory.Components.Length; i++)
        {
            var component = inventory.Components[i];
            writer.WriteStartObject();
            writer.WriteString("name"u8, component.Name.Span);
            writer.WriteString("version"u8, component.Version.Span);
            writer.WriteString("ecosystem", component.Ecosystem);
            writer.WriteString("dependency"u8, GetDependencyTypeUtf8(component.DependencyType));
            writer.WriteString("purl"u8, component.Purl.Span);
            writer.WriteString("sourceId"u8, component.SourceId.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("occurrences");
        var occurrenceVariants = inventory.OccurrenceVariants;
        var occurrenceVariantIndex = 0;
        for (var i = 0; i < inventory.Occurrences.Length; i++)
        {
            var occurrence = inventory.Occurrences[i];
            writer.WriteStartObject();
            writer.WriteNumber("contextIndex", occurrence.ContextIndex);
            writer.WriteNumber("componentIndex", occurrence.ComponentIndex);
            if (occurrenceVariants is not null
                && occurrenceVariantIndex < occurrenceVariants.Length
                && occurrenceVariants[occurrenceVariantIndex].OccurrenceIndex == i)
            {
                writer.WriteString("variant"u8, occurrenceVariants[occurrenceVariantIndex].Value.Span);
                occurrenceVariantIndex++;
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("edges");
        for (var i = 0; i < inventory.Edges.Length; i++)
        {
            var edge = inventory.Edges[i];
            writer.WriteStartObject();
            writer.WriteNumber("contextIndex", edge.ContextIndex);
            writer.WriteNumber("fromOccurrenceIndex", edge.FromOccurrenceIndex);
            writer.WriteNumber("toOccurrenceIndex", edge.ToOccurrenceIndex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteLogicalPath(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, Utf8Slice value)
    {
        var path = value.Span;
        var isAbsolute = path.Length > 0 && path[0] is (byte)'/' or (byte)'\\'
            || path.Length >= 3
                && (path[0] is >= (byte)'A' and <= (byte)'Z' || path[0] is >= (byte)'a' and <= (byte)'z')
                && path[1] == (byte)':'
                && path[2] is (byte)'/' or (byte)'\\';
        if (isAbsolute)
        {
            var separator = path.LastIndexOfAny((byte)'/', (byte)'\\');
            path = separator < 0 ? path : path[(separator + 1)..];
        }

        writer.WriteString(propertyName, path);
    }
}

/// <param name="UnresolvedWarningCount">Warnings on components the scan did not resolve to one license.</param>
/// <param name="ResolvedWarningCount">Warnings on components that resolved despite them.</param>
/// <remarks>
/// The two counts are reported separately rather than summed. Collecting evidence Ol could not read is
/// routine — a repository outside GitHub, a registry with no license field — and when the component
/// resolved from other evidence anyway, that warning changed no outcome. One total makes a fully resolved
/// report announce findings a reader then has to open the JSON to dismiss.
/// </remarks>
internal readonly record struct ScanSummary(
    int Matched,
    int Conflict,
    int Unknown,
    int Ambiguous,
    int Invalid,
    int Error,
    int UnresolvedWarningCount,
    int ResolvedWarningCount,
    int DeprecatedSpdxCount,
    int SbomOnlyCount,
    int PackageManagerOnlyCount,
    int BothSuppliedCount)
{
    public static ScanSummary Create(ReadOnlySpan<GroupRow> groups)
    {
        var total = default(ScanSummary);
        for (var i = 0; i < groups.Length; i++)
        {
            var summary = Create(groups[i].Components.Span);
            total = new ScanSummary(
                total.Matched + summary.Matched,
                total.Conflict + summary.Conflict,
                total.Unknown + summary.Unknown,
                total.Ambiguous + summary.Ambiguous,
                total.Invalid + summary.Invalid,
                total.Error + summary.Error,
                total.UnresolvedWarningCount + summary.UnresolvedWarningCount,
                total.ResolvedWarningCount + summary.ResolvedWarningCount,
                total.DeprecatedSpdxCount + summary.DeprecatedSpdxCount,
                total.SbomOnlyCount + summary.SbomOnlyCount,
                total.PackageManagerOnlyCount + summary.PackageManagerOnlyCount,
                total.BothSuppliedCount + summary.BothSuppliedCount);
        }

        return total;
    }

    public static ScanSummary Create(ReadOnlySpan<ScanComponent> components)
    {
        var matched = 0;
        var conflict = 0;
        var unknown = 0;
        var ambiguous = 0;
        var invalid = 0;
        var error = 0;
        var unresolvedWarningCount = 0;
        var resolvedWarningCount = 0;
        var deprecatedSpdxCount = 0;
        var sbomOnlyCount = 0;
        var packageManagerOnlyCount = 0;
        var bothSuppliedCount = 0;

        for (var i = 0; i < components.Length; i++)
        {
            // Per component, SUPPLIED answers "which input saw this one". Only the totals answer whether an
            // input was worth passing, which is the question a combined scan is configured to ask and the
            // one nothing in the report reached without walking every component.
            switch (components[i].SuppliedBy)
            {
                case ComponentSupply.Sbom:
                    sbomOnlyCount++;
                    break;
                case ComponentSupply.PackageManager:
                    packageManagerOnlyCount++;
                    break;
                case ComponentSupply.Sbom | ComponentSupply.PackageManager:
                    bothSuppliedCount++;
                    break;
            }

            switch (components[i].Status)
            {
                case LicenseStatus.Matched:
                    matched++;
                    break;
                case LicenseStatus.Conflict:
                    conflict++;
                    break;
                case LicenseStatus.Unknown:
                    unknown++;
                    break;
                case LicenseStatus.Ambiguous:
                    ambiguous++;
                    break;
                case LicenseStatus.Invalid:
                    invalid++;
                    break;
                case LicenseStatus.Error:
                    error++;
                    break;
            }

            // A component that reached one license resolved whatever else failed on the way, so its
            // warnings describe collection rather than the result.
            var warningCount = BitOperations.PopCount((uint)components[i].Warnings);
            if (components[i].Status == LicenseStatus.Matched) resolvedWarningCount += warningCount;
            else unresolvedWarningCount += warningCount;

            for (var candidateIndex = 0; candidateIndex < components[i].CandidateCount; candidateIndex++)
            {
                if (components[i].GetCandidate(candidateIndex).Deprecated)
                {
                    deprecatedSpdxCount++;
                }
            }
        }

        return new ScanSummary(
            matched,
            conflict,
            unknown,
            ambiguous,
            invalid,
            error,
            unresolvedWarningCount,
            resolvedWarningCount,
            deprecatedSpdxCount,
            sbomOnlyCount,
            packageManagerOnlyCount,
            bothSuppliedCount);
    }
}
