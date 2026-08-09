using Ol.Core.PackageMetadata;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides Go module proxy metadata.</summary>
public sealed class GoPackageMetadataProvider : PackageMetadataProvider
{
    private static readonly Uri BaseUri = new("https://proxy.golang.org/");
    public override string Ecosystem => "golang";
    public override Uri CreateEndpoint(PackageMetadataRequest request)
        => new(BaseUri, string.Concat(PackageMetadataJson.EscapePath(request.Namespace.Length == 0 ? request.Name : string.Concat(request.Namespace, "/", request.Name)), "/@v/", Uri.EscapeDataString(request.Version), ".info"));
    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var origin = PackageMetadataJson.ReadElement(root, "Origin");
        var url = PackageMetadataJson.ReadString(origin, "URL");
        return url.Length != 0
            ? new("go-module-proxy", string.Empty, url, PackageMetadataJson.ReadString(origin, "Ref"))
            : new("go-module-proxy", string.Empty, CreateGitHubRepositoryUrl(request), string.Empty);
    }

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
