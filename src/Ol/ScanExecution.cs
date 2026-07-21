using System.Text.Json;
using Ol.Core;
using Ol.Core.PackageMetadata;
using Ol.Core.SourceRepository;

internal readonly record struct ScanPreparation(
    ScanCommands.ScanInputSelection Input,
    SpdxData Spdx,
    CacheDirectories CacheDirectories,
    int Concurrency,
    int Retry);

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
        bool skipEnrichment,
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
        if (!skipEnrichment)
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

        SpdxData spdx;
        try
        {
            spdx = SpdxData.Load(spdxData);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or KeyNotFoundException)
        {
            preparation = default;
            error = $"Unable to load SPDX data: {exception.Message}";
            return false;
        }

        preparation = new ScanPreparation(inputSelection, spdx, cacheDirectories, concurrency, retry);
        error = string.Empty;
        return true;
    }

    public static bool TryExecute(
        in ScanPreparation preparation,
        bool refresh,
        bool skipEnrichment,
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

        var enrichedComponents = scanResult.Components;
        PackageMetadataSummary packageMetadataSummary;
        SourceRepositorySummary sourceRepositorySummary;
        if (skipEnrichment)
        {
            packageMetadataSummary = new PackageMetadataSummary(0, 0, 0, 0, 0, 0, preparation.Concurrency, preparation.Retry);
            sourceRepositorySummary = new SourceRepositorySummary(0, 0, 0, 0, 0, 0, "none", preparation.Concurrency, preparation.Retry);
        }
        else
        {
            var metadataService = new PackageMetadataService(preparation.Spdx.Index, new PackageMetadataCache(preparation.CacheDirectories.PackageMetadata), refresh, preparation.Retry);
            var enrichment = metadataService.EnrichAsync(enrichedComponents, preparation.Concurrency).GetAwaiter().GetResult();
            enrichedComponents = enrichment.Components;
            packageMetadataSummary = enrichment.Summary;
            var sourceService = new SourceRepositoryService(preparation.Spdx.Index, new PackageMetadataCache(preparation.CacheDirectories.PackageMetadata), new SourceRepositoryCache(preparation.CacheDirectories.SourceRepository), refresh, preparation.Retry);
            var sourceEnrichment = sourceService.EnrichAsync(enrichedComponents, preparation.Concurrency).GetAwaiter().GetResult();
            enrichedComponents = sourceEnrichment.Components;
            sourceRepositorySummary = sourceEnrichment.Summary;
        }

        completed = new CompletedScanExecution(scanResult with { Components = enrichedComponents }, packageMetadataSummary, sourceRepositorySummary);
        error = string.Empty;
        return true;
    }
}
