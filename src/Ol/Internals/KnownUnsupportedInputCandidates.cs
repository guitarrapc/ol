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
    private static readonly CandidateRule[] Rules =
    [
        new(
            Bit: 1UL << 0,
            FileName: "Cargo.lock",
            Extension: null,
            DirectoryPattern: "Cargo.lock",
            Advice: "Cargo.lock is not a supported input. Run 'cargo metadata --format-version 1 --locked > cargo-metadata.json', then scan cargo-metadata.json.",
            WarningSubject: "Rust",
            SatisfiedFormat: ScanInputFormat.CargoMetadata,
            SatisfiedEcosystem: "cargo"),
        new(
            Bit: 1UL << 1,
            FileName: null,
            Extension: ".csproj",
            DirectoryPattern: "*.csproj",
            Advice: ".csproj is not a resolved dependency input. Run 'dotnet restore', then scan obj/project.assets.json.",
            WarningSubject: ".NET",
            SatisfiedFormat: ScanInputFormat.NuGetAssets,
            SatisfiedEcosystem: "nuget"),

        // A Cargo library does not commit its lockfile, so detecting only Cargo.lock leaves the ecosystem
        // silently unscanned in exactly the repositories that publish crates. The manifest is the file every
        // Cargo project has. It is superseded rather than independent because a project carrying a lockfile
        // always carries a manifest too, so two rules would report one unscanned ecosystem twice, and the
        // lockfile's advice is the one that reproduces the recorded resolution.
        new(
            Bit: 1UL << 2,
            FileName: "Cargo.toml",
            Extension: null,
            DirectoryPattern: "Cargo.toml",
            Advice: "Cargo.toml is not a supported input. Run 'cargo metadata --format-version 1 > cargo-metadata.json', then scan cargo-metadata.json.",
            WarningSubject: "Rust",
            SatisfiedFormat: ScanInputFormat.CargoMetadata,
            SatisfiedEcosystem: "cargo",
            SupersededBy: 1UL << 0),
    ];

    /// <summary>Reports whether a more specific rule already speaks for this one's ecosystem.</summary>
    private static bool IsSuperseded(in CandidateRule rule, in InputCandidateDiagnostics diagnostics)
        => rule.SupersededBy != 0 && diagnostics.IsDetected(rule.SupersededBy);

    public static void DetectDirectory(string directory, EnumerationOptions options, ref InputCandidateDiagnostics diagnostics)
    {
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if (diagnostics.IsDetected(rule.Bit))
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

    public static void DetectFileName(ReadOnlySpan<char> fileName, ref InputCandidateDiagnostics diagnostics)
    {
        var extension = Path.GetExtension(fileName);
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if (diagnostics.IsDetected(rule.Bit))
            {
                continue;
            }

            if ((rule.FileName is not null && fileName.Equals(rule.FileName, StringComparison.OrdinalIgnoreCase))
                || (rule.Extension is not null && extension.Equals(rule.Extension, StringComparison.OrdinalIgnoreCase)))
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

            if (handler.Format == rule.SatisfiedFormat)
            {
                diagnostics.MarkSatisfied(rule.Bit);
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
        if (exception.Message != DependencyInputRegistry.UnsupportedFormatMessage)
        {
            error = string.Empty;
            return false;
        }

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((rule.FileName is not null && string.Equals(fileName, rule.FileName, StringComparison.OrdinalIgnoreCase))
                || (rule.Extension is not null && string.Equals(extension, rule.Extension, StringComparison.OrdinalIgnoreCase)))
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
            if ((unresolved & rule.Bit) != 0 && !IsSuperseded(rule, diagnostics))
            {
                // Nothing was scanned at all on this path, so the warning's "were not scanned" framing would
                // misreport the failure. The advice alone is what the caller can act on.
                error = rule.Advice;
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
            if ((unresolved & rule.Bit) == 0 || IsSuperseded(rule, diagnostics))
            {
                continue;
            }

            writer.Write("Warning: ");
            writer.Write(rule.WarningSubject);
            writer.Write(" dependencies were not scanned: ");
            writer.WriteLine(rule.Advice);
        }
    }

    /// <summary>Counts the candidates a reader is asked to act on, which excludes any a rule already speaks for.</summary>
    public static int GetUnresolvedCount(in InputCandidateDiagnostics diagnostics)
    {
        var count = 0;
        var unresolved = diagnostics.Unresolved;
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) != 0 && !IsSuperseded(rule, diagnostics))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Names the candidates a reader is asked to act on, in rule order, for the canonical report.</summary>
    /// <remarks>
    /// The values are directory patterns Ol declares rather than anything read from the file system, so the
    /// result carries no path and is the same on every machine that discovered the same candidates. The report
    /// and the stderr summary therefore name one vocabulary, and this runs once per scan rather than per file.
    /// </remarks>
    public static string[] GetUnresolvedNames(in InputCandidateDiagnostics diagnostics)
    {
        var count = GetUnresolvedCount(diagnostics);
        if (count == 0) return [];

        var names = new string[count];
        var written = 0;
        var unresolved = diagnostics.Unresolved;
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) == 0 || IsSuperseded(rule, diagnostics))
            {
                continue;
            }

            names[written++] = rule.DirectoryPattern;
        }

        return names;
    }

    public static void WriteUnresolvedNames(in InputCandidateDiagnostics diagnostics, TextWriter writer)
    {
        var unresolved = diagnostics.Unresolved;
        var written = 0;
        for (var ruleIndex = 0; ruleIndex < Rules.Length; ruleIndex++)
        {
            ref readonly var rule = ref Rules[ruleIndex];
            if ((unresolved & rule.Bit) == 0 || IsSuperseded(rule, diagnostics))
            {
                continue;
            }

            if (written++ > 0)
            {
                writer.Write(", ");
            }

            writer.Write(rule.DirectoryPattern);
        }
    }

    /// <summary>One known dependency input Ol cannot consume, and the supported input that replaces it.</summary>
    /// <param name="Bit">The rule's bit in <see cref="InputCandidateDiagnostics"/>.</param>
    /// <param name="FileName">The exact file name identifying this candidate when it is named directly, when it has one.</param>
    /// <param name="Extension">The file extension identifying this candidate when it is named directly, when it has one.</param>
    /// <param name="DirectoryPattern">The pattern automatic directory discovery detects this candidate with.</param>
    /// <param name="Advice">The supported input to produce, and how.</param>
    /// <param name="WarningSubject">The ecosystem named by the directory-discovery warning.</param>
    /// <param name="SatisfiedFormat">The input format whose presence proves the ecosystem was scanned.</param>
    /// <param name="SatisfiedEcosystem">The component ecosystem whose presence proves the ecosystem was scanned.</param>
    /// <param name="SupersededBy">The bit of a rule that reports this one's ecosystem better, or zero when this rule stands alone.</param>
    private readonly record struct CandidateRule(
        ulong Bit,
        string? FileName,
        string? Extension,
        string DirectoryPattern,
        string Advice,
        string WarningSubject,
        ScanInputFormat SatisfiedFormat,
        string SatisfiedEcosystem,
        ulong SupersededBy = 0);
}
