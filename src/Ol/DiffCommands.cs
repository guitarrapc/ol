using System.Buffers;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Ol.Core.Reporting;
using Ol.Internals;

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
            Console.Write(format == DiffFormat.Json ? RenderJson(changes) : RenderText(changes));
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to write diff result: {exception.Message}");
            return 1;
        }

        // A diff reports; it does not enforce. Policy enforcement stays in `check` so exit codes keep one meaning.
        return 0;
    }

    private static string RenderText(ReadOnlySpan<ScanReportChange> changes)
    {
        if (changes.IsEmpty) return $"No license-relevant changes.{Environment.NewLine}";

        var builder = new StringBuilder();
        builder.Append("License-relevant changes: ");
        builder.Append(changes.Length);
        builder.AppendLine(changes.Length == 1 ? " change." : " changes.");
        builder.AppendLine();
        builder.AppendLine("Change\tEcosystem\tName\tPrevious\tCurrent");
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            builder.Append(ToToken(change.Kind));
            builder.Append('\t');
            builder.Append(change.Ecosystem.Length == 0 ? "-" : change.Ecosystem);
            builder.Append('\t');
            builder.Append(change.Name);
            builder.Append('\t');
            builder.Append(Describe(change, previousSide: true));
            builder.Append('\t');
            builder.AppendLine(Describe(change, previousSide: false));
        }

        return builder.ToString();
    }

    private static string Describe(in ScanReportChange change, bool previousSide) => change.Kind switch
    {
        ScanReportChangeKind.VersionChanged => Or(previousSide ? change.PreviousVersion : change.CurrentVersion),
        ScanReportChangeKind.StatusChanged => Or(previousSide ? change.PreviousStatus : change.CurrentStatus),
        ScanReportChangeKind.Added => previousSide ? "-" : Or(change.CurrentVersion),
        ScanReportChangeKind.Removed => previousSide ? Or(change.PreviousVersion) : "-",
        _ => Or(previousSide ? change.PreviousLicense : change.CurrentLicense),
    };

    private static string Or(string value) => value.Length == 0 ? "-" : value;

    private static string RenderJson(ReadOnlySpan<ScanReportChange> changes)
    {
        var buffer = new ArrayBufferWriter<byte>(128 + (changes.Length * 160));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion"u8, JsonSchemaVersion);
            writer.WriteStartArray("changes"u8);
            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes[i];
                writer.WriteStartObject();
                writer.WriteString("kind"u8, ToToken(change.Kind));
                if (change.Ecosystem.Length != 0) writer.WriteString("ecosystem"u8, change.Ecosystem);
                writer.WriteString("name"u8, change.Name);
                WriteTransition(writer, "version"u8, change.PreviousVersion, change.CurrentVersion);
                WriteTransition(writer, "license"u8, change.PreviousLicense, change.CurrentLicense);
                WriteTransition(writer, "status"u8, change.PreviousStatus, change.CurrentStatus);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("changeCount"u8, changes.Length);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
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
    /// <summary>Human-readable tab-separated text.</summary>
    Text,

    /// <summary>Machine-readable JSON.</summary>
    Json,
}
