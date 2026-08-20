using System.Buffers;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Ol.Core.Reporting;

/// <summary>Compare two persisted scan reports.</summary>
internal sealed class DiffCommands
{
    /// <summary>The diff document schema version.</summary>
    private const int JsonSchemaVersion = 1;

    /// <summary>Compare two persisted JSON scan reports and report license-relevant changes.</summary>
    /// <param name="previous">Previously persisted JSON scan report.</param>
    /// <param name="current">Current JSON scan report.</param>
    /// <param name="format">Output format.</param>
    [Command("diff")]
    public int Diff(
        string previous,
        string current,
        DiffFormat format = DiffFormat.Text)
    {
        if (!ScanReportFile.TryRead(previous, out var previousReport, out var previousError))
        {
            Console.Error.WriteLine(previousError);
            return 1;
        }

        if (!ScanReportFile.TryRead(current, out var currentReport, out var currentError))
        {
            Console.Error.WriteLine(currentError);
            return 1;
        }

        var changes = ScanReportDiff.Compare(previousReport.Components, currentReport.Components);
        try
        {
            Console.Write(format == DiffFormat.Json
                ? RenderJson(changes, previousReport.ExcludedInputPaths, currentReport.ExcludedInputPaths, previousReport.View, currentReport.View)
                : RenderText(changes, previousReport.ExcludedInputPaths, currentReport.ExcludedInputPaths, previousReport.View, currentReport.View));
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to write diff result: {exception.Message}");
            return 1;
        }

        // A diff reports; it does not enforce. Policy enforcement stays in `check` so exit codes keep one meaning.
        return 0;
    }

    private static string RenderText(
        ReadOnlySpan<ScanReportChange> changes,
        string[] previousExcludedInputPaths,
        string[] currentExcludedInputPaths,
        in ScanReportViewScope previousView,
        in ScanReportViewScope currentView)
    {
        if (changes.IsEmpty
            && previousExcludedInputPaths.Length == 0
            && currentExcludedInputPaths.Length == 0
            && !previousView.IsFiltered
            && !currentView.IsFiltered)
        {
            return $"No license-relevant changes.{Environment.NewLine}";
        }

        var builder = new StringBuilder();
        AppendInputScope(builder, previousExcludedInputPaths, currentExcludedInputPaths);
        AppendEvaluatedView(builder, previousView, currentView);
        if (changes.IsEmpty)
        {
            builder.AppendLine("No component license changes.");
            return builder.ToString();
        }

        var componentCount = CountComponents(changes);
        builder.Append("License-relevant changes: ");
        builder.Append(changes.Length);
        builder.Append(changes.Length == 1 ? " change in " : " changes in ");
        builder.Append(componentCount);
        builder.AppendLine(componentCount == 1 ? " component." : " components.");
        builder.AppendLine();

        var start = 0;
        while (start < changes.Length)
        {
            var end = start + 1;
            while (end < changes.Length && SameComponent(changes[start], changes[end])) end++;
            if (start != 0) builder.AppendLine();
            AppendComponent(builder, changes[start..end]);
            start = end;
        }

        return builder.ToString();
    }

    private static void AppendInputScope(StringBuilder builder, string[] previous, string[] current)
    {
        if (previous.Length == 0 && current.Length == 0) return;

        builder.AppendLine("Audit boundary:");
        AppendExcludedInputPaths(builder, "  previous excluded input paths: ", previous);
        AppendExcludedInputPaths(builder, "  current excluded input paths: ", current);
        builder.Append("  changed: ");
        builder.AppendLine(SetChanged(previous, current) ? "yes" : "no");
        builder.AppendLine();
    }

    /// <summary>
    /// States the view each report was rendered under, when either was narrowed.
    /// </summary>
    /// <remarks>
    /// A <c>--dependency</c>-filtered report holds fewer components than its scan resolved, so a diff over two of
    /// them compares populations rather than resolutions, and two different filters make every difference between
    /// them an artifact of the filters. Ol states the two views and whether they match and draws no conclusion:
    /// which components a comparison covers is a scope decision its reader makes, exactly as the excluded input
    /// paths above are.
    /// </remarks>
    private static void AppendEvaluatedView(StringBuilder builder, in ScanReportViewScope previous, in ScanReportViewScope current)
    {
        if (!previous.IsFiltered && !current.IsFiltered) return;

        builder.AppendLine("Evaluated view:");
        AppendDependencyFilter(builder, "  previous dependency filter: ", previous);
        AppendDependencyFilter(builder, "  current dependency filter: ", current);
        builder.Append("  changed: ");
        builder.AppendLine(ViewChanged(previous, current) ? "yes" : "no");
        builder.AppendLine();
    }

    private static void AppendDependencyFilter(StringBuilder builder, string prefix, in ScanReportViewScope view)
    {
        builder.Append(prefix);
        if (!view.IsFiltered)
        {
            builder.AppendLine("none");
            return;
        }

        builder.Append(view.DependencyFilter);
        builder.Append("; ");
        builder.Append(view.ExcludedCount);
        builder.Append(view.ExcludedCount == 1 ? " component excluded" : " components excluded");
        if (view.ExcludedUnknownCount > 0)
        {
            builder.Append(", ");
            builder.Append(view.ExcludedUnknownCount);
            builder.Append(" with an unknown relationship");
        }

        builder.AppendLine();
    }

