using System.Buffers;
using System.Net;
using Ol.Core.Licensing;
using Ol.Core.SourceRepository;
using Ol.Core.Spdx;

namespace Ol.Core.GitHub;

/// <summary>Summarizes exact declared GitHub file collection.</summary>
public readonly record struct DeclaredGitHubFileArtifactCollectionSummary(
    int TargetCount,
    int GitHubRequestCount,
    int DocumentCount,
    int MatchedCount,
    int FetchErrorCount);

/// <summary>Contains components enriched from exact declared GitHub files.</summary>
public readonly record struct DeclaredGitHubFileArtifactCollection(
    ScanComponent[] Components,
    DeclaredGitHubFileArtifactCollectionSummary Summary);

/// <summary>Fetches exact GitHub file URLs only when no package-artifact document was already collected.</summary>
public sealed class DeclaredGitHubFileArtifactCollector
{
    private const int LinearPlanningComponentLimit = 8;
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly GitHubLicenseApiClient client;
    private readonly SpdxLicenseTextMatcher matcher;
    private readonly SpdxLicenseIndex spdxLicenseIndex;
    private readonly int retryCount;

    /// <summary>Creates a collector using the process-wide HTTP client and dedicated Ol GitHub token.</summary>
    public DeclaredGitHubFileArtifactCollector(
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex,
        int retryCount)
        : this(matcher, spdxLicenseIndex, retryCount, SharedHttpClient, GitHubAuthentication.FromEnvironment()) { }

