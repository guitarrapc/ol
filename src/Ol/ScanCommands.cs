using System.Security.Cryptography;
using System.Buffers;
using System.Buffers.Text;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Ol.Core;
using Ol.Core.Generated;
using Ol.Core.GitHub;
using Ol.Core.Licensing;
using Ol.Core.PackageManagers;
using Ol.Core.Reporting;
using Ol.Core.Spdx;
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
    /// <param name="refresh">Ignore cached package metadata and source repository entries and fetch them again.</param>
    /// <param name="cacheDir">Root directory for isolated package-metadata and source-repository caches.</param>
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
        var groups = groupBy is null or "" ? null : ScanView.Group(components, groupBy);
        if (format == ReportFormat.Json)
        {
            try
            {
                var scope = new ScanReportScope(!noExternalEvidence, dependency is null or "" ? null : dependency, dependencyFilteredCount, excludedUnknownCount);
                WriteJson(standardOutput ?? Console.OpenStandardOutput(), scanResult.Inventory, components, componentUsages, groups, groupBy, spdx, packageMetadataSummary, sourceRepositorySummary, scope);
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
            var emptyInventory = scanResult.Inventory.Components.Length == 0;
            var text = groups is null
                ? ReportRenderer.RenderMarkdown(scanResult.Inventory, components, verbose, emptyInventory)
                : ReportRenderer.RenderMarkdown(groups, groupBy!, emptyInventory);
            text = ReportRenderer.RenderInputHeader(format, scanResult.Inventory.Input) + text;
            if (!text.EndsWith('\n'))
            {
                text += '\n';
            }

            try
            {
                Console.Write(text);
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
        PackageMetadataSummary metadataSummary,
        SourceRepositorySummary sourceSummary,
        ScanReportScope scope)
    {
        using var buffer = new PooledStreamBufferWriter(output);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            if (groups is null)
            {
                ReportRenderer.WriteJson(writer, inventory, components, componentUsages, spdx, metadataSummary, sourceSummary, scope);
            }
            else
            {
                ReportRenderer.WriteJson(writer, inventory, groups, groupBy!, spdx, metadataSummary, sourceSummary, scope);
            }

            writer.Flush();
        }

        var newline = buffer.GetSpan(1);
        newline[0] = (byte)'\n';
        buffer.Advance(1);
        buffer.Flush();
    }

    internal static DependencyInventory ScanInputs(ScanInputSelection selection, SpdxLicenseIndex spdx, bool includeHash)
    {
        var files = CollectInputFiles(selection);
        var inventories = new DependencyInventory[files.Length];
        var handlers = new DependencyInputHandler[files.Length];
        var consumed = new bool[files.Length];
        var loadedInputs = includeHash ? new byte[files.Length][] : null;
        IncrementalHash? sourceHash = includeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var expectedFormat = selection.HasExpectedFormat ? selection.ExpectedHandler.Format : default;
        var kind = default(ScanInputKind);
        var format = default(ScanInputFormat);
        var specificationVersion = default(Utf8Slice);
        var inventoryCount = 0;
        var sbomCount = 0;
        try
        {
            if (sourceHash is not null)
            {
                for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    var inputBytes = File.ReadAllBytes(files[fileIndex].Path);
                    loadedInputs![fileIndex] = inputBytes;
                    sourceHash.AppendData(Encoding.UTF8.GetBytes(files[fileIndex].LogicalPath));
                    sourceHash.AppendData([0]);
                    sourceHash.AppendData(SHA256.HashData(inputBytes));
                }
            }

            for (var i = 0; i < files.Length; i++)
            {
                if (consumed[i]) continue;
                DependencyInventory inventory;
                DependencyInputHandler handler;
                if (TryCollectInputBundle(files, i, consumed, out handler, out var bundleIndexes))
                {
                    var bundleSources = new byte[bundleIndexes.Length][];
                    for (var bundleIndex = 0; bundleIndex < bundleIndexes.Length; bundleIndex++)
                    {
                        var fileIndex = bundleIndexes[bundleIndex];
                        bundleSources[bundleIndex] = loadedInputs?[fileIndex] ?? File.ReadAllBytes(files[fileIndex].Path);
                        consumed[fileIndex] = true;
                    }

                    if (!string.IsNullOrEmpty(expectedFormat.Name) && expectedFormat != handler.Format)
                    {
                        throw new InvalidOperationException($"Input format {expectedFormat.Name} does not match the detected {handler.Format.Name} format.");
                    }

                    inventory = DependencyInputScanner.ScanBundle(bundleSources, spdx, handler.Format);
                }
                else
                {
                    var inputBytes = loadedInputs?[i] ?? File.ReadAllBytes(files[i].Path);
                    inventory = DependencyInputScanner.Scan(inputBytes, spdx, expectedFormat: expectedFormat);
                    consumed[i] = true;
                    if (!DependencyInputRegistry.Default.TryGetInputFormat(inventory.Input.Format.Name, out handler))
                    {
                        throw new InvalidOperationException($"Detected input format is not registered: {inventory.Input.Format.Name}");
                    }
                }

                // A repository-wide SBOM and per-project package-manager inputs describe one resolution at two
                // granularities, so they combine. Two repository-wide documents are a contradiction in the input
                // rather than something Ol can resolve.
                if (inventory.Input.Kind == ScanInputKind.Sbom)
                {
                    if (sbomCount > 0)
                    {
                        throw new InvalidOperationException("A collection accepts at most one SBOM document.");
                    }

                    sbomCount++;
                }

                inventories[inventoryCount] = inventory;
                handlers[inventoryCount] = handler;
                if (inventoryCount == 0)
                {
                    kind = inventory.Input.Kind;
                    format = inventory.Input.Format;
                    specificationVersion = inventory.Input.SpecificationVersion;
                }
                else
                {
                    if (kind != inventory.Input.Kind)
                    {
                        kind = ScanInputKind.Collection;
                    }

                    if (format != inventory.Input.Format)
                    {
                        format = ScanInputFormat.Collection;
                        specificationVersion = default;
                    }
                    else if (!specificationVersion.Span.SequenceEqual(inventory.Input.SpecificationVersion.Span))
                    {
                        specificationVersion = default;
                    }
                }

                inventoryCount++;
            }

            var descriptor = new ScanInputDescriptor(
                kind,
                format,
                GetInputSourceReference(selection.Paths),
                sourceHash is null ? string.Empty : Convert.ToHexString(sourceHash.GetHashAndReset()).ToLowerInvariant(),
                specificationVersion);
            // One input goes through the same combiner as several. A registered format declares what makes two
            // observations the same package, and skipping the combiner made a single input the only path that did not
            // apply its own declaration: one CycloneDX document reported a component per bom-ref alone and per purl
            // when a lockfile was scanned beside it. The occurrences still record every entry the document listed.
            return DependencyInventoryCombiner.Combine(inventories.AsSpan(0, inventoryCount), handlers.AsSpan(0, inventoryCount), descriptor);
        }
        finally
        {
            sourceHash?.Dispose();
        }
    }

    private static bool TryCollectInputBundle(
        ReadOnlySpan<CollectedInputFile> files,
        int candidateIndex,
        ReadOnlySpan<bool> consumed,
        out DependencyInputHandler handler,
        out int[] bundleIndexes)
    {
        var candidateName = Path.GetFileName(files[candidateIndex].Path);
        var candidateDirectory = Path.GetDirectoryName(files[candidateIndex].Path) ?? string.Empty;
        var fileNameComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var registeredHandlers = DependencyInputRegistry.Default.RegisteredHandlers;
        for (var handlerIndex = 0; handlerIndex < registeredHandlers.Length; handlerIndex++)
        {
            var candidateHandler = registeredHandlers[handlerIndex];
            if (candidateHandler.BundleParser is null) continue;
            var requiredNames = candidateHandler.DirectoryFileNames.Span;
            var ownsCandidate = false;
            for (var requiredIndex = 0; requiredIndex < requiredNames.Length; requiredIndex++)
            {
                if (string.Equals(requiredNames[requiredIndex], candidateName, fileNameComparison))
                {
                    ownsCandidate = true;
                    break;
                }
            }

            if (!ownsCandidate) continue;
            bundleIndexes = new int[requiredNames.Length];
            bundleIndexes.AsSpan().Fill(-1);
            for (var requiredIndex = 0; requiredIndex < requiredNames.Length; requiredIndex++)
            {
                for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    if (consumed[fileIndex]
                        || !string.Equals(Path.GetDirectoryName(files[fileIndex].Path) ?? string.Empty, candidateDirectory, fileNameComparison)
                        || !string.Equals(Path.GetFileName(files[fileIndex].Path), requiredNames[requiredIndex], fileNameComparison))
                    {
                        continue;
                    }

                    bundleIndexes[requiredIndex] = fileIndex;
                    break;
                }

                if (bundleIndexes[requiredIndex] < 0)
                {
                    throw new InvalidOperationException($"Input format {candidateHandler.Format.Name} requires companion file {requiredNames[requiredIndex]} in the same directory.");
                }
            }

            handler = candidateHandler;
            return true;
        }

        handler = default;
        bundleIndexes = [];
        return false;
    }

    private static CollectedInputFile[] CollectInputFiles(ScanInputSelection selection)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var collectedByPath = new Dictionary<string, CollectedInputFile>(pathComparer);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        for (var inputIndex = 0; inputIndex < selection.Paths.Length; inputIndex++)
        {
            var inputPath = Path.GetFullPath(selection.Paths[inputIndex]);
            if (File.Exists(inputPath))
            {
                AddCollectedFile(collectedByPath, inputPath, Path.GetFileName(inputPath));
                continue;
            }

            var rootName = new DirectoryInfo(inputPath).Name;
            if (selection.HasExpectedFormat)
            {
                DiscoverDirectoryFiles(inputPath, rootName, selection.ExpectedHandler, enumerationOptions, collectedByPath);
                continue;
            }

            var registeredHandlers = DependencyInputRegistry.Default.RegisteredHandlers;
            for (var handlerIndex = 0; handlerIndex < registeredHandlers.Length; handlerIndex++)
            {
                DiscoverDirectoryFiles(inputPath, rootName, registeredHandlers[handlerIndex], enumerationOptions, collectedByPath);
            }
        }

        if (collectedByPath.Count == 0)
        {
            throw new InvalidOperationException("No registered dependency input files were found in the input directories.");
        }

        var files = new CollectedInputFile[collectedByPath.Count];
        var fileIndex = 0;
        foreach (var item in collectedByPath.Values)
        {
            files[fileIndex++] = item;
        }

        Array.Sort(files, CollectedInputFileComparer.Instance);
        return files;
    }

    private static void DiscoverDirectoryFiles(
        string directory,
        string rootName,
        DependencyInputHandler handler,
        EnumerationOptions options,
        Dictionary<string, CollectedInputFile> collectedByPath)
    {
        var fileNames = handler.DirectoryFileNames.Span;
        for (var fileNameIndex = 0; fileNameIndex < fileNames.Length; fileNameIndex++)
        {
            var paths = Directory.GetFiles(directory, fileNames[fileNameIndex], options);
            for (var pathIndex = 0; pathIndex < paths.Length; pathIndex++)
            {
                var fullPath = Path.GetFullPath(paths[pathIndex]);
                var relativePath = Path.GetRelativePath(directory, fullPath).Replace('\\', '/');
                AddCollectedFile(collectedByPath, fullPath, string.Concat(rootName, "/", relativePath));
            }
        }
    }

    private static void AddCollectedFile(Dictionary<string, CollectedInputFile> collectedByPath, string path, string logicalPath)
    {
        var candidate = new CollectedInputFile(path, logicalPath);
        if (!collectedByPath.TryGetValue(path, out var existing) || string.CompareOrdinal(candidate.LogicalPath, existing.LogicalPath) < 0)
        {
            collectedByPath[path] = candidate;
        }
    }

    private static string GetInputSourceReference(string[] inputPaths)
    {
        if (inputPaths.Length != 1)
        {
            return string.Concat(inputPaths.Length, " inputs");
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputPaths[0]));
        return Path.GetFileName(path);
    }

    internal static bool TryResolveInput(string[]? input, string? inputFormat, out ScanInputSelection selection, out string error)
    {
        selection = default;
        var hasInput = input is { Length: > 0 };
        if (!hasInput)
        {
            error = "--input must be specified.";
            return false;
        }

        for (var inputIndex = 0; inputIndex < input!.Length; inputIndex++)
        {
            if (string.IsNullOrWhiteSpace(input[inputIndex]))
            {
                error = "Input paths must not be empty.";
                return false;
            }
        }

        if (string.IsNullOrEmpty(inputFormat) || string.Equals(inputFormat, "auto", StringComparison.OrdinalIgnoreCase))
        {
            selection = new ScanInputSelection(input, default);
            error = string.Empty;
            return true;
        }

        if (!DependencyInputRegistry.Default.TryGetInputFormat(inputFormat, out var handler))
        {
            error = $"Unsupported input format: {inputFormat}";
            return false;
        }

        selection = new ScanInputSelection(input, handler);
        error = string.Empty;
        return true;
    }

    private static void WriteDetectedInputFormat(in ScanInputDescriptor input)
    {
        Console.Error.Write("Detected input format: ");
        Console.Error.Write(input.Kind.Name);
        Console.Error.Write('/');
        Console.Error.WriteLine(input.Format.Name);
    }

    internal readonly record struct ScanInputSelection(string[] Paths, DependencyInputHandler ExpectedHandler)
    {
        public bool HasExpectedFormat => !string.IsNullOrEmpty(ExpectedHandler.Format.Name);
    }

    private readonly record struct CollectedInputFile(string Path, string LogicalPath);

    private sealed class CollectedInputFileComparer : IComparer<CollectedInputFile>
    {
        public static CollectedInputFileComparer Instance { get; } = new();

        public int Compare(CollectedInputFile left, CollectedInputFile right)
        {
            var comparison = string.CompareOrdinal(left.LogicalPath, right.LogicalPath);
            return comparison != 0 ? comparison : string.CompareOrdinal(left.Path, right.Path);
        }
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

/// <summary>
/// Describes how to derive the active SPDX data digests, without deriving them.
/// </summary>
/// <remarks>
/// Only the JSON report writes these digests, but <see cref="SpdxData"/> is built for every run.
/// Deriving them eagerly cost 33 KB per process — a joined string of every generated identifier plus
/// its UTF-8 copy, or a second full read of each installed SPDX file — for a value that text and
/// Markdown output never read. A run that writes JSON to both a file and standard output asks twice,
/// so each digest is retained after the first request.
/// </remarks>
internal sealed class SpdxDataDigest
{
    private readonly string? exceptionsPath;
    private readonly string? licensesPath;
    private string? exceptions;
    private string? licenses;

    private SpdxDataDigest(string? licensesPath, string? exceptionsPath)
    {
        this.licensesPath = licensesPath;
        this.exceptionsPath = exceptionsPath;
    }

    /// <summary>Describes the digests of the bundled generated identifiers.</summary>
    public static SpdxDataDigest ForGeneratedData() => new(null, null);

    /// <summary>Describes the digests of an installed SPDX data directory.</summary>
    public static SpdxDataDigest ForFiles(string licensesPath, string exceptionsPath) => new(licensesPath, exceptionsPath);

    /// <summary>Calculates the active licenses digest once per run.</summary>
    /// <remarks>
    /// Covers names and <c>seeAlso</c> URLs as well as identifiers, because all of them decide what a
    /// value resolves to. Hashing only the identifiers would give two snapshots that rename a license, or
    /// move a URL between licenses, the same digest.
    /// </remarks>
    public string GetLicensesSha256()
        => licenses ??= licensesPath is null
            ? ComputeGeneratedDataHash(SpdxGeneratedLicenseData.LicenseIds, SpdxGeneratedLicenseData.LicenseNames, SpdxGeneratedLicenseData.SeeAlsoUrls, SpdxGeneratedLicenseData.SeeAlsoLicenseIds)
            : HashFile(licensesPath);

    /// <summary>Calculates the active exceptions digest once per run.</summary>
    public string GetExceptionsSha256()
        => exceptions ??= exceptionsPath is null ? ComputeGeneratedDataHash(SpdxGeneratedLicenseData.ExceptionIds) : HashFile(exceptionsPath);

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ComputeGeneratedDataHash(params string[][] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values.SelectMany(static value => value))))).ToLowerInvariant();
}

