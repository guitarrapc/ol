using System.Text.Json;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.GitHub;
using Ol.Core.PackageManagers;
using Ol.Internals;

/// <summary>
/// Scan resolved dependency license evidence.
/// </summary>
internal sealed class ScanCommands
{
    private readonly Stream? standardOutput;

    public ScanCommands()
    {
    }

    internal ScanCommands(Stream standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        this.standardOutput = standardOutput;
    }

    /// <summary>
    /// Scan a resolved dependency input.
    /// </summary>
    /// <param name="input">Repeatable resolved dependency input files or directories.</param>
    /// <param name="excludeInputPath">Repeatable file or directory paths excluded from directory input discovery.</param>
    /// <param name="inputFormat">Input format: auto (default), cyclonedx, spdx, nuget-assets, npm-package-lock, pnpm-lock, yarn-classic-lock, yarn-berry-lock, cargo-metadata, go-module-graph, pip-inspect, composer-lock, bundler-lock, maven-dependency-tree, swift-package-resolved, or cocoapods-lock.</param>
    /// <param name="format">Output format: text, json, or markdown.</param>
    /// <param name="verbose">Include verbose columns and input detection diagnostics.</param>
    /// <param name="dependency">Dependency output filter: root,direct,transitive,unknown.</param>
    /// <param name="groupBy">Group output by fields: name,version,license,ecosystem,dependency,status.</param>
    /// <param name="sort">Sort keys: ecosystem,name,version,license,dependency,status,purl.</param>
    /// <param name="sortOrder">Sort order: asc or desc.</param>
    /// <param name="spdxData">Directory containing licenses.json and exceptions.json.</param>
    /// <param name="quiet">Suppress stderr summary.</param>
    /// <param name="refresh">Ignore cached package metadata, source repository, and GitHub file entries and fetch them again.</param>
    /// <param name="cacheDir">Root directory for isolated package-metadata, source-repository, and GitHub file caches.</param>
    /// <param name="noExternalEvidence">Use only license evidence declared in the input; package registries, source repositories, and their caches are never read.</param>
    /// <param name="skipEvidencePackages">Comma-separated package URL prefixes whose external evidence is never collected. A prefix may stop at the ecosystem, as in pkg:github/.</param>
    /// <param name="concurrency">Maximum concurrent package metadata and source repository lookups.</param>
    /// <param name="retry">Retry count for package registry and GitHub License API requests.</param>
    [Command("scan")]
    public int Scan(
        [InputPathsParser] string[] input,
        [InputPathsParser] string[]? excludeInputPath = null,
        string inputFormat = "auto",
        ReportFormat format = ReportFormat.Text,
        bool verbose = false,
        string? dependency = null,
        string? groupBy = null,
        string sort = "ecosystem,name,version",
        SortOrder sortOrder = SortOrder.Asc,
        string? spdxData = null,
        bool quiet = false,
        bool refresh = false,
        string? cacheDir = null,
        bool noExternalEvidence = false,
        string? skipEvidencePackages = null,
        int concurrency = 0,
        int retry = 1)
    {
        try
        {
            ScanView.Validate(dependency, sort, groupBy);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Invalid scan option: {exception.Message}");
            return 1;
        }

        var uncollectedPrefixes = skipEvidencePackages?.Split(',', StringSplitOptions.None);
        if (!ScanExecution.TryPrepare(input, inputFormat, excludeInputPath, spdxData, cacheDir, noExternalEvidence, uncollectedPrefixes, concurrency, retry, out var preparation, out var preparationError))
        {
            Console.Error.WriteLine(preparationError);
            return 1;
        }

        if (!ScanExecution.TryExecute(preparation, refresh, noExternalEvidence, format == ReportFormat.Json, out var completed, out var executionError))
        {
            Console.Error.WriteLine(executionError);
            return 1;
        }

        var scanResult = completed.Result;
        var spdx = preparation.Spdx;
        var packageArtifactSummary = completed.PackageArtifactSummary;
        var declaredGitHubFileSummary = completed.DeclaredGitHubFileSummary;
        var packageMetadataSummary = completed.PackageMetadataSummary;
        var sourceRepositorySummary = completed.SourceRepositorySummary;

        KnownUnsupportedInputCandidates.WriteWarnings(completed.InputCandidateDiagnostics, Console.Error);
        WriteSkippedIncompleteInputWarnings(completed.SkippedIncompleteInputs, completed.SkippedIncompleteInputCount, Console.Error);

        // A reached rate limit means source evidence is missing for reasons the run can act on, so it is
        // reported even under --quiet, where the counters that would otherwise hint at it are suppressed.
        if (completed.GitHubRateLimit is { } gitHubRateLimit)
        {
            WriteGitHubRateLimitDiagnostic(gitHubRateLimit);
        }

        if (verbose)
        {
            WriteDetectedInputFormat(scanResult.Inventory.Input);
            if (preparation.UncollectedPackages is { } uncollected)
            {
                PurlPrefixDiagnostics.WriteMatches("Skipped evidence", uncollected, scanResult.Components);
            }
        }

        var excludedUnknownCount = dependency is null or "" ? 0 : ScanView.CountExcludedUnknown(scanResult.Inventory.Components, dependency);
        var viewComponents = scanResult.Components.Length == 0 ? [] : (ScanComponent[])scanResult.Components.Clone();

        // Development usage is persisted only when the input determined it, and only for the JSON report that policy
        // re-evaluation consumes. Resolving before the view sort keeps each usage positionally tied to its component.
        DependencyUsage[]? viewUsages = null;
        int componentCount;
        if (format == ReportFormat.Json && scanResult.Inventory.UsageDeterminedRanges is not null && viewComponents.Length > 0)
        {
            viewUsages = new DependencyUsage[viewComponents.Length];
            DependencyUsageResolver.Resolve(scanResult.Inventory, viewUsages);
            componentCount = ScanView.Apply(viewComponents, viewUsages, dependency, sort, sortOrder);
        }
        else
        {
            componentCount = ScanView.Apply(viewComponents, dependency, sort, sortOrder);
        }

        var components = viewComponents.AsSpan(0, componentCount);
        var componentUsages = viewUsages is null ? default : viewUsages.AsSpan(0, componentCount);
        var dependencyFilteredCount = dependency is null or "" ? 0 : scanResult.Inventory.Components.Length - components.Length;
        var groups = groupBy is null or "" ? null : ScanView.Group(viewComponents, viewUsages, componentCount, groupBy);
        // The stderr summary is shared by every format. It was once withheld from JSON because the document
        // repeats it, but the document and the terminal have different readers: a CI job redirects the report to a
        // file, and the person reading the log cannot open it. Withholding it made the recommended path the one
        // path that left no trace of having run. Redirecting stdout is unaffected, because the summary is stderr.
        if (format == ReportFormat.Json)
        {
            try
            {
                var discovery = new ScanInputDiscovery(
                    completed.DetectedInputFileCount,
                    KnownUnsupportedInputCandidates.GetUnresolvedNames(completed.InputCandidateDiagnostics),
                    completed.SkippedIncompleteInputCount);
                var scope = new ScanReportScope(!noExternalEvidence, dependency is null or "" ? null : dependency, dependencyFilteredCount, excludedUnknownCount, completed.ExcludedInputPaths, discovery);
                WriteJson(standardOutput ?? Console.OpenStandardOutput(), scanResult.Inventory, components, componentUsages, groups, groupBy, spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Unable to write report: {exception.Message}");
                return 1;
            }
        }
        else if (format == ReportFormat.Text)
        {
            try
            {
                WriteText(standardOutput ?? Console.OpenStandardOutput(), scanResult.Inventory, components, groups, groupBy, verbose, scanResult.Inventory.Components.Length == 0);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Unable to write report: {exception.Message}");
                return 1;
            }
        }
        else
        {
            try
            {
                WriteMarkdown(standardOutput ?? Console.OpenStandardOutput(), scanResult.Inventory, components, groups, groupBy, verbose, scanResult.Inventory.Components.Length == 0);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Unable to write report: {exception.Message}");
                return 1;
            }
        }

        if (!quiet)
        {
            var summary = ScanSummary.Create(components);
            var packageArtifacts = packageArtifactSummary;
            var declaredGitHubFiles = declaredGitHubFileSummary;
            var packageMetadata = packageMetadataSummary;
            var source = sourceRepositorySummary;
            Console.Error.WriteLine();
            Console.Error.WriteLine("Scan summary");
            Console.Error.WriteLine($"  License results: {components.Length} displayed component{(components.Length == 1 ? string.Empty : "s")}; {summary.Matched} matched; {summary.Conflict} conflict; {summary.Unknown} unknown; {summary.Ambiguous} ambiguous; {summary.Invalid} invalid; {summary.Error} error");
            Console.Error.WriteLine($"  Findings: {summary.UnresolvedWarningCount} warning{(summary.UnresolvedWarningCount == 1 ? string.Empty : "s")} on unresolved components; {summary.ResolvedWarningCount} warning{(summary.ResolvedWarningCount == 1 ? string.Empty : "s")} on resolved components; {summary.DeprecatedSpdxCount} deprecated SPDX identifier{(summary.DeprecatedSpdxCount == 1 ? string.Empty : "s")}");

            // Two inputs rarely enumerate the same set, and which of them a component came from is the fact
            // that says whether the second input earned its place. Per component the report already says it;
            // only the totals say it about the run.
            Console.Error.WriteLine($"  Supplied by: {summary.SbomOnlyCount} sbom only; {summary.PackageManagerOnlyCount} package-manager only; {summary.BothSuppliedCount} both");
            if (verbose)
            {
                WriteSupplyByEcosystem(components, Console.Error);
            }

            // Zeroed collection counters read as "nothing was needed" rather than "nothing was attempted",
            // which is the whole point of this mode, so state the absence instead of printing the counters.
            if (noExternalEvidence)
            {
                Console.Error.WriteLine("  External evidence: not collected; package registries, source repositories, and their caches were not read (--no-external-evidence)");
            }
            else
            {
                WriteEvidenceTable(packageArtifacts, declaredGitHubFiles, packageMetadata, source, Console.Error);
                Console.Error.WriteLine($"  Run: concurrency {packageMetadata.Concurrency}; retries {packageMetadata.RetryCount}; GitHub auth {source.AuthMode}");
            }

            WriteInputDiscoverySummary(
                completed.DetectedInputFileCount,
                completed.InputCandidateDiagnostics,
                completed.SkippedIncompleteInputCount,
                completed.ExcludedInputPaths,
                scanResult.Inventory.Components,
                Console.Error);
            Console.Error.WriteLine($"  Input: {scanResult.Inventory.Input.SourceReference}; input format {scanResult.Inventory.Input.Format.DisplayName}; SPDX {spdx.LicenseListVersion} ({spdx.Source})");
            if (dependency is not null and not "")
            {
                Console.Error.WriteLine($"  Filter: {dependencyFilteredCount} components excluded; {excludedUnknownCount} with unknown dependency type");
            }
        }

        return 0;
    }

    // An incomplete companion set is reported rather than aborting the run, so the report is only trustworthy
    // if the reader is told what it left out. That holds under --quiet, where the summary is suppressed.
    private static void WriteSkippedIncompleteInputWarnings(SkippedIncompleteInput[] skipped, int skippedCount, TextWriter writer)
    {
        for (var skippedIndex = 0; skippedIndex < skippedCount; skippedIndex++)
        {
            ref readonly var input = ref skipped[skippedIndex];
            writer.Write("Warning: ");
            writer.Write(input.LogicalPath);
            writer.Write(" was not scanned: input format ");
            writer.Write(input.FormatName);
            writer.Write(" requires companion file ");
            writer.Write(input.MissingFileName);
            writer.WriteLine(" in the same directory.");
        }
    }

    /// <summary>
    /// States, per ecosystem, which input kinds supplied its components.
    /// </summary>
    /// <remarks>
    /// The totals say whether a second input earned its place in the collection; this says where. One
    /// ecosystem supplied by both inputs and another by only one is the ordinary shape of a polyglot
    /// scan rather than a defect — a source-tree SBOM generator reads npm lockfiles and does not read
    /// NuGet restore output — and the split is what lets a reader see which case they are in.
    ///
    /// Ol prints the counts and draws no conclusion from them. A threshold that called a one-sided
    /// ecosystem a scope mismatch was considered and rejected: measured across eight polyglot
    /// repositories it would have fired on every one of them, all correctly configured, because the
    /// NuGet population is package-manager-only in all of them. A hint that always fires is one readers
    /// learn to skip, which costs more than the missed hint.
    ///
    /// It is a verbose diagnostic rather than a summary fact because the report already carries
    /// <c>ecosystem</c> and <c>suppliedBy</c> per component, so a consumer of the canonical JSON can
    /// compute exactly this. Only the human reading a text or Markdown run cannot, and the default
    /// summary is long enough already.
    /// </remarks>
    private static void WriteSupplyByEcosystem(ReadOnlySpan<ScanComponent> components, TextWriter writer)
    {
        var counts = new Dictionary<string, SupplyCounts>(StringComparer.Ordinal);
        for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            // An empty ecosystem is displayed as "-" everywhere else in the report, and the generator's
            // own root component is the usual one, so the rows keep summing to the totals line.
            var ecosystem = components[componentIndex].Ecosystem;
            if (string.IsNullOrEmpty(ecosystem)) ecosystem = "-";

            counts.TryGetValue(ecosystem, out var entry);
            switch (components[componentIndex].SuppliedBy)
            {
                case ComponentSupply.Sbom: entry.SbomOnly++; break;
                case ComponentSupply.PackageManager: entry.PackageManagerOnly++; break;
                case ComponentSupply.Sbom | ComponentSupply.PackageManager: entry.Both++; break;
            }

            counts[ecosystem] = entry;
        }

        if (counts.Count == 0)
        {
            return;
        }

        var ecosystems = new string[counts.Count];
        counts.Keys.CopyTo(ecosystems, 0);
        Array.Sort(ecosystems, StringComparer.Ordinal);
        for (var ecosystemIndex = 0; ecosystemIndex < ecosystems.Length; ecosystemIndex++)
        {
            var entry = counts[ecosystems[ecosystemIndex]];
            writer.Write("    ");
            writer.Write(ecosystems[ecosystemIndex]);
            writer.Write(": ");
            writer.Write(entry.SbomOnly);
            writer.Write(" sbom only; ");
            writer.Write(entry.PackageManagerOnly);
            writer.Write(" package-manager only; ");
            writer.Write(entry.Both);
            writer.WriteLine(" both");
        }
    }

