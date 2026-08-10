using Ol.Core;

namespace Ol.Tests;

public sealed class PurlPrefixSetTests
{
    [Test]
    [Arguments("pkg:npm/@acme/", "pkg:npm/%40acme/util@1.0.0", true)]
    [Arguments("pkg:npm/@acme", "pkg:npm/%40acme/util@1.0.0", true)]
    [Arguments("pkg:npm/%40acme/", "pkg:npm/%40acme/util@1.0.0", true)]
    [Arguments("pkg:npm/@acme/util@1.0.0", "pkg:npm/%40acme/util@1.0.0", true)]
    [Arguments("pkg:npm/left-pad@1.3.0", "pkg:npm/left-pad@1.3.0", true)]
    [Arguments("pkg:npm/@acme/", "pkg:npm/%40other/util@1.0.0", false)]
    [Arguments("pkg:npm/@acme/", "pkg:npm/acme-util@1.0.0", false)]
    public async Task Match_ScopedNpmPrefix_AcceptsWrittenAtSignAndCanonicalEncoding(string prefix, string purl, bool expected)
    {
        var created = PurlPrefixSet.TryCreate([prefix], out var set, out var error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(set!.Contains(purl)).IsEqualTo(expected);
    }

    [Test]
    public async Task TryCreate_WrittenAndEncodedScope_ProduceOneCanonicalPrefix()
    {
        PurlPrefixSet.TryCreate([" pkg:npm/@acme/ ", "pkg:npm/%40acme/"], out var set, out _);

        var prefixes = set!.Prefixes.ToArray();

        await Assert.That(prefixes.Length).IsEqualTo(1);
        await Assert.That(prefixes[0]).IsEqualTo("pkg:npm/%40acme/");
    }

    [Test]
    public async Task TryCreate_VersionSeparator_IsNotEncoded()
    {
        PurlPrefixSet.TryCreate(["pkg:npm/left-pad@1.3.0"], out var set, out _);

        await Assert.That(set!.Prefixes.ToArray()[0]).IsEqualTo("pkg:npm/left-pad@1.3.0");
    }

    [Test]
    [Arguments("pkg:npm/@")]
    [Arguments("npm/@acme")]
    [Arguments("pkg:")]
    [Arguments("pkg:/")]
    [Arguments("")]
    public async Task TryCreate_WithoutAnEcosystem_RejectsEntry(string value)
    {
        var created = PurlPrefixSet.TryCreate([value], out _, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    [Arguments("pkg:github/")]
    [Arguments("pkg:github")]
    public async Task TryCreate_EcosystemOnly_SelectsThatEcosystem(string value)
    {
        // A generator can inject a whole ecosystem a project never depended on, and listing every namespace it
        // happens to emit is not a stable answer. Selecting the ecosystem is a legitimate intent.
        var created = PurlPrefixSet.TryCreate([value], out var set, out var error);

        await Assert.That(created).IsTrue();
        await Assert.That(error).IsEmpty();
        await Assert.That(set!.Contains(Purl("pkg:github/actions/checkout@v4"))).IsTrue();
        await Assert.That(set.Contains(Purl("pkg:github/golangci/golangci-lint-action@v8.0.0"))).IsTrue();
    }

    [Test]
    [Arguments("pkg:github/")]
    [Arguments("pkg:github")]
    public async Task TryCreate_EcosystemOnly_StopsAtTheEcosystemBoundary(string value)
    {
        // The boundary still has to hold, or "pkg:github" would reach an ecosystem merely starting with those letters.
        PurlPrefixSet.TryCreate([value], out var set, out _);

        await Assert.That(set!.Contains(Purl("pkg:npm/left-pad@1.3.0"))).IsFalse();
        await Assert.That(set.Contains(Purl("pkg:githubfoo/actions/checkout@v4"))).IsFalse();
    }

    private static Utf8Slice Purl(string value) => Utf8Slice.FromOwnedBytes(System.Text.Encoding.UTF8.GetBytes(value));
}
