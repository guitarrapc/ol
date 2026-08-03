using System.Buffers;
using System.Text;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Core.Reporting;
using Ol.Internals;

/// <summary>Check resolved dependency licenses against an allow-list.</summary>
internal sealed class CheckCommands
{
    /// <summary>Indicates that exit code 1 came from completed policy evaluation rather than CLI parsing.</summary>
    public static bool PolicyViolationReturned { get; private set; }

    /// <summary>Gets the running tool version recorded in generated artifacts.</summary>
    internal static string ToolVersion => typeof(CheckCommands).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>Check a resolved dependency input against allowed SPDX licenses.</summary>
    /// <param name="input">Repeatable resolved dependency input files or directories.</param>
    /// <param name="allowLicenses">Comma-separated SPDX License Identifiers.</param>
    /// <param name="allowDevLicenses">Comma-separated SPDX License Identifiers additionally allowed for development-only components.</param>
    /// <param name="inputFormat">Input format assertion; defaults to auto detection.</param>
    /// <param name="spdxData">Directory containing licenses.json and exceptions.json.</param>
    /// <param name="verbose">Include input detection diagnostics.</param>
    /// <param name="refresh">Ignore cached package metadata and source repository entries and fetch them again.</param>
    /// <param name="cacheDir">Root directory for isolated package-metadata and source-repository caches.</param>
    /// <param name="noExternalEvidence">Use only license evidence declared in the input; package registries, source repositories, and their caches are never read.</param>
    /// <param name="concurrency">Maximum concurrent package metadata lookups.</param>
    /// <param name="retry">Reserved package metadata retry count.</param>
    /// <param name="baseline">Baseline file acknowledging already reviewed unresolved components.</param>
    /// <param name="updateBaseline">Rewrite the baseline file as a complete snapshot.</param>
    /// <param name="report">Persisted JSON scan report to evaluate instead of scanning an input.</param>
    /// <param name="sarif">Write violations as SARIF to this file for CI code scanning.</param>
    [Command("check")]
    public int Check(
        [InputPathsParser] string[]? input = null,
        string? allowLicenses = null,
        string? allowDevLicenses = null,
        string? inputFormat = null,
        string? spdxData = null,
        bool verbose = false,
        bool refresh = false,
        string? cacheDir = null,
        bool noExternalEvidence = false,
        int concurrency = 0,
        int retry = 1,
        string? baseline = null,
        bool updateBaseline = false,
        string? report = null,
        string? sarif = null)
    {
        if (string.IsNullOrWhiteSpace(allowLicenses))
        {
            Console.Error.WriteLine("Invalid license policy: --allow-licenses must be specified.");
            return 2;
        }

        var developmentLicenseIds = string.IsNullOrWhiteSpace(allowDevLicenses)
            ? []
            : allowDevLicenses.Split(',', StringSplitOptions.None);

        var baselinePath = string.IsNullOrWhiteSpace(baseline) ? null : baseline;
        if (updateBaseline && baselinePath is null)
        {
            Console.Error.WriteLine("Invalid license policy: --update-baseline requires --baseline.");
            return 2;
        }

        var reportPath = string.IsNullOrWhiteSpace(report) ? null : report;
        if (reportPath is not null && (input is { Length: > 0 } || inputFormat is not null || refresh || noExternalEvidence || cacheDir is not null))
        {
            Console.Error.WriteLine("Invalid license policy: --report cannot be combined with input or evidence-collection options.");
            return 2;
        }

        // A persisted report already contains the evidence, so the pipeline is not prepared at all.
        // This is what makes report evaluation free of input parsing and network access.
        ScanComponent[] components;
        string licenseListVersion;
        LicenseAllowPolicy policy;
        var inventory = default(DependencyInventory);
        DependencyUsage[]? reportComponentUsages = null;
        if (reportPath is not null)
        {
            if (!ScanExecution.TryResolveSpdx(spdxData, out var reportSpdx, out var spdxError))
            {
                Console.Error.WriteLine(spdxError);
                return 2;
            }

            if (!LicenseAllowPolicy.TryCreate(allowLicenses.Split(',', StringSplitOptions.None), developmentLicenseIds, reportSpdx.Index, out policy, out var reportPolicyError))
            {
                Console.Error.WriteLine($"Invalid license policy: {reportPolicyError}");
                return 2;
            }

            if (!ScanReportFile.TryRead(reportPath, out var persisted, out var readError))
            {
                Console.Error.WriteLine(readError);
                return 2;
            }

            components = persisted.Components;
            inventory = persisted.Inventory;
            reportComponentUsages = persisted.ComponentUsages;
            licenseListVersion = reportSpdx.LicenseListVersion;
            if (verbose)
            {
                Console.Error.WriteLine($"Evaluating persisted report: {persisted.SourceReference}; SPDX {persisted.LicenseListVersion} at scan time");
            }
        }
        else
        {
            if (!ScanExecution.TryPrepare(input, inputFormat, spdxData, cacheDir, noExternalEvidence, concurrency, retry, out var preparation, out var preparationError))
            {
                Console.Error.WriteLine(preparationError);
                return 2;
            }

            if (!LicenseAllowPolicy.TryCreate(allowLicenses.Split(',', StringSplitOptions.None), developmentLicenseIds, preparation.Spdx.Index, out policy, out var policyError))
            {
                Console.Error.WriteLine($"Invalid license policy: {policyError}");
                return 2;
            }

            if (!ScanExecution.TryExecute(preparation, refresh, noExternalEvidence, includeHash: false, out var completed, out var executionError))
            {
                Console.Error.WriteLine(executionError);
                return 2;
            }

            if (verbose)
            {
                WriteDetectedInputFormat(completed.Result.Inventory.Input);
            }

            components = completed.Result.Components;
            licenseListVersion = preparation.Spdx.LicenseListVersion;
            inventory = completed.Result.Inventory;
        }

        // An unusable baseline is a command failure rather than a silently empty baseline, so a mistyped
        // path is reported instead of changing which components fail.
        LicenseBaseline? acknowledgements = null;
        if (baselinePath is not null && !updateBaseline && !BaselineFile.TryRead(baselinePath, out acknowledgements, out var baselineError))
        {
            Console.Error.WriteLine(baselineError);
            return 2;
        }

        if (updateBaseline)
        {
            var entries = LicenseBaseline.CreateEntries(components, policy);
            if (!BaselineFile.TryWrite(baselinePath!, entries, licenseListVersion, out var writeError))
            {
                Console.Error.WriteLine(writeError);
                return 2;
            }

            acknowledgements = LicenseBaseline.FromEntries(entries);
        }

        int acknowledgedCount;
        int policyComponentCount;
        int developmentAllowedCount;
        var developmentAllowedComponents = Array.Empty<int>();
        LicensePolicyViolation[] violations;
        if (developmentLicenseIds.Length == 0)
        {
            violations = policy.Evaluate(components, default, acknowledgements, out acknowledgedCount, out policyComponentCount, out _);
            developmentAllowedCount = -1;
        }
        else if (reportComponentUsages is not null)
        {
            // A persisted report already carries per-component usage aligned with its components.
            violations = policy.Evaluate(components, reportComponentUsages, acknowledgements, out acknowledgedCount, out policyComponentCount, out developmentAllowedComponents);
            developmentAllowedCount = developmentAllowedComponents.Length;
        }
        else
        {
            // Live input: aggregate occurrence usage into a per-component verdict once, using pooled scratch that never escapes.
            var usageLength = inventory.Components.Length;
            var usages = ArrayPool<DependencyUsage>.Shared.Rent(Math.Max(usageLength, 1));
            try
            {
                DependencyUsageResolver.Resolve(inventory, usages.AsSpan(0, usageLength));
                violations = policy.Evaluate(components, usages.AsSpan(0, usageLength), acknowledgements, out acknowledgedCount, out policyComponentCount, out developmentAllowedComponents);
                developmentAllowedCount = developmentAllowedComponents.Length;
            }
            finally
            {
                ArrayPool<DependencyUsage>.Shared.Return(usages);
            }
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
                return 2;
            }
        }