    /// <summary>How many components of one ecosystem each input kind supplied.</summary>
    private struct SupplyCounts
    {
        public int SbomOnly;
        public int PackageManagerOnly;
        public int Both;
    }

    /// <summary>Column headers of the evidence table, in display order.</summary>
    private static readonly string[] EvidenceColumnHeaders = ["targets", "requests", "hits", "misses", "docs", "matched", "errors"];

    /// <summary>Row labels of the evidence table, in display order.</summary>
    private static readonly string[] EvidenceRowLabels = ["Package artifacts", "Declared GitHub files", "Package metadata", "Source repositories"];

    /// <summary>
    /// States what each evidence collector was pointed at and what came back, as one aligned table.
    /// </summary>
    /// <remarks>
    /// The four collectors share most of their vocabulary — targets, requests, cache hits and misses,
    /// documents, matches, errors — but not all of it, and as four semicolon-separated lines the shared
    /// counters never landed in the same place. Comparing one counter across collectors meant re-reading
    /// four lines of up to 150 characters to find where each had put it. Alignment is the whole change:
    /// the values are the ones those lines already carried, and the mode qualifier the four lines repeated
    /// is stated once in the heading.
    ///
    /// A cell a collector has no counter for is written "-", not 0, because a zero claims it attempted the
    /// work and found nothing. That is the same distinction the External evidence line draws for a run.
    /// Counters only one collector has stay in a named line under the table rather than adding a column
    /// that would be "-" in three of the four rows.
    /// </remarks>
    private static void WriteEvidenceTable(
        in PackageArtifactCollectionSummary packageArtifacts,
        in DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFiles,
        in PackageMetadataSummary packageMetadata,
        in SourceRepositorySummary source,
        TextWriter writer)
    {
        const string Heading = "  Evidence (full scan)";
        const string RowIndent = "    ";
        const string NoCounter = "-";

        // Package metadata counts supported components rather than planned lookups here, because the row's
        // cache hits and misses are counted per component: a lookup count would not sum with them.
        string[][] rows =
        [
            [Count(packageArtifacts.TargetCount), NoCounter, NoCounter, NoCounter, Count(packageArtifacts.DocumentCount), Count(packageArtifacts.MatchedCount), NoCounter],
            [Count(declaredGitHubFiles.TargetCount), Count(declaredGitHubFiles.GitHubRequestCount), Count(declaredGitHubFiles.CacheHitCount), Count(declaredGitHubFiles.CacheMissCount), Count(declaredGitHubFiles.DocumentCount), Count(declaredGitHubFiles.MatchedCount), Count(declaredGitHubFiles.FetchErrorCount)],
            [Count(packageMetadata.SupportedComponentCount), NoCounter, Count(packageMetadata.CacheHitCount), Count(packageMetadata.CacheMissCount), NoCounter, NoCounter, Count(packageMetadata.FetchErrorCount)],
            [Count(source.TargetCount), Count(source.GitHubRequestCount), Count(source.CacheHitCount), Count(source.CacheMissCount), NoCounter, NoCounter, Count(source.FetchErrorCount)],
        ];

        var labelWidth = Heading.Length;
        for (var rowIndex = 0; rowIndex < EvidenceRowLabels.Length; rowIndex++)
        {
            labelWidth = Math.Max(labelWidth, RowIndent.Length + EvidenceRowLabels[rowIndex].Length);
        }

        Span<int> widths = stackalloc int[EvidenceColumnHeaders.Length];
        for (var columnIndex = 0; columnIndex < EvidenceColumnHeaders.Length; columnIndex++)
        {
            var width = EvidenceColumnHeaders[columnIndex].Length;
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                width = Math.Max(width, rows[rowIndex][columnIndex].Length);
            }

            widths[columnIndex] = width;
        }

