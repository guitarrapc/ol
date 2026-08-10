namespace Ol.Core.Licensing;

/// <summary>
/// A publisher's unverified claim about where its license can be found.
/// </summary>
/// <param name="Kind">What sort of place was declared.</param>
/// <param name="Value">The declared location, exactly as the publisher wrote it.</param>
/// <remarks>
/// The declared license reference contract in spdx.md, which lists the per-ecosystem sources. A reference never
/// contributes a license value: Ol has not read what it points to, and measured across three .NET repositories most
/// declared locations lead to an overview page or a redirector rather than to a license document.
/// <para>
/// A reference type rather than a value type, like the other provenance shapes: most candidates declare no location,
/// and a value type would grow every candidate in every scan to carry a field almost none of them use.
/// </para>
/// </remarks>
public sealed record DeclaredLicenseReference(DeclaredLicenseReferenceKind Kind, Utf8Slice Value);

/// <summary>Identifies what sort of place a <see cref="DeclaredLicenseReference"/> names.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DeclaredLicenseReferenceKind>))]
public enum DeclaredLicenseReferenceKind : byte
{
    /// <summary>No location was declared.</summary>
    None,
    /// <summary>A URL the publisher named. It may be license text, an overview page, or a redirector.</summary>
    Location,
    /// <summary>A path inside the published artifact.</summary>
    ArtifactPath,
    /// <summary>License text carried in the metadata itself. Only its existence is recorded, never the text.</summary>
    InlineText,
}
