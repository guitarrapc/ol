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
    /// Specified in packagemanager.md. Go module resolution rather than a guess about layout: the Go command treats
    /// the first two <c>github.com</c> path elements as the repository root. Keep it to that host — a vanity import
    /// path such as <c>gopkg.in/yaml.v3</c> is a redirect only a <c>go-get</c> request resolves, so deriving a URL
    /// from it would invent one. No ref is derived: a proxy that omitted the origin stated no tag or commit either.
    /// </remarks>
    private static string CreateGitHubRepositoryUrl(PackageMetadataRequest request)
    {
        var modulePath = request.Namespace is not { Length: > 0 } value ? string.Empty : string.Concat(value, "/", request.Name);
        return modulePath.StartsWith("github.com/", StringComparison.Ordinal) && modulePath.AsSpan("github.com/".Length).IndexOf('/') > 0
            ? string.Concat("https://", modulePath)
            : string.Empty;
    }
}
