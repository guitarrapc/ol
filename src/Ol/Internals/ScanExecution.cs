using System.Text.Json;
using Ol.Core;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;

namespace Ol.Internals;

internal readonly record struct ScanPreparation(
    ScanCommands.ScanInputSelection Input,
    SpdxData Spdx,
    CacheDirectories CacheDirectories,
    int Concurrency,
    int Retry,
    PurlPrefixSet? UncollectedPackages = null);

internal readonly record struct CompletedScanExecution(
    ScanResult Result,
    PackageMetadataSummary PackageMetadataSummary,
    SourceRepositorySummary SourceRepositorySummary);

internal static class ScanExecution
{
    public static bool TryPrepare(
        string[]? input,
        string? inputFormat,
        string? spdxData,
        string? cacheDir,
        bool noExternalEvidence,
        int concurrency,
        int retry,
        out ScanPreparation preparation,
        out string error)
        => TryPrepare(input, inputFormat, spdxData, cacheDir, noExternalEvidence, null, concurrency, retry, out preparation, out error);

    public static bool TryPrepare(
        string[]? input,
        string? inputFormat,
        string? spdxData,
        string? cacheDir,
        bool noExternalEvidence,
        string[]? skipEvidencePackages,
        int concurrency,
        int retry,
        out ScanPreparation preparation,
        out string error)
    {
        if (!ScanCommands.TryResolveInput(input, inputFormat, out var inputSelection, out var inputError))
        {
            preparation = default;
            error = $"Invalid scan input: {inputError}";
            return false;
        }

        for (var i = 0; i < inputSelection.Paths.Length; i++)
        {
            var inputPath = inputSelection.Paths[i];
            if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
            {
                preparation = default;
                error = $"Input file or directory not found: {inputPath}";
                return false;
            }
        }

        concurrency = concurrency == 0 ? Math.Max(4, Math.Min(Environment.ProcessorCount, 8)) : concurrency;
        if (concurrency < 1)
        {
            preparation = default;
            error = "Concurrency must be at least 1.";
            return false;
        }

        if (retry < 0)
        {
            preparation = default;
            error = "Retry must not be negative.";
            return false;
        }

        var cacheDirectories = default(CacheDirectories);
        if (!noExternalEvidence)
        {
            try
            {
                cacheDirectories = CachePaths.Resolve(cacheDir);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                preparation = default;
                error = $"Invalid cache directory: {exception.Message}";
                return false;
            }
        }

        if (!PurlPrefixSet.TryCreate(skipEvidencePackages ?? [], out var uncollectedPackages, out var uncollectedError))
        {
            preparation = default;
            error = $"Invalid uncollected package selection: {uncollectedError}";
            return false;
        }

        if (!TryResolveSpdx(spdxData, out var spdx, out error))
        {
            preparation = default;
            return false;
        }

        preparation = new ScanPreparation(inputSelection, spdx, cacheDirectories, concurrency, retry, uncollectedPackages);
        error = string.Empty;
        return true;
    }

    /// <summary>Resolves active SPDX data without preparing an evidence pipeline.</summary>
    /// <remarks>Evaluating a persisted report still needs SPDX data to normalize the allow-list, but must not touch inputs or caches.</remarks>
    public static bool TryResolveSpdx(string? spdxData, out SpdxData spdx, out string error)
    {
        try
        {
            spdx = SpdxData.Load(spdxData);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException)
        {
            spdx = default;
            error = $"Unable to load SPDX data: {exception.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryExecute(
        in ScanPreparation preparation,
        bool refresh,
        bool noExternalEvidence,
        bool includeHash,
        out CompletedScanExecution completed,
        out string error)
    {
        ScanResult scanResult;
        try
        {
            var inventory = ScanCommands.ScanInputs(preparation.Input, preparation.Spdx.Index, includeHash);
            scanResult = ScanResult.FromInventory(inventory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            completed = default;
            error = $"Unable to scan input: {exception.Message}";
            return false;
        }

        try
        {
            var enrichedComponents = scanResult.Components;
            PackageMetadataSummary packageMetadataSummary;
            SourceRepositorySummary sourceRepositorySummary;
            if (noExternalEvidence)
            {
                packageMetadataSummary = new PackageMetadataSummary(0, 0, 0, 0, 0, 0, preparation.Concurrency, preparation.Retry);
                sourceRepositorySummary = new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", preparation.Concurrency, preparation.Retry);
            }
            else
            {
                using var workspace = new PackageMetadataWorkspace(enrichedComponents.Length);
                var metadataService = new PackageMetadataService(preparation.Spdx.Index, new PackageMetadataCache(preparation.CacheDirectories.PackageMetadata), refresh, preparation.Retry, preparation.UncollectedPackages);
                var enrichment = metadataService.EnrichAsync(enrichedComponents, workspace, preparation.Concurrency).GetAwaiter().GetResult();
                enrichedComponents = enrichment.Components;
                packageMetadataSummary = enrichment.Summary;
                var sourceService = new SourceRepositoryService(preparation.Spdx.Index, new SourceRepositoryCache(preparation.CacheDirectories.SourceRepository), refresh, preparation.Retry, client: null, preparation.UncollectedPackages);
                var sourceEnrichment = sourceService.EnrichAsync(enrichedComponents, workspace, preparation.Concurrency).GetAwaiter().GetResult();
                enrichedComponents = sourceEnrichment.Components;
                sourceRepositorySummary = sourceEnrichment.Summary;
            }

            completed = new CompletedScanExecution(scanResult with { Components = enrichedComponents }, packageMetadataSummary, sourceRepositorySummary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            completed = default;
            error = $"Unable to collect license evidence: {exception.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
