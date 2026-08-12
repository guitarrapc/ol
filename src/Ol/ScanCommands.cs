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
        if (!ScanExecution.TryPrepare(input, inputFormat, spdxData, cacheDir, noExternalEvidence, uncollectedPrefixes, concurrency, retry, out var preparation, out var preparationError))
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
        if (format == ReportFormat.Json)
        {
            try
            {
                var scope = new ScanReportScope(!noExternalEvidence, dependency is null or "" ? null : dependency, dependencyFilteredCount, excludedUnknownCount);
                WriteJson(standardOutput ?? Console.OpenStandardOutput(), scanResult.Inventory, components, componentUsages, groups, groupBy, spdx, packageArtifactSummary, declaredGitHubFileSummary, packageMetadataSummary, sourceRepositorySummary, scope);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Unable to write report: {exception.Message}");
                return 1;
            }

            return 0;
        }

        if (format == ReportFormat.Text)
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
            Console.Error.WriteLine($"  Findings: {summary.UnresolvedWarningCount} warning{(summary.UnresolvedWarningCount == 1 ? string.Empty : "s")} on unresolved components; {summary.ResolvedWarningCount} on resolved components; {summary.DeprecatedSpdxCount} deprecated SPDX identifier{(summary.DeprecatedSpdxCount == 1 ? string.Empty : "s")}");

            // Zeroed collection counters read as "nothing was needed" rather than "nothing was attempted",
            // which is the whole point of this mode, so state the absence instead of printing the counters.
            if (noExternalEvidence)
            {
                Console.Error.WriteLine("  External evidence: not collected; package registries, source repositories, and their caches were not read (--no-external-evidence)");
            }
            else
            {
                Console.Error.WriteLine($"  Package artifacts (full scan): {packageArtifacts.TargetCount} targets; {packageArtifacts.DocumentCount} documents; {packageArtifacts.MatchedCount} matched");
                Console.Error.WriteLine($"  Declared GitHub files (full scan): {declaredGitHubFiles.TargetCount} targets; {declaredGitHubFiles.GitHubRequestCount} GitHub requests; {declaredGitHubFiles.CacheHitCount} cache hits; {declaredGitHubFiles.CacheMissCount} cache misses; {declaredGitHubFiles.DocumentCount} documents; {declaredGitHubFiles.MatchedCount} matched; {declaredGitHubFiles.FetchErrorCount} fetch errors");
                Console.Error.WriteLine($"  Package metadata (full scan): {packageMetadata.SupportedComponentCount} supported; {packageMetadata.CacheHitCount} cache hits; {packageMetadata.CacheMissCount} cache misses; {packageMetadata.RefreshedCount} refreshed; {packageMetadata.FetchErrorCount} fetch errors; {packageMetadata.UnsupportedEcosystemCount} unsupported ecosystems; {packageMetadata.UnversionedPurlCount} unversioned purls");
                Console.Error.WriteLine($"  Source repositories (full scan): {source.TargetCount} targets; {source.GitHubRequestCount} GitHub requests; {source.CacheHitCount} cache hits; {source.CacheMissCount} cache misses; {source.FetchErrorCount} fetch errors; {source.UnknownCount} components without source license");
                Console.Error.WriteLine($"  Run: concurrency {packageMetadata.Concurrency}; retries {packageMetadata.RetryCount}; GitHub auth {source.AuthMode}");
            }

            Console.Error.WriteLine($"  Input: {scanResult.Inventory.Input.SourceReference}; input format {scanResult.Inventory.Input.Format.DisplayName}; SPDX {spdx.LicenseListVersion} ({spdx.Source})");
            if (dependency is not null and not "")
            {
                Console.Error.WriteLine($"  Filter: {dependencyFilteredCount} components excluded; {excludedUnknownCount} with unknown dependency type");
            }
        }

        return 0;
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
