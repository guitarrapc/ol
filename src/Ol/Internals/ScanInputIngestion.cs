using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ol.Core;
using Ol.Core.PackageManagers;
using Ol.Core.Spdx;

namespace Ol.Internals;

/// <summary>Identifies the resolved dependency inputs one scan reads, and the format the user named.</summary>
/// <param name="Paths">The input files or directories, in the order the user named them.</param>
/// <param name="ExcludedPaths">File or directory paths omitted from directory input discovery.</param>
/// <param name="ExpectedHandler">The handler for an explicitly named format, or default for automatic detection.</param>
internal readonly record struct ScanInputSelection(string[] Paths, string[] ExcludedPaths, DependencyInputHandler ExpectedHandler)
{
    public bool HasExpectedFormat => !string.IsNullOrEmpty(ExpectedHandler.Format.Name);
}

/// <summary>Identifies one physical dependency input that has a restored-artifact collector.</summary>
internal readonly record struct ResolvedPackageArtifactInput(string Path, PackageArtifactCollector Collector);

/// <summary>Identifies one discovered input left unscanned because its registered companion set was incomplete.</summary>
/// <param name="LogicalPath">The discovered file, as the scan refers to it.</param>
/// <param name="FormatName">The format the file belongs to.</param>
/// <param name="MissingFileName">The companion file that was not beside it.</param>
internal readonly record struct SkippedIncompleteInput(string LogicalPath, string FormatName, string MissingFileName);

