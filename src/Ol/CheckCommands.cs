using System.Buffers;
using System.Buffers.Text;
using System.Text;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;
using Ol.Internals;

/// <summary>Check a canonical JSON scan report against an allow-list.</summary>
internal sealed class CheckCommands
{
    /// <summary>Gets the running tool version recorded in generated artifacts.</summary>
    internal static string ToolVersion => typeof(CheckCommands).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Check a canonical JSON scan report against allowed SPDX licenses.</summary>
    /// <param name="report">Persisted canonical JSON scan report to evaluate.</param>
    /// <param name="allowLicenses">Comma-separated SPDX License Identifiers.</param>
    /// <param name="allowDevLicenses">Comma-separated SPDX License Identifiers additionally allowed for development-only components.</param>
    /// <param name="excludePackages">Comma-separated package URL prefixes whose components are not evaluated. A prefix may stop at the ecosystem, as in pkg:github/.</param>
    /// <param name="spdxData">Directory containing licenses.json and exceptions.json.</param>
    /// <param name="verbose">Include persisted report diagnostics.</param>
    /// <param name="baseline">Repeatable baseline files acknowledging already reviewed unresolved components. A component is acknowledged when any of them states it.</param>
    /// <param name="updateBaseline">Rewrite the last baseline file, holding what the earlier ones do not already acknowledge.</param>
    /// <param name="sarif">Write violations as SARIF to this file for CI code scanning.</param>
    /// <param name="format">Output format: text or markdown.</param>
    [Command("check")]
    public int Check(
        string report,
        string allowLicenses,
        string? allowDevLicenses = null,
        string? excludePackages = null,
        string? spdxData = null,
        bool verbose = false,
        [InputPathsParser] string[]? baseline = null,
        bool updateBaseline = false,
        string? sarif = null,
        CheckFormat format = CheckFormat.Text)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            Console.Error.WriteLine("Invalid license policy: --report must be specified.");
            return 1;
        }

        var developmentLicenseIds = allowDevLicenses is null
            ? []
            : allowDevLicenses.Split(',', StringSplitOptions.None);

        // An empty option is a supplied-but-empty scope statement, which is a configuration error rather than "exclude nothing".
        var excludedPackagePrefixes = excludePackages is null
            ? []
            : excludePackages.Split(',', StringSplitOptions.None);

        var baselinePaths = NormalizeBaselinePaths(baseline);
        if (updateBaseline && baselinePaths.Length == 0)
        {
            Console.Error.WriteLine("Invalid license policy: --update-baseline requires --baseline.");
            return 1;
        }

        if (!ScanExecution.TryResolveSpdx(spdxData, out var reportSpdx, out var spdxError))
        {
            Console.Error.WriteLine(spdxError);
            return 1;
        }

        if (!LicenseAllowPolicy.TryCreate(allowLicenses.Split(',', StringSplitOptions.None), developmentLicenseIds, excludedPackagePrefixes, reportSpdx.Index, out var policy, out var reportPolicyError))
        {
            Console.Error.WriteLine($"Invalid license policy: {reportPolicyError}");
            return 1;
        }

        if (!ScanReportFile.TryRead(report, out var persisted, out var readError))
        {
            Console.Error.WriteLine(readError);
            return 1;
        }

        var components = persisted.Components;
        var inventory = persisted.Inventory;
        var reportComponentUsages = persisted.ComponentUsages;
        var licenseListVersion = reportSpdx.LicenseListVersion;
        if (verbose)
        {
            Console.Error.WriteLine($"Evaluating persisted report: {persisted.SourceReference}; SPDX {persisted.LicenseListVersion} at scan time");
        }

        // An unusable baseline is a command failure rather than a silently empty baseline, so a mistyped
        // path is reported instead of changing which components fail. When updating, the last file is the
        // one being replaced, so only the files before it are read.
        var readCount = updateBaseline ? baselinePaths.Length - 1 : baselinePaths.Length;
        if (!TryComposeBaselines(baselinePaths.AsSpan(0, readCount), out var acknowledgements, out var baselineError))
        {
            Console.Error.WriteLine(baselineError);
            return 1;
        }

        if (updateBaseline)
        {
            // Only what the earlier files do not already state. Writing the complete snapshot would copy
            // the shared population into the file that composes with it, which is the duplication
            // composing them removes.
            var entries = LicenseBaseline.CreateEntries(SelectUnacknowledged(components, acknowledgements), policy);
            if (!BaselineFile.TryWrite(baselinePaths[^1], entries, licenseListVersion, out var writeError))
            {
                Console.Error.WriteLine(writeError);
                return 1;
            }

            acknowledgements = Compose(acknowledgements, LicenseBaseline.FromEntries(entries));
        }

        int acknowledgedCount;
        int policyComponentCount;
        int excludedCount;
        int ambiguityAllowedCount;
        int developmentAllowedCount;
        var developmentAllowedComponents = Array.Empty<int>();
        LicensePolicyViolation[] violations;
        if (developmentLicenseIds.Length == 0)
        {
            violations = policy.Evaluate(components, default, acknowledgements, out acknowledgedCount, out policyComponentCount, out _, out excludedCount, out ambiguityAllowedCount);
            developmentAllowedCount = -1;
        }
        else if (reportComponentUsages is not null)
        {
            // A persisted report already carries per-component usage aligned with its components.
            violations = policy.Evaluate(components, reportComponentUsages, acknowledgements, out acknowledgedCount, out policyComponentCount, out developmentAllowedComponents, out excludedCount, out ambiguityAllowedCount);
            developmentAllowedCount = developmentAllowedComponents.Length;
        }
        else
        {
            // A report without persisted usage cannot prove development-only reachability and therefore fails closed.
            violations = policy.Evaluate(components, default, acknowledgements, out acknowledgedCount, out policyComponentCount, out developmentAllowedComponents, out excludedCount, out ambiguityAllowedCount);
            developmentAllowedCount = developmentAllowedComponents.Length;
        }

        // Per-prefix attribution makes an exclusion entry that matches nothing visible, which the aggregate count cannot show.
        if (verbose && excludePackages is not null)
        {
            WriteExclusionMatches(policy, components);
        }

        // SARIF carries the same violation set as the text result; it is an additional projection, not a filter.
        if (!string.IsNullOrWhiteSpace(sarif))
        {
            try
            {
                File.WriteAllBytes(sarif, SarifRenderer.Render(inventory, components, violations, developmentAllowedComponents, ToolVersion, persisted.View, persisted.ExcludedInputPaths, persisted.DeclaresNoComponents));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine($"Unable to write SARIF: {exception.Message}");
                return 1;
            }
        }

        try
        {
            using var writer = new PooledStreamBufferWriter(Console.OpenStandardOutput());
            if (format == CheckFormat.Markdown)
            {
                CheckRenderer.WriteMarkdown(
                    writer,
                    persisted,
                    violations,
                    policyComponentCount,
                    baselinePaths.Length == 0 ? -1 : acknowledgedCount,
                    developmentAllowedCount,
                    excludePackages is null ? -1 : excludedCount,
                    ambiguityAllowedCount,
                    allowLicenses);
            }
            else
            {
                CheckRenderer.Write(
                    writer,
                    inventory,
                    components,
                    violations,
                    policyComponentCount,
                    baselinePaths.Length == 0 ? -1 : acknowledgedCount,
                    developmentAllowedCount,
                    excludePackages is null ? -1 : excludedCount,
                    ambiguityAllowedCount,
                    persisted.ExcludedInputPaths,
                    persisted.View,
                    persisted.DeclaresNoComponents);
            }
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to write check result: {exception.Message}");
            return 1;
        }

        // The run completed and the report is complete; what it proves is nothing, which is the state exit 3
        // exists to name. A pass here would make an unrestored project read as a project whose dependencies
        // are all allowed, and no baseline can acknowledge an inventory that has no components to acknowledge.
        if (persisted.DeclaresNoComponents) return 3;

        if (violations.Length == 0) return 0;

        // A run whose only findings are collection failures resolved nothing and proved nothing; reporting it as a
        // policy violation would make a registry outage indistinguishable from a forbidden license in CI.
        return CheckRenderer.IsIncomplete(violations) ? 3 : 2;
    }

    /// <summary>Drops empty entries so a supplied-but-blank option is not read as a path.</summary>
    private static string[] NormalizeBaselinePaths(string[]? baseline)
    {
        if (baseline is null || baseline.Length == 0) return [];

        var paths = new List<string>(baseline.Length);
        for (var i = 0; i < baseline.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(baseline[i])) paths.Add(baseline[i]);
        }

        return [.. paths];
    }

    /// <summary>Reads every supplied baseline and unions them, or reports the first that cannot be read.</summary>
    private static bool TryComposeBaselines(ReadOnlySpan<string> paths, out LicenseBaseline? composed, out string error)
    {
        composed = null;
        error = string.Empty;
        if (paths.IsEmpty) return true;

        var baselines = new LicenseBaseline[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            if (!BaselineFile.TryRead(paths[i], out var parsed, out error)) return false;
            baselines[i] = parsed!;
        }

        composed = LicenseBaseline.Compose(baselines);
        return true;
    }

    private static LicenseBaseline Compose(LicenseBaseline? earlier, LicenseBaseline written)
        => earlier is null ? written : LicenseBaseline.Compose([earlier, written]);

    /// <summary>Returns the components no already-supplied baseline acknowledges.</summary>
    private static ScanComponent[] SelectUnacknowledged(ScanComponent[] components, LicenseBaseline? acknowledgements)
    {
        if (acknowledgements is null || acknowledgements.Count == 0) return components;

        var remaining = new List<ScanComponent>(components.Length);
        for (var i = 0; i < components.Length; i++)
        {
            if (!acknowledgements.IsAcknowledged(components[i])) remaining.Add(components[i]);
        }

        return [.. remaining];
    }

    private static void WriteExclusionMatches(LicenseAllowPolicy policy, ReadOnlySpan<ScanComponent> components)
    {
        var prefixes = policy.ExclusionPrefixes;
        if (prefixes.IsEmpty) return;

        var counts = ArrayPool<int>.Shared.Rent(prefixes.Length);
        try
        {
            policy.CountExclusionMatches(components, counts.AsSpan(0, prefixes.Length));
            PurlPrefixDiagnostics.WriteMatches("Exclusion", prefixes, counts.AsSpan(0, prefixes.Length));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
        }
    }

}

