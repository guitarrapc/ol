using Ol.Core;

namespace Ol.Internals;

/// <summary>Bounded diagnostic state for known dependency inputs Ol cannot consume directly.</summary>
internal struct InputCandidateDiagnostics
{
    private ulong detected;
    private ulong satisfied;

    internal readonly ulong Unresolved => detected & ~satisfied;

    internal readonly bool IsDetected(ulong candidate) => (detected & candidate) != 0;

    internal void MarkDetected(ulong candidate) => detected |= candidate;

    internal void MarkSatisfied(ulong candidate) => satisfied |= candidate;
}

/// <summary>Detects known unsupported inputs and owns their actionable diagnostics.</summary>
internal static class KnownUnsupportedInputCandidates
{
    private const string UnsupportedFormatMessage = "Unsupported dependency input format: no registered format signature matched.";

    private static readonly CandidateRule[] Rules =
    [
        new(
            Bit: 1UL << 0,
            FileName: "Cargo.lock",
            Extension: null,
            DirectoryPattern: "Cargo.lock",
            Advice: "Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.",
            Warning: "Rust dependencies were not scanned: Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.",
            SatisfiedFormat: ScanInputFormat.CargoMetadata,
            SatisfiedEcosystem: "cargo"),
        new(
            Bit: 1UL << 1,
            FileName: null,
            Extension: ".csproj",
            DirectoryPattern: null,
            Advice: ".csproj is not a resolved dependency input. Run 'dotnet restore', then scan obj/project.assets.json.",
            Warning: null,
            SatisfiedFormat: default,
            SatisfiedEcosystem: null),
    ];

    public static void DetectDirectory(string directory, EnumerationOptions options, ref InputCandidateDiagnostics diagnostics)
    {
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if (diagnostics.IsDetected(rule.Bit) || rule.DirectoryPattern is null)
            {
                continue;
            }

            using var paths = Directory.EnumerateFiles(directory, rule.DirectoryPattern, options).GetEnumerator();
            if (paths.MoveNext())
            {
                diagnostics.MarkDetected(rule.Bit);
            }
        }
    }

    public static void ObserveScannedInput(in DependencyInventory inventory, DependencyInputHandler handler, ref InputCandidateDiagnostics diagnostics)
    {
        var unresolved = diagnostics.Unresolved;
        if (unresolved == 0)
        {
            return;
        }

        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) == 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(rule.SatisfiedFormat.Name) && handler.Format == rule.SatisfiedFormat)
            {
                diagnostics.MarkSatisfied(rule.Bit);
                continue;
            }

            if (rule.SatisfiedEcosystem is null)
            {
                continue;
            }

            for (var componentIndex = 0; componentIndex < inventory.Components.Length; componentIndex++)
            {
                if (inventory.Components[componentIndex].Ecosystem == rule.SatisfiedEcosystem)
                {
                    diagnostics.MarkSatisfied(rule.Bit);
                    break;
                }
            }
        }
    }

    public static bool TryGetDirectInputError(string path, Exception exception, out string error)
    {
        if (exception.Message != UnsupportedFormatMessage)
        {
            error = string.Empty;
            return false;
        }

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if (rule.FileName is not null && string.Equals(fileName, rule.FileName, StringComparison.OrdinalIgnoreCase)
                || rule.Extension is not null && string.Equals(extension, rule.Extension, StringComparison.OrdinalIgnoreCase))
            {
                error = rule.Advice;
                return true;
            }
        }

        error = string.Empty;
        return false;
    }

    public static bool TryGetUnscannedInputError(in InputCandidateDiagnostics diagnostics, out string error)
    {
        var unresolved = diagnostics.Unresolved;
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) != 0 && rule.Warning is not null)
            {
                error = rule.Warning;
                return true;
            }
        }

        error = string.Empty;
        return false;
    }

    public static void WriteWarnings(in InputCandidateDiagnostics diagnostics, TextWriter writer)
    {
        var unresolved = diagnostics.Unresolved;
        if (unresolved == 0)
        {
            return;
        }

        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) == 0 || rule.Warning is null)
            {
                continue;
            }

            writer.Write("Warning: ");
            writer.WriteLine(rule.Warning);
        }
    }

    public static int GetUnresolvedCount(in InputCandidateDiagnostics diagnostics)
        => System.Numerics.BitOperations.PopCount(diagnostics.Unresolved);

    public static void WriteUnresolvedNames(in InputCandidateDiagnostics diagnostics, TextWriter writer)
    {
        var unresolved = diagnostics.Unresolved;
        var written = 0;
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) == 0)
            {
                continue;
            }

            if (written++ > 0)
            {
                writer.Write(", ");
            }

            writer.Write(rule.FileName ?? string.Concat("*", rule.Extension));
        }
    }

    private readonly record struct CandidateRule(
        ulong Bit,
        string? FileName,
        string? Extension,
        string? DirectoryPattern,
        string Advice,
        string? Warning,
        ScanInputFormat SatisfiedFormat,
        string? SatisfiedEcosystem);
}
