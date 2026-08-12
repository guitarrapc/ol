using System.Security.Cryptography;
using System.Text;
using Ol.Core;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;

namespace Ol.Internals;

/// <summary>Identifies the resolved dependency inputs one scan reads, and the format the user named.</summary>
/// <param name="Paths">The input files or directories, in the order the user named them.</param>
/// <param name="ExpectedHandler">The handler for an explicitly named format, or default for automatic detection.</param>
internal readonly record struct ScanInputSelection(string[] Paths, DependencyInputHandler ExpectedHandler)
{
    public bool HasExpectedFormat => !string.IsNullOrEmpty(ExpectedHandler.Format.Name);
}

/// <summary>Identifies one physical dependency input that has a restored-artifact collector.</summary>
internal readonly record struct ResolvedPackageArtifactInput(string Path, PackageArtifactCollector Collector);

/// <summary>Contains the combined inventory and the bounded physical inputs needed by local artifact collection.</summary>
internal readonly record struct ScanInputIngestionResult(
    DependencyInventory Inventory,
    ResolvedPackageArtifactInput[] PackageArtifactInputs,
    int PackageArtifactInputCount);

/// <summary>
/// Turns named input paths into one combined dependency inventory.
/// </summary>
/// <remarks>
/// Discovery, parsing, and combination are the first pipeline stage rather than a command concern, so this
/// stays below the commands that invoke it: <see cref="ScanExecution"/> depends on it, and it depends on
/// nothing in the command layer. It reads files and reports failure by exception, which the command layer
/// turns into a message and an exit code.
/// </remarks>
internal static class ScanInputIngestion
{
    /// <summary>Validates the named input paths and format, without touching the file system.</summary>
    public static bool TryResolve(string[]? input, string? inputFormat, out ScanInputSelection selection, out string error)
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

    /// <summary>Reads every selected input and combines them into one inventory.</summary>
    public static ScanInputIngestionResult Ingest(
        ScanInputSelection selection,
        SpdxLicenseIndex spdx,
        bool includeHash,
        bool collectPackageArtifacts)
    {
        var files = CollectInputFiles(selection);
        var inventories = new DependencyInventory[files.Length];
        var handlers = new DependencyInputHandler[files.Length];
        var packageArtifactInputs = collectPackageArtifacts ? new ResolvedPackageArtifactInput[files.Length] : [];
        var consumed = new bool[files.Length];
        var loadedInputs = includeHash ? new byte[files.Length][] : null;
        IncrementalHash? sourceHash = includeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var expectedFormat = selection.HasExpectedFormat ? selection.ExpectedHandler.Format : default;
        var kind = default(ScanInputKind);
        var format = default(ScanInputFormat);
        var specificationVersion = default(Utf8Slice);
        var inventoryCount = 0;
        var packageArtifactInputCount = 0;
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
                    if (collectPackageArtifacts
                        && PackageArtifactCollectorRegistry.Default.TryGet(handler.Format, out var artifactHandler))
                    {
                        packageArtifactInputs[packageArtifactInputCount++] = new ResolvedPackageArtifactInput(files[bundleIndexes[0]].Path, artifactHandler.Collector);
                    }
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

                    if (collectPackageArtifacts
                        && PackageArtifactCollectorRegistry.Default.TryGet(handler.Format, out var artifactHandler))
                    {
                        packageArtifactInputs[packageArtifactInputCount++] = new ResolvedPackageArtifactInput(files[i].Path, artifactHandler.Collector);
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
            return new ScanInputIngestionResult(
                DependencyInventoryCombiner.Combine(inventories.AsSpan(0, inventoryCount), handlers.AsSpan(0, inventoryCount), descriptor),
                packageArtifactInputs,
                packageArtifactInputCount);
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