        var text = CheckRenderer.Render(
            components,
            violations,
            policyComponentCount,
            baselinePath is null ? -1 : acknowledgedCount,
            developmentAllowedCount);
        try
        {
            Console.Write(text);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to write check result: {exception.Message}");
            return 2;
        }

        PolicyViolationReturned = violations.Length != 0;
        return PolicyViolationReturned ? 1 : 0;
    }

    private static void WriteDetectedInputFormat(in ScanInputDescriptor input)
    {
        Console.Error.Write("Detected input format: ");
        Console.Error.Write(input.Kind.Name);
        Console.Error.Write('/');
        Console.Error.WriteLine(input.Format.Name);
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
    public static string Render(
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<LicensePolicyViolation> violations,
        int policyComponentCount,
        int acknowledgedCount = -1,
        int developmentAllowedCount = -1)
    {
        if (violations.IsEmpty)
        {
            return string.Concat(
                Acknowledgement(acknowledgedCount),
                DevelopmentAllowance(developmentAllowedCount),
                $"License check passed: {policyComponentCount} component{(policyComponentCount == 1 ? string.Empty : "s")} satisf{(policyComponentCount == 1 ? "ies" : "y")} the allow-list.{Environment.NewLine}");
        }

        var builder = new StringBuilder();
        builder.Append(Acknowledgement(acknowledgedCount));
        builder.Append(DevelopmentAllowance(developmentAllowedCount));
        builder.Append("License check failed: ");
        builder.Append(violations.Length);
        builder.Append(" violation");
        if (violations.Length != 1) builder.Append('s');
        builder.AppendLine(".");
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

    /// <summary>Makes a supplied baseline visible even when the run passes.</summary>
    private static string Acknowledgement(int acknowledgedCount)
        => acknowledgedCount < 0
            ? string.Empty
            : $"Acknowledged by baseline: {acknowledgedCount} component{(acknowledgedCount == 1 ? string.Empty : "s")}.{Environment.NewLine}";

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