    // `--dependency` is an unordered list the view filter parses with entries trimmed, so the same configuration can
    // be spelled several ways and only the set it denotes is the boundary. Casing is significant, because a token the
    // filter does not recognize selects the unknown relationship rather than the one it was misspelled from.
    private static bool ViewChanged(in ScanReportViewScope previous, in ScanReportViewScope current)
        => SetChanged(DependencyFilterTokens(previous), DependencyFilterTokens(current));

    private static string[] DependencyFilterTokens(in ScanReportViewScope view)
        => view.IsFiltered
            ? view.DependencyFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    private static void AppendExcludedInputPaths(StringBuilder builder, string prefix, string[] paths)
    {
        builder.Append(prefix);
        if (paths.Length == 0)
        {
            builder.AppendLine("none");
            return;
        }

        builder.Append(paths[0]);
        for (var i = 1; i < paths.Length; i++)
        {
            builder.Append(", ");
            builder.Append(paths[i]);
        }
        builder.AppendLine();
    }

    private static void AppendComponent(StringBuilder builder, ReadOnlySpan<ScanReportChange> changes)
    {
        var first = changes[0];
        builder.Append(first.Kind switch
        {
            ScanReportChangeKind.Added => '+',
            ScanReportChangeKind.Removed => '-',
            _ => '~',
        });
        builder.Append(' ');
        if (first.Ecosystem.Length != 0)
        {
            builder.Append(first.Ecosystem);
            builder.Append(':');
        }

        builder.Append(first.Name);
        AppendStableVersion(builder, changes);
        builder.AppendLine();
        if (first.Kind is ScanReportChangeKind.Added or ScanReportChangeKind.Removed)
        {
            var added = first.Kind == ScanReportChangeKind.Added;
            AppendValue(builder, "license", added ? first.CurrentLicense : first.PreviousLicense);
            AppendValue(builder, "status", added ? first.CurrentStatus : first.PreviousStatus);
            return;
        }

        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            switch (change.Kind)
            {
                case ScanReportChangeKind.VersionChanged:
                    AppendTransition(builder, "version", change.PreviousVersion, change.CurrentVersion);
                    break;
                case ScanReportChangeKind.StatusChanged:
                    AppendTransition(builder, "status", change.PreviousStatus, change.CurrentStatus);
                    break;
                case ScanReportChangeKind.LicenseChanged:
                    AppendTransition(builder, "license", change.PreviousLicense, change.CurrentLicense);
                    break;
                case ScanReportChangeKind.EvidenceChanged:
                    AppendValue(builder, "evidence", "changed");
                    break;
            }
        }
    }

    private static void AppendStableVersion(StringBuilder builder, ReadOnlySpan<ScanReportChange> changes)
    {
        string? version = null;
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            var candidate = change.Kind switch
            {
                ScanReportChangeKind.Added => change.CurrentVersion,
                ScanReportChangeKind.Removed => change.PreviousVersion,
                _ when string.Equals(change.PreviousVersion, change.CurrentVersion, StringComparison.Ordinal) => change.CurrentVersion,
                _ => string.Empty,
            };
            if (candidate.Length == 0 || version is not null && !string.Equals(version, candidate, StringComparison.Ordinal)) return;
            version = candidate;
        }

        if (version is null) return;

        builder.Append('@');
        builder.Append(version);
    }

    private static int CountComponents(ReadOnlySpan<ScanReportChange> changes)
    {
        if (changes.IsEmpty) return 0;

        var count = 1;
        for (var i = 1; i < changes.Length; i++)
        {
            if (!SameComponent(changes[i - 1], changes[i])) count++;
        }

        return count;
    }

    private static bool SameComponent(in ScanReportChange left, in ScanReportChange right)
        => string.Equals(left.Ecosystem, right.Ecosystem, StringComparison.Ordinal) &&
           string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private static void AppendTransition(StringBuilder builder, string field, string previous, string current)
    {
        builder.Append("    ");
        builder.Append(field);
        builder.Append(": ");
        builder.Append(Or(previous));
        builder.Append(" -> ");
        builder.AppendLine(Or(current));
    }

    private static void AppendValue(StringBuilder builder, string field, string value)
    {
        builder.Append("    ");
        builder.Append(field);
        builder.Append(": ");
        builder.AppendLine(Or(value));
    }

    private static string Or(string value) => value.Length == 0 ? "-" : value;

    private static string RenderJson(
        ReadOnlySpan<ScanReportChange> changes,
        string[] previousExcludedInputPaths,
        string[] currentExcludedInputPaths,
        in ScanReportViewScope previousView,
        in ScanReportViewScope currentView)
    {
        var buffer = new ArrayBufferWriter<byte>(128 + (changes.Length * 160));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion"u8, JsonSchemaVersion);
            WriteInputScope(writer, previousExcludedInputPaths, currentExcludedInputPaths);
            WriteView(writer, previousView, currentView);
            writer.WriteStartArray("changes"u8);
            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes[i];
                writer.WriteStartObject();
                writer.WriteString("kind"u8, ToToken(change.Kind));
                if (change.Ecosystem.Length != 0) writer.WriteString("ecosystem"u8, change.Ecosystem);
                writer.WriteString("name"u8, change.Name);
                switch (change.Kind)
                {
                    case ScanReportChangeKind.VersionChanged:
                        WriteTransition(writer, "version"u8, change.PreviousVersion, change.CurrentVersion);
                        break;
                    case ScanReportChangeKind.LicenseChanged:
                        WriteTransition(writer, "version"u8, change.PreviousVersion, change.CurrentVersion);
                        WriteTransition(writer, "license"u8, change.PreviousLicense, change.CurrentLicense);
                        break;
                    case ScanReportChangeKind.StatusChanged:
                        WriteTransition(writer, "version"u8, change.PreviousVersion, change.CurrentVersion);
                        WriteTransition(writer, "status"u8, change.PreviousStatus, change.CurrentStatus);
                        break;
                    default:
                        WriteTransition(writer, "version"u8, change.PreviousVersion, change.CurrentVersion);
                        WriteTransition(writer, "license"u8, change.PreviousLicense, change.CurrentLicense);
                        WriteTransition(writer, "status"u8, change.PreviousStatus, change.CurrentStatus);
                        break;
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("componentCount"u8, CountComponents(changes));
            writer.WriteNumber("changeCount"u8, changes.Length);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
    }

    private static void WriteInputScope(Utf8JsonWriter writer, string[] previous, string[] current)
    {
        writer.WriteStartObject("inputScope"u8);
        writer.WriteBoolean("changed"u8, SetChanged(previous, current));
        WriteExcludedInputPaths(writer, "previous"u8, previous);
        WriteExcludedInputPaths(writer, "current"u8, current);
        writer.WriteEndObject();
    }

    // Written whether or not either side was filtered, for the same reason inputScope is: a consumer must not have to
    // determine the document's shape before it can read a boundary the absence of a key would leave ambiguous.
    private static void WriteView(Utf8JsonWriter writer, in ScanReportViewScope previous, in ScanReportViewScope current)
    {
        writer.WriteStartObject("view"u8);
        writer.WriteBoolean("changed"u8, ViewChanged(previous, current));
        WriteDependencyFilter(writer, "previous"u8, previous);
        WriteDependencyFilter(writer, "current"u8, current);
        writer.WriteEndObject();
    }

    private static void WriteDependencyFilter(Utf8JsonWriter writer, ReadOnlySpan<byte> name, in ScanReportViewScope view)
    {
        writer.WriteStartObject(name);
        if (view.IsFiltered) writer.WriteString("dependencyFilter"u8, view.DependencyFilter);
        else writer.WriteNull("dependencyFilter"u8);
        writer.WriteNumber("excludedCount"u8, view.ExcludedCount);
        writer.WriteNumber("excludedUnknownCount"u8, view.ExcludedUnknownCount);
        writer.WriteEndObject();
    }

    private static void WriteExcludedInputPaths(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string[] paths)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("excludedPathCount"u8, paths.Length);
        writer.WriteStartArray("excludedPaths"u8);
        for (var i = 0; i < paths.Length; i++) writer.WriteStringValue(paths[i]);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool SetChanged(string[] previous, string[] current)
    {
        for (var i = 0; i < previous.Length; i++)
        {
            var found = false;
            for (var j = 0; j < current.Length; j++)
            {
                if (!string.Equals(previous[i], current[j], StringComparison.Ordinal)) continue;
                found = true;
                break;
            }
            if (!found) return true;
        }

        for (var i = 0; i < current.Length; i++)
        {
            var found = false;
            for (var j = 0; j < previous.Length; j++)
            {
                if (!string.Equals(current[i], previous[j], StringComparison.Ordinal)) continue;
                found = true;
                break;
            }
            if (!found) return true;
        }
        return false;
    }

    private static void WriteTransition(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string previous, string current)
    {
        if (previous.Length == 0 && current.Length == 0) return;

        writer.WriteStartObject(name);
        writer.WriteString("previous"u8, previous);
        writer.WriteString("current"u8, current);
        writer.WriteEndObject();
    }

    private static string ToToken(ScanReportChangeKind kind) => kind switch
    {
        ScanReportChangeKind.Added => "added",
        ScanReportChangeKind.Removed => "removed",
        ScanReportChangeKind.VersionChanged => "version-changed",
        ScanReportChangeKind.StatusChanged => "status-changed",
        ScanReportChangeKind.LicenseChanged => "license-changed",
        ScanReportChangeKind.EvidenceChanged => "evidence-changed",
        _ => "changed",
    };
}

/// <summary>Selects the diff output format.</summary>
internal enum DiffFormat
{
    /// <summary>Human-readable vertical diff.</summary>
    Text,

    /// <summary>Machine-readable JSON.</summary>
    Json,
}