/// <summary>Selects the check command output format.</summary>
internal enum CheckFormat
{
    /// <summary>Human-readable ASCII output for terminals.</summary>
    Text,

    /// <summary>GitHub-flavored Markdown output for CI summaries.</summary>
    Markdown,
}

/// <summary>Reads a persisted scan report at the application I/O boundary.</summary>
internal static class ScanReportFile
{
    public static bool TryRead(string path, out ScanReport report, out string error)
    {
        report = default;
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Unable to read report: {exception.Message}";
            return false;
        }

        if (ScanReportReader.TryRead(content, out report, out var parseError))
        {
            error = string.Empty;
            return true;
        }

        error = $"Unable to read report: {parseError}";
        return false;
    }
}

/// <summary>Reads and writes the acknowledgement baseline at the application I/O boundary.</summary>
internal static class BaselineFile
{
    public static bool TryRead(string path, out LicenseBaseline? baseline, out string error)
    {
        baseline = null;
        byte[] content;
        try
        {
            content = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Unable to read baseline: {exception.Message}";
            return false;
        }

        if (LicenseBaseline.TryParse(content, out var parsed, out var parseError))
        {
            baseline = parsed;
            error = string.Empty;
            return true;
        }

        error = $"Unable to read baseline: {parseError}";
        return false;
    }

    public static bool TryWrite(string path, ReadOnlySpan<LicenseBaselineEntry> entries, string licenseListVersion, out string error)
    {
        try
        {
            File.WriteAllBytes(path, LicenseBaseline.Serialize(entries, ToolVersion(), licenseListVersion));
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Unable to write baseline: {exception.Message}";
            return false;
        }
    }

    private static string ToolVersion()
        => typeof(BaselineFile).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}

internal static class CheckRenderer
{
    /// <summary>Reports whether every violation is a collection failure, which makes the run inconclusive rather than failed.</summary>
    public static bool IsIncomplete(ReadOnlySpan<LicensePolicyViolation> violations)
    {
        for (var i = 0; i < violations.Length; i++)
        {
            if (violations[i].Kind != LicensePolicyViolationKind.Error) return false;
        }

        return true;
    }

