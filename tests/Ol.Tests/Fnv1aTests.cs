using System.Text;
using Ol.Core;

namespace Ol.Tests;

/// <summary>
/// Covers the shared 32-bit FNV-1a primitive the resolved-input parsers key their node-index tables with.
/// </summary>
public sealed class Fnv1aTests
{
    [Test]
    public async Task Hash_WithPublishedVectors_MatchesFnv1a32()
    {
        // The published 32-bit FNV-1a vectors. They pin the offset basis and the prime, so a table keyed by
        // this hash keeps distributing the same way after the implementation moved out of each parser.
        await Assert.That(Fnv1a.Hash([])).IsEqualTo(2166136261u);
        await Assert.That(Fnv1a.Hash("a"u8)).IsEqualTo(0xe40c292cu);
        await Assert.That(Fnv1a.Hash("foobar"u8)).IsEqualTo(0xbf9cf968u);
    }

    [Test]
    public async Task Hash_WithSeed_ContinuesAnEarlierHash()
    {
        // Chaining is what lets a composite key be hashed part by part without joining the parts first.
        await Assert.That(Fnv1a.Hash("bar"u8, Fnv1a.Hash("foo"u8))).IsEqualTo(Fnv1a.Hash("foobar"u8));
    }

    [Test]
    public async Task HashSeparator_DistinguishesPartBoundaries()
    {
        // Without a separator, ("ab", "") and ("a", "b") collide. Composite keys rely on them differing.
        var joined = Fnv1a.Hash([], Fnv1a.HashSeparator(Fnv1a.Hash("ab"u8)));
        var split = Fnv1a.Hash("b"u8, Fnv1a.HashSeparator(Fnv1a.Hash("a"u8)));
        await Assert.That(joined).IsNotEqualTo(split);
    }

    [Test]
    public async Task Hash_IsByteWise()
    {
        // "あ" is three bytes; the hash must consume all of them.
        await Assert.That(Fnv1a.Hash(Encoding.UTF8.GetBytes("あ"))).IsEqualTo(Fnv1a.Hash([0xE3, 0x81, 0x82]));
    }
}