/// <remarks>
/// <see cref="Digest"/> is never null in practice: every instance comes from <see cref="Load"/>.
/// </remarks>
internal readonly record struct SpdxData(
    SpdxLicenseIndex Index,
    string Source,
    string LicenseListVersion,
    string DataRef,
    SpdxDataDigest Digest)
{
    private static readonly SpdxData Bundled = CreateBundled();

    /// <summary>Calculates the active licenses digest. Named as a method because the calculation is not free.</summary>
    public string GetLicensesSha256() => Digest.GetLicensesSha256();

    /// <summary>Calculates the active exceptions digest. Named as a method because the calculation is not free.</summary>
    public string GetExceptionsSha256() => Digest.GetExceptionsSha256();

    public static SpdxData Load(string? directory)
    {
        if (directory is not null and not "")
        {
            return LoadFromDirectory(directory, "cli-argument", "cli-argument");
        }

        if (SpdxStore.TryGetActiveDirectory(out var activeDirectory))
        {
            var version = SpdxStore.GetActiveVersion();
            return LoadFromDirectory(activeDirectory, "user", $"ol/spdx/{version}");
        }

        return Bundled;
    }

    private static SpdxData CreateBundled()
    {
        return new SpdxData(
            new SpdxLicenseIndex(
                SpdxGeneratedLicenseData.LicenseIds,
                SpdxGeneratedLicenseData.ExceptionIds,
                SpdxGeneratedLicenseData.DeprecatedLicenseIds,
                SpdxGeneratedLicenseData.LicenseNames,
                SpdxGeneratedLicenseData.SeeAlsoUrls,
                SpdxGeneratedLicenseData.SeeAlsoLicenseIds),
            "bundled",
            SpdxGeneratedLicenseData.LicenseListVersion,
            "bundled/spdx/builtin",
            SpdxDataDigest.ForGeneratedData());
    }

    private static SpdxData LoadFromDirectory(string directory, string source, string dataRef)
    {
        var licensesPath = Path.Combine(directory, "licenses.json");
        var exceptionsPath = Path.Combine(directory, "exceptions.json");
        if (!File.Exists(licensesPath) || !File.Exists(exceptionsPath))
        {
            throw new DirectoryNotFoundException("SPDX data directory must contain licenses.json and exceptions.json.");
        }

        var licenses = ReadSpdxData(licensesPath, "licenses", "licenseId");
        var exceptions = ReadSpdxData(exceptionsPath, "exceptions", "licenseExceptionId");
        return new SpdxData(
            new SpdxLicenseIndex(licenses.Ids, exceptions.Ids, licenses.DeprecatedIds, licenses.Names, licenses.SeeAlsoUrls, licenses.SeeAlsoIds),
            source,
            licenses.Version,
            dataRef,
            SpdxDataDigest.ForFiles(licensesPath, exceptionsPath));
    }

    /// <summary>Reads identifiers, their SPDX names, and the deprecated set from one SPDX document.</summary>
    /// <remarks>
    /// <c>Names</c> shares its index with <c>Ids</c>, and an entry that states no name keeps an empty
    /// string so the two stay aligned. The exceptions document carries names too, but Ol resolves an
    /// exception only as an operand of <c>WITH</c>, where the operand is an identifier.
    /// </remarks>
    private static (string Version, string[] Ids, string[] Names, string[] DeprecatedIds, string[] SeeAlsoUrls, string[] SeeAlsoIds) ReadSpdxData(string path, string arrayName, string propertyName)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(SkipUtf8Bom(bytes));
        var values = document.RootElement.GetProperty(arrayName);
        var ids = new string[values.GetArrayLength()];
        var names = new string[ids.Length];
        var deprecatedIds = new List<string>();
        // One license publishes several URLs and one URL several licenses, so these cannot share the
        // identifier index.
        var seeAlsoUrls = new List<string>();
        var seeAlsoIds = new List<string>();
        var index = 0;
        foreach (var item in values.EnumerateArray())
        {
            var id = item.GetProperty(propertyName).GetString() ?? string.Empty;
            ids[index] = id;
            names[index] = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
            if (item.TryGetProperty("isDeprecatedLicenseId", out var deprecated) && deprecated.ValueKind == JsonValueKind.True)
            {
                deprecatedIds.Add(id);
            }

            if (item.TryGetProperty("seeAlso", out var seeAlso) && seeAlso.ValueKind == JsonValueKind.Array)
            {
                foreach (var url in seeAlso.EnumerateArray())
                {
                    if (url.ValueKind != JsonValueKind.String) continue;

                    var value = url.GetString();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    seeAlsoUrls.Add(value);
                    seeAlsoIds.Add(id);
                }
            }

            index++;
        }

        return (
            document.RootElement.TryGetProperty("licenseListVersion", out var version) ? version.GetString() ?? "unknown" : "unknown",
            ids,
            names,
            deprecatedIds.ToArray(),
            seeAlsoUrls.ToArray(),
            seeAlsoIds.ToArray());
    }

    private static ReadOnlyMemory<byte> SkipUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes;
}