    public static void Write(
        IBufferWriter<byte> writer,
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<LicensePolicyViolation> violations,
        int policyComponentCount,
        int acknowledgedCount = -1,
        int developmentAllowedCount = -1,
        int excludedCount = -1,
        int ambiguityAllowedCount = 0,
        string[]? excludedInputPaths = null,
        ScanReportViewScope view = default,
        bool declaresNoComponents = false)
    {
        WriteDependencyFilter(writer, view);
        WriteExcludedInputPaths(writer, excludedInputPaths);
        WriteOptionalCount(writer, "Excluded from evaluation: "u8, excludedCount, includeZero: true);
        WriteOptionalCount(writer, "Acknowledged by baseline: "u8, acknowledgedCount, includeZero: true);
        WriteOptionalCount(writer, "Allowed by development policy: "u8, developmentAllowedCount, includeZero: true);
        WriteOptionalCount(writer, "Allowed on every reading of ambiguous evidence: "u8, ambiguityAllowedCount, includeZero: false);

        // A report whose input contributed no inventory proves nothing about licenses, and every count being zero
        // is exactly what a fully allowed project looks like. Stated before the allow-list result, because the
        // allow-list result is the sentence a reader would otherwise take as the answer.
        if (declaresNoComponents)
        {
            WriteUtf8(writer, "License check incomplete: the report states its input declared no resolved dependencies."u8);
            WriteNewLine(writer);
            return;
        }

        if (violations.IsEmpty)
        {
            WriteUtf8(writer, "License check passed: "u8);
            WriteInt32(writer, policyComponentCount);
            WriteUtf8(writer, policyComponentCount == 1 ? " component satisfies the allow-list."u8 : " components satisfy the allow-list."u8);
            WriteNewLine(writer);
            return;
        }

        // An incomplete run is stated as such: nothing was proven about those components, which is not the same
        // claim as a policy violation, and the exit code makes the same distinction.
        if (IsIncomplete(violations))
        {
            WriteUtf8(writer, "License check incomplete: "u8);
            WriteInt32(writer, violations.Length);
            WriteUtf8(writer, violations.Length == 1 ? " component could not be evaluated."u8 : " components could not be evaluated."u8);
        }
        else
        {
            WriteUtf8(writer, "License check failed: "u8);
            WriteInt32(writer, violations.Length);
            WriteUtf8(writer, violations.Length == 1 ? " violation."u8 : " violations."u8);
        }
        WriteNewLine(writer);
        WriteNewLine(writer);
        // Reason states why policy rejected the component; Mechanism states why its evidence never settled,
        // and only the second one names an action. The path names the direct dependency a reviewer can
        // actually change, which the row identifying only the offending package never does when the
        // violation is transitive.
        Span<int> widths = stackalloc int[]
        {
            "Package"u8.Length,
            "Version"u8.Length,
            "Ecosystem"u8.Length,
            "Purl"u8.Length,
            "License/Status"u8.Length,
            "Reason"u8.Length,
            "Mechanism"u8.Length,
            "Reference"u8.Length,
            "Path"u8.Length,
        };
        // The reference and the path are built strings, so the width pass keeps what it derived and the
        // write pass replays it. Resolve first, so nothing sits between the rental and its try.
        using var rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
        var rows = ArrayPool<ViolationRow>.Shared.Rent(violations.Length);
        try
        {
            for (var i = 0; i < violations.Length; i++)
            {
                var violation = violations[i];
                ref readonly var component = ref components[violation.ComponentIndex];
                var path = DependencyPathText.Introducer(inventory, rootPaths, component, violation.ComponentIndex);
                var row = ProjectViolation(component, violation.Kind, path);
                rows[i] = row;
                TextTable.Include(ref widths[0], Display(component.Name));
                TextTable.Include(ref widths[1], Display(component.Version));
                TextTable.Include(ref widths[2], component.Ecosystem);
                TextTable.Include(ref widths[3], Display(component.Purl));
                TextTable.Include(ref widths[4], LicenseOrStatus(component, violation.Kind));
                TextTable.Include(ref widths[5], Reason(violation.Kind));
                TextTable.Include(ref widths[6], MechanismUtf8(row));
                TextTable.Include(ref widths[7], row.Reference);
                TextTable.Include(ref widths[8], row.Path);
            }

            TextTable.WriteCell(writer, "Package"u8, widths[0]);
            TextTable.WriteCell(writer, "Version"u8, widths[1]);
            TextTable.WriteCell(writer, "Ecosystem"u8, widths[2]);
            TextTable.WriteCell(writer, "Purl"u8, widths[3]);
            TextTable.WriteCell(writer, "License/Status"u8, widths[4]);
            TextTable.WriteCell(writer, "Reason"u8, widths[5]);
            TextTable.WriteCell(writer, "Mechanism"u8, widths[6]);
            TextTable.WriteCell(writer, "Reference"u8, widths[7]);
            TextTable.WriteCell(writer, "Path"u8, widths[8], last: true);
            TextTable.WriteNewLine(writer);
            TextTable.WriteSeparator(writer, widths);

            var mechanismTally = new MechanismTally();
            for (var i = 0; i < violations.Length; i++)
            {
                var violation = violations[i];
                ref readonly var component = ref components[violation.ComponentIndex];
                var row = rows[i];
                if (row.Tallied) mechanismTally.Add(row.MechanismKind);

                TextTable.WriteCell(writer, Display(component.Name), widths[0]);
                TextTable.WriteCell(writer, Display(component.Version), widths[1]);
                TextTable.WriteCell(writer, component.Ecosystem, widths[2]);
                TextTable.WriteCell(writer, Display(component.Purl), widths[3]);
                TextTable.WriteCell(writer, LicenseOrStatus(component, violation.Kind), widths[4]);
                TextTable.WriteCell(writer, Reason(violation.Kind), widths[5]);
                TextTable.WriteCell(writer, MechanismUtf8(row), widths[6]);
                TextTable.WriteCell(writer, row.Reference, widths[7]);
                TextTable.WriteCell(writer, row.Path, widths[8], last: true);
                TextTable.WriteNewLine(writer);
            }

            mechanismTally.Write(writer);
        }
        finally
        {
            ArrayPool<ViolationRow>.Shared.Return(rows, clearArray: true);
        }
    }

