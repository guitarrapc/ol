using System.Text;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.Licensing;
using Ol.Internals;

/// <summary>Check resolved dependency licenses against an allow-list.</summary>
internal sealed class CheckCommands
{
    /// <summary>Indicates that exit code 1 came from completed policy evaluation rather than CLI parsing.</summary>
    public static bool PolicyViolationReturned { get; private set; }

    /// <summary>Check a resolved dependency input against allowed SPDX licenses.</summary>
    /// <param name="input">Repeatable resolved dependency input files or directories.</param>
    /// <param name="allowLicenses">Comma-separated SPDX License Identifiers.</param>
    /// <param name="inputFormat">Input format assertion; defaults to auto detection.</param>
    /// <param name="spdxData">Directory containing licenses.json and exceptions.json.</param>
    /// <param name="verbose">Include input detection diagnostics.</param>
    /// <param name="refresh">Skip package metadata cache entries.</param>
    /// <param name="cacheDir">Root directory for isolated package-metadata and source-repository caches.</param>
    /// <param name="skipEnrichment">Use only evidence already present in the dependency input.</param>
    /// <param name="concurrency">Maximum concurrent package metadata lookups.</param>
    /// <param name="retry">Reserved package metadata retry count.</param>
    /// <param name="baseline">Baseline file acknowledging already reviewed unresolved components.</param>
    /// <param name="updateBaseline">Rewrite the baseline file as a complete snapshot.</param>
    [Command("check")]
    public int Check(
        [InputPathsParser] string[]? input = null,
        string? allowLicenses = null,
        string? inputFormat = null,
        string? spdxData = null,
        bool verbose = false,
        bool refresh = false,
        string? cacheDir = null,
        bool skipEnrichment = false,
        int concurrency = 0,
        int retry = 1,
        string? baseline = null,
        bool updateBaseline = false)
    {
        if (string.IsNullOrWhiteSpace(allowLicenses))
        {
            Console.Error.WriteLine("Invalid license policy: --allow-licenses must be specified.");
            return 2;
        }

        var baselinePath = string.IsNullOrWhiteSpace(baseline) ? null : baseline;
        if (updateBaseline && baselinePath is null)
        {
            Console.Error.WriteLine("Invalid license policy: --update-baseline requires --baseline.");
            return 2;
        }

        if (!ScanExecution.TryPrepare(input, inputFormat, spdxData, cacheDir, skipEnrichment, concurrency, retry, out var preparation, out var preparationError))
        {
            Console.Error.WriteLine(preparationError);
            return 2;
        }

        var allowedLicenseIds = allowLicenses.Split(',', StringSplitOptions.None);
        if (!LicenseAllowPolicy.TryCreate(allowedLicenseIds, preparation.Spdx.Index, out var policy, out var policyError))
        {
            Console.Error.WriteLine($"Invalid license policy: {policyError}");
            return 2;
        }

        // An unusable baseline is a command failure rather than a silently empty baseline, so a mistyped
        // path is reported instead of changing which components fail.
        LicenseBaseline? acknowledgements = null;
        if (baselinePath is not null && !updateBaseline && !BaselineFile.TryRead(baselinePath, out acknowledgements, out var baselineError))
        {
            Console.Error.WriteLine(baselineError);
            return 2;
        }

        if (!ScanExecution.TryExecute(preparation, refresh, skipEnrichment, includeHash: false, out var completed, out var executionError))
        {
            Console.Error.WriteLine(executionError);
            return 2;
        }

        if (verbose)
        {
            WriteDetectedInputFormat(completed.Result.Inventory.Input);
        }

        if (updateBaseline)
        {
            var entries = LicenseBaseline.CreateEntries(completed.Result.Components, policy);
            if (!BaselineFile.TryWrite(baselinePath!, entries, preparation.Spdx.LicenseListVersion, out var writeError))
            {
                Console.Error.WriteLine(writeError);
                return 2;
            }

            acknowledgements = LicenseBaseline.FromEntries(entries);
        }

        var violations = policy.Evaluate(completed.Result.Components, acknowledgements, out var acknowledgedCount);
        var text = CheckRenderer.Render(completed.Result.Components, violations, baselinePath is null ? -1 : acknowledgedCount);
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
    public static string Render(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<LicensePolicyViolation> violations, int acknowledgedCount = -1)
    {
        if (violations.IsEmpty)
        {
            return string.Concat(
                Acknowledgement(acknowledgedCount),
                $"License check passed: {components.Length} component{(components.Length == 1 ? string.Empty : "s")} satisf{(components.Length == 1 ? "ies" : "y")} the allow-list.{Environment.NewLine}");
        }

        var builder = new StringBuilder();
        builder.Append(Acknowledgement(acknowledgedCount));
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
