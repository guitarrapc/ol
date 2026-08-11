using Ol.Core.Licensing;
using Ol.Core.PackageMetadata;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

/// <summary>Provides version-specific public CocoaPods podspec metadata.</summary>
public sealed class CocoaPodsPackageMetadataProvider : PackageMetadataProvider
{
    private const int MaxStackNameBytes = 512;

    private static readonly Uri BaseUri = new("https://cdn.cocoapods.org/Specs/");

    public override string Ecosystem => "cocoapods";

    public override bool TryCreate(ReadOnlySpan<byte> purl, out PackageMetadataRequest request)
    {
        if (!base.TryCreate(purl, out request)
            || request.Namespace.Length != 0
            || !IsValidName(request.Name))
        {
            request = default;
            return false;
        }
        return true;
    }

    public override Uri CreateEndpoint(PackageMetadataRequest request)
    {
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(request.Name.Length);
        byte[]? rented = null;
        Span<byte> utf8 = maximumByteCount <= MaxStackNameBytes
            ? stackalloc byte[MaxStackNameBytes]
            : (rented = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(request.Name, utf8);
            Span<byte> hash = stackalloc byte[MD5.HashSizeInBytes];
            MD5.HashData(utf8[..byteCount], hash);
            var name = Uri.EscapeDataString(request.Name);
            return new(
                BaseUri,
                string.Concat(
                    Nibble(hash[0] >> 4), "/",
                    Nibble(hash[0] & 0x0f), "/",
                    Nibble(hash[1] >> 4), "/",
                    name, "/",
                    Uri.EscapeDataString(request.Version), "/",
                    name,
                    ".podspec.json"));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public override PackageMetadataResponse ParseResponse(JsonElement root, PackageMetadataRequest request)
    {
        var license = PackageMetadataJson.ReadElement(root, "license");
        var rawLicense = license.ValueKind switch
        {
            JsonValueKind.String => license.GetString() ?? string.Empty,
            JsonValueKind.Object => PackageMetadataJson.ReadString(license, "type"),
            _ => string.Empty,
        };
        var source = PackageMetadataJson.ReadElement(root, "source");
        var repository = PackageMetadataJson.ReadString(source, "git");
        var repositoryRef = PackageMetadataJson.ReadString(source, "commit");
        if (repositoryRef.Length == 0) repositoryRef = PackageMetadataJson.ReadString(source, "tag");
        if (repositoryRef.Length == 0) repositoryRef = PackageMetadataJson.ReadString(source, "branch");
        var licenseFile = license.ValueKind == JsonValueKind.Object ? PackageMetadataJson.ReadString(license, "file") : string.Empty;
        var hasLicenseText = license.ValueKind == JsonValueKind.Object && PackageMetadataJson.ReadString(license, "text").Length != 0;
        // Embedded text is recorded as existing and never retained: a cache is not a place to keep a
        // license document, and the report contract keeps license text out of default output.
        var (referenceKind, referenceValue) = licenseFile.Length != 0
            ? (DeclaredLicenseReferenceKind.ArtifactPath, licenseFile)
            : hasLicenseText ? (DeclaredLicenseReferenceKind.InlineText, string.Empty)
            : (DeclaredLicenseReferenceKind.None, string.Empty);
        return new("cocoapods-cdn", rawLicense, repository, repositoryRef, LicenseCandidateWarnings.None, referenceKind, referenceValue);
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || name[0] == '.') return false;
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsWhiteSpace(name[index]) || name[index] == '+') return false;
        }
        return true;
    }

    private static char Nibble(int value) => (char)(value < 10 ? '0' + value : 'a' + value - 10);
}