    /// <summary>Creates a collector using the shared GitHub authentication and retry boundary.</summary>
    public DeclaredGitHubFileArtifactCollector(
        SpdxLicenseTextMatcher matcher,
        SpdxLicenseIndex spdxLicenseIndex,
        int retryCount,
        HttpClient httpClient,
        GitHubAuthentication authentication,
        Uri? apiBaseUri = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(spdxLicenseIndex);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);
        this.matcher = matcher;
        this.spdxLicenseIndex = spdxLicenseIndex;
        this.retryCount = retryCount;
        client = new GitHubLicenseApiClient(httpClient, authentication, apiBaseUri);
    }

    /// <summary>Gets the rate limit that stopped collection, or null while none was reached.</summary>
    public GitHubRateLimitStatus? RateLimit => client.RateLimit;

    /// <summary>Plans distinct exact file targets, performs bounded requests, and projects deterministic evidence.</summary>
    public ValueTask<DeclaredGitHubFileArtifactCollection> EnrichAsync(
        ScanComponent[] components,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (components.Length == 0) return ValueTask.FromResult(new DeclaredGitHubFileArtifactCollection(components, default));
        if (components.Length == 1)
        {
            if (!TryGetTarget(components[0], out var target))
            {
                return ValueTask.FromResult(new DeclaredGitHubFileArtifactCollection(components, default));
            }

            return EnrichSingleAsync(components, target, cancellationToken);
        }

        return EnrichCoreAsync(components, concurrency, cancellationToken);
    }

    private async ValueTask<DeclaredGitHubFileArtifactCollection> EnrichSingleAsync(
        ScanComponent[] components,
        DeclaredGitHubFileTarget target,
        CancellationToken cancellationToken)
    {
        var lookup = await FetchAsync(target, cancellationToken).ConfigureAwait(false);
        var matched = 0;
        if (lookup.HasDocument)
        {
            var candidate = CreateCandidate(components[0], target, lookup.Result);
            components[0] = LicenseReconciler.AddCandidate(components[0], candidate);
            matched = candidate.Status == LicenseStatus.Matched ? 1 : 0;
        }

        return new DeclaredGitHubFileArtifactCollection(
            components,
            new DeclaredGitHubFileArtifactCollectionSummary(
                1,
                lookup.Requested ? 1 : 0,
                lookup.HasDocument ? 1 : 0,
                matched,
                lookup.FetchError ? 1 : 0));
    }

    private async ValueTask<DeclaredGitHubFileArtifactCollection> EnrichCoreAsync(
        ScanComponent[] components,
        int concurrency,
        CancellationToken cancellationToken)
    {

        var targets = ArrayPool<DeclaredGitHubFileTarget>.Shared.Rent(components.Length);
        var results = ArrayPool<FileLookupResult>.Shared.Rent(components.Length);
        var componentTargetIndexes = ArrayPool<int>.Shared.Rent(components.Length);
        componentTargetIndexes.AsSpan(0, components.Length).Fill(-1);
        Dictionary<string, int>? targetIndexes = null;
        var targetCount = 0;
        try
        {
            targetCount = PlanTargets(components, targets, componentTargetIndexes, ref targetIndexes);
            if (targetCount == 1)
            {
                results[0] = await FetchAsync(targets[0], cancellationToken).ConfigureAwait(false);
            }
            else if (targetCount > 1)
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = cancellationToken };
                await Parallel.ForAsync(0, targetCount, options, async (index, token) =>
                {
                    results[index] = await FetchAsync(targets[index], token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            var matchedCount = 0;
            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                var targetIndex = componentTargetIndexes[componentIndex];
                if (targetIndex < 0 || !results[targetIndex].HasDocument) continue;
                var candidate = CreateCandidate(components[componentIndex], targets[targetIndex], results[targetIndex].Result);
                components[componentIndex] = LicenseReconciler.AddCandidate(components[componentIndex], candidate);
                if (candidate.Status == LicenseStatus.Matched) matchedCount++;
            }

            var requests = 0;
            var documents = 0;
            var errors = 0;
            for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                requests += results[targetIndex].Requested ? 1 : 0;
                documents += results[targetIndex].HasDocument ? 1 : 0;
                errors += results[targetIndex].FetchError ? 1 : 0;
            }

            return new DeclaredGitHubFileArtifactCollection(
                components,
                new DeclaredGitHubFileArtifactCollectionSummary(targetCount, requests, documents, matchedCount, errors));
        }
        finally
        {
            targets.AsSpan(0, targetCount).Clear();
            results.AsSpan(0, targetCount).Clear();
            ArrayPool<DeclaredGitHubFileTarget>.Shared.Return(targets);
            ArrayPool<FileLookupResult>.Shared.Return(results);
            ArrayPool<int>.Shared.Return(componentTargetIndexes);
        }
    }

    private static int PlanTargets(
        ReadOnlySpan<ScanComponent> components,
        Span<DeclaredGitHubFileTarget> targets,
        Span<int> componentTargetIndexes,
        ref Dictionary<string, int>? targetIndexes)
    {
        var targetCount = 0;
        for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
        {
            ref readonly var component = ref components[componentIndex];
            if (!TryGetTarget(component, out var target)) continue;
            if (targetIndexes is null && targetCount == LinearPlanningComponentLimit)
            {
                targetIndexes = new Dictionary<string, int>(components.Length, StringComparer.Ordinal);
                for (var existingIndex = 0; existingIndex < targetCount; existingIndex++)
                {
                    targetIndexes.Add(targets[existingIndex].CacheKey, existingIndex);
                }
            }

            var targetIndex = -1;
            if (targetIndexes is null)
            {
                for (var existingIndex = 0; existingIndex < targetCount; existingIndex++)
                {
                    if (!string.Equals(targets[existingIndex].CacheKey, target.CacheKey, StringComparison.Ordinal)) continue;
                    targetIndex = existingIndex;
                    break;
                }
            }
            else if (!targetIndexes.TryGetValue(target.CacheKey, out targetIndex))
            {
                targetIndex = -1;
            }

            if (targetIndex < 0)
            {
                targetIndex = targetCount++;
                targets[targetIndex] = target;
                targetIndexes?.Add(target.CacheKey, targetIndex);
            }

            componentTargetIndexes[componentIndex] = targetIndex;
        }

        return targetCount;
    }

    private static bool TryGetTarget(in ScanComponent component, out DeclaredGitHubFileTarget target)
    {
        target = default;
        if (component.Purl.IsEmpty && component.SourceId.IsEmpty) return false;
        for (var candidateIndex = 0; candidateIndex < component.CandidateCount; candidateIndex++)
        {
            if (component.GetCandidate(candidateIndex).Evidence.PackageArtifact is not null) return false;
        }

        for (var candidateIndex = 0; candidateIndex < component.CandidateCount; candidateIndex++)
        {
            if (component.GetCandidate(candidateIndex).Evidence.DeclaredReference is { Kind: DeclaredLicenseReferenceKind.Location } reference
                && CouldBeGitHubFileUrl(reference.Value.Span)
                && DeclaredGitHubFileTarget.TryCreate(reference.Value.ToString(), out target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CouldBeGitHubFileUrl(ReadOnlySpan<byte> value)
        => AsciiStartsWithIgnoreCase(value, "https://github.com/"u8)
        || AsciiStartsWithIgnoreCase(value, "https://raw.githubusercontent.com/"u8)
        || AsciiStartsWithIgnoreCase(value, "https://raw.github.com/"u8);

    private static bool AsciiStartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length) return false;
        for (var index = 0; index < prefix.Length; index++)
        {
            var current = value[index];
            if (current is >= (byte)'A' and <= (byte)'Z') current += 32;
            if (current != prefix[index]) return false;
        }

        return true;
    }

    private async Task<FileLookupResult> FetchAsync(DeclaredGitHubFileTarget target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await GitHubLicenseFetchScheduler.FetchFileAsync(client, target, matcher, retryCount, cancellationToken).ConfigureAwait(false);
            return new FileLookupResult(result, Requested: true, HasDocument: result.StatusCode == HttpStatusCode.OK, FetchError: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OperationCanceledException or SourceRepositoryFetchException or HttpRequestException or IOException)
        {
            return new FileLookupResult(default, Requested: true, HasDocument: false, FetchError: true);
        }
    }

    private LicenseCandidate CreateCandidate(
        in ScanComponent component,
        in DeclaredGitHubFileTarget target,
        in DeclaredGitHubFileResult result)
    {
        var artifact = component.Purl.IsEmpty ? component.SourceId.ToString() : component.Purl.ToString();
        var evidence = new LicenseEvidence(
            LicenseEvidenceKind.PackageArtifact,
            PackageArtifact: new PackageArtifactEvidence(
                artifact,
                target.Path,
                result.ContentSha256,
                "spdx-template",
                matcher.CorpusVersion));
        if (result.LicenseId is { } licenseId)
        {
            return LicenseCandidateFactory.Create(
                LicenseCandidateSource.PackageArtifact,
                LicenseCandidateKind.License,
                Utf8Slice.FromString(licenseId),
                spdxLicenseIndex,
                evidence);
        }

        return new LicenseCandidate(
            LicenseCandidateSource.PackageArtifact,
            LicenseCandidateKind.License,
            default,
            default,
            LicenseStatus.Unknown,
            false,
            LicenseCandidateWarnings.SourceLicenseNotDetected,
            evidence);
    }

    private readonly record struct FileLookupResult(
        DeclaredGitHubFileResult Result,
        bool Requested,
        bool HasDocument,
        bool FetchError);
}
