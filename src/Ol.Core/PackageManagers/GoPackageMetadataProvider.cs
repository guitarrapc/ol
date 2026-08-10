using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Go module metadata from the module proxy, with licenses from deps.dev.</summary>
public sealed class GoPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://proxy.golang.org/");
    public override string Ecosystem => "golang";

    /// <summary>
    /// The license source is an addition to what the proxy already states, so it does not gate the lookup.
    /// </summary>
    public override bool FollowUpIsRequired => false;

    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(PackageMetadataJson.EscapePath(request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name)), "/@v/", Uri.EscapeDataString(request.Version), ".info"));

    /// <summary>
    /// Asks deps.dev for the license the proxy never carries.
    /// </summary>
    /// <remarks>
    /// The proxy states a module's origin but no license, so before this a Go module could only be resolved by
    /// whatever its repository host happened to expose through an API Ol collects from. That left every module
    /// hosted outside GitHub unresolvable no matter how plainly it was licensed: measuring three Go projects, all
    /// of <c>golang.org/x/*</c>, <c>google.golang.org/protobuf</c>, <c>gopkg.in/*</c>, and <c>rsc.io/*</c> came back
    /// unresolved, and they appear in nearly every Go build. deps.dev derives its values from the module contents,
    /// so it answers for those the same way it answers for a GitHub-hosted module.
    /// </remarks>
    public override Uri? CreateFollowUpEndpoint(JsonElement root, PackageMetadataRequest request)
        => root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("licenses", out _)
            ? DepsDevMetadata.CreateVersionEndpoint("go", ModulePath(request), request.Version)
            : null;

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
        => ParseResponse(root, root, request);

    public override PackageMetadataResponse ParseResponse(JsonElement root, JsonElement initial, PackageMetadataRequest request)
    {
        // The proxy answered first and owns the origin; the deps.dev document that replaced it owns the license.
        // No identity check is needed to tell them apart: a proxy document carries no licenses, so reading them from
        // whatever document arrived last yields nothing when the follow-up did not happen or did not answer.
        var origin = PackageMetadataJson.ReadElement(initial, "Origin");
        var url = PackageMetadataJson.ReadString(origin, "URL");
        var repositoryUrl = url.Length != 0 ? url : CreateGitHubRepositoryUrl(request);
        var repositoryRef = url.Length != 0 ? PackageMetadataJson.ReadString(origin, "Ref") : string.Empty;
        var license = DepsDevMetadata.ReadLicenses(root);

        // The source names where the license came from, so it stays the proxy only while there is no license to name.
        return new(license.Length == 0 ? "go-module-proxy" : DepsDevMetadata.Source, license, repositoryUrl, repositoryRef);
    }

    private static string ModulePath(PackageMetadataRequest request)
        => request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name);

    /// <summary>Derives the repository from the module path when the proxy states no origin.</summary>
    /// <remarks>
    /// <para>
    /// The proxy omits <c>Origin</c> for module versions it cached before it began recording one, which
    /// covers a large share of the widely used modules published before that field existed. Without it a
    /// module has no repository at all, so source evidence is never even attempted for a package whose
    /// location is fully determined.
    /// </para>
    /// <para>
    /// This is the Go module resolution rule rather than a guess about layout: for <c>github.com</c> the
    /// Go command itself treats the first two path elements as the repository root, and a module path is
    /// by definition where the module is fetched from. It is deliberately limited to that host. A vanity
    /// import path such as <c>gopkg.in/yaml.v3</c> or <c>rsc.io/pdf</c> names a redirect that only a
    /// <c>go-get</c> request resolves, and its module path is not a repository URL, so it stays
    /// unresolved rather than becoming an invented one. No ref is derived either, because the proxy that
    /// omitted the origin also stated no tag or commit for the version.
    /// </para>
    /// </remarks>
    private static string CreateGitHubRepositoryUrl(PackageMetadataRequest request)
    {
        var modulePath = request.Namespace is not { Length: > 0 } value ? string.Empty : string.Concat(value, "/", request.Name);
        return modulePath.StartsWith("github.com/", StringComparison.Ordinal) && modulePath.AsSpan("github.com/".Length).IndexOf('/') > 0
            ? string.Concat("https://", modulePath)
            : string.Empty;
    }
}
