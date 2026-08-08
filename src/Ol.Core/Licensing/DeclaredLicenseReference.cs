namespace Ol.Core.Licensing;

/// <summary>
/// A publisher's unverified claim about where its license can be found.
/// </summary>
/// <param name="Kind">What sort of place was declared.</param>
/// <param name="Value">The declared location, exactly as the publisher wrote it.</param>
/// <remarks>
/// <para>
/// A reference is not a license and is not license text. Ol has not read what it points to, so it never
/// contributes a license value: it is resolved by reading the thing it names, or it stays an unresolved
/// declaration. Measured across three .NET repositories, the majority of declared locations lead to a
/// licensing overview page or a redirector rather than to a license document, which is why the type
/// says where the publisher pointed and refuses to imply what is there.
/// </para>
/// <para>
/// The same shape appears in every ecosystem Ol reads: a URL in NuGet <c>licenseUrl</c>, CycloneDX
/// <c>license.url</c>, and npm's legacy license collection; a path inside the published artifact in
/// NuGet <c>licenseFile</c>, Cargo <c>license_file</c>, and CocoaPods <c>license.file</c>. One type
/// keeps them one concept instead of one warning vocabulary per ecosystem.
/// </para>
/// <para>
/// Modeled as a reference type alongside the other provenance shapes, and for the same reason: most
/// candidates declare no location, and a value type would grow every candidate in every scan to carry
/// a field almost none of them use.
/// </para>
/// </remarks>
public sealed record DeclaredLicenseReference(DeclaredLicenseReferenceKind Kind, Utf8Slice Value);

/// <summary>Identifies what sort of place a <see cref="DeclaredLicenseReference"/> names.</summary>
public enum DeclaredLicenseReferenceKind : byte
{
    /// <summary>No location was declared.</summary>
    None,
    /// <summary>A URL the publisher named. It may be license text, an overview page, or a redirector.</summary>
    Location,
    /// <summary>A path inside the published artifact.</summary>
    ArtifactPath,
}
