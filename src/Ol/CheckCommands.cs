using System.Text;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.Licensing;

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
        int retry = 1)
    {
        if (string.IsNullOrWhiteSpace(allowLicenses))
        {
            Console.Error.WriteLine("Invalid license policy: --allow-licenses must be specified.");
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

        if (!ScanExecution.TryExecute(preparation, refresh, skipEnrichment, includeHash: false, out var completed, out var executionError))
        {
            Console.Error.WriteLine(executionError);
            return 2;
        }

        if (verbose)
        {
            WriteDetectedInputFormat(completed.Result.Inventory.Input);
        }

        var violations = policy.Evaluate(completed.Result.Components);
        var text = CheckRenderer.Render(completed.Result.Components, violations);
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

internal static class CheckRenderer
{
    public static string Render(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<LicensePolicyViolation> violations)
    {
        if (violations.IsEmpty)
        {
            return $"License check passed: {components.Length} component{(components.Length == 1 ? string.Empty : "s")} satisf{(components.Length == 1 ? "ies" : "y")} the allow-list.{Environment.NewLine}";
        }

        var builder = new StringBuilder();
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
