using System.Buffers;
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
    /// <param name="baseline">Baseline file acknowledging already reviewed unresolved components.</param>
    /// <param name="updateBaseline">Rewrite the baseline file as a complete snapshot.</param>
    /// <param name="sarif">Write violations as SARIF to this file for CI code scanning.</param>
    [Command("check")]
    public int Check(
        string report,
        string allowLicenses,
        string? allowDevLicenses = null,
        string? excludePackages = null,
        string? spdxData = null,
        bool verbose = false,
        string? baseline = null,
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

        var baselinePath = string.IsNullOrWhiteSpace(baseline) ? null : baseline;
        if (updateBaseline && baselinePath is null)
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
        // path is reported instead of changing which components fail.
        LicenseBaseline? acknowledgements = null;
        if (baselinePath is not null && !updateBaseline && !BaselineFile.TryRead(baselinePath, out acknowledgements, out var baselineError))
        {
            Console.Error.WriteLine(baselineError);
            return 1;
        }

        if (updateBaseline)
        {
            var entries = LicenseBaseline.CreateEntries(components, policy);
            if (!BaselineFile.TryWrite(baselinePath!, entries, licenseListVersion, out var writeError))
            {
                Console.Error.WriteLine(writeError);
                return 1;
            }

            acknowledgements = LicenseBaseline.FromEntries(entries);
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
                File.WriteAllBytes(sarif, SarifRenderer.Render(inventory, components, violations, developmentAllowedComponents, ToolVersion));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Console.Error.WriteLine($"Unable to write SARIF: {exception.Message}");
                return 1;
            }
        }

        var text = CheckRenderer.Render(
            components,
            violations,
            policyComponentCount,
            baselinePath is null ? -1 : acknowledgedCount,
            developmentAllowedCount,
            excludePackages is null ? -1 : excludedCount,
            ambiguityAllowedCount);
        try
        {
            Console.Write(text);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to write check result: {exception.Message}");
            return 1;
        }

        if (violations.Length == 0) return 0;

        // A run whose only findings are collection failures resolved nothing and proved nothing; reporting it as a
        // policy violation would make a registry outage indistinguishable from a forbidden license in CI.
        return CheckRenderer.IsIncomplete(violations) ? 3 : 2;
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

    public static string Render(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<LicensePolicyViolation> violations,
        int policyComponentCount,
        int acknowledgedCount = -1,
        int developmentAllowedCount = -1,
        int excludedCount = -1,
        int ambiguityAllowedCount = 0)
    {
        if (violations.IsEmpty)
        {
            return string.Concat(
                Exclusion(excludedCount),
                Acknowledgement(acknowledgedCount),
                DevelopmentAllowance(developmentAllowedCount),
                AmbiguityAllowance(ambiguityAllowedCount),
                $"License check passed: {policyComponentCount} component{(policyComponentCount == 1 ? string.Empty : "s")} satisf{(policyComponentCount == 1 ? "ies" : "y")} the allow-list.{Environment.NewLine}");
        }

        var builder = new StringBuilder();
        builder.Append(Exclusion(excludedCount));
        builder.Append(Acknowledgement(acknowledgedCount));
        builder.Append(DevelopmentAllowance(developmentAllowedCount));
        builder.Append(AmbiguityAllowance(ambiguityAllowedCount));
        // An incomplete run is stated as such: nothing was proven about those components, which is not the same
        // claim as a policy violation, and the exit code makes the same distinction.
        if (IsIncomplete(violations))
        {
            builder.Append("License check incomplete: ");
            builder.Append(violations.Length);
            builder.Append(" component");
            if (violations.Length != 1) builder.Append('s');
            builder.AppendLine(" could not be evaluated.");
        }
        else
        {
            builder.Append("License check failed: ");
            builder.Append(violations.Length);
            builder.Append(" violation");
            if (violations.Length != 1) builder.Append('s');
            builder.AppendLine(".");
        }
        builder.AppendLine();
        builder.AppendLine("Package\tVersion\tEcosystem\tPurl\tLicense/Status\tReason");
        for (var i = 0; i < violations.Length; i++)
        {
            var violation = violations[i];
            var component = components[violation.ComponentIndex];
            Append(builder, component.Name);
            builder.Append('\t');
            Append(builder, component.Version);
            builder.Append('\t');
            builder.Append(component.Ecosystem);
            builder.Append('\t');
            Append(builder, component.Purl, "-");
            builder.Append('\t');
            if (component.Status == LicenseStatus.Matched) Append(builder, component.License);
            else builder.Append(Status(violation.Kind));
            builder.Append('\t');
            builder.AppendLine(Reason(violation.Kind));
        }

        return builder.ToString();
    }

    /// <summary>Reports how many components the exclusion prefixes removed from evaluation, shown whenever the option is supplied.</summary>
    private static string Exclusion(int excludedCount)
        => excludedCount < 0
            ? string.Empty
            : $"Excluded from evaluation: {excludedCount} component{(excludedCount == 1 ? string.Empty : "s")}.{Environment.NewLine}";

    /// <summary>Makes a supplied baseline visible even when the run passes.</summary>
    private static string Acknowledgement(int acknowledgedCount)
        => acknowledgedCount < 0
            ? string.Empty
            : $"Acknowledged by baseline: {acknowledgedCount} component{(acknowledgedCount == 1 ? string.Empty : "s")}.{Environment.NewLine}";

    /// <summary>
    /// Reports how many ambiguous components the allow-list admitted on every reading of their evidence.
    /// </summary>
    /// <remarks>
    /// No option turns this on, so it is shown only when it happened. Those components stay ambiguous in
    /// the scan report, and the count is what connects that to the absence of a violation here.
    /// </remarks>
    private static string AmbiguityAllowance(int ambiguityAllowedCount)
        => ambiguityAllowedCount <= 0
            ? string.Empty
            : $"Allowed on every reading of ambiguous evidence: {ambiguityAllowedCount} component{(ambiguityAllowedCount == 1 ? string.Empty : "s")}.{Environment.NewLine}";

    /// <summary>Reports how many components the development allow-list admitted, shown whenever the option is supplied.</summary>
    private static string DevelopmentAllowance(int developmentAllowedCount)
        => developmentAllowedCount < 0
            ? string.Empty
            : $"Allowed by development policy: {developmentAllowedCount} component{(developmentAllowedCount == 1 ? string.Empty : "s")}.{Environment.NewLine}";

    private static void Append(StringBuilder builder, Utf8Slice value, string empty = "")
        => builder.Append(value.IsEmpty ? empty : value.ToString());

    private static string Status(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.Conflict => "conflict",
        LicensePolicyViolationKind.Unknown => "unknown",
        LicensePolicyViolationKind.Ambiguous => "ambiguous",
        LicensePolicyViolationKind.Invalid => "invalid",
        LicensePolicyViolationKind.Error => "error",
        _ => "matched",
    };

    private static string Reason(LicensePolicyViolationKind kind) => kind switch
    {
        LicensePolicyViolationKind.NotAllowed => "license is not allowed",
        LicensePolicyViolationKind.Conflict => "license evidence conflicts",
        LicensePolicyViolationKind.Unknown => "license is unresolved",
        LicensePolicyViolationKind.Ambiguous => "license is ambiguous",
        LicensePolicyViolationKind.Invalid => "license expression is invalid",
        LicensePolicyViolationKind.Error => "license evidence could not be completed",
        _ => "license policy violation",
    };
}