internal static class ScanView
{
    public static void Validate(string? dependency, string sort, string? groupBy)
    {
        if (dependency is not null and not "")
        {
            var tokens = dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                throw new ArgumentException("Dependency filter must contain at least one value.");
            }

            foreach (var token in tokens)
            {
                ParseDependency(token);
            }
        }

        ParseSortFields(sort);
        if (groupBy is not null and not "")
        {
            ParseGroupFields(groupBy);
        }
    }

    public static int Apply(ScanComponent[] components, string? dependency, string sort, SortOrder sortOrder)
    {
        var count = FilterByDependency(components, dependency);
        components.AsSpan(0, count).Sort(CreateComparison(sort, sortOrder));
        return count;
    }

    /// <summary>Filters and sorts <paramref name="components"/> while keeping <paramref name="usages"/> positionally aligned.</summary>
    public static int Apply(ScanComponent[] components, DependencyUsage[] usages, string? dependency, string sort, SortOrder sortOrder)
    {
        var count = FilterByDependency(components, usages, dependency);
        Array.Sort(components, usages, 0, count, Comparer<ScanComponent>.Create(CreateComparison(sort, sortOrder)));
        return count;
    }

    public static GroupRow[] Group(ReadOnlySpan<ScanComponent> components, string groupBy)
    {
        var fields = ParseGroupFields(groupBy);
        var indexes = new Dictionary<string[], int>(GroupValuesComparer.Instance);
        var groupValues = new List<string[]>();
        var counts = new List<int>();
        var componentGroups = ArrayPool<int>.Shared.Rent(Math.Max(components.Length, 1));
        try
        {
            for (var i = 0; i < components.Length; i++)
            {
                var candidate = CreateGroupValues(components[i], fields);
                if (!indexes.TryGetValue(candidate, out var group))
                {
                    group = groupValues.Count;
                    indexes.Add(candidate, group);
                    groupValues.Add(candidate);
                    counts.Add(0);
                }

                componentGroups[i] = group;
                counts[group]++;
            }

            var buckets = new ScanComponent[groupValues.Count][];
            var filled = ArrayPool<int>.Shared.Rent(Math.Max(groupValues.Count, 1));
            try
            {
                for (var i = 0; i < buckets.Length; i++)
                {
                    buckets[i] = new ScanComponent[counts[i]];
                    filled[i] = 0;
                }

                for (var i = 0; i < components.Length; i++)
                {
                    var group = componentGroups[i];
                    buckets[group][filled[group]++] = components[i];
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(filled);
            }

            var result = new GroupRow[buckets.Length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new GroupRow(groupValues[i], counts[i], buckets[i]);
            }

            Array.Sort(result, CompareGroupRows);
            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(componentGroups);
        }
    }

    public static int CountExcludedUnknown(ReadOnlySpan<ScanComponent> components, string dependency)
    {
        var includesUnknown = false;
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseDependency(token) == DependencyType.Unknown)
            {
                includesUnknown = true;
                break;
            }
        }

        if (includesUnknown)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i].DependencyType == DependencyType.Unknown)
            {
                count++;
            }
        }

        return count;
    }

    private static int FilterByDependency(Span<ScanComponent> components, string? dependency)
    {
        if (dependency is null or "")
        {
            return components.Length;
        }

        Span<bool> allowed = stackalloc bool[4];
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            allowed[(int)ParseDependency(token)] = true;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (allowed[(int)components[i].DependencyType])
            {
                components[count] = components[i];
                count++;
            }
        }

        return count;
    }

    private static int FilterByDependency(Span<ScanComponent> components, Span<DependencyUsage> usages, string? dependency)
    {
        if (dependency is null or "")
        {
            return components.Length;
        }

        Span<bool> allowed = stackalloc bool[4];
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            allowed[(int)ParseDependency(token)] = true;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (allowed[(int)components[i].DependencyType])
            {
                components[count] = components[i];
                usages[count] = usages[i];
                count++;
            }
        }

        return count;
    }

    private static DependencyType ParseDependency(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "root" => DependencyType.Root,
            "direct" => DependencyType.Direct,
            "transitive" => DependencyType.Transitive,
            "unknown" => DependencyType.Unknown,
            _ => throw new ArgumentException($"Unknown dependency value: {value}"),
        };
    }

    private static Comparison<ScanComponent> CreateComparison(string sort, SortOrder sortOrder)
    {
        var keys = ParseSortFields(sort);
        return (left, right) =>
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var comparison = CompareByKey(left, right, keys[i]);
                if (comparison != 0)
                {
                    return sortOrder == SortOrder.Desc ? -comparison : comparison;
                }
            }

            return 0;
        };
    }

    private static SortField[] ParseSortFields(string sort)
    {
        var tokens = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Sort must contain at least one key.");
        }

        var fields = new SortField[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            fields[i] = ParseSortField(tokens[i]);
        }

        return fields;
    }

    private static SortField ParseSortField(string value)
    {
        if (value.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Name;
        }

        if (value.Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Version;
        }

        if (value.Equals("license", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.License;
        }

        if (value.Equals("ecosystem", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Ecosystem;
        }

        if (value.Equals("dependency", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Dependency;
        }

        if (value.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Status;
        }

        if (value.Equals("purl", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Purl;
        }

        throw new ArgumentException($"Unknown sort key: {value}");
    }

    private static int CompareByKey(ScanComponent left, ScanComponent right, SortField key)
    {
        return key switch
        {
            SortField.Name => Utf8Slice.CompareOrdinal(left.Name, right.Name),
            SortField.Version => Utf8Slice.CompareOrdinal(left.Version, right.Version),
            SortField.License => Utf8Slice.CompareOrdinal(left.License, right.License),
            SortField.Ecosystem => string.CompareOrdinal(left.Ecosystem, right.Ecosystem),
            SortField.Dependency => left.DependencyType.CompareTo(right.DependencyType),
            SortField.Status => left.Status.CompareTo(right.Status),
            SortField.Purl => Utf8Slice.CompareOrdinal(left.Purl, right.Purl),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    private static GroupField[] ParseGroupFields(string groupBy)
    {
        var tokens = groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Group-by must contain at least one key.");
        }

        var fields = new GroupField[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            fields[i] = tokens[i].ToLowerInvariant() switch
            {
                "name" => GroupField.Name,
                "version" => GroupField.Version,
                "license" => GroupField.License,
                "ecosystem" => GroupField.Ecosystem,
                "dependency" => GroupField.Dependency,
                "status" => GroupField.Status,
                _ => throw new ArgumentException($"Unknown group key: {tokens[i]}"),
            };
        }

        return fields;
    }

    private static string[] CreateGroupValues(ScanComponent component, GroupField[] fields)
    {
        var values = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            values[i] = fields[i] switch
            {
                GroupField.Name => component.Name.ToString(),
                GroupField.Version => component.Version.ToString(),
                GroupField.License => component.License.ToString(),
                GroupField.Ecosystem => component.Ecosystem,
                GroupField.Dependency => ReportRenderer.GetDependencyTypeName(component.DependencyType),
                GroupField.Status => ReportRenderer.GetStatusName(component.Status),
                _ => throw new ArgumentOutOfRangeException(nameof(fields)),
            };
        }

        return values;
    }

    /// <summary>Compares group value arrays so the values themselves can serve as the row key.</summary>
    private sealed class GroupValuesComparer : IEqualityComparer<string[]>
    {
        public static readonly GroupValuesComparer Instance = new();

        public bool Equals(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++)
            {
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            }

            return true;
        }

        public int GetHashCode(string[] obj)
        {
            var hash = new HashCode();
            for (var i = 0; i < obj.Length; i++)
            {
                hash.Add(obj[i], StringComparer.Ordinal);
            }

            return hash.ToHashCode();
        }
    }

    private static int CompareGroupRows(GroupRow left, GroupRow right)
    {
        for (var i = 0; i < left.Values.Length; i++)
        {
            var comparison = string.CompareOrdinal(left.Values[i], right.Values[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}

internal enum GroupField
{
    Name,
    Version,
    License,
    Ecosystem,
    Dependency,
    Status,
}

internal enum SortField
{
    Name,
    Version,
    License,
    Ecosystem,
    Dependency,
    Status,
    Purl,
}

internal readonly record struct GroupRow(string[] Values, int Count, ScanComponent[] Components);

/// <summary>What the run did and what the rendered view left out, as the canonical JSON report states it.</summary>
/// <remarks>
/// The text and Markdown views state both facts in the stderr summary, and the JSON view emits no stderr
/// summary because the document is supposed to carry them instead. Neither is derivable from the counters:
/// a run that never collected external evidence and a run that collected and found nothing to do produce
/// the same zeroed metadata, and a filtered report is indistinguishable from a complete one once the
/// excluded components are gone. Read as a complete report, a filtered one is a false pass.
/// </remarks>
/// <param name="ExternalEvidenceCollected">Whether package registries and source repositories were read at all.</param>
/// <param name="DependencyFilter">The <c>--dependency</c> filter applied to the view, or null when unfiltered.</param>
/// <param name="ExcludedCount">Components the filter removed from the view.</param>
/// <param name="ExcludedUnknownCount">Removed components whose dependency relationship is unknown.</param>
internal readonly record struct ScanReportScope(
    bool ExternalEvidenceCollected,
    string? DependencyFilter,
    int ExcludedCount,
    int ExcludedUnknownCount);

internal static class ReportRenderer
{
    private const int JsonSchemaVersion = 1;
    private const string ToolName = "ol";
    private const string ToolInformationUri = "https://github.com/guitarrapc/ol";
    private static readonly string ToolVersion =
        typeof(ReportRenderer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReportRenderer).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string RenderInputHeader(ReportFormat format, ScanInputDescriptor input)
        => format == ReportFormat.Markdown
            ? $"Input: `{input.Kind.Name}/{input.Format.Name}`{Environment.NewLine}{Environment.NewLine}"
            : $"Input: {input.Kind.Name}/{input.Format.Name}{Environment.NewLine}{Environment.NewLine}";

    /// <summary>The identifier and sentence that state an input contributed no dependency inventory.</summary>
    /// <remarks>
    /// A recognized input that resolves nothing produces a report where every count is zero, and a
    /// zero-violation policy result follows from it. Read as a pass, that is the one false negative a
    /// compliance gate cannot recover from, and the causes are ordinary: an unrestored project, an obj
    /// directory from a different build, an SBOM generated before install. So it is stated in the primary
    /// result, which <c>--quiet</c> does not suppress, and retained in the JSON report's warnings. It is
    /// not a command failure: the input was read and the report is complete, and only the reader knows
    /// whether "no dependencies" is the expected answer.
    /// </remarks>
    private const string EmptyInventoryWarning = "input_declares_no_components";
    private static ReadOnlySpan<byte> EmptyInventoryHeadingUtf8 => "No components"u8;
    private static ReadOnlySpan<byte> EmptyInventorySentenceUtf8 => "The input declares no resolved dependencies, so this report states nothing about licenses."u8;

    public static void WriteText(
        IBufferWriter<byte> writer,
        in DependencyInventory inventory,
        ReadOnlySpan<ScanComponent> components,
        bool verbose,
        bool emptyInventory = false)
    {
        WriteInputHeader(writer, inventory.Input);
        WriteUtf8(writer, verbose
            ? "NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED PURL"u8
            : "NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED"u8);
        WriteNewLine(writer);
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            WriteDisplay(writer, component.Name);
            WriteUtf8(writer, " "u8);
            WriteDisplay(writer, component.Version);
            WriteUtf8(writer, " "u8);
            WriteDisplay(writer, component.License);
            WriteUtf8(writer, " "u8);
            WriteDisplay(writer, component.Ecosystem);
            WriteUtf8(writer, " "u8);
            WriteUtf8(writer, GetDependencyTypeUtf8(component.DependencyType));
            WriteUtf8(writer, " "u8);
            WriteUtf8(writer, component.Status.ToUtf8());
            WriteUtf8(writer, " "u8);
            WriteUtf8(writer, GetSuppliedByUtf8(component.SuppliedBy));
            if (verbose)
            {
                WriteUtf8(writer, " "u8);
                WriteDisplay(writer, component.Purl);
            }

            WriteNewLine(writer);
        }

        WriteEmptyInventoryText(writer, emptyInventory);
        WriteUnresolvedText(writer, inventory, components);
    }

    private static void WriteEmptyInventoryText(IBufferWriter<byte> writer, bool emptyInventory)
    {
        if (!emptyInventory)
        {
            return;
        }

        WriteNewLine(writer);
        WriteUtf8(writer, EmptyInventoryHeadingUtf8);
        WriteNewLine(writer);
        WriteUtf8(writer, "  "u8);
        WriteUtf8(writer, EmptyInventorySentenceUtf8);
        WriteNewLine(writer);
    }

    /// <summary>
    /// Explains every displayed component the scan did not resolve to one license.
    /// </summary>
    /// <remarks>
    /// The table alone cannot answer "why", and the answer decides what a reviewer does next: wait for
    /// Ol, open a document, or ask the publisher. The reason uses the same identifiers as the JSON
    /// report so one vocabulary describes both, and the reference is a location Ol actually observed
    /// rather than one it constructed. The section is omitted when there is nothing to explain.
    /// </remarks>
    private static void WriteUnresolvedText(IBufferWriter<byte> writer, in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components)
    {
        var first = true;
        var rootPaths = default(DependencyRootPaths);
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (IsExplainedElsewhere(component) || !TryGetUnresolvedReason(component, out var reason))
            {
                continue;
            }

            if (first)
            {
                WriteNewLine(writer);
                WriteUtf8(writer, "Unresolved components"u8);
                WriteNewLine(writer);
                rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
                first = false;
            }

            WriteUtf8(writer, "  "u8);
            WriteDisplay(writer, component.Name);
            WriteUtf8(writer, " "u8);
            WriteDisplay(writer, component.Version);
            WriteUtf8(writer, " "u8);
            WriteUtf8(writer, reason);
            var reference = GetUnresolvedReference(component, reason);
            if (reference.Length != 0)
            {
                WriteUtf8(writer, " "u8);
                WriteUtf8(writer, reference);
            }

            // The section says what to do next, and for a transitive component that is to change the
            // direct dependency that pulled it in rather than the component the row names.
            var path = DependencyPathText.Introducer(inventory, rootPaths, component, i);
            if (path.Length != 0)
            {
                WriteUtf8(writer, " via "u8);
                WriteUtf8(writer, path);
            }

            WriteNewLine(writer);
        }
    }

    /// <summary>Selects the one mechanism that best explains an unresolved component.</summary>
    /// <remarks>
    /// A component can carry several warnings at once, and listing all of them restates plumbing instead of naming
    /// the next action. A component with no mechanism at all is not listed: repeating its status would add a row
    /// without adding a fact the table does not already show. <see cref="SelectUnresolvedReason"/> holds the order,
    /// which is a reported contract rather than an implementation detail.
    /// </remarks>
    private static bool TryGetUnresolvedReason(in ScanComponent component, out ReadOnlySpan<byte> reason)
    {
        reason = SelectUnresolvedReason(CollectUnresolvedEvidence(component));
        return !reason.IsEmpty;
    }

    /// <summary>
    /// Reduces a component's candidates to the facts the ranking asks about.
    /// </summary>
    /// <remarks>
    /// Declared references and the family classifier are derived here rather than recorded by each provider, because
    /// what a reviewer does next follows from the kind of place a publisher named and from nothing else. Several
    /// sources can each declare a different kind for one component, so the flags accumulate and the ranking picks the
    /// strongest present rather than whichever source spoke first.
    /// </remarks>
    private static UnresolvedEvidence CollectUnresolvedEvidence(in ScanComponent component)
    {
        var evidence = default(UnresolvedEvidence);
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            evidence.Warnings |= candidate.Warnings;
            switch (candidate.Evidence.DeclaredReference?.Kind)
            {
                case DeclaredLicenseReferenceKind.ArtifactPath: evidence.DeclaredFile = true; break;
                case DeclaredLicenseReferenceKind.InlineText: evidence.DeclaredText = true; break;
                case DeclaredLicenseReferenceKind.Location: evidence.DeclaredLocation = true; break;
            }

            evidence.FamilyClassifier |= candidate.Status == LicenseStatus.Ambiguous && PyPiLicenseClassifier.IsNotSpecific(candidate.Raw.Span);
        }

        return evidence;
    }

    /// <summary>Ranks the mechanisms from the most specific and actionable to the most general.</summary>
    private static ReadOnlySpan<byte> SelectUnresolvedReason(UnresolvedEvidence evidence)
    {
        // Collection that never ran, or a registry that answered "no such package", settles the component: no later
        // mechanism can explain more than the fact that there was nothing to explain.
        if (evidence.Has(LicenseCandidateWarnings.ExternalEvidenceNotCollected)) return "external_evidence_not_collected"u8;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataNotFound)) return "package_metadata_not_found"u8;

        // A document that certainly answers the question outranks any outcome about where Ol looked.
        if (evidence.DeclaredFile) return "declared_license_file_not_collected"u8;
        if (evidence.DeclaredText) return "declared_license_text_not_collected"u8;

        // A document Ol did read but could not classify still points at something to open.
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotRecognized)) return "license_not_recognized"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceLicenseNotDetected)) return "license_not_detected"u8;

        // A URL may lead anywhere, so it ranks below a named document; a family classifier names no place at all.
        if (evidence.DeclaredLocation) return "declared_license_location_not_collected"u8;
        if (evidence.FamilyClassifier) return "license_classifier_not_specific"u8;

        // A component no registry could be asked about also has no repository, because nothing produced one. Naming
        // the repository would report the consequence and send the reader hunting for something never sought.
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataUnversionedPurl)) return "package_metadata_unversioned_purl"u8;
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedPackageMetadata)) return "unsupported_package_metadata"u8;

        // Last come the outcomes that describe only where Ol looked, ending with the two that a later run may change.
        if (evidence.Has(LicenseCandidateWarnings.UnsupportedSourceRepository)) return "unsupported_source_repository"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositorySubdirectory)) return "source_repository_subdirectory"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryUnavailable)) return "source_repository_unavailable"u8;
        if (evidence.Has(LicenseCandidateWarnings.SourceRepositoryFetchFailed)) return "source_repository_fetch_failed"u8;
        if (evidence.Has(LicenseCandidateWarnings.PackageMetadataFetchFailed)) return "package_metadata_fetch_failed"u8;

        return default;
    }

    /// <summary>The facts about one component that decide which unresolved mechanism is reported.</summary>
    private struct UnresolvedEvidence
    {
        public LicenseCandidateWarnings Warnings;
        public bool DeclaredFile;
        public bool DeclaredText;
        public bool DeclaredLocation;
        public bool FamilyClassifier;

        public readonly bool Has(LicenseCandidateWarnings warning) => (Warnings & warning) != 0;
    }

    /// <summary>
    /// Reports whether a component needs no entry in the unresolved section.
    /// </summary>
    /// <remarks>
    /// A resolved component has nothing to explain. Neither does a root: it is the subject of the scan rather than a
    /// dependency of it, and <see href="cli.md#contract-policy-checks">policy evaluates all non-root components</see>,
    /// so an entry would ask a reviewer for work that no check will ever require. It stays in the table, because the
    /// report must not stop saying what the input described.
    /// </remarks>
    private static bool IsExplainedElsewhere(in ScanComponent component)
        => component.Status == LicenseStatus.Matched || component.DependencyType == DependencyType.Root;

    /// <summary>Returns the location Ol observed for this reason, or an empty value.</summary>
    /// <remarks>
    /// Only the two mechanisms whose whole point is an unread document supply one: a repository license
    /// file GitHub could not identify, and a repository URL Ol cannot collect from. It is tied to the
    /// selected reason rather than to any candidate, because a homepage printed beside an unread license
    /// file would read as the place that file can be found. Ol never constructs a URL evidence did not
    /// supply, so a package whose license text is inside its own artifact shows no reference.
    /// </remarks>
    private static string GetUnresolvedReference(in ScanComponent component, ReadOnlySpan<byte> reason)
    {
        // A location the publisher declared outranks anything Ol inferred, because it is the place the
        // publisher said the license is rather than a place Ol happened to look. Embedded text names no
        // place at all and is retained with an empty value by design, so it is skipped rather than
        // returned: reporting it would print a blank reference and hide the one a later source states.
        for (var i = 0; i < component.CandidateCount; i++)
        {
            if (component.GetCandidate(i).Evidence.DeclaredReference is { Value.IsEmpty: false } declared)
            {
                return declared.Value.ToString();
            }
        }

        var recognized = reason.SequenceEqual("license_not_recognized"u8);
        if (!recognized && !reason.SequenceEqual("unsupported_source_repository"u8))
        {
            return string.Empty;
        }

        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            if (recognized)
            {
                if ((candidate.Warnings & LicenseCandidateWarnings.SourceLicenseNotRecognized) != 0
                    && candidate.Evidence.SourceRepository is { LicenseUrl.Length: > 0 } evidence)
                {
                    return evidence.LicenseUrl;
                }
            }
            else if ((candidate.Warnings & LicenseCandidateWarnings.UnsupportedSourceRepository) != 0 && !candidate.Raw.IsEmpty)
            {
                return candidate.Raw.ToString();
            }
        }

        return string.Empty;
    }

    public static string RenderMarkdown(in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, bool verbose, bool emptyInventory = false)
    {
        var builder = new StringBuilder();
        builder.AppendLine(verbose ? "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | SUPPLIED | PURL |" : "| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS | SUPPLIED |");
        builder.AppendLine(verbose ? "|---|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|");
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            builder.Append("| ");
            AppendMarkdownValue(builder, component.Name);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.Version);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.License);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.Ecosystem);
            builder.Append(" | ");
            builder.Append(GetDependencyTypeName(component.DependencyType));
            builder.Append(" | ");
            builder.Append(GetStatusName(component.Status));
            builder.Append(" | ");
            builder.Append(GetSuppliedByName(component.SuppliedBy));
            if (verbose)
            {
                builder.Append(" | ");
                AppendMarkdownValue(builder, component.Purl);
            }

            builder.AppendLine(" |");
        }

        AppendEmptyInventoryMarkdown(builder, emptyInventory);
        AppendUnresolvedMarkdown(builder, inventory, components);
        return builder.ToString();
    }

    /// <summary>Renders the same statement as the text report. See <see cref="EmptyInventoryWarning"/>.</summary>
    private static void AppendEmptyInventoryMarkdown(StringBuilder builder, bool emptyInventory)
    {
        if (!emptyInventory)
        {
            return;
        }

        builder.AppendLine();
        builder.Append("## ").AppendLine(Encoding.UTF8.GetString(EmptyInventoryHeadingUtf8));
        builder.AppendLine();
        builder.AppendLine(Encoding.UTF8.GetString(EmptyInventorySentenceUtf8));
    }

    /// <summary>Renders the same explanation as the text report. See <see cref="WriteUnresolvedText"/>.</summary>
    private static void AppendUnresolvedMarkdown(StringBuilder builder, in DependencyInventory inventory, ReadOnlySpan<ScanComponent> components)
    {
        var first = true;
        var rootPaths = default(DependencyRootPaths);
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            if (IsExplainedElsewhere(component) || !TryGetUnresolvedReason(component, out var reason))
            {
                continue;
            }

            if (first)
            {
                builder.AppendLine();
                builder.AppendLine("## Unresolved components");
                builder.AppendLine();
                builder.AppendLine("| NAME | VERSION | REASON | REFERENCE | PATH |");
                builder.AppendLine("|---|---|---|---|---|");
                rootPaths = DependencyPathResolver.BuildRootPaths(inventory);
                first = false;
            }

            builder.Append("| ");
            AppendMarkdownValue(builder, component.Name);
            builder.Append(" | ");
            AppendMarkdownValue(builder, component.Version);
            builder.Append(" | ");
            AppendAscii(builder, reason);
            builder.Append(" | ");
            AppendMarkdownValue(builder, GetUnresolvedReference(component, reason));
            builder.Append(" | ");
            AppendMarkdownValue(builder, DependencyPathText.Introducer(inventory, rootPaths, component, i));
            builder.AppendLine(" |");
        }
    }

    public static void WriteText(
        IBufferWriter<byte> writer,
        ScanInputDescriptor input,
        ReadOnlySpan<GroupRow> groups,
        string groupBy,
        bool emptyInventory = false)
    {
        WriteInputHeader(writer, input);
        var headerCount = GetGroupFieldCount(groupBy);
        for (var i = 0; i < headerCount; i++)
        {
            if (i != 0)
            {
                WriteUtf8(writer, " "u8);
            }

            WriteUtf8(writer, GetGroupHeaderUtf8(groupBy, i));
        }

        WriteUtf8(writer, " COUNT"u8);
        WriteNewLine(writer);
        for (var i = 0; i < groups.Length; i++)
        {
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                if (valueIndex != 0)
                {
                    WriteUtf8(writer, " "u8);
                }

                WriteDisplay(writer, groups[i].Values[valueIndex]);
            }

            WriteUtf8(writer, " "u8);
            var destination = writer.GetSpan(11);
            if (!Utf8Formatter.TryFormat(groups[i].Count, destination, out var bytesWritten))
            {
                throw new InvalidOperationException("Unable to format group count.");
            }

            writer.Advance(bytesWritten);
            WriteNewLine(writer);
        }

        WriteEmptyInventoryText(writer, emptyInventory);
    }

    public static string RenderMarkdown(ReadOnlySpan<GroupRow> groups, string groupBy, bool emptyInventory = false)
    {
        var headers = groupBy.ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();
        builder.Append("| ");
        builder.AppendJoin(" | ", headers);
        builder.AppendLine(" | COUNT |");
        builder.Append('|');
        for (var i = 0; i < headers.Length + 1; i++)
        {
            builder.Append("---|");
        }

        builder.AppendLine();
        for (var i = 0; i < groups.Length; i++)
        {
            builder.Append("| ");
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                AppendMarkdownValue(builder, groups[i].Values[valueIndex]);
                builder.Append(" | ");
            }

            builder.Append(groups[i].Count);
            builder.AppendLine(" |");
        }

        AppendEmptyInventoryMarkdown(builder, emptyInventory);
        return builder.ToString();
    }

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, SpdxData spdx, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
        => WriteJson(writer, inventory, components, default, spdx, metadataSummary, sourceSummary, scope);

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, ReadOnlySpan<ScanComponent> components, ReadOnlySpan<DependencyUsage> componentUsages, SpdxData spdx, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", JsonSchemaVersion);
        writer.WriteStartObject("metadata");
        WriteToolMetadata(writer);
        WriteInputMetadata(writer, inventory.Input);
        WriteSpdxMetadata(writer, spdx);
        WritePackageMetadata(writer, metadataSummary);
        WriteSourceRepositoryMetadata(writer, sourceSummary);
        WriteScopeMetadata(writer, scope);
        writer.WriteEndObject();

        WriteInventory(writer, inventory);

        writer.WriteStartArray("components");
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            writer.WriteStartObject();
            writer.WriteString("name"u8, component.Name.Span);
            writer.WriteString("version"u8, component.Version.Span);
            writer.WriteString("license"u8, component.License.IsEmpty ? "-"u8 : component.License.Span);
            writer.WriteString("ecosystem", component.Ecosystem);
            writer.WriteString("dependency"u8, GetDependencyTypeUtf8(component.DependencyType));
            writer.WriteString("status"u8, component.Status.ToUtf8());
            writer.WriteString("purl"u8, component.Purl.Span);
            writer.WriteString("sourceId"u8, component.SourceId.Span);
            WriteSuppliedBy(writer, component.SuppliedBy);
            if (i < componentUsages.Length && componentUsages[i] != DependencyUsage.Unknown)
            {
                writer.WriteString("usage"u8, componentUsages[i] == DependencyUsage.Development ? "development"u8 : "runtime"u8);
            }

            WriteLicenseCandidates(writer, component);
            WriteWarnings(writer, component.Warnings);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        WriteSummary(writer, ScanSummary.Create(components));
        WriteWarnings(writer, components, inventory.Components.Length == 0);
        writer.WriteEndObject();
    }

    public static void WriteJson(Utf8JsonWriter writer, DependencyInventory inventory, GroupRow[] groups, string groupBy, SpdxData spdx, PackageMetadataSummary metadataSummary, SourceRepositorySummary sourceSummary, ScanReportScope scope)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", JsonSchemaVersion);
        writer.WriteStartObject("metadata");
        WriteToolMetadata(writer);
        WriteInputMetadata(writer, inventory.Input);
        WriteSpdxMetadata(writer, spdx);
        WritePackageMetadata(writer, metadataSummary);
        WriteSourceRepositoryMetadata(writer, sourceSummary);
        WriteScopeMetadata(writer, scope);
        writer.WriteEndObject();

        WriteInventory(writer, inventory);

        writer.WriteStartArray("groups");
        for (var i = 0; i < groups.Length; i++)
        {
            writer.WriteStartObject();
            for (var valueIndex = 0; valueIndex < groups[i].Values.Length; valueIndex++)
            {
                writer.WriteString(GetGroupPropertyNameUtf8(groupBy, valueIndex), groups[i].Values[valueIndex]);
            }

            writer.WriteNumber("count", groups[i].Count);
            writer.WriteStartArray("components");
            for (var componentIndex = 0; componentIndex < groups[i].Components.Length; componentIndex++)
            {
                var component = groups[i].Components[componentIndex];
                writer.WriteStartObject();
                writer.WriteString("name"u8, component.Name.Span);
                writer.WriteString("version"u8, component.Version.Span);
                writer.WriteString("ecosystem", component.Ecosystem);
                writer.WriteString("purl"u8, component.Purl.Span);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteSummary(writer, ScanSummary.Create(groups));
        WriteWarnings(writer, groups, inventory.Components.Length == 0);
        writer.WriteEndObject();
    }

    private static void WriteToolMetadata(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("tool");
        writer.WriteString("name", ToolName);
        writer.WriteString("version", ToolVersion);
        writer.WriteString("informationUri", ToolInformationUri);
        writer.WriteEndObject();
    }

    private static void WriteInputHeader(IBufferWriter<byte> writer, ScanInputDescriptor input)
    {
        WriteUtf8(writer, "Input: "u8);
        WriteUtf8(writer, input.Kind.Name);
        WriteUtf8(writer, "/"u8);
        WriteUtf8(writer, input.Format.Name);
        WriteNewLine(writer);
        WriteNewLine(writer);
    }

    /// <summary>Appends an ASCII identifier without decoding it into a separate string first.</summary>
    private static void AppendAscii(StringBuilder builder, ReadOnlySpan<byte> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            builder.Append((char)value[i]);
        }
    }

    private static void WriteDisplay(IBufferWriter<byte> writer, string value)
    {
        if (value.Length == 0)
        {
            WriteUtf8(writer, "-"u8);
        }
        else
        {
            WriteUtf8(writer, value);
        }
    }

    private static void WriteDisplay(IBufferWriter<byte> writer, Utf8Slice value)
    {
        WriteUtf8(writer, value.IsEmpty ? "-"u8 : value.Span);
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

    private static int GetGroupFieldCount(string groupBy)
    {
        var value = groupBy.AsSpan();
        var count = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != ',')
            {
                continue;
            }

            if (!TrimAsciiWhitespace(value[start..i]).IsEmpty)
            {
                count++;
            }

            start = i + 1;
        }

        return count;
    }

    private static ReadOnlySpan<byte> GetGroupHeaderUtf8(string groupBy, int targetIndex)
    {
        var propertyName = GetGroupPropertyNameUtf8(groupBy, targetIndex);
        if (propertyName.SequenceEqual("name"u8)) return "NAME"u8;
        if (propertyName.SequenceEqual("version"u8)) return "VERSION"u8;
        if (propertyName.SequenceEqual("license"u8)) return "LICENSE"u8;
        if (propertyName.SequenceEqual("ecosystem"u8)) return "ECOSYSTEM"u8;
        if (propertyName.SequenceEqual("dependency"u8)) return "DEPENDENCY"u8;
        if (propertyName.SequenceEqual("status"u8)) return "STATUS"u8;
        throw new ArgumentOutOfRangeException(nameof(targetIndex));
    }

    private static void AppendMarkdownValue(StringBuilder builder, string value)
    {
        builder.Append(Display(value).Replace("|", "\\|", StringComparison.Ordinal));
    }

    private static void AppendMarkdownValue(StringBuilder builder, Utf8Slice value)
    {
        AppendMarkdownValue(builder, value.ToString());
    }

    private static string Display(string value) => value.Length == 0 ? "-" : value;

    private static string Display(Utf8Slice value) => value.IsEmpty ? "-" : value.ToString();

    private static ReadOnlySpan<byte> GetDependencyTypeUtf8(DependencyType value) => value switch
    {
        DependencyType.Unknown => "unknown"u8,
        DependencyType.Root => "root"u8,
        DependencyType.Direct => "direct"u8,
        DependencyType.Transitive => "transitive"u8,
        _ => default,
    };

    /// <summary>The same tokens as the UTF-8 forms, for the views that build a <see cref="string"/>.</summary>
    /// <remarks>
    /// Constants rather than <c>ToString().ToLowerInvariant()</c>, which allocated two strings per value
    /// per component in the Markdown table and in every grouped row, none of which outlived the call.
    /// </remarks>
    internal static string GetDependencyTypeName(DependencyType value) => value switch
    {
        DependencyType.Unknown => "unknown",
        DependencyType.Root => "root",
        DependencyType.Direct => "direct",
        DependencyType.Transitive => "transitive",
        _ => string.Empty,
    };

    /// <summary>See <see cref="GetDependencyTypeName"/>.</summary>
    internal static string GetStatusName(LicenseStatus value) => value switch
    {
        LicenseStatus.Matched => "matched",
        LicenseStatus.Conflict => "conflict",
        LicenseStatus.Unknown => "unknown",
        LicenseStatus.Ambiguous => "ambiguous",
        LicenseStatus.Invalid => "invalid",
        LicenseStatus.Error => "error",
        _ => string.Empty,
    };

    /// <summary>See <see cref="GetDependencyTypeName"/>.</summary>
    private static string GetSuppliedByName(ComponentSupply value) => value switch
    {
        ComponentSupply.Sbom => "sbom",
        ComponentSupply.PackageManager => "package-manager",
        ComponentSupply.Sbom | ComponentSupply.PackageManager => "sbom,package-manager",
        _ => "-",
    };

    // Human output keeps the same tokens the canonical JSON uses so one vocabulary describes both views.
    private static ReadOnlySpan<byte> GetSuppliedByUtf8(ComponentSupply value) => value switch
    {
        ComponentSupply.Sbom => "sbom"u8,
        ComponentSupply.PackageManager => "package-manager"u8,
        ComponentSupply.Sbom | ComponentSupply.PackageManager => "sbom,package-manager"u8,
        _ => "-"u8,
    };

    private static ReadOnlySpan<byte> GetGroupPropertyNameUtf8(string groupBy, int targetIndex)
    {
        var value = groupBy.AsSpan();
        var fieldIndex = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] != ',')
            {
                continue;
            }

            var field = TrimAsciiWhitespace(value[start..i]);
            start = i + 1;
            if (field.IsEmpty)
            {
                continue;
            }

            if (fieldIndex++ != targetIndex)
            {
                continue;
            }

            if (field.Equals("name", StringComparison.OrdinalIgnoreCase)) return "name"u8;
            if (field.Equals("version", StringComparison.OrdinalIgnoreCase)) return "version"u8;
            if (field.Equals("license", StringComparison.OrdinalIgnoreCase)) return "license"u8;
            if (field.Equals("ecosystem", StringComparison.OrdinalIgnoreCase)) return "ecosystem"u8;
            if (field.Equals("dependency", StringComparison.OrdinalIgnoreCase)) return "dependency"u8;
            if (field.Equals("status", StringComparison.OrdinalIgnoreCase)) return "status"u8;
            break;
        }

        throw new ArgumentOutOfRangeException(nameof(targetIndex));
    }

    private static ReadOnlySpan<char> TrimAsciiWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is ' ' or '\t' or '\r' or '\n') start++;
        var end = value.Length;
        while (end > start && value[end - 1] is ' ' or '\t' or '\r' or '\n') end--;
        return value[start..end];
    }

    private static void WriteSpdxMetadata(Utf8JsonWriter writer, SpdxData spdx)
    {
        writer.WriteStartObject("spdx");
        writer.WriteString("source", spdx.Source);
        writer.WriteString("licenseListVersion", spdx.LicenseListVersion);
        writer.WriteString("dataRef", spdx.DataRef);
        writer.WriteString("licensesSha256", spdx.GetLicensesSha256());
        writer.WriteString("exceptionsSha256", spdx.GetExceptionsSha256());
        writer.WriteEndObject();
    }

    private static void WritePackageMetadata(Utf8JsonWriter writer, PackageMetadataSummary summary)
    {
        writer.WriteStartObject("packageMetadata");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("supportedComponentCount", summary.SupportedComponentCount);
        writer.WriteNumber("cacheHitCount", summary.CacheHitCount);
        writer.WriteNumber("cacheMissCount", summary.CacheMissCount);
        writer.WriteNumber("refreshedCount", summary.RefreshedCount);
        writer.WriteNumber("fetchErrorCount", summary.FetchErrorCount);
        writer.WriteNumber("unsupportedEcosystemCount", summary.UnsupportedEcosystemCount);
        writer.WriteNumber("unversionedPurlCount", summary.UnversionedPurlCount);
        writer.WriteNumber("concurrency", summary.Concurrency);
        writer.WriteNumber("retryCount", summary.RetryCount);
        writer.WriteEndObject();
    }

    private static void WriteSourceRepositoryMetadata(Utf8JsonWriter writer, SourceRepositorySummary summary)
    {
        writer.WriteStartObject("sourceRepository");
        writer.WriteNumber("targetCount", summary.TargetCount);
        writer.WriteNumber("githubLicenseRequestCount", summary.GitHubRequestCount);
        writer.WriteNumber("cacheHitCount", summary.CacheHitCount);
        writer.WriteNumber("cacheMissCount", summary.CacheMissCount);
        writer.WriteNumber("fetchErrorCount", summary.FetchErrorCount);
        writer.WriteNumber("unknownCount", summary.UnknownCount);
        writer.WriteEndObject();
        writer.WriteStartObject("network");
        writer.WriteString("githubAuth", summary.AuthMode);
        writer.WriteEndObject();
    }

    // Stated rather than implied, because the counters above are zero both when collection was disabled and
    // when it was enabled with nothing to do, and because a filtered view is otherwise indistinguishable
    // from a complete report once the excluded components are gone.
    private static void WriteScopeMetadata(Utf8JsonWriter writer, ScanReportScope scope)
    {
        writer.WriteStartObject("collection");
        writer.WriteString("externalEvidence", scope.ExternalEvidenceCollected ? "collected" : "not-collected");
        writer.WriteEndObject();
        writer.WriteStartObject("view");
        if (scope.DependencyFilter is { } dependencyFilter) writer.WriteString("dependencyFilter", dependencyFilter);
        else writer.WriteNull("dependencyFilter");
        writer.WriteNumber("excludedCount", scope.ExcludedCount);
        writer.WriteNumber("excludedUnknownCount", scope.ExcludedUnknownCount);
        writer.WriteEndObject();
    }

    // Which inputs supplied a component is a list rather than a single token so that a collection needs no combined
    // vocabulary such as "both", and so a reader parses the same shape whatever the input was.
    private static void WriteSuppliedBy(Utf8JsonWriter writer, ComponentSupply supply)
    {
        writer.WriteStartArray("suppliedBy"u8);
        if ((supply & ComponentSupply.Sbom) != 0) writer.WriteStringValue("sbom"u8);
        if ((supply & ComponentSupply.PackageManager) != 0) writer.WriteStringValue("package-manager"u8);
        writer.WriteEndArray();
    }

    private static void WriteLicenseCandidates(Utf8JsonWriter writer, ScanComponent component)
    {
        writer.WriteStartArray("licenseCandidates");
        for (var i = 0; i < component.CandidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            writer.WriteStartObject();
            writer.WriteString("source"u8, candidate.Source.ToUtf8());
            writer.WriteString("kind"u8, candidate.Kind.ToUtf8());
            writer.WriteString("raw"u8, candidate.Raw.Span);
            writer.WriteString("normalized"u8, candidate.Normalized.Span);
            writer.WriteString("status"u8, candidate.Status.ToUtf8());
            writer.WriteBoolean("deprecated", candidate.Deprecated);
            WriteCandidateWarnings(writer, candidate.Warnings);
            WriteLicenseEvidence(writer, candidate.Evidence);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteLicenseEvidence(Utf8JsonWriter writer, LicenseEvidence evidence)
    {
        if (evidence.Kind == LicenseEvidenceKind.None)
        {
            return;
        }

        writer.WriteStartObject("evidence");
        // The location a publisher declared is provenance for every source that can state one, so it is
        // written once here rather than inside each source's own shape.
        if (evidence.DeclaredReference is { } declaredReference)
        {
            writer.WriteString("declaredLicenseReferenceKind", declaredReference.Kind switch
            {
                DeclaredLicenseReferenceKind.Location => "location",
                DeclaredLicenseReferenceKind.InlineText => "inline-text",
                _ => "artifact-path",
            });
            writer.WriteString("declaredLicenseReference", declaredReference.Value.Span);
        }

        switch (evidence.Kind)
        {
            case LicenseEvidenceKind.Sbom:
                writer.WriteString("type", "sbom");
                var field = evidence.SbomField switch
                {
                    SbomLicenseField.CycloneDxLicenses => "licenses",
                    SbomLicenseField.CycloneDxEvidenceLicenses => "evidence.licenses",
                    SbomLicenseField.SpdxLicenseDeclared => "licenseDeclared",
                    SbomLicenseField.SpdxLicenseConcluded => "licenseConcluded",
                    _ => null,
                };
                if (field is not null)
                {
                    writer.WriteString("field", field);
                }

                if (evidence.Acknowledgement != LicenseAcknowledgement.None)
                {
                    writer.WriteString("acknowledgement", evidence.Acknowledgement == LicenseAcknowledgement.Declared ? "declared" : "concluded");
                }

                break;
            case LicenseEvidenceKind.DependencyInput:
                writer.WriteString("type", "dependency-input");
                if (evidence.DependencyInput is { } input)
                {
                    writer.WriteString("format", input.Format);
                    writer.WriteString("field", input.Field);
                }

                break;
            case LicenseEvidenceKind.PackageRegistry:
                writer.WriteString("type", "package-registry");
                if (evidence.PackageRegistry?.CacheKeySha256 is { Length: > 0 } cacheKeySha256)
                {
                    writer.WriteString("cacheKeySha256", cacheKeySha256);
                }

                if (evidence.PackageRegistry is { } packageDetails && packageDetails.CollectedAt != default)
                {
                    writer.WriteString("collectedAt", packageDetails.CollectedAt);
                }

                break;
            case LicenseEvidenceKind.SourceRepository:
                writer.WriteString("type", "source-repository");
                if (evidence.SourceRepository is { } sourceRepository)
                {
                    WriteSourceRepositoryEvidence(writer, sourceRepository);
                }

                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteSourceRepositoryEvidence(Utf8JsonWriter writer, SourceRepositoryEvidence value)
    {
        writer.WriteString("repository", value.Repository);
        writer.WriteString("ref", value.Ref);
        if (value.HttpStatus is { } status) writer.WriteNumber("httpStatus", status);
        else writer.WriteNull("httpStatus");
        writer.WriteString("cacheKeySha256", value.CacheKeySha256);
        writer.WriteString("licensePath", value.LicensePath);
        writer.WriteString("licenseSha", value.LicenseSha);
        writer.WriteString("licenseKey", value.LicenseKey);
        writer.WriteString("licenseName", value.LicenseName);
        writer.WriteString("licenseUrl", value.LicenseUrl);
    }

    private static void WriteWarnings(Utf8JsonWriter writer, ReadOnlySpan<string> warnings)
    {
        writer.WriteStartArray("warnings");
        for (var i = 0; i < warnings.Length; i++)
        {
            writer.WriteStringValue(warnings[i]);
        }

        writer.WriteEndArray();
    }

    private static void WriteCandidateWarnings(Utf8JsonWriter writer, LicenseCandidateWarnings warnings)
    {
        writer.WriteStartArray("warnings");
        if ((warnings & LicenseCandidateWarnings.DeprecatedSpdxIdentifier) != 0) writer.WriteStringValue("deprecated_spdx_identifier"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataFetchFailed) != 0) writer.WriteStringValue("package_metadata_fetch_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryCacheInvalid) != 0) writer.WriteStringValue("source_repository_cache_invalid"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryCacheWriteFailed) != 0) writer.WriteStringValue("source_repository_cache_write_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryFetchFailed) != 0) writer.WriteStringValue("source_repository_fetch_failed"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryUnavailable) != 0) writer.WriteStringValue("source_repository_unavailable"u8);
        if ((warnings & LicenseCandidateWarnings.UnsupportedPackageMetadata) != 0) writer.WriteStringValue("unsupported_package_metadata"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataUnversionedPurl) != 0) writer.WriteStringValue("package_metadata_unversioned_purl"u8);
        if ((warnings & LicenseCandidateWarnings.UnsupportedSourceRepository) != 0) writer.WriteStringValue("unsupported_source_repository"u8);
        if ((warnings & LicenseCandidateWarnings.ExternalEvidenceNotCollected) != 0) writer.WriteStringValue("external_evidence_not_collected"u8);
        if ((warnings & LicenseCandidateWarnings.PackageMetadataNotFound) != 0) writer.WriteStringValue("package_metadata_not_found"u8);
        if ((warnings & LicenseCandidateWarnings.SourceLicenseNotDetected) != 0) writer.WriteStringValue("license_not_detected"u8);
        if ((warnings & LicenseCandidateWarnings.SourceLicenseNotRecognized) != 0) writer.WriteStringValue("license_not_recognized"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositorySubdirectory) != 0) writer.WriteStringValue("source_repository_subdirectory"u8);
        if ((warnings & LicenseCandidateWarnings.SourceRepositoryRefNotFound) != 0) writer.WriteStringValue("source_repository_ref_not_found"u8);
        writer.WriteEndArray();
    }

    private static void WriteSummary(Utf8JsonWriter writer, ScanSummary summary)
    {
        writer.WriteStartObject("summary");
        writer.WriteNumber("matched", summary.Matched);
        writer.WriteNumber("conflict", summary.Conflict);
        writer.WriteNumber("unknown", summary.Unknown);
        writer.WriteNumber("ambiguous", summary.Ambiguous);
        writer.WriteNumber("invalid", summary.Invalid);
        writer.WriteNumber("error", summary.Error);
        writer.WriteEndObject();
    }

    private static void WriteWarnings(Utf8JsonWriter writer, ReadOnlySpan<ScanComponent> components, bool emptyInventory)
    {
        writer.WriteStartArray("warnings");
        if (HasDeprecatedWarning(components))
        {
            writer.WriteStringValue("deprecated_spdx_identifier");
        }

        if (emptyInventory)
        {
            writer.WriteStringValue(EmptyInventoryWarning);
        }

        writer.WriteEndArray();
    }

    private static void WriteWarnings(Utf8JsonWriter writer, ReadOnlySpan<GroupRow> groups, bool emptyInventory)
    {
        writer.WriteStartArray("warnings");
        for (var i = 0; i < groups.Length; i++)
        {
            if (HasDeprecatedWarning(groups[i].Components))
            {
                writer.WriteStringValue("deprecated_spdx_identifier");
                break;
            }
        }

        if (emptyInventory)
        {
            writer.WriteStringValue(EmptyInventoryWarning);
        }

        writer.WriteEndArray();
    }

    private static bool HasDeprecatedWarning(ReadOnlySpan<ScanComponent> components)
    {
        for (var i = 0; i < components.Length; i++)
        {
            if (Array.IndexOf(components[i].Warnings, "deprecated_spdx_identifier") >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteInputMetadata(Utf8JsonWriter writer, ScanInputDescriptor input)
    {
        writer.WriteStartObject("input");
        writer.WriteString("kind", input.Kind.Name);
        writer.WriteString("format", input.Format.Name);
        writer.WriteString("sourceRef", input.SourceReference);
        writer.WriteString("sourceSha256", input.SourceSha256);
        writer.WriteString("parser", input.Format.Parser);
        writer.WriteString("specificationVersion"u8, input.SpecificationVersion.Span);
        if (input.Kind == ScanInputKind.Sbom)
        {
            writer.WriteString("sbomRef", input.SourceReference);
            writer.WriteString("sbomFormat", input.Format.DisplayName);
            writer.WriteString("sbomSpecVersion"u8, input.SpecificationVersion.Span);
            writer.WriteString("sbomSha256", input.SourceSha256);
        }

        writer.WriteEndObject();
    }

    private static void WriteInventory(Utf8JsonWriter writer, DependencyInventory inventory)
    {
        writer.WriteStartObject("inventory");
        writer.WriteStartArray("contexts");
        for (var i = 0; i < inventory.Contexts.Length; i++)
        {
            var context = inventory.Contexts[i];
            writer.WriteStartObject();
            WriteLogicalPath(writer, "projectOrigin"u8, context.ProjectOrigin);
            writer.WriteString("target"u8, context.Target.Span);
            writer.WriteString("runtime"u8, context.Runtime.Span);
            writer.WriteString("platform"u8, context.Platform.Span);
            writer.WriteString("architecture"u8, context.Architecture.Span);
            writer.WriteString("variant"u8, context.Variant.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("components");
        for (var i = 0; i < inventory.Components.Length; i++)
        {
            var component = inventory.Components[i];
            writer.WriteStartObject();
            writer.WriteString("name"u8, component.Name.Span);
            writer.WriteString("version"u8, component.Version.Span);
            writer.WriteString("ecosystem", component.Ecosystem);
            writer.WriteString("dependency"u8, GetDependencyTypeUtf8(component.DependencyType));
            writer.WriteString("purl"u8, component.Purl.Span);
            writer.WriteString("sourceId"u8, component.SourceId.Span);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("occurrences");
        var occurrenceVariants = inventory.OccurrenceVariants;
        var occurrenceVariantIndex = 0;
        for (var i = 0; i < inventory.Occurrences.Length; i++)
        {
            var occurrence = inventory.Occurrences[i];
            writer.WriteStartObject();
            writer.WriteNumber("contextIndex", occurrence.ContextIndex);
            writer.WriteNumber("componentIndex", occurrence.ComponentIndex);
            if (occurrenceVariants is not null
                && occurrenceVariantIndex < occurrenceVariants.Length
                && occurrenceVariants[occurrenceVariantIndex].OccurrenceIndex == i)
            {
                writer.WriteString("variant"u8, occurrenceVariants[occurrenceVariantIndex].Value.Span);
                occurrenceVariantIndex++;
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("edges");
        for (var i = 0; i < inventory.Edges.Length; i++)
        {
            var edge = inventory.Edges[i];
            writer.WriteStartObject();
            writer.WriteNumber("contextIndex", edge.ContextIndex);
            writer.WriteNumber("fromOccurrenceIndex", edge.FromOccurrenceIndex);
            writer.WriteNumber("toOccurrenceIndex", edge.ToOccurrenceIndex);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteLogicalPath(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, Utf8Slice value)
    {
        var path = value.Span;
        var isAbsolute = path.Length > 0 && path[0] is (byte)'/' or (byte)'\\'
            || path.Length >= 3
                && (path[0] is >= (byte)'A' and <= (byte)'Z' || path[0] is >= (byte)'a' and <= (byte)'z')
                && path[1] == (byte)':'
                && path[2] is (byte)'/' or (byte)'\\';
        if (isAbsolute)
        {
            var separator = path.LastIndexOfAny((byte)'/', (byte)'\\');
            path = separator < 0 ? path : path[(separator + 1)..];
        }

        writer.WriteString(propertyName, path);
    }
}

/// <param name="UnresolvedWarningCount">Warnings on components the scan did not resolve to one license.</param>
/// <param name="ResolvedWarningCount">Warnings on components that resolved despite them.</param>
/// <remarks>
/// The two counts are reported separately rather than summed. Collecting evidence Ol could not read is
/// routine — a repository outside GitHub, a registry with no license field — and when the component
/// resolved from other evidence anyway, that warning changed no outcome. One total makes a fully resolved
/// report announce findings a reader then has to open the JSON to dismiss.
/// </remarks>
internal readonly record struct ScanSummary(int Matched, int Conflict, int Unknown, int Ambiguous, int Invalid, int Error, int UnresolvedWarningCount, int ResolvedWarningCount, int DeprecatedSpdxCount)
{
    public static ScanSummary Create(ReadOnlySpan<GroupRow> groups)
    {
        var total = default(ScanSummary);
        for (var i = 0; i < groups.Length; i++)
        {
            var summary = Create(groups[i].Components);
            total = new ScanSummary(
                total.Matched + summary.Matched,
                total.Conflict + summary.Conflict,
                total.Unknown + summary.Unknown,
                total.Ambiguous + summary.Ambiguous,
                total.Invalid + summary.Invalid,
                total.Error + summary.Error,
                total.UnresolvedWarningCount + summary.UnresolvedWarningCount,
                total.ResolvedWarningCount + summary.ResolvedWarningCount,
                total.DeprecatedSpdxCount + summary.DeprecatedSpdxCount);
        }

        return total;
    }

    public static ScanSummary Create(ReadOnlySpan<ScanComponent> components)
    {
        var matched = 0;
        var conflict = 0;
        var unknown = 0;
        var ambiguous = 0;
        var invalid = 0;
        var error = 0;
        var unresolvedWarningCount = 0;
        var resolvedWarningCount = 0;
        var deprecatedSpdxCount = 0;

        for (var i = 0; i < components.Length; i++)
        {
            switch (components[i].Status)
            {
                case LicenseStatus.Matched:
                    matched++;
                    break;
                case LicenseStatus.Conflict:
                    conflict++;
                    break;
                case LicenseStatus.Unknown:
                    unknown++;
                    break;
                case LicenseStatus.Ambiguous:
                    ambiguous++;
                    break;
                case LicenseStatus.Invalid:
                    invalid++;
                    break;
                case LicenseStatus.Error:
                    error++;
                    break;
            }

            // A component that reached one license resolved whatever else failed on the way, so its
            // warnings describe collection rather than the result.
            if (components[i].Status == LicenseStatus.Matched) resolvedWarningCount += components[i].Warnings.Length;
            else unresolvedWarningCount += components[i].Warnings.Length;

            for (var candidateIndex = 0; candidateIndex < components[i].CandidateCount; candidateIndex++)
            {
                if (components[i].GetCandidate(candidateIndex).Deprecated)
                {
                    deprecatedSpdxCount++;
                }
            }
        }

        return new ScanSummary(matched, conflict, unknown, ambiguous, invalid, error, unresolvedWarningCount, resolvedWarningCount, deprecatedSpdxCount);
    }
}