/// <summary>Contains the combined inventory and the bounded physical inputs needed by local artifact collection.</summary>
internal readonly record struct ScanInputIngestionResult(
    DependencyInventory Inventory,
    ResolvedPackageArtifactInput[] PackageArtifactInputs,
    int PackageArtifactInputCount,
    int DetectedInputFileCount,
    InputCandidateDiagnostics InputCandidateDiagnostics,
    SkippedIncompleteInput[] SkippedIncompleteInputs,
    int SkippedIncompleteInputCount,
    string[] ExcludedInputPaths);

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
        => TryResolve(input, excludedInputPaths: null, inputFormat, out selection, out error);

    /// <summary>Validates the named input and excluded paths and format, without touching the file system.</summary>
    public static bool TryResolve(string[]? input, string[]? excludedInputPaths, string? inputFormat, out ScanInputSelection selection, out string error)
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

        excludedInputPaths ??= [];
        for (var excludedIndex = 0; excludedIndex < excludedInputPaths.Length; excludedIndex++)
        {
            if (string.IsNullOrWhiteSpace(excludedInputPaths[excludedIndex]))
            {
                error = "Excluded input paths must not be empty.";
                return false;
            }
        }

        if (string.IsNullOrEmpty(inputFormat) || string.Equals(inputFormat, "auto", StringComparison.OrdinalIgnoreCase))
        {
            selection = new ScanInputSelection(input, excludedInputPaths, default);
            error = string.Empty;
            return true;
        }

        if (!DependencyInputRegistry.Default.TryGetInputFormat(inputFormat, out var handler))
        {
            error = $"Unsupported input format: {inputFormat}";
            return false;
        }

        selection = new ScanInputSelection(input, excludedInputPaths, handler);
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
        var files = CollectInputFiles(selection, out var inputCandidateDiagnostics, out var excludedInputPaths, out var resolvedInputPaths);
        var inventories = new DependencyInventory[files.Length];
        var handlers = new DependencyInputHandler[files.Length];
        var packageArtifactInputs = collectPackageArtifacts ? new ResolvedPackageArtifactInput[files.Length] : [];
        var consumed = new bool[files.Length];
        var skippedIncompleteInputs = new SkippedIncompleteInput[files.Length];
        var skippedIncompleteInputCount = 0;
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
                if (TryCollectInputBundle(files, i, consumed, out handler, out var bundleIndexes, out var missingCompanionName))
                {
                    if (missingCompanionName is not null)
                    {
                        // Naming a file is an assertion that Ol should read it, so an incomplete set there is a
                        // failure. Directory discovery only proposes candidates, and aborting over one it proposed
                        // would let a file the user never named decide whether every other input gets reported.
                        if (!files[i].Discovered || selection.HasExpectedFormat)
                        {
                            throw new InvalidOperationException($"Input format {handler.Format.Name} requires companion file {missingCompanionName} in the same directory.");
                        }

                        for (var bundleIndex = 0; bundleIndex < bundleIndexes.Length; bundleIndex++)
                        {
                            if (bundleIndexes[bundleIndex] >= 0)
                            {
                                consumed[bundleIndexes[bundleIndex]] = true;
                            }
                        }

                        skippedIncompleteInputs[skippedIncompleteInputCount++] = new SkippedIncompleteInput(files[i].LogicalPath, handler.Format.Name, missingCompanionName);
                        continue;
                    }

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

                    try
                    {
                        inventory = DependencyInputScanner.ScanBundle(bundleSources, spdx, handler.Format);
                    }
                    catch (Exception exception) when (TryDescribeDiscoveredInputFailure(files[i], exception, out var discoveredError))
                    {
                        throw new InvalidOperationException(discoveredError, exception);
                    }

                    if (collectPackageArtifacts
                        && PackageArtifactCollectorRegistry.Default.TryGet(handler.Format, out var artifactHandler))
                    {
                        packageArtifactInputs[packageArtifactInputCount++] = new ResolvedPackageArtifactInput(files[bundleIndexes[0]].Path, artifactHandler.Collector);
                    }
                }
                else
                {
                    var inputBytes = loadedInputs?[i] ?? File.ReadAllBytes(files[i].Path);
                    try
                    {
                        inventory = DependencyInputScanner.Scan(inputBytes, spdx, expectedFormat: expectedFormat);
                    }
                    catch (Exception exception) when (KnownUnsupportedInputCandidates.TryGetDirectInputError(files[i].Path, exception, out var inputError))
                    {
                        throw new InvalidOperationException(inputError, exception);
                    }
                    catch (Exception exception) when (TryDescribeDiscoveredInputFailure(files[i], exception, out var discoveredError))
                    {
                        throw new InvalidOperationException(discoveredError, exception);
                    }
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

                KnownUnsupportedInputCandidates.ObserveScannedInput(inventory, handler, ref inputCandidateDiagnostics);

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

            // Skipping every discovered candidate leaves nothing to report, and an empty report reads as
            // "this repository has no dependencies" rather than "Ol could not read them".
            if (inventoryCount == 0 && skippedIncompleteInputCount > 0)
            {
                ref readonly var firstSkipped = ref skippedIncompleteInputs[0];
                throw new InvalidOperationException($"No dependency input could be scanned: input format {firstSkipped.FormatName} requires companion file {firstSkipped.MissingFileName} in the same directory ({firstSkipped.LogicalPath}).");
            }

            var descriptor = new ScanInputDescriptor(
                kind,
                format,
                GetInputSourceReference(resolvedInputPaths),
                sourceHash is null ? string.Empty : Convert.ToHexString(sourceHash.GetHashAndReset()).ToLowerInvariant(),
                specificationVersion);
            // One input goes through the same combiner as several. A registered format declares what makes two
            // observations the same package, and skipping the combiner made a single input the only path that did not
            // apply its own declaration: one CycloneDX document reported a component per bom-ref alone and per purl
            // when a lockfile was scanned beside it. The occurrences still record every entry the document listed.
            return new ScanInputIngestionResult(
                DependencyInventoryCombiner.Combine(inventories.AsSpan(0, inventoryCount), handlers.AsSpan(0, inventoryCount), descriptor),
                packageArtifactInputs,
                packageArtifactInputCount,
                files.Length,
                inputCandidateDiagnostics,
                skippedIncompleteInputs,
                skippedIncompleteInputCount,
                excludedInputPaths);
        }
        finally
        {
            sourceHash?.Dispose();
        }
    }

    /// <summary>
    /// Names the file in a failure the user cannot otherwise place. A named input is already identified by the
    /// command line, but a discovered one is a file the user never mentioned, so a bare parse failure gives them
    /// nothing to act on.
    /// </summary>
    private static bool TryDescribeDiscoveredInputFailure(in CollectedInputFile file, Exception exception, out string error)
    {
        if (!file.Discovered || exception is not (JsonException or InvalidOperationException or NotSupportedException or ArgumentException))
        {
            error = string.Empty;
            return false;
        }

        error = string.Concat(file.LogicalPath, ": ", exception.Message);
        return true;
    }

    private static bool TryCollectInputBundle(
        ReadOnlySpan<CollectedInputFile> files,
        int candidateIndex,
        ReadOnlySpan<bool> consumed,
        out DependencyInputHandler handler,
        out int[] bundleIndexes,
        out string? missingCompanionName)
    {
        missingCompanionName = null;
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
                    missingCompanionName = requiredNames[requiredIndex];
                    break;
                }
            }

            handler = candidateHandler;
            return true;
        }

        handler = default;
        bundleIndexes = [];
        return false;
    }

    private static CollectedInputFile[] CollectInputFiles(
        ScanInputSelection selection,
        out InputCandidateDiagnostics inputCandidateDiagnostics,
        out string[] excludedInputPaths,
        out string[] resolvedInputPaths)
    {
        inputCandidateDiagnostics = default;
        var resolvedPaths = ResolveActualPaths(selection);
        resolvedInputPaths = resolvedPaths.InputPaths;
        var pathComparer = selection.ExcludedPaths.Length == 0 && OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var collectedByPath = new Dictionary<string, CollectedInputFile>(pathComparer);
        var exclusions = ResolveExcludedInputPaths(selection, resolvedPaths);
        excludedInputPaths = exclusions.LogicalPaths;
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        for (var inputIndex = 0; inputIndex < selection.Paths.Length; inputIndex++)
        {
            var inputPath = selection.ExcludedPaths.Length == 0
                ? Path.GetFullPath(resolvedInputPaths[inputIndex])
                : resolvedInputPaths[inputIndex];
            if (File.Exists(inputPath))
            {
                if (IsExcluded(inputPath, exclusions.FullPaths))
                {
                    throw new InvalidOperationException($"Explicit input file is inside an excluded input path: {selection.Paths[inputIndex]}");
                }

                AddCollectedFile(collectedByPath, inputPath, Path.GetFileName(inputPath), discovered: false);
                continue;
            }

            if (exclusions.SkippedDirectoryInputs is { } skippedDirectoryInputs && skippedDirectoryInputs[inputIndex])
            {
                continue;
            }

            var rootName = new DirectoryInfo(inputPath).Name;
            if (selection.HasExpectedFormat)
            {
                // An explicit format is an assertion about what to scan, so detecting the candidates it
                // excludes would report a deliberate choice as an oversight.
                DiscoverDirectoryFiles(inputPath, rootName, [selection.ExpectedHandler], enumerationOptions, exclusions.FullPaths, collectedByPath, ref inputCandidateDiagnostics, detectUnsupportedCandidates: false);
                continue;
            }

            DiscoverDirectoryFiles(inputPath, rootName, DependencyInputRegistry.Default.RegisteredHandlers, enumerationOptions, exclusions.FullPaths, collectedByPath, ref inputCandidateDiagnostics, detectUnsupportedCandidates: true);
        }

        if (collectedByPath.Count == 0)
        {
            if (KnownUnsupportedInputCandidates.TryGetUnscannedInputError(inputCandidateDiagnostics, out var inputError))
            {
                throw new InvalidOperationException(inputError);
            }

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
        ReadOnlySpan<DependencyInputHandler> handlers,
        EnumerationOptions options,
        string[] excludedPaths,
        Dictionary<string, CollectedInputFile> collectedByPath,
        ref InputCandidateDiagnostics inputCandidateDiagnostics,
        bool detectUnsupportedCandidates)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(directory);
        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            foreach (var path in Directory.EnumerateFiles(currentDirectory, "*", options))
            {
                var fullPath = Path.GetFullPath(path);
                if (IsExcluded(fullPath, excludedPaths))
                {
                    continue;
                }

                if (detectUnsupportedCandidates)
                {
                    KnownUnsupportedInputCandidates.DetectFile(fullPath, ref inputCandidateDiagnostics);
                }

                if (!MatchesRegisteredFileName(Path.GetFileName(fullPath.AsSpan()), handlers)) continue;

                var relativePath = Path.GetRelativePath(directory, fullPath).Replace('\\', '/');
                AddCollectedFile(collectedByPath, fullPath, string.Concat(rootName, "/", relativePath), discovered: true);
            }

            foreach (var path in Directory.EnumerateDirectories(currentDirectory, "*", options))
            {
                var fullPath = Path.GetFullPath(path);
                if (!IsExcluded(fullPath, excludedPaths))
                {
                    pendingDirectories.Push(fullPath);
                }
            }
        }
    }

    private static bool MatchesRegisteredFileName(ReadOnlySpan<char> fileName, ReadOnlySpan<DependencyInputHandler> handlers)
    {
        for (var handlerIndex = 0; handlerIndex < handlers.Length; handlerIndex++)
        {
            var registeredFileNames = handlers[handlerIndex].DirectoryFileNames.Span;
            for (var fileNameIndex = 0; fileNameIndex < registeredFileNames.Length; fileNameIndex++)
            {
                if (fileName.Equals(registeredFileNames[fileNameIndex], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ResolvedScanPaths ResolveActualPaths(ScanInputSelection selection)
    {
        if (selection.ExcludedPaths.Length == 0)
        {
            return new ResolvedScanPaths(selection.Paths, []);
        }

        var fullPaths = new string[selection.Paths.Length + selection.ExcludedPaths.Length];
        for (var inputIndex = 0; inputIndex < selection.Paths.Length; inputIndex++)
        {
            fullPaths[inputIndex] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selection.Paths[inputIndex]));
        }

        for (var excludedIndex = 0; excludedIndex < selection.ExcludedPaths.Length; excludedIndex++)
        {
            var excludedPath = selection.ExcludedPaths[excludedIndex];
            var fullExcludedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(excludedPath));
            if (!File.Exists(fullExcludedPath) && !Directory.Exists(fullExcludedPath))
            {
                throw new InvalidOperationException($"Excluded input path not found: {excludedPath}");
            }

            fullPaths[selection.Paths.Length + excludedIndex] = fullExcludedPath;
        }

        var resolutionRoot = TryGetCommonResolutionRoot(fullPaths);
        var requests = new PendingPathResolution[fullPaths.Length];
        for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
        {
            requests[requestIndex] = CreatePathResolution(
                fullPaths[requestIndex],
                resolutionRoot ?? Path.GetPathRoot(fullPaths[requestIndex])!);
        }

        ResolvePathSegments(requests);
        var inputPaths = new string[selection.Paths.Length];
        for (var inputIndex = 0; inputIndex < inputPaths.Length; inputIndex++)
        {
            inputPaths[inputIndex] = requests[inputIndex].ResolvedPath;
        }

        var excludedPaths = new string[selection.ExcludedPaths.Length];
        for (var excludedIndex = 0; excludedIndex < excludedPaths.Length; excludedIndex++)
        {
            excludedPaths[excludedIndex] = requests[selection.Paths.Length + excludedIndex].ResolvedPath;
        }

        return new ResolvedScanPaths(inputPaths, excludedPaths);
    }

    private static string? TryGetCommonResolutionRoot(string[] fullPaths)
    {
        var pathRoot = Path.GetPathRoot(fullPaths[0]);
        var rootsMatchOrdinally = true;
        for (var pathIndex = 1; pathIndex < fullPaths.Length; pathIndex++)
        {
            var otherRoot = Path.GetPathRoot(fullPaths[pathIndex]);
            if (string.Equals(pathRoot, otherRoot, StringComparison.Ordinal))
            {
                continue;
            }

            rootsMatchOrdinally = false;
            if (!OperatingSystem.IsWindows()
                || !string.Equals(pathRoot, otherRoot, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (!rootsMatchOrdinally)
        {
            return pathRoot;
        }

        var resolutionRoot = Path.GetDirectoryName(fullPaths[0]) ?? Path.GetPathRoot(fullPaths[0])!;
        while (true)
        {
            var containsEveryPath = true;
            for (var pathIndex = 1; pathIndex < fullPaths.Length; pathIndex++)
            {
                if (string.Equals(fullPaths[pathIndex], resolutionRoot, StringComparison.Ordinal)
                    || IsDescendant(fullPaths[pathIndex], resolutionRoot))
                {
                    continue;
                }

                containsEveryPath = false;
                break;
            }

            if (containsEveryPath)
            {
                return resolutionRoot;
            }

            var parent = Path.GetDirectoryName(resolutionRoot);
            if (parent is null)
            {
                return resolutionRoot;
            }

            resolutionRoot = parent;
        }
    }

    private static PendingPathResolution CreatePathResolution(string fullPath, string resolutionRoot)
    {
        var relativePath = fullPath.AsSpan(resolutionRoot.Length);
        var segments = relativePath.IsEmpty
            ? []
            : relativePath.ToString().Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return new PendingPathResolution(resolutionRoot, segments);
    }

    private static void ResolvePathSegments(PendingPathResolution[] requests)
    {
        var unresolvedCount = requests.Length;
        while (unresolvedCount > 0)
        {
            var requestsByParent = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var requestIndex = 0; requestIndex < requests.Length; requestIndex++)
            {
                ref var request = ref requests[requestIndex];
                if (request.SegmentIndex == request.Segments.Length)
                {
                    unresolvedCount--;
                    request.SegmentIndex++;
                    continue;
                }

                if (request.SegmentIndex > request.Segments.Length)
                {
                    continue;
                }

                if (!requestsByParent.TryGetValue(request.ResolvedPath, out var indexes))
                {
                    indexes = [];
                    requestsByParent.Add(request.ResolvedPath, indexes);
                }

                indexes.Add(requestIndex);
            }

            foreach (var group in requestsByParent)
            {
                ResolveChildSegments(group.Key, group.Value, requests);
            }
        }
    }

    private static void ResolveChildSegments(string parentPath, List<int> requestIndexes, PendingPathResolution[] requests)
    {
        var matcher = new RequestedPathSegmentMatcher(requests, requestIndexes);
        var entries = new FileSystemEnumerable<ResolvedFileSystemEntry>(
            parentPath,
            static (ref FileSystemEntry entry) => new ResolvedFileSystemEntry(entry.ToFullPath(), entry.IsDirectory),
            new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            })
        {
            ShouldIncludePredicate = matcher.ShouldInclude,
        };

        foreach (var entry in entries)
        {
            var actualName = Path.GetFileName(entry.FullPath.AsSpan());
            for (var index = 0; index < requestIndexes.Count; index++)
            {
                ref var request = ref requests[requestIndexes[index]];
                var requestedName = request.Segments[request.SegmentIndex];
                if (actualName.Equals(requestedName, StringComparison.Ordinal))
                {
                    request.ExactMatch = entry;
                }
                else if (actualName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    request.CaseInsensitiveMatch = entry;
                    request.CaseInsensitiveMatchCount++;
                }
            }

            if (AllSegmentsHaveExactMatch(requestIndexes, requests))
            {
                break;
            }
        }

        for (var index = 0; index < requestIndexes.Count; index++)
        {
            ref var request = ref requests[requestIndexes[index]];
            var match = request.ExactMatch.FullPath is not null
                ? request.ExactMatch
                : request.CaseInsensitiveMatchCount == 1
                    ? request.CaseInsensitiveMatch
                    : throw new InvalidOperationException($"Unable to resolve the file-system casing of path: {Path.Combine(request.ResolvedPath, request.Segments[request.SegmentIndex])}");
            if (request.SegmentIndex + 1 < request.Segments.Length && !match.IsDirectory)
            {
                throw new InvalidOperationException($"A path segment is not a directory: {match.FullPath}");
            }

            request.ResolvedPath = match.FullPath!;
            request.SegmentIndex++;
            request.ExactMatch = default;
            request.CaseInsensitiveMatch = default;
            request.CaseInsensitiveMatchCount = 0;
        }
    }

    private static bool AllSegmentsHaveExactMatch(List<int> requestIndexes, PendingPathResolution[] requests)
    {
        for (var index = 0; index < requestIndexes.Count; index++)
        {
            if (requests[requestIndexes[index]].ExactMatch.FullPath is null)
            {
                return false;
            }
        }

        return true;
    }

    private static ResolvedInputExclusions ResolveExcludedInputPaths(ScanInputSelection selection, ResolvedScanPaths resolvedPaths)
    {
        if (selection.ExcludedPaths.Length == 0)
        {
            return new ResolvedInputExclusions([], [], null);
        }

        var fullPaths = new List<string>(selection.ExcludedPaths.Length);
        var fullPathSet = new HashSet<string>(StringComparer.Ordinal);
        var logicalPaths = new List<string>(selection.ExcludedPaths.Length);
        var skippedDirectoryInputs = new bool[selection.Paths.Length];
        for (var excludedIndex = 0; excludedIndex < selection.ExcludedPaths.Length; excludedIndex++)
        {
            var excludedPath = selection.ExcludedPaths[excludedIndex];
            var fullExcludedPath = resolvedPaths.ExcludedPaths[excludedIndex];

            var matchedInput = false;
            string? logicalPath = null;
            for (var inputIndex = 0; inputIndex < selection.Paths.Length; inputIndex++)
            {
                var inputRoot = resolvedPaths.InputPaths[inputIndex];
                if (File.Exists(inputRoot))
                {
                    if (string.Equals(inputRoot, fullExcludedPath, StringComparison.Ordinal) || IsDescendant(inputRoot, fullExcludedPath))
                    {
                        matchedInput = true;
                    }

                    continue;
                }

                if (!Directory.Exists(inputRoot))
                {
                    continue;
                }

                if (string.Equals(inputRoot, fullExcludedPath, StringComparison.Ordinal) || IsDescendant(inputRoot, fullExcludedPath))
                {
                    // An explicit directory that is itself inside an exclusion is intentionally skipped. This
                    // keeps an excluded subtree from being traversed a second time through a narrower input.
                    skippedDirectoryInputs[inputIndex] = true;
                    matchedInput = true;
                    logicalPath ??= Path.GetRelativePath(Environment.CurrentDirectory, fullExcludedPath).Replace('\\', '/');
                    continue;
                }

                if (!IsDescendant(fullExcludedPath, inputRoot))
                {
                    continue;
                }

                matchedInput = true;
                logicalPath ??= (Path.IsPathRooted(excludedPath)
                    ? Path.GetRelativePath(inputRoot, fullExcludedPath)
                    : Path.GetRelativePath(Environment.CurrentDirectory, fullExcludedPath)).Replace('\\', '/');
            }

            if (!matchedInput)
            {
                throw new InvalidOperationException($"Excluded input path must be inside a directory input: {excludedPath}");
            }

            logicalPath ??= Path.GetRelativePath(Environment.CurrentDirectory, fullExcludedPath).Replace('\\', '/');

            if (fullPathSet.Add(fullExcludedPath))
            {
                fullPaths.Add(fullExcludedPath);
                logicalPaths.Add(logicalPath!);
            }
        }

        return new ResolvedInputExclusions(fullPaths.ToArray(), logicalPaths.ToArray(), skippedDirectoryInputs);
    }

    private static bool IsExcluded(string path, string[] excludedPaths)
    {
        for (var excludedIndex = 0; excludedIndex < excludedPaths.Length; excludedIndex++)
        {
            if (string.Equals(path, excludedPaths[excludedIndex], StringComparison.Ordinal) || IsDescendant(path, excludedPaths[excludedIndex]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendant(string path, string directory)
    {
        if (path.Length <= directory.Length || !path.AsSpan(0, directory.Length).Equals(directory, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = path[directory.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static void AddCollectedFile(Dictionary<string, CollectedInputFile> collectedByPath, string path, string logicalPath, bool discovered)
    {
        if (collectedByPath.TryGetValue(path, out var existing))
        {
            // Naming a file directly is an assertion about it, and it stays one when a directory input happens
            // to discover the same file too.
            discovered &= existing.Discovered;
            if (string.CompareOrdinal(logicalPath, existing.LogicalPath) >= 0)
            {
                logicalPath = existing.LogicalPath;
            }
        }

        collectedByPath[path] = new CollectedInputFile(path, logicalPath, discovered);
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

    /// <summary>One physical input file, and whether directory discovery proposed it rather than the user naming it.</summary>
    private readonly record struct CollectedInputFile(string Path, string LogicalPath, bool Discovered);

    /// <summary>Canonical paths used for pruning and logical paths persisted in the report.</summary>
    private readonly record struct ResolvedInputExclusions(string[] FullPaths, string[] LogicalPaths, bool[]? SkippedDirectoryInputs);

    /// <summary>Actual file-system paths for named inputs and exclusions, preserving the casing returned by enumeration.</summary>
    private readonly record struct ResolvedScanPaths(string[] InputPaths, string[] ExcludedPaths);

    private struct PendingPathResolution(string root, string[] segments)
    {
        public string[] Segments { get; } = segments;
        public string ResolvedPath { get; set; } = root;
        public int SegmentIndex { get; set; }
        public ResolvedFileSystemEntry ExactMatch { get; set; }
        public ResolvedFileSystemEntry CaseInsensitiveMatch { get; set; }
        public int CaseInsensitiveMatchCount { get; set; }
    }

    private readonly record struct ResolvedFileSystemEntry(string? FullPath, bool IsDirectory);

    private sealed class RequestedPathSegmentMatcher(PendingPathResolution[] requests, List<int> requestIndexes)
    {
        public bool ShouldInclude(ref FileSystemEntry entry)
        {
            var actualName = entry.FileName;
            for (var index = 0; index < requestIndexes.Count; index++)
            {
                ref var request = ref requests[requestIndexes[index]];
                if (actualName.Equals(request.Segments[request.SegmentIndex], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

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