        WriteEvidenceRow(Heading, EvidenceColumnHeaders, labelWidth, widths, writer);
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            WriteEvidenceRow(RowIndent + EvidenceRowLabels[rowIndex], rows[rowIndex], labelWidth, widths, writer);
        }

        writer.WriteLine($"{RowIndent}Package metadata: {packageMetadata.RefreshedCount} refreshed; {packageMetadata.UnsupportedEcosystemCount} unsupported ecosystems; {packageMetadata.UnversionedPurlCount} unversioned purls; {packageMetadata.NoPurlCount} without purl");
        writer.WriteLine($"{RowIndent}Source repositories: {source.UnknownCount} components without source license");

        static string Count(int value) => value.ToString();
    }

    /// <summary>Writes one evidence row with the label left-aligned and every counter right-aligned in its column.</summary>
    private static void WriteEvidenceRow(string label, string[] cells, int labelWidth, ReadOnlySpan<int> widths, TextWriter writer)
    {
        writer.Write(label.PadRight(labelWidth));
        for (var columnIndex = 0; columnIndex < cells.Length; columnIndex++)
        {
            writer.Write("  ");
            writer.Write(cells[columnIndex].PadLeft(widths[columnIndex]));
        }

        writer.WriteLine();
    }

    private static void WriteInputDiscoverySummary(
        int detectedInputFileCount,
        in InputCandidateDiagnostics candidateDiagnostics,
        int skippedIncompleteInputCount,
        string[] excludedInputPaths,
        ReadOnlySpan<ScanComponent> components,
        TextWriter writer)
    {
        var ignoredCandidateCount = KnownUnsupportedInputCandidates.GetUnresolvedCount(candidateDiagnostics);
        writer.Write("  Input discovery: ");
        writer.Write(detectedInputFileCount);
        writer.Write(detectedInputFileCount == 1 ? " detected file; " : " detected files; ");
        writer.Write(ignoredCandidateCount);
        writer.Write(ignoredCandidateCount == 1 ? " ignored candidate" : " ignored candidates");
        if (ignoredCandidateCount > 0)
        {
            writer.Write(" (");
            KnownUnsupportedInputCandidates.WriteUnresolvedNames(candidateDiagnostics, writer);
            writer.Write(')');
        }

        writer.Write("; ");
        writer.Write(skippedIncompleteInputCount);
        writer.Write(skippedIncompleteInputCount == 1 ? " incomplete input set" : " incomplete input sets");
        writer.Write("; ");
        writer.Write(excludedInputPaths.Length);
        writer.Write(excludedInputPaths.Length == 1 ? " excluded input path" : " excluded input paths");
        if (excludedInputPaths.Length > 0)
        {
            writer.Write(" (");
            for (var excludedIndex = 0; excludedIndex < excludedInputPaths.Length; excludedIndex++)
            {
                if (excludedIndex > 0) writer.Write(", ");
                writer.Write(excludedInputPaths[excludedIndex]);
            }

            writer.Write(')');
        }

        writer.Write("; ecosystems ");
        var ecosystems = new HashSet<string>(StringComparer.Ordinal);
        for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            var ecosystem = components[componentIndex].Ecosystem;
            if (!string.IsNullOrEmpty(ecosystem) && ecosystem != "-")
            {
                ecosystems.Add(ecosystem);
            }
        }

        if (ecosystems.Count == 0)
        {
            writer.WriteLine("none");
            return;
        }

        var sortedEcosystems = new string[ecosystems.Count];
        ecosystems.CopyTo(sortedEcosystems);
        Array.Sort(sortedEcosystems, StringComparer.Ordinal);
        for (var ecosystemIndex = 0; ecosystemIndex < sortedEcosystems.Length; ecosystemIndex++)
        {
            if (ecosystemIndex > 0)
            {
                writer.Write(", ");
            }

            writer.Write(sortedEcosystems[ecosystemIndex]);
        }

        writer.WriteLine();
    }

    private static void WriteText(
        Stream output,
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        GroupRow[]? groups,
        string? groupBy,
        bool verbose,
        bool emptyInventory)
    {
        using var buffer = new PooledStreamBufferWriter(output);
        if (groups is null)
        {
            ReportRenderer.WriteText(buffer, inventory, components, verbose, emptyInventory);
        }
        else
        {
            ReportRenderer.WriteText(buffer, inventory.Input, groups, groupBy!, emptyInventory);
        }
    }

    /// <summary>Writes the Markdown report through the same pooled buffer the other views use.</summary>
    private static void WriteMarkdown(
        Stream output,
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        GroupRow[]? groups,
        string? groupBy,
        bool verbose,
        bool emptyInventory)
    {
        using var buffer = new PooledStreamBufferWriter(output);
        ReportRenderer.WriteMarkdownInputHeader(buffer, inventory.Input);
        if (groups is null)
        {
            ReportRenderer.WriteMarkdown(buffer, inventory, components, verbose, emptyInventory);
        }
        else
        {
            ReportRenderer.WriteMarkdown(buffer, groups, groupBy!, emptyInventory);
        }
    }

    /// <summary>Explains a reached GitHub rate limit in terms of what the next run can change.</summary>
    /// <remarks>
    /// The remedy differs by kind and must not be conflated. A token raises the primary allowance but
    /// does nothing for a secondary limit, which is about request pace.
    /// </remarks>
    internal static void WriteGitHubRateLimitDiagnostic(GitHubRateLimitStatus rateLimit, TextWriter? writer = null)
    {
        writer ??= Console.Error;
        var reset = rateLimit.ResetsAt is { } resetsAt
            ? $" Allowance resets at {resetsAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}."
            : string.Empty;
        var remedy = rateLimit.Kind switch
        {
            GitHubRateLimitKind.Primary when rateLimit.IsUnauthenticated
                => "Unauthenticated requests share a small hourly allowance. Set OL_GITHUB_TOKEN to raise it, then run again.",
            GitHubRateLimitKind.Primary when rateLimit.IsTokenNotApplied
                => "GitHub applied the anonymous allowance although OL_GITHUB_TOKEN is set, so the token did not reach GitHub. Waiting will not help; check for a proxy or handler that drops the Authorization header.",
            GitHubRateLimitKind.Primary
                => "The hourly allowance for this token is spent. Run again after it resets, or narrow the scan with --skip-evidence-packages.",
            _ => "GitHub accepted requests more slowly than Ol issued them. Run again with a lower --concurrency.",
        };

        var kind = rateLimit.Kind == GitHubRateLimitKind.Primary ? "primary" : "secondary";
        writer.WriteLine($"GitHub {kind} rate limit reached (HTTP {(int)rateLimit.StatusCode}); source repository collection stopped.{reset}");
        writer.WriteLine($"  {remedy}");
        writer.WriteLine("  Affected components report source_repository_fetch_failed and were not cached, so a later run collects them normally.");
    }

    private static void WriteJson(
        Stream output,
        DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        ReadOnlySpan<DependencyUsage> componentUsages,
        GroupRow[]? groups,
        string? groupBy,
        SpdxData spdx,
        PackageArtifactCollectionSummary packageArtifactSummary,
        DeclaredGitHubFileArtifactCollectionSummary declaredGitHubFileSummary,
        PackageMetadataSummary metadataSummary,
        SourceRepositorySummary sourceSummary,
        ScanReportScope scope)
    {
        using var buffer = new PooledStreamBufferWriter(output);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            if (groups is null)
            {
                ReportRenderer.WriteJson(writer, inventory, components, componentUsages, spdx, packageArtifactSummary, declaredGitHubFileSummary, metadataSummary, sourceSummary, scope);
            }
            else
            {
                ReportRenderer.WriteJson(writer, inventory, groups, groupBy!, spdx, packageArtifactSummary, declaredGitHubFileSummary, metadataSummary, sourceSummary, scope);
            }

            writer.Flush();
        }

        var newline = buffer.GetSpan(1);
        newline[0] = (byte)'\n';
        buffer.Advance(1);
        buffer.Flush();
    }

    private static void WriteDetectedInputFormat(in ScanInputDescriptor input)
    {
        Console.Error.Write("Detected input format: ");
        Console.Error.Write(input.Kind.Name);
        Console.Error.Write('/');
        Console.Error.WriteLine(input.Format.Name);
    }
}

internal enum ReportFormat
{
    Text,
    Json,
    Markdown,
}

internal enum SortOrder
{
    Asc,
    Desc,
}
