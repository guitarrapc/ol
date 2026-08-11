using System.Text;
using Ol.Core;

namespace Ol.Tests;

/// <summary>
/// Covers the shared purl percent-encoding primitive the resolved-input parsers write their package URLs
/// through. Every parser produced the same encoding from its own copy of these rules, so the rules are
/// asserted once here rather than through fifteen format-specific scans.
/// </summary>
public sealed class Utf8PurlTests
{
    [Test]
    public async Task Encode_WithUnreservedCharacters_PassesThemThrough()
    {
        // RFC 3986 unreserved: ALPHA / DIGIT / "-" / "." / "_" / "~". A purl that escaped these would not
        // round-trip against any registry.
        await Assert.That(Encode("abcXYZ019-._~")).IsEqualTo("abcXYZ019-._~");
    }

    [Test]
    public async Task Encode_WithReservedCharacters_WritesUppercaseHex()
    {
        await Assert.That(Encode("a b")).IsEqualTo("a%20b");
        await Assert.That(Encode("a:b")).IsEqualTo("a%3Ab");
        await Assert.That(Encode("a@b")).IsEqualTo("a%40b");
        await Assert.That(Encode("a%b")).IsEqualTo("a%25b");
        await Assert.That(Encode("a#b")).IsEqualTo("a%23b");
        await Assert.That(Encode("a?b")).IsEqualTo("a%3Fb");
    }

    [Test]
    public async Task Encode_WithSlashDisallowed_EscapesIt()
    {
        await Assert.That(Encode("scope/name")).IsEqualTo("scope%2Fname");
    }

    [Test]
    public async Task Encode_WithSlashAllowed_KeepsIt()
    {
        // Namespaced ecosystems carry the separator into the purl name: golang and npm scopes rely on this.
        await Assert.That(Encode("github.com/owner/repo", allowSlash: true)).IsEqualTo("github.com/owner/repo");
    }

    [Test]
    public async Task Encode_WithSlashAllowed_StillEscapesEveryOtherReservedCharacter()
    {
        // Allowing the separator must not widen the safe set to anything else.
        await Assert.That(Encode("a/b c", allowSlash: true)).IsEqualTo("a/b%20c");
    }

    [Test]
    public async Task Encode_WithMultiByteUtf8_EncodesEveryByte()
    {
        // "あ" is E3 81 82. Encoding must be byte-wise, not rune-wise.
        await Assert.That(Encode("あ")).IsEqualTo("%E3%81%82");
    }

    [Test]
    public async Task Encode_WithEmptyValue_WritesNothing()
    {
        await Assert.That(Utf8Purl.GetEncodedLength([])).IsEqualTo(0);
        await Assert.That(Encode(string.Empty)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GetEncodedLength_MatchesWhatWriteEncodedProduces()
    {
        // The length is used to size the destination, so a disagreement is an overflow rather than a wrong
        // string. Asserted for both safe-set variants.
        foreach (var value in new[] { string.Empty, "plain", "scope/name", "a b/c@d", "あ-1.0.0" })
        {
            foreach (var allowSlash in new[] { false, true })
            {
                var utf8 = Encoding.UTF8.GetBytes(value);
                await Assert.That(Encode(value, allowSlash).Length).IsEqualTo(Utf8Purl.GetEncodedLength(utf8, allowSlash));
            }
        }
    }

    private static string Encode(string value, bool allowSlash = false)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var destination = new byte[Utf8Purl.GetEncodedLength(utf8, allowSlash)];
        var written = 0;
        Utf8Purl.WriteEncoded(utf8, destination, ref written, allowSlash);
        return Encoding.UTF8.GetString(destination.AsSpan(0, written));
    }
}
