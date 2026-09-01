using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class BundlerLockInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:gem/"u8;
    private static readonly Utf8Slice ProjectIdentity = Utf8Slice.FromOwnedBytes("Gemfile.lock"u8.ToArray());
    private static readonly Utf8Slice RegistryVariant = Utf8Slice.FromOwnedBytes("source=registry"u8.ToArray());
    private static readonly Utf8Slice GitVariant = Utf8Slice.FromOwnedBytes("source=git"u8.ToArray());
    private static readonly Utf8Slice PathVariant = Utf8Slice.FromOwnedBytes("source=path"u8.ToArray());

    internal static bool Detect(ReadOnlySpan<byte> inputUtf8)
    {
        var sources = false;
        var platforms = false;
        var dependencies = false;
        var position = 0;
        while (position < inputUtf8.Length)
        {
            var endOffset = inputUtf8[position..].IndexOf((byte)'\n');
            var end = endOffset < 0 ? inputUtf8.Length : position + endOffset;
            var line = inputUtf8[position..end];
            if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
            if (!line.IsEmpty && line[0] != (byte)' ')
            {
                sources |= line.SequenceEqual("GEM"u8) || line.SequenceEqual("GIT"u8) || line.SequenceEqual("PATH"u8);
                platforms |= line.SequenceEqual("PLATFORMS"u8);
                dependencies |= line.SequenceEqual("DEPENDENCIES"u8);
            }

            position = endOffset < 0 ? inputUtf8.Length : end + 1;
        }

        return sources && platforms && dependencies;
    }

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
    {
        var nodes = ArrayPool<BundlerNode>.Shared.Rent(16);
        var dependencies = ArrayPool<BundlerDependency>.Shared.Rent(32);
        var directNames = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var platforms = ArrayPool<Utf8Slice>.Shared.Rent(8);
        var nodeCount = 0;
        var dependencyCount = 0;
        var directCount = 0;
        var platformCount = 0;
        try
        {
            var section = BundlerSection.None;
            var sourceKind = BundlerSource.None;
            var publicRubyGems = false;
            var sawRemote = false;
            var inSpecs = false;
            var currentNode = -1;
            var rubyVersion = default(Utf8Slice);
            var bundlerVersion = default(Utf8Slice);
            var sawSource = false;
            var sawPlatforms = false;
            var sawDependencies = false;
            var position = offset;
            while (position < source.Length)
            {
                var lineStart = position;
                var relativeEnd = source.AsSpan(position).IndexOf((byte)'\n');
                var lineEnd = relativeEnd < 0 ? source.Length : position + relativeEnd;
                position = relativeEnd < 0 ? source.Length : lineEnd + 1;
                if (lineEnd > lineStart && source[lineEnd - 1] == (byte)'\r') lineEnd--;
                var line = source.AsSpan(lineStart, lineEnd - lineStart);
                if (line.IsEmpty) continue;

                var indent = 0;
                while (indent < line.Length && line[indent] == (byte)' ') indent++;
                if (indent == 0)
                {
                    currentNode = -1;
                    inSpecs = false;
                    if (line.SequenceEqual("GEM"u8))
                    {
                        section = BundlerSection.Source;
                        sourceKind = BundlerSource.RubyGems;
                        publicRubyGems = false;
                        sawRemote = false;
                        sawSource = true;
                    }
                    else if (line.SequenceEqual("GIT"u8))
                    {
                        section = BundlerSection.Source;
                        sourceKind = BundlerSource.Git;
                        publicRubyGems = false;
                        sawRemote = false;
                        sawSource = true;
                    }
                    else if (line.SequenceEqual("PATH"u8))
                    {
                        section = BundlerSection.Source;
                        sourceKind = BundlerSource.Path;
                        publicRubyGems = false;
                        sawRemote = false;
                        sawSource = true;
                    }
                    else if (line.SequenceEqual("PLATFORMS"u8))
                    {
                        section = BundlerSection.Platforms;
                        sawPlatforms = true;
                    }
                    else if (line.SequenceEqual("DEPENDENCIES"u8))
                    {
                        section = BundlerSection.Dependencies;
                        sawDependencies = true;
                    }
                    else if (line.SequenceEqual("RUBY VERSION"u8)) section = BundlerSection.RubyVersion;
                    else if (line.SequenceEqual("BUNDLED WITH"u8)) section = BundlerSection.BundledWith;
                    else section = BundlerSection.None;
                    continue;
                }

                var content = line[indent..];
                if (section == BundlerSection.Source)
                {
                    if (indent == 2 && content.SequenceEqual("specs:"u8))
                    {
                        inSpecs = true;
                        continue;
                    }

                    if (indent == 2)
                    {
                        if (sourceKind == BundlerSource.RubyGems && content.StartsWith("remote:"u8))
                        {
                            var remote = Trim(content[7..]);
                            publicRubyGems = !sawRemote
                                ? remote.SequenceEqual("https://rubygems.org/"u8)
                                : publicRubyGems && remote.SequenceEqual("https://rubygems.org/"u8);
                            sawRemote = true;
                        }

                        continue;
                    }

                    if (inSpecs && indent == 4)
                    {
                        EnsureCapacity(ref nodes, nodeCount);
                        var (name, version) = ParseSpec(source, lineStart + indent, content);
                        currentNode = nodeCount;
                        nodes[nodeCount++] = new BundlerNode(name, version, version, default, sourceKind, publicRubyGems && sawRemote, dependencyCount, 0);
                        continue;
                    }

                    if (inSpecs && indent == 6 && currentNode >= 0)
                    {
                        EnsureCapacity(ref dependencies, dependencyCount);
                        var name = ReadName(source, lineStart + indent, content);
                        if (name.IsEmpty) throw new JsonException("Bundler package dependency name is required.");
                        dependencies[dependencyCount++] = new BundlerDependency(name);
                        var node = nodes[currentNode];
                        nodes[currentNode] = node with { DependencyCount = node.DependencyCount + 1 };
                    }
                }
                else if (section == BundlerSection.Platforms)
                {
                    EnsureCapacity(ref platforms, platformCount);
                    platforms[platformCount++] = SliceTrimmed(source, lineStart + indent, content.Length);
                }
                else if (section == BundlerSection.Dependencies)
                {
                    EnsureCapacity(ref directNames, directCount);
                    var name = ReadName(source, lineStart + indent, content);
                    if (!name.IsEmpty && name.Span[^1] == (byte)'!') name = name.Slice(0, name.Length - 1);
                    if (name.IsEmpty) throw new JsonException("Bundler direct dependency name is required.");
                    directNames[directCount++] = name;
                }
                else if (section == BundlerSection.RubyVersion)
                {
                    rubyVersion = SliceTrimmed(source, lineStart + indent, content.Length);
                }
                else if (section == BundlerSection.BundledWith)
                {
                    bundlerVersion = SliceTrimmed(source, lineStart + indent, content.Length);
                }
            }

            if (!sawSource || !sawPlatforms || !sawDependencies || nodeCount == 0 || platformCount == 0)
            {
                throw new JsonException("Bundler lock requires source specs, PLATFORMS, and DEPENDENCIES sections.");
            }

            SplitPlatforms(nodes.AsSpan(0, nodeCount), platforms.AsSpan(0, platformCount));
            ValidatePlatforms(platforms.AsSpan(0, platformCount));
            return BuildInventory(
                nodes.AsSpan(0, nodeCount),
                dependencies.AsSpan(0, dependencyCount),
                directNames.AsSpan(0, directCount),
                platforms.AsSpan(0, platformCount),
                rubyVersion,
                bundlerVersion,
                retainGraph);
        }
        finally
        {
            ArrayPool<BundlerNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<BundlerDependency>.Shared.Return(dependencies, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(directNames, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(platforms, clearArray: true);
        }
    }

    private static DependencyInventory BuildInventory(
        ReadOnlySpan<BundlerNode> nodes,
        ReadOnlySpan<BundlerDependency> dependencies,
        ReadOnlySpan<Utf8Slice> directNames,
        ReadOnlySpan<Utf8Slice> platforms,
        Utf8Slice rubyVersion,
        Utf8Slice bundlerVersion,
        bool retainGraph)
    {
        var indexCapacity = GetIndexCapacity(nodes.Length);
        var nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
        nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
        for (var i = 0; i < nodes.Length; i++)
        {
            if (!AddNodeIndex(nodes, nodeIndexes, indexCapacity, i))
            {
                ArrayPool<int>.Shared.Return(nodeIndexes);
                throw new JsonException("Bundler lock contains duplicate package identity for one platform.");
            }
        }

        var components = new ScanComponent[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            var purl = node.PublicRubyGems ? CreatePurl(node.Name, node.Version, node.Platform) : default;
            components[i] = new ScanComponent(
                node.Name,
                node.Version,
                default,
                node.PublicRubyGems ? "gem" : "-",
                DependencyType.Unknown,
                LicenseStatus.Unknown,
                purl,
                CreateSourceId(node.Name, node.FullVersion),
                default,
                []);
        }

        var contexts = new DependencyResolutionContext[platforms.Length];
        var contextVariant = CreateContextVariant(bundlerVersion);
        for (var i = 0; i < platforms.Length; i++)
        {
            contexts[i] = new DependencyResolutionContext(ProjectIdentity, default, rubyVersion, platforms[i], default, contextVariant, default);
        }

        var occurrenceCapacity = checked(nodes.Length * platforms.Length);
        var edgeCapacity = checked((dependencies.Length + directNames.Length) * platforms.Length);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(Math.Max(16, occurrenceCapacity));
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(Math.Max(16, edgeCapacity));
        var variants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(Math.Max(16, occurrenceCapacity));
        var depths = ArrayPool<int>.Shared.Rent(nodes.Length);
        var queue = ArrayPool<int>.Shared.Rent(Math.Max(16, nodes.Length));
        var occurrenceByNode = ArrayPool<int>.Shared.Rent(nodes.Length);
        var occurrenceCount = 0;
        var edgeCount = 0;
        var variantCount = 0;
        try
        {
            for (var contextIndex = 0; contextIndex < platforms.Length; contextIndex++)
            {
                var platform = platforms[contextIndex];
                depths.AsSpan(0, nodes.Length).Fill(int.MinValue);
                occurrenceByNode.AsSpan(0, nodes.Length).Fill(-1);
                var head = 0;
                var tail = 0;
                for (var directIndex = 0; directIndex < directNames.Length; directIndex++)
                {
                    var target = FindTarget(nodes, nodeIndexes, indexCapacity, directNames[directIndex], platform);
                    if (target < 0 || depths[target] == 0) continue;
                    depths[target] = 0;
                    EnsureCapacity(ref queue, tail);
                    queue[tail++] = target;
                }

                while (head < tail)
                {
                    var ownerIndex = queue[head++];
                    var owner = nodes[ownerIndex];
                    for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
                    {
                        var target = FindTarget(nodes, nodeIndexes, indexCapacity, dependencies[dependencyIndex].Name, platform);
                        if (target < 0) continue;
                        var depth = depths[ownerIndex] + 1;
                        if (depths[target] != int.MinValue && depths[target] <= depth) continue;
                        depths[target] = depth;
                        EnsureCapacity(ref queue, tail);
                        queue[tail++] = target;
                    }
                }

                for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                {
                    var node = nodes[nodeIndex];
                    if (!IsCompatible(node, platform)) continue;
                    EnsureCapacity(ref occurrences, occurrenceCount);
                    occurrenceByNode[nodeIndex] = occurrenceCount;
                    occurrences[occurrenceCount++] = new DependencyOccurrence(contextIndex, nodeIndex);
                    if (depths[nodeIndex] != int.MinValue)
                    {
                        var dependencyType = depths[nodeIndex] == 0 ? DependencyType.Direct : DependencyType.Transitive;
                        components[nodeIndex] = components[nodeIndex] with { DependencyType = DependencyTypes.Merge(components[nodeIndex].DependencyType, dependencyType) };
                    }

                    var variant = CreateOccurrenceVariant(node);
                    if (!variant.IsEmpty)
                    {
                        EnsureCapacity(ref variants, variantCount);
                        variants[variantCount++] = new DependencyOccurrenceVariant(occurrenceCount - 1, variant);
                    }
                }

                for (var directIndex = 0; directIndex < directNames.Length; directIndex++)
                {
                    var target = FindTarget(nodes, nodeIndexes, indexCapacity, directNames[directIndex], platform);
                    if (target < 0 || occurrenceByNode[target] < 0) continue;
                    EnsureCapacity(ref edges, edgeCount);
                    edges[edgeCount++] = new DependencyEdge(contextIndex, DependencyOccurrence.ContextRoot, occurrenceByNode[target]);
                }

                for (var ownerIndex = 0; ownerIndex < nodes.Length; ownerIndex++)
                {
                    if (depths[ownerIndex] == int.MinValue || occurrenceByNode[ownerIndex] < 0) continue;
                    var owner = nodes[ownerIndex];
                    for (var dependencyIndex = owner.DependencyStart; dependencyIndex < owner.DependencyStart + owner.DependencyCount; dependencyIndex++)
                    {
                        var target = FindTarget(nodes, nodeIndexes, indexCapacity, dependencies[dependencyIndex].Name, platform);
                        if (target < 0 || occurrenceByNode[target] < 0) continue;
                        EnsureCapacity(ref edges, edgeCount);
                        edges[edgeCount++] = new DependencyEdge(contextIndex, occurrenceByNode[ownerIndex], occurrenceByNode[target]);
                    }
                }
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, bundlerVersion),
                contexts,
                components,
                retainGraph ? occurrences.AsSpan(0, occurrenceCount).ToArray() : [],
                retainGraph ? edges.AsSpan(0, edgeCount).ToArray() : [],
                retainGraph ? variants.AsSpan(0, variantCount).ToArray() : []);
        }
        finally
        {
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(variants, clearArray: true);
            ArrayPool<int>.Shared.Return(depths);
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<int>.Shared.Return(occurrenceByNode);
            ArrayPool<int>.Shared.Return(nodeIndexes);
        }
    }

    private static (Utf8Slice Name, Utf8Slice Version) ParseSpec(byte[] source, int start, ReadOnlySpan<byte> content)
    {
        var separator = content.IndexOf(" ("u8);
        if (separator <= 0 || content[^1] != (byte)')') throw new JsonException("Bundler spec requires a name and resolved version.");
        var name = new Utf8Slice(source, start, separator);
        var version = new Utf8Slice(source, start + separator + 2, content.Length - separator - 3);
        if (version.IsEmpty) throw new JsonException("Bundler spec version is required.");
        return (name, version);
    }

    private static Utf8Slice ReadName(byte[] source, int start, ReadOnlySpan<byte> content)
    {
        var separator = content.IndexOf((byte)' ');
        var length = separator < 0 ? content.Length : separator;
        return length == 0 ? default : new Utf8Slice(source, start, length);
    }

    private static void SplitPlatforms(Span<BundlerNode> nodes, ReadOnlySpan<Utf8Slice> platforms)
    {
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var node = nodes[nodeIndex];
            var best = default(Utf8Slice);
            for (var platformIndex = 0; platformIndex < platforms.Length; platformIndex++)
            {
                var platform = platforms[platformIndex];
                if (platform.Span.SequenceEqual("ruby"u8) || platform.Length <= best.Length) continue;
                var fullVersion = node.FullVersion.Span;
                if (fullVersion.Length > platform.Length
                    && fullVersion[fullVersion.Length - platform.Length - 1] == (byte)'-'
                    && fullVersion[^platform.Length..].SequenceEqual(platform.Span))
                {
                    best = platform;
                }
            }

            if (!best.IsEmpty)
            {
                nodes[nodeIndex] = node with
                {
                    Version = node.FullVersion.Slice(0, node.FullVersion.Length - best.Length - 1),
                    Platform = best,
                };
            }
        }
    }

    private static void ValidatePlatforms(ReadOnlySpan<Utf8Slice> platforms)
    {
        for (var i = 0; i < platforms.Length; i++)
        {
            if (platforms[i].IsEmpty) throw new JsonException("Bundler platforms must be non-empty.");
            for (var j = i + 1; j < platforms.Length; j++)
            {
                if (platforms[i].Equals(platforms[j]))
                {
                    throw new JsonException("Bundler lock contains a duplicate platform.");
                }
            }
        }
    }

    private static int FindTarget(
        ReadOnlySpan<BundlerNode> nodes,
        ReadOnlySpan<int> indexes,
        int capacity,
        Utf8Slice name,
        Utf8Slice platform)
    {
        if (TryGetNodeIndex(nodes, indexes, capacity, name.Span, platform.Span, out var nodeIndex))
        {
            return nodeIndex;
        }

        return TryGetNodeIndex(nodes, indexes, capacity, name.Span, default, out nodeIndex) ? nodeIndex : -1;
    }

    private static bool IsCompatible(BundlerNode node, Utf8Slice platform)
        => node.Platform.IsEmpty || node.Platform.Equals(platform);

    private static bool AddNodeIndex(ReadOnlySpan<BundlerNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var node = nodes[nodeIndex];
        var slot = (int)(Fnv1a.Hash(node.Platform.Span, Fnv1a.HashSeparator(Fnv1a.Hash(node.Name.Span))) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            var registered = nodes[indexes[slot]];
            if (registered.Name.Equals(node.Name) && registered.Platform.Equals(node.Platform)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetNodeIndex(
        ReadOnlySpan<BundlerNode> nodes,
        ReadOnlySpan<int> indexes,
        int capacity,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> platform,
        out int nodeIndex)
    {
        var slot = (int)(Fnv1a.Hash(platform, Fnv1a.HashSeparator(Fnv1a.Hash(name))) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            var node = nodes[nodeIndex];
            if (node.Name.Span.SequenceEqual(name) && node.Platform.Span.SequenceEqual(platform)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        var required = checked(count * 2);
        while (capacity < required) capacity = checked(capacity * 2);
        return capacity;
    }

    private static Utf8Slice CreateContextVariant(Utf8Slice bundlerVersion)
    {
        if (bundlerVersion.IsEmpty) return default;
        var bytes = new byte["bundler="u8.Length + bundlerVersion.Length];
        "bundler="u8.CopyTo(bytes);
        bundlerVersion.Span.CopyTo(bytes.AsSpan("bundler="u8.Length));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateOccurrenceVariant(BundlerNode node)
    {
        var source = node.Source switch
        {
            BundlerSource.RubyGems when !node.PublicRubyGems => RegistryVariant,
            BundlerSource.Git => GitVariant,
            BundlerSource.Path => PathVariant,
            _ => default,
        };
        if (node.Platform.IsEmpty) return source;
        var prefixLength = source.IsEmpty ? 0 : source.Length + 1;
        var bytes = new byte[prefixLength + "platform="u8.Length + node.Platform.Length];
        var index = 0;
        if (!source.IsEmpty)
        {
            source.Span.CopyTo(bytes);
            index = source.Length;
            bytes[index++] = (byte)';';
        }

        "platform="u8.CopyTo(bytes.AsSpan(index));
        index += "platform="u8.Length;
        node.Platform.Span.CopyTo(bytes.AsSpan(index));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateSourceId(Utf8Slice name, Utf8Slice version)
    {
        var bytes = new byte[name.Length + 1 + version.Length];
        name.Span.CopyTo(bytes);
        bytes[name.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(name.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version, Utf8Slice platform)
    {
        var nameLength = Utf8Purl.GetEncodedLength(name.Span);
        var versionLength = Utf8Purl.GetEncodedLength(version.Span);
        var qualifierLength = platform.IsEmpty ? 0 : "?platform="u8.Length + Utf8Purl.GetEncodedLength(platform.Span);
        var bytes = new byte[PurlPrefix.Length + nameLength + 1 + versionLength + qualifierLength];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        Utf8Purl.WriteEncoded(name.Span, bytes, ref index);
        bytes[index++] = (byte)'@';
        Utf8Purl.WriteEncoded(version.Span, bytes, ref index);
        if (!platform.IsEmpty)
        {
            "?platform="u8.CopyTo(bytes.AsSpan(index));
            index += "?platform="u8.Length;
            Utf8Purl.WriteEncoded(platform.Span, bytes, ref index);
        }

        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] == (byte)' ') start++;
        while (end > start && value[end - 1] == (byte)' ') end--;
        return value[start..end];
    }

    private static Utf8Slice SliceTrimmed(byte[] source, int start, int length)
    {
        while (length > 0 && source[start] == (byte)' ') { start++; length--; }
        while (length > 0 && source[start + length - 1] == (byte)' ') length--;
        return length == 0 ? default : new Utf8Slice(source, start, length);
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var replacement = ArrayPool<T>.Shared.Rent(checked(values.Length * 2));
        values.AsSpan(0, count).CopyTo(replacement);
        ArrayPool<T>.Shared.Return(values, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = replacement;
    }

    private readonly record struct BundlerNode(
        Utf8Slice Name,
        Utf8Slice FullVersion,
        Utf8Slice Version,
        Utf8Slice Platform,
        BundlerSource Source,
        bool PublicRubyGems,
        int DependencyStart,
        int DependencyCount);

    private readonly record struct BundlerDependency(Utf8Slice Name);

    private enum BundlerSection : byte
    {
        None,
        Source,
        Platforms,
        Dependencies,
        RubyVersion,
        BundledWith,
    }

    private enum BundlerSource : byte
    {
        None,
        RubyGems,
        Git,
        Path,
    }
}