    /// <summary>Writes the policy result and scan evidence as GitHub-flavored Markdown.</summary>
    public static void WriteMarkdown(
        IBufferWriter<byte> writer,
        in ScanReport report,
        ReadOnlySpan<LicensePolicyViolation> violations,
        int policyComponentCount,
        int acknowledgedCount = -1,
        int developmentAllowedCount = -1,
        int excludedCount = -1,
        int ambiguityAllowedCount = 0,
        string allowLicenses = "")
    {
        var components = report.Components;
        var summary = ScanSummary.Create(components);

        WriteUtf8(writer, "## ol license check"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);

        WriteUtf8(writer, "### Result"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteMarkdownResultBanner(writer, report.DeclaresNoComponents, violations, policyComponentCount);
        WriteNewLine(writer);
        WriteUtf8(writer, "| Item | Value |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---|"u8);
        WriteNewLine(writer);
        WriteMarkdownTextRow(writer, "Result"u8, GetMarkdownResult(report.DeclaresNoComponents, violations));
        WriteMarkdownTextRow(writer, "Allow-list"u8, allowLicenses);
        WriteMarkdownCountTextRow(writer, "Evaluated components"u8, policyComponentCount);
        WriteMarkdownCountTextRow(writer, "Violations"u8, violations.Length);
        WriteMarkdownOptionalTextRow(writer, "Acknowledged by baseline"u8, acknowledgedCount);
        WriteMarkdownOptionalTextRow(writer, "Allowed by development policy"u8, developmentAllowedCount);
        WriteMarkdownOptionalTextRow(writer, "Excluded from evaluation"u8, excludedCount);
        if (ambiguityAllowedCount > 0)
        {
            WriteMarkdownCountTextRow(writer, "Allowed on every ambiguous reading"u8, ambiguityAllowedCount);
        }

        if (report.DeclaresNoComponents)
        {
            WriteMarkdownTextRow(writer, "Note"u8, "report declares no resolved dependencies"u8);
        }
        else if (!violations.IsEmpty && IsIncomplete(violations))
        {
            WriteMarkdownTextRow(writer, "Note"u8, "collection failures make the result inconclusive"u8);
        }

        WriteNewLine(writer);
        WriteUtf8(writer, "### Violations"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        var mechanismTally = new MechanismTally();
        if (violations.IsEmpty)
        {
            WriteUtf8(writer, "No policy violations."u8);
            WriteNewLine(writer);
        }
        else
        {
            WriteUtf8(writer, "| Package | Version | Ecosystem | Purl | License/Status | Reason | Mechanism | Reference | Origin(s) | Path |"u8);
            WriteNewLine(writer);
            WriteUtf8(writer, "|---|---|---|---|---|---|---|---|---|---|"u8);
            WriteNewLine(writer);

            using var rootPaths = DependencyPathResolver.BuildRootPaths(report.Inventory);
            var usageOrigins = UsageOriginProjection.Create(report.Inventory, components, violations, rootPaths);
            for (var i = 0; i < violations.Length; i++)
            {
                var violation = violations[i];
                ref readonly var component = ref components[violation.ComponentIndex];
                var path = DependencyPathText.Introducer(report.Inventory, rootPaths, component, violation.ComponentIndex);
                var row = ProjectViolation(component, violation.Kind, path);
                if (row.Tallied) mechanismTally.Add(row.MechanismKind);

                WriteUtf8(writer, "| "u8);
                WriteMarkdownValue(writer, component.Name);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, component.Version);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, component.Ecosystem);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, component.Purl);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, MarkdownLicenseOrStatus(component, violation.Kind));
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, Reason(violation.Kind));
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, MechanismUtf8(row));
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, row.Reference);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownOrigins(writer, usageOrigins.GetOrigins(i), report.Inventory.Contexts);
                WriteUtf8(writer, " | "u8);
                WriteMarkdownValue(writer, row.Path);
                WriteUtf8(writer, " |"u8);
                WriteNewLine(writer);
            }

            WriteMarkdownUsageOrigins(writer, usageOrigins, components, report.Inventory.Contexts);
        }

        mechanismTally.WriteMarkdown(writer);

        WriteNewLine(writer);
        WriteUtf8(writer, "### Resolved license usage"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteMarkdownLicenseCounts(writer, components);

        WriteNewLine(writer);
        WriteMarkdownCoverage(writer, summary, components);

        WriteNewLine(writer);
        WriteMarkdownAllComponents(writer, components);

        WriteNewLine(writer);
        WriteMarkdownScanDiagnostics(writer, report);
    }

    /// <summary>
    /// States the <c>--dependency</c> filter the producing scan applied, when it applied one.
    /// </summary>
    /// <remarks>
    /// A filtered report is a narrower population than the scan resolved, and <c>check</c> evaluates whatever the
    /// report holds. Saying nothing would leave a partial evaluation reading exactly like a complete one, which is
    /// the failure the report's own <c>metadata.view</c> exists to prevent. The excluded relationships no input
    /// determined are counted separately because policy keeps those fail-closed, so dropping them is the part of the
    /// exclusion that changes what a gate can prove.
    /// </remarks>
    private static void WriteDependencyFilter(IBufferWriter<byte> writer, in ScanReportViewScope view)
    {
        if (!view.IsFiltered) return;

        WriteUtf8(writer, "Dependency filter: "u8);
        WriteUtf8(writer, view.DependencyFilter);
        WriteUtf8(writer, "; "u8);
        WriteInt32(writer, view.ExcludedCount);
        WriteUtf8(writer, view.ExcludedCount == 1 ? " component excluded by the producing scan"u8 : " components excluded by the producing scan"u8);
        if (view.ExcludedUnknownCount > 0)
        {
            WriteUtf8(writer, ", "u8);
            WriteInt32(writer, view.ExcludedUnknownCount);
            WriteUtf8(writer, " with an unknown relationship"u8);
        }

        WriteUtf8(writer, "."u8);
        WriteNewLine(writer);
    }

    private static void WriteExcludedInputPaths(IBufferWriter<byte> writer, string[]? excludedInputPaths)
    {
        if (excludedInputPaths is not { Length: > 0 }) return;

        WriteUtf8(writer, "Excluded input paths: "u8);
        for (var i = 0; i < excludedInputPaths.Length; i++)
        {
            if (i != 0) WriteUtf8(writer, ", "u8);
            WriteUtf8(writer, excludedInputPaths[i]);
        }
        WriteUtf8(writer, "."u8);
        WriteNewLine(writer);
    }

    /// <summary>One violation row's derived text, kept between the width pass and the write pass.</summary>
    private readonly record struct ViolationRow(bool Tallied, bool NamedMechanism, UnresolvedMechanismKind MechanismKind, string Reference, string Path);

    /// <summary>Associates one violated report component with one inventory resolution context.</summary>
    private readonly record struct ComponentOrigin(int ViolationIndex, int ComponentIndex, int ContextIndex);

    /// <summary>Locates a report component in the inventory when no dependency-path index was built.</summary>
    private readonly record struct ComponentIdentity(
        Utf8Slice Name,
        Utf8Slice Version,
        Utf8Slice Purl,
        Utf8Slice SourceId,
        string Ecosystem)
    {
        public ComponentIdentity(in ScanComponent component)
            : this(component.Name, component.Version, component.Purl, component.SourceId, component.Ecosystem)
        {
        }
    }

    /// <summary>Projects distinct usage origins once for both the violation rows and the origin summary.</summary>
    private readonly record struct UsageOriginProjection(
        ComponentOrigin[] ByViolation,
        ComponentOrigin[] ByOrigin,
        OriginRange[] Ranges)
    {
        public static UsageOriginProjection Create(
            in DependencyInventory inventory,
            ReadOnlySpan<ScanComponent> components,
            ReadOnlySpan<LicensePolicyViolation> violations,
            scoped in DependencyRootPaths rootPaths)
        {
            var contexts = inventory.Contexts;
            var occurrences = inventory.Occurrences;
            if (contexts.Length == 0 || occurrences.Length == 0)
            {
                return new UsageOriginProjection([], [], []);
            }

            var violationByComponent = new int[inventory.Components.Length];
            violationByComponent.AsSpan().Fill(-1);
            Dictionary<ComponentIdentity, int>? inventoryByIdentity = null;
            for (var violationIndex = 0; violationIndex < violations.Length; violationIndex++)
            {
                var reportComponentIndex = violations[violationIndex].ComponentIndex;
                ref readonly var component = ref components[reportComponentIndex];
                var inventoryComponentIndex = rootPaths.FindComponentIndex(component, reportComponentIndex);
                if (inventoryComponentIndex < 0)
                {
                    inventoryByIdentity ??= BuildInventoryIdentityIndex(inventory.Components);
                    if (!inventoryByIdentity.TryGetValue(new ComponentIdentity(component), out inventoryComponentIndex))
                    {
                        inventoryComponentIndex = -1;
                    }
                }

                if ((uint)inventoryComponentIndex < (uint)violationByComponent.Length)
                {
                    violationByComponent[inventoryComponentIndex] = violationIndex;
                }
            }

            // Sized for every occurrence, but only the violating and distinct pairs survive: rent the scratch
            // buffer so its unused tail is not retained for the length of the Markdown write.
            var pairs = ArrayPool<ComponentOrigin>.Shared.Rent(occurrences.Length);
            try
            {
                var pairCount = 0;
                for (var occurrenceIndex = 0; occurrenceIndex < occurrences.Length; occurrenceIndex++)
                {
                    var occurrence = occurrences[occurrenceIndex];
                    if ((uint)occurrence.ComponentIndex >= (uint)violationByComponent.Length
                        || (uint)occurrence.ContextIndex >= (uint)contexts.Length)
                    {
                        continue;
                    }

                    var violationIndex = violationByComponent[occurrence.ComponentIndex];
                    if (violationIndex < 0 || GetUsageOriginPrimary(contexts[occurrence.ContextIndex]).IsEmpty) continue;
                    pairs[pairCount++] = new ComponentOrigin(
                        violationIndex,
                        violations[violationIndex].ComponentIndex,
                        occurrence.ContextIndex);
                }

                if (pairCount == 0)
                {
                    return new UsageOriginProjection([], [], []);
                }

                var comparer = new ComponentOriginComparer(contexts, originFirst: false);
                Array.Sort(pairs, 0, pairCount, comparer);
                var distinctCount = 0;
                for (var i = 0; i < pairCount; i++)
                {
                    if (distinctCount != 0
                        && pairs[distinctCount - 1].ViolationIndex == pairs[i].ViolationIndex
                        && UsageOriginEquals(contexts[pairs[distinctCount - 1].ContextIndex], contexts[pairs[i].ContextIndex]))
                    {
                        continue;
                    }

                    pairs[distinctCount++] = pairs[i];
                }

                var ranges = new OriginRange[violations.Length];
                for (var start = 0; start < distinctCount;)
                {
                    var end = start + 1;
                    while (end < distinctCount && pairs[end].ViolationIndex == pairs[start].ViolationIndex) end++;
                    ranges[pairs[start].ViolationIndex] = new OriginRange(start, end - start);
                    start = end;
                }

                var byViolation = pairs.AsSpan(0, distinctCount).ToArray();
                var byOrigin = byViolation.AsSpan().ToArray();
                Array.Sort(byOrigin, new ComponentOriginComparer(contexts, originFirst: true));
                return new UsageOriginProjection(byViolation, byOrigin, ranges);
            }
            finally
            {
                ArrayPool<ComponentOrigin>.Shared.Return(pairs);
            }
        }

        public ReadOnlySpan<ComponentOrigin> GetOrigins(int violationIndex)
        {
            if ((uint)violationIndex >= (uint)Ranges.Length) return [];
            var range = Ranges[violationIndex];
            return ByViolation.AsSpan(range.Start, range.Length);
        }

        private static Dictionary<ComponentIdentity, int> BuildInventoryIdentityIndex(ScanComponent[] components)
        {
            var result = new Dictionary<ComponentIdentity, int>(components.Length);
            for (var i = 0; i < components.Length; i++) result.TryAdd(new ComponentIdentity(components[i]), i);
            return result;
        }
    }

    private readonly record struct OriginRange(int Start, int Length);

    private sealed class ComponentOriginComparer(DependencyResolutionContext[] contexts, bool originFirst) : IComparer<ComponentOrigin>
    {
        public int Compare(ComponentOrigin left, ComponentOrigin right)
        {
            var byOrigin = CompareUsageOrigin(contexts[left.ContextIndex], contexts[right.ContextIndex]);
            var byViolation = left.ViolationIndex.CompareTo(right.ViolationIndex);
            return originFirst
                ? byOrigin != 0 ? byOrigin : byViolation
                : byViolation != 0 ? byViolation : byOrigin;
        }
    }

    private static Utf8Slice GetUsageOriginPrimary(in DependencyResolutionContext context)
        => context.ProjectIdentity.IsEmpty ? context.InputPath : context.ProjectIdentity;

    private static Utf8Slice GetUsageOriginInputPath(in DependencyResolutionContext context)
        => context.ProjectIdentity.IsEmpty
            || context.InputPath.IsEmpty
            || context.ProjectIdentity.Equals(context.InputPath)
                ? default
                : context.InputPath;

    private static int CompareUsageOrigin(in DependencyResolutionContext left, in DependencyResolutionContext right)
    {
        var byPrimary = Utf8Slice.CompareOrdinal(GetUsageOriginPrimary(left), GetUsageOriginPrimary(right));
        return byPrimary != 0
            ? byPrimary
            : Utf8Slice.CompareOrdinal(GetUsageOriginInputPath(left), GetUsageOriginInputPath(right));
    }

    private static bool UsageOriginEquals(in DependencyResolutionContext left, in DependencyResolutionContext right)
        => GetUsageOriginPrimary(left).Equals(GetUsageOriginPrimary(right))
            && GetUsageOriginInputPath(left).Equals(GetUsageOriginInputPath(right));

    private static void WriteMarkdownOrigin(IBufferWriter<byte> writer, in DependencyResolutionContext context)
    {
        WriteMarkdownValue(writer, GetUsageOriginPrimary(context));
        var inputPath = GetUsageOriginInputPath(context);
        if (inputPath.IsEmpty) return;
        WriteUtf8(writer, " ("u8);
        WriteMarkdownValue(writer, inputPath);
        WriteUtf8(writer, ")"u8);
    }

    private static void WriteMarkdownOrigins(
        IBufferWriter<byte> writer,
        ReadOnlySpan<ComponentOrigin> origins,
        DependencyResolutionContext[] contexts)
    {
        if (origins.IsEmpty)
        {
            WriteUtf8(writer, "-"u8);
            return;
        }

        for (var i = 0; i < origins.Length; i++)
        {
            if (i != 0) WriteUtf8(writer, ", "u8);
            WriteMarkdownOrigin(writer, contexts[origins[i].ContextIndex]);
        }
    }

    private static void WriteMarkdownUsageOrigins(
        IBufferWriter<byte> writer,
        in UsageOriginProjection projection,
        ReadOnlySpan<ScanComponent> components,
        DependencyResolutionContext[] contexts)
    {
        WriteNewLine(writer);
        WriteUtf8(writer, "### Usage origins"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        var origins = projection.ByOrigin;
        if (origins.Length == 0)
        {
            WriteUtf8(writer, "No usage origins are recorded for these violations."u8);
            WriteNewLine(writer);
            return;
        }

        WriteUtf8(writer, "| Origin | Ecosystem | Violating packages |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---|---|"u8);
        WriteNewLine(writer);
        var ecosystems = new HashSet<string>(StringComparer.Ordinal);
        for (var start = 0; start < origins.Length;)
        {
            ref readonly var origin = ref contexts[origins[start].ContextIndex];
            var end = start + 1;
            while (end < origins.Length && UsageOriginEquals(origin, contexts[origins[end].ContextIndex])) end++;

            WriteUtf8(writer, "| "u8);
            WriteMarkdownOrigin(writer, origin);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownOriginEcosystems(writer, origins[start..end], components, ecosystems);
            WriteUtf8(writer, " | "u8);
            for (var i = start; i < end; i++)
            {
                if (i != start) WriteUtf8(writer, ", "u8);
                ref readonly var component = ref components[origins[i].ComponentIndex];
                WriteMarkdownValue(writer, component.Name);
                if (!component.Version.IsEmpty)
                {
                    WriteUtf8(writer, " "u8);
                    WriteMarkdownValue(writer, component.Version);
                }
            }
            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
            start = end;
        }
    }

    private static void WriteMarkdownOriginEcosystems(
        IBufferWriter<byte> writer,
        ReadOnlySpan<ComponentOrigin> origins,
        ReadOnlySpan<ScanComponent> components,
        HashSet<string> ecosystems)
    {
        ecosystems.Clear();
        var written = 0;
        for (var i = 0; i < origins.Length; i++)
        {
            var ecosystem = components[origins[i].ComponentIndex].Ecosystem;
            if (!ecosystems.Add(ecosystem)) continue;
            if (written++ != 0) WriteUtf8(writer, ", "u8);
            WriteMarkdownValue(writer, ecosystem);
        }
    }

    /// <summary>Projects the Mechanism and Reference columns and the mechanism tally key.</summary>
    /// <remarks>
    /// A resolved license the allow-list rejects has no collection mechanism to name, so it is left out of
    /// the tally rather than counted as an unexplained one: the allow-list already explains it, and mixing
    /// the two populations would make the tally report the larger one.
    /// </remarks>
    private static ViolationRow ProjectViolation(in ScanComponent component, LicensePolicyViolationKind kind, string path)
    {
        if (kind == LicensePolicyViolationKind.NotAllowed)
        {
            return new ViolationRow(Tallied: false, NamedMechanism: false, default, "-", path);
        }

        if (!UnresolvedMechanism.TryGetReason(component, out var reason))
        {
            return new ViolationRow(Tallied: true, NamedMechanism: false, UnresolvedMechanismKind.None, "-", path);
        }

        var reference = UnresolvedMechanism.GetReference(component, reason);
        return new ViolationRow(Tallied: true, NamedMechanism: true, reason, reference.Length == 0 ? "-" : reference, path);
    }

    private static ReadOnlySpan<byte> MechanismUtf8(in ViolationRow row)
        => row.NamedMechanism ? UnresolvedMechanism.GetNameUtf8(row.MechanismKind) : "-"u8;

    /// <summary>
    /// Counts how many violations each unresolved mechanism explains.
    /// </summary>
    /// <remarks>
    /// A hundred rows reading "license is unresolved" look like a hundred problems. They are usually a
    /// handful of populations, and which population a component belongs to decides what a reviewer does
    /// about all of them at once. Ordered by count so the largest is read first, ties broken by name so
    /// two runs over the same report print the same block.
    /// </remarks>
    private sealed class MechanismTally
    {
        private readonly Dictionary<UnresolvedMechanismKind, int> counts = [];

        public void Add(UnresolvedMechanismKind mechanism)
            => counts[mechanism] = counts.TryGetValue(mechanism, out var count) ? count + 1 : 1;

        public void Write(IBufferWriter<byte> writer)
        {
            if (counts.Count == 0)
            {
                return;
            }

            var ordered = new KeyValuePair<UnresolvedMechanismKind, int>[counts.Count];
            ((ICollection<KeyValuePair<UnresolvedMechanismKind, int>>)counts).CopyTo(ordered, 0);
            Array.Sort(ordered, static (left, right) =>
            {
                var byCount = right.Value.CompareTo(left.Value);
                return byCount != 0
                    ? byCount
                    : UnresolvedMechanism.GetNameUtf8(left.Key).SequenceCompareTo(UnresolvedMechanism.GetNameUtf8(right.Key));
            });

            WriteNewLine(writer);
            WriteUtf8(writer, "Unresolved mechanisms"u8);
            WriteNewLine(writer);
            for (var i = 0; i < ordered.Length; i++)
            {
                WriteUtf8(writer, "  "u8);
                WriteUtf8(writer, UnresolvedMechanism.GetNameUtf8(ordered[i].Key));
                WriteUtf8(writer, ": "u8);
                WriteInt32(writer, ordered[i].Value);
                WriteNewLine(writer);
            }
        }

        public void WriteMarkdown(IBufferWriter<byte> writer)
        {
            if (counts.Count == 0) return;

            var ordered = new KeyValuePair<UnresolvedMechanismKind, int>[counts.Count];
            ((ICollection<KeyValuePair<UnresolvedMechanismKind, int>>)counts).CopyTo(ordered, 0);
            Array.Sort(ordered, static (left, right) =>
            {
                var byCount = right.Value.CompareTo(left.Value);
                return byCount != 0
                    ? byCount
                    : UnresolvedMechanism.GetNameUtf8(left.Key).SequenceCompareTo(UnresolvedMechanism.GetNameUtf8(right.Key));
            });

            WriteNewLine(writer);
            WriteUtf8(writer, "### Unresolved mechanisms"u8);
            WriteNewLine(writer);
            WriteNewLine(writer);
            WriteUtf8(writer, "| Mechanism | Components |"u8);
            WriteNewLine(writer);
            WriteUtf8(writer, "|---|---:|"u8);
            WriteNewLine(writer);
            for (var i = 0; i < ordered.Length; i++)
            {
                WriteUtf8(writer, "| "u8);
                WriteUtf8(writer, UnresolvedMechanism.GetNameUtf8(ordered[i].Key));
                WriteUtf8(writer, " | "u8);
                WriteInt32(writer, ordered[i].Value);
                WriteUtf8(writer, " |"u8);
                WriteNewLine(writer);
            }
        }
    }

    private static void WriteMarkdownLicenseCounts(IBufferWriter<byte> writer, ReadOnlySpan<ScanComponent> components)
    {
        var counts = new Dictionary<Utf8Slice, int>();
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (component.Status != LicenseStatus.Matched || component.License.IsEmpty) continue;
            counts[component.License] = counts.TryGetValue(component.License, out var count) ? count + 1 : 1;
        }

        if (counts.Count == 0)
        {
            WriteUtf8(writer, "No resolved license expressions."u8);
            WriteNewLine(writer);
            return;
        }

        var ordered = new KeyValuePair<Utf8Slice, int>[counts.Count];
        ((ICollection<KeyValuePair<Utf8Slice, int>>)counts).CopyTo(ordered, 0);
        Array.Sort(ordered, static (left, right) =>
        {
            var byCount = right.Value.CompareTo(left.Value);
            return byCount != 0 ? byCount : Utf8Slice.CompareOrdinal(left.Key, right.Key);
        });

        WriteUtf8(writer, "| SPDX expression | Components |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---:|"u8);
        WriteNewLine(writer);
        for (var i = 0; i < ordered.Length; i++)
        {
            WriteUtf8(writer, "| "u8);
            WriteMarkdownValue(writer, ordered[i].Key);
            WriteUtf8(writer, " | "u8);
            WriteInt32(writer, ordered[i].Value);
            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
        }
    }

    private static void WriteMarkdownResultBanner(
        IBufferWriter<byte> writer,
        bool declaresNoComponents,
        ReadOnlySpan<LicensePolicyViolation> violations,
        int policyComponentCount)
    {
        WriteUtf8(writer, "> "u8);
        if (declaresNoComponents)
        {
            WriteUtf8(writer, "⚠️ **inconclusive** — report declares no resolved dependencies."u8);
        }
        else if (!violations.IsEmpty && IsIncomplete(violations))
        {
            WriteUtf8(writer, "⚠️ **inconclusive** — collection failures make the result inconclusive."u8);
        }
        else if (violations.IsEmpty)
        {
            WriteUtf8(writer, "✅ **passed** — "u8);
            WriteInt32(writer, policyComponentCount);
            WriteUtf8(writer, policyComponentCount == 1 ? " component satisfies the allow-list."u8 : " components satisfy the allow-list."u8);
        }
        else
        {
            WriteUtf8(writer, "❌ **failed** — "u8);
            WriteInt32(writer, violations.Length);
            WriteUtf8(writer, violations.Length == 1 ? " violation."u8 : " violations."u8);
        }

        WriteNewLine(writer);
    }

    private static void WriteMarkdownCoverage(IBufferWriter<byte> writer, in ScanSummary summary, ReadOnlySpan<ScanComponent> components)
    {
        WriteUtf8(writer, "### Coverage"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteUtf8(writer, "| License status | Components |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---:|"u8);
        WriteNewLine(writer);
        WriteMarkdownCountRow(writer, "Total"u8, components.Length);
        WriteMarkdownCountRow(writer, "Matched"u8, summary.Matched);
        WriteMarkdownCountRow(writer, "Conflict"u8, summary.Conflict);
        WriteMarkdownCountRow(writer, "Unknown"u8, summary.Unknown);
        WriteMarkdownCountRow(writer, "Ambiguous"u8, summary.Ambiguous);
        WriteMarkdownCountRow(writer, "Invalid"u8, summary.Invalid);
        WriteMarkdownCountRow(writer, "Error"u8, summary.Error);
        WriteNewLine(writer);
        WriteUtf8(writer, "| Supplied by | Components |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---:|"u8);
        WriteNewLine(writer);
        WriteMarkdownCountRow(writer, "SBOM only"u8, summary.SbomOnlyCount);
        WriteMarkdownCountRow(writer, "Package manager only"u8, summary.PackageManagerOnlyCount);
        WriteMarkdownCountRow(writer, "Both"u8, summary.BothSuppliedCount);
        WriteNewLine(writer);
        WriteUtf8(writer, "| Finding | Count |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---:|"u8);
        WriteNewLine(writer);
        WriteMarkdownCountRow(writer, "Warnings on unresolved components"u8, summary.UnresolvedWarningCount);
        WriteMarkdownCountRow(writer, "Warnings on resolved components"u8, summary.ResolvedWarningCount);
        WriteMarkdownCountRow(writer, "Deprecated SPDX identifiers"u8, summary.DeprecatedSpdxCount);
        WriteNewLine(writer);
        WriteMarkdownEcosystemRows(writer, components);
    }

    private static void WriteMarkdownEcosystemRows(IBufferWriter<byte> writer, ReadOnlySpan<ScanComponent> components)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < components.Length; i++)
        {
            var ecosystem = components[i].Ecosystem;
            counts[ecosystem] = counts.TryGetValue(ecosystem, out var count) ? count + 1 : 1;
        }

        WriteUtf8(writer, "| Ecosystem | Components |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---:|"u8);
        WriteNewLine(writer);

        if (counts.Count == 0) return;

        var ordered = new KeyValuePair<string, int>[counts.Count];
        ((ICollection<KeyValuePair<string, int>>)counts).CopyTo(ordered, 0);
        Array.Sort(ordered, static (left, right) =>
        {
            var byCount = right.Value.CompareTo(left.Value);
            return byCount != 0 ? byCount : StringComparer.Ordinal.Compare(left.Key, right.Key);
        });

        for (var i = 0; i < ordered.Length; i++)
        {
            WriteUtf8(writer, "| "u8);
            WriteMarkdownValue(writer, ordered[i].Key);
            WriteUtf8(writer, " | "u8);
            WriteInt32(writer, ordered[i].Value);
            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
        }
    }

    private static void WriteMarkdownAllComponents(IBufferWriter<byte> writer, ReadOnlySpan<ScanComponent> components)
    {
        WriteUtf8(writer, "### All components"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteUtf8(writer, "<details>"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "<summary>Show all components ("u8);
        WriteInt32(writer, components.Length);
        WriteUtf8(writer, ")</summary>"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteUtf8(writer, "| Package | Version | Ecosystem | License | Status | Dependency | Supply | Purl |"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "|---|---|---|---|---|---|---|---|"u8);
        WriteNewLine(writer);
        for (var i = 0; i < components.Length; i++)
        {
            ref readonly var component = ref components[i];
            WriteUtf8(writer, "| "u8);
            WriteMarkdownValue(writer, component.Name);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Version);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Ecosystem);
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, Display(component.License));
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Status.ToUtf8());
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, GetDependencyTypeUtf8(component.DependencyType));
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, GetSuppliedByUtf8(component.SuppliedBy));
            WriteUtf8(writer, " | "u8);
            WriteMarkdownValue(writer, component.Purl);
            WriteUtf8(writer, " |"u8);
            WriteNewLine(writer);
        }

        WriteNewLine(writer);
        WriteUtf8(writer, "</details>"u8);
        WriteNewLine(writer);
    }

    private static void WriteMarkdownScanDiagnostics(IBufferWriter<byte> writer, in ScanReport report)
    {
        WriteUtf8(writer, "### Diagnostics"u8);
        WriteNewLine(writer);
        WriteNewLine(writer);
        WriteUtf8(writer, "- input: `"u8);
        WriteMarkdownValue(writer, report.Inventory.Input.Kind.Name);
        WriteUtf8(writer, "/"u8);
        WriteMarkdownValue(writer, report.Inventory.Input.Format.Name);
        WriteUtf8(writer, "`"u8);
        WriteNewLine(writer);
        WriteUtf8(writer, "- source: "u8);
        WriteMarkdownValue(writer, report.SourceReference);
        WriteNewLine(writer);
        WriteUtf8(writer, "- SPDX license list: "u8);
        WriteMarkdownValue(writer, report.LicenseListVersion);
        WriteNewLine(writer);
        if (report.View.IsFiltered)
        {
            WriteUtf8(writer, "- dependency filter: `"u8);
            WriteMarkdownValue(writer, report.View.DependencyFilter);
            WriteUtf8(writer, "`, "u8);
            WriteInt32(writer, report.View.ExcludedCount);
            WriteUtf8(writer, " excluded, "u8);
            WriteInt32(writer, report.View.ExcludedUnknownCount);
            WriteUtf8(writer, " with unknown relationship"u8);
            WriteNewLine(writer);
        }

        if (report.InputDiscovery is { } inputDiscovery)
        {
            WriteUtf8(writer, "- detected input files: "u8);
            WriteInt32(writer, inputDiscovery.DetectedFileCount);
            WriteNewLine(writer);
            if (inputDiscovery.IgnoredCandidates is { Length: > 0 })
            {
                WriteUtf8(writer, "- ignored input candidates: "u8);
                for (var i = 0; i < inputDiscovery.IgnoredCandidates.Length; i++)
                {
                    if (i != 0) WriteUtf8(writer, ", "u8);
                    WriteUtf8(writer, "`"u8);
                    WriteMarkdownValue(writer, inputDiscovery.IgnoredCandidates[i]);
                    WriteUtf8(writer, "`"u8);
                }

                WriteNewLine(writer);
            }

            if (inputDiscovery.IncompleteInputSetCount > 0)
            {
                WriteUtf8(writer, "- incomplete input sets: "u8);
                WriteInt32(writer, inputDiscovery.IncompleteInputSetCount);
                WriteNewLine(writer);
            }
        }

        if (report.ExcludedInputPaths is { Length: > 0 } excludedInputPaths)
        {
            WriteUtf8(writer, "- excluded input paths: "u8);
            for (var i = 0; i < excludedInputPaths.Length; i++)
            {
                if (i != 0) WriteUtf8(writer, ", "u8);
                WriteUtf8(writer, "`"u8);
                WriteMarkdownValue(writer, excludedInputPaths[i]);
                WriteUtf8(writer, "`"u8);
            }

            WriteNewLine(writer);
        }

        if (report.Warnings is { Length: > 0 } warnings)
        {
            WriteUtf8(writer, "- warnings: "u8);
            for (var i = 0; i < warnings.Length; i++)
            {
                if (i != 0) WriteUtf8(writer, ", "u8);
                WriteUtf8(writer, "`"u8);
                WriteMarkdownValue(writer, warnings[i]);
                WriteUtf8(writer, "`"u8);
            }

            WriteNewLine(writer);
        }
    }

    private static void WriteMarkdownCountRow(IBufferWriter<byte> writer, ReadOnlySpan<byte> label, int count)
    {
        WriteUtf8(writer, "| "u8);
        WriteUtf8(writer, label);
        WriteUtf8(writer, " | "u8);
        WriteInt32(writer, count);
        WriteUtf8(writer, " |"u8);
        WriteNewLine(writer);
    }

    private static void WriteMarkdownTextRow(IBufferWriter<byte> writer, ReadOnlySpan<byte> label, ReadOnlySpan<byte> value)
    {
        WriteUtf8(writer, "| "u8);
        WriteUtf8(writer, label);
        WriteUtf8(writer, " | "u8);
        WriteMarkdownValue(writer, value);
        WriteUtf8(writer, " |"u8);
        WriteNewLine(writer);
    }

    private static void WriteMarkdownTextRow(IBufferWriter<byte> writer, ReadOnlySpan<byte> label, string value)
    {
        WriteUtf8(writer, "| "u8);
        WriteUtf8(writer, label);
        WriteUtf8(writer, " | "u8);
        WriteMarkdownValue(writer, value);
        WriteUtf8(writer, " |"u8);
        WriteNewLine(writer);
    }

    private static void WriteMarkdownCountTextRow(IBufferWriter<byte> writer, ReadOnlySpan<byte> label, int count)
    {
        WriteUtf8(writer, "| "u8);
        WriteUtf8(writer, label);
        WriteUtf8(writer, " | "u8);
        WriteInt32(writer, count);
        WriteUtf8(writer, " |"u8);
        WriteNewLine(writer);
    }

    private static void WriteMarkdownOptionalTextRow(IBufferWriter<byte> writer, ReadOnlySpan<byte> label, int count)
    {
        if (count < 0) return;
        WriteMarkdownCountTextRow(writer, label, count);
    }

    private static ReadOnlySpan<byte> GetMarkdownResult(bool declaresNoComponents, ReadOnlySpan<LicensePolicyViolation> violations)
        => declaresNoComponents || !violations.IsEmpty && IsIncomplete(violations)
            ? "inconclusive"u8
            : violations.IsEmpty ? "passed"u8 : "failed"u8;

    /// <summary>Writes one optional component counter without materializing its decimal or surrounding sentence.</summary>
    private static void WriteOptionalCount(IBufferWriter<byte> writer, ReadOnlySpan<byte> prefix, int count, bool includeZero)
    {
        if (count < 0 || (!includeZero && count == 0)) return;

        WriteUtf8(writer, prefix);
        WriteInt32(writer, count);
        WriteUtf8(writer, count == 1 ? " component."u8 : " components."u8);
        WriteNewLine(writer);
    }

    private static ReadOnlySpan<byte> Display(Utf8Slice value)
        => value.IsEmpty ? "-"u8 : value.Span;

    /// <summary>Writes a table value while keeping source-backed UTF-8 values zero-copy.</summary>
    private static void WriteMarkdownValue(IBufferWriter<byte> writer, Utf8Slice value)
        => MarkdownTableCellWriter.Write(writer, value);

    /// <summary>Writes a UTF-8 table value while keeping source-backed bytes zero-copy.</summary>
    private static void WriteMarkdownValue(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
        => MarkdownTableCellWriter.Write(writer, value);

    /// <summary>Writes an owned table value without allocating an escaped copy.</summary>
    private static void WriteMarkdownValue(IBufferWriter<byte> writer, string value)
        => MarkdownTableCellWriter.Write(writer, value);

    private static ReadOnlySpan<byte> LicenseOrStatus(in ScanComponent component, LicensePolicyViolationKind kind)
        => component.Status == LicenseStatus.Matched ? Display(component.License) : Status(kind);

    private static ReadOnlySpan<byte> MarkdownLicenseOrStatus(in ScanComponent component, LicensePolicyViolationKind kind)
        => !component.License.IsEmpty ? Display(component.License) : Status(kind);

    private static ReadOnlySpan<byte> GetDependencyTypeUtf8(DependencyType value) => value switch
    {
        DependencyType.Unknown => "unknown"u8,
        DependencyType.Root => "root"u8,
        DependencyType.Direct => "direct"u8,
        DependencyType.Transitive => "transitive"u8,
        _ => default,
    };

    private static ReadOnlySpan<byte> GetSuppliedByUtf8(ComponentSupply value) => value switch
    {
        ComponentSupply.Sbom => "sbom"u8,
        ComponentSupply.PackageManager => "package-manager"u8,
        ComponentSupply.Sbom | ComponentSupply.PackageManager => "sbom,package-manager"u8,
        _ => "-"u8,
    };

    private static void WriteInt32(IBufferWriter<byte> writer, int value)
    {
        var destination = writer.GetSpan(11);
        Utf8Formatter.TryFormat(value, destination, out var written);
        writer.Advance(written);
    }

    private static void WriteNewLine(IBufferWriter<byte> writer)
        => WriteUtf8(writer, Environment.NewLine);

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, ReadOnlySpan<char> value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
    }

    private static ReadOnlySpan<byte> Status(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.Conflict => "conflict"u8,
        LicensePolicyViolationKind.Unknown => "unknown"u8,
        LicensePolicyViolationKind.Ambiguous => "ambiguous"u8,
        LicensePolicyViolationKind.Invalid => "invalid"u8,
        LicensePolicyViolationKind.Error => "error"u8,
        _ => "matched"u8,
    };

    private static ReadOnlySpan<byte> Reason(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "license is not allowed"u8,
        LicensePolicyViolationKind.Conflict => "license evidence conflicts"u8,
        LicensePolicyViolationKind.Unknown => "license is unresolved"u8,
        LicensePolicyViolationKind.Ambiguous => "license is ambiguous"u8,
        LicensePolicyViolationKind.Invalid => "license expression is invalid"u8,
        LicensePolicyViolationKind.Error => "license evidence could not be completed"u8,
        _ => "license policy violation"u8,
    };
}
