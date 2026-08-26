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
        string? sarif = null)
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
        using var rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
        for (var i = 0; i < violations.Length; i++)
        {
            var violation = violations[i];
            var component = components[violation.ComponentIndex];
            GetMechanism(component, violation.Kind, out var mechanism, out var reference, out _);
            TextTable.Include(ref widths[0], Display(component.Name));
            TextTable.Include(ref widths[1], Display(component.Version));
            TextTable.Include(ref widths[2], component.Ecosystem);
            TextTable.Include(ref widths[3], Display(component.Purl));
            TextTable.Include(ref widths[4], LicenseOrStatus(component, violation.Kind));
            TextTable.Include(ref widths[5], Reason(violation.Kind));
            TextTable.Include(ref widths[6], mechanism);
            TextTable.Include(ref widths[7], reference);
            var path = DependencyPathText.Introducer(inventory, rootPaths, component, violation.ComponentIndex);
            TextTable.Include(ref widths[8], path);
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
            var component = components[violation.ComponentIndex];
            if (GetMechanism(component, violation.Kind, out var mechanism, out var reference, out var mechanismKind))
            {
                mechanismTally.Add(mechanismKind);
            }

            TextTable.WriteCell(writer, Display(component.Name), widths[0]);
            TextTable.WriteCell(writer, Display(component.Version), widths[1]);
            TextTable.WriteCell(writer, component.Ecosystem, widths[2]);
            TextTable.WriteCell(writer, Display(component.Purl), widths[3]);
            TextTable.WriteCell(writer, LicenseOrStatus(component, violation.Kind), widths[4]);
            TextTable.WriteCell(writer, Reason(violation.Kind), widths[5]);
            TextTable.WriteCell(writer, mechanism, widths[6]);
            TextTable.WriteCell(writer, reference, widths[7]);
            TextTable.WriteCell(writer, DependencyPathText.Introducer(inventory, rootPaths, component, violation.ComponentIndex), widths[8], last: true);
            TextTable.WriteNewLine(writer);
        }

        mechanismTally.Write(writer);
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

    /// <summary>Projects the Mechanism and Reference columns and the mechanism tally key.</summary>
    /// <remarks>
    /// A resolved license the allow-list rejects has no collection mechanism to name, so it is left out of
    /// the tally rather than counted as an unexplained one: the allow-list already explains it, and mixing
    /// the two populations would make the tally report the larger one.
    /// </remarks>
    private static bool GetMechanism(
        in ScanComponent component,
        LicensePolicyViolationKind kind,
        out ReadOnlySpan<byte> mechanism,
        out string reference,
        out UnresolvedMechanismKind mechanismKind)
    {
        if (kind == LicensePolicyViolationKind.NotAllowed)
        {
            mechanism = "-"u8;
            reference = "-";
            mechanismKind = default;
            return false;
        }

        if (!UnresolvedMechanism.TryGetReason(component, out var reason))
        {
            mechanism = "-"u8;
            reference = "-";
            mechanismKind = UnresolvedMechanismKind.None;
            return true;
        }

        mechanism = UnresolvedMechanism.GetNameUtf8(reason);
        var resolvedReference = UnresolvedMechanism.GetReference(component, reason);
        reference = resolvedReference.Length == 0 ? "-" : resolvedReference;
        mechanismKind = reason;
        return true;
    }

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
    }

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

    private static ReadOnlySpan<byte> LicenseOrStatus(in ScanComponent component, LicensePolicyViolationKind kind)
        => component.Status == LicenseStatus.Matched ? Display(component.License) : Status(kind);

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
