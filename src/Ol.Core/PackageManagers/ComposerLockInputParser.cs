using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class ComposerLockInputParser
{
    private static readonly LicenseEvidence PackageLicenseEvidence = new(
        LicenseEvidenceKind.DependencyInput,
        DependencyInput: new DependencyInputEvidence("composer-lock", "packages[].license"));
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:composer/"u8;
    private static ReadOnlySpan<byte> DevVariant => "dev"u8;

    // Root requirement owners. Both are negative so the graph and edge projection continue to treat them as the
    // project root, while reachability can still tell a production `require` seed from a `require-dev` seed.
    private const int ProductionRootOwner = -1;
    private const int DevelopmentRootOwner = -2;

    internal static DependencyInventory Parse(byte[][] sources, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        if (sources.Length != 2) throw new JsonException("Composer input requires composer.json and composer.lock.");
        var nodes = ArrayPool<ComposerNode>.Shared.Rent(16);
        var requirements = ArrayPool<ComposerRequirement>.Shared.Rent(32);
        var links = ArrayPool<ComposerLink>.Shared.Rent(16);
        var licenses = ArrayPool<Utf8Slice>.Shared.Rent(16);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var occurrences = ArrayPool<DependencyOccurrence>.Shared.Rent(16);
        var variants = ArrayPool<DependencyOccurrenceVariant>.Shared.Rent(8);
        var developmentOccurrences = ArrayPool<int>.Shared.Rent(8);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        int[]? nodeIndexes = null;
        int[]? depths = null;
        int[]? queue = null;
        bool[]? productionReachable = null;
        var nodeCount = 0;
        var requirementCount = 0;
        var linkCount = 0;
        var licenseCount = 0;
        var edgeCount = 0;
        try
        {
            ReadManifest(sources[0], ref requirements, ref requirementCount, out var projectOrigin);
            ReadLock(
                sources[1],
                ref nodes,
                ref nodeCount,
                ref requirements,
                ref requirementCount,
                ref links,
                ref linkCount,
                ref licenses,
                ref licenseCount,
                out var pluginApiVersion);

            var indexCapacity = GetIndexCapacity(nodeCount);
            nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
            nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                if (!AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, nodeIndex))
                {
                    throw new JsonException("composer.lock cannot contain duplicate package names.");
                }
            }

            depths = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            queue = ArrayPool<int>.Shared.Rent(Math.Max(nodeCount, 1));
            ResolveDepths(
                nodes.AsSpan(0, nodeCount),
                requirements.AsSpan(0, requirementCount),
                links.AsSpan(0, linkCount),
                nodeIndexes,
                indexCapacity,
                depths.AsSpan(0, nodeCount),
                queue.AsSpan(0, nodeCount));

            // Production reachability validates the lock buckets: usage is not read from packages-dev membership alone.
            // The depth queue is free once ResolveDepths returns, so the reach BFS reuses it instead of renting again.
            productionReachable = ArrayPool<bool>.Shared.Rent(Math.Max(nodeCount, 1));
            ResolveProductionReach(
                nodes.AsSpan(0, nodeCount),
                requirements.AsSpan(0, requirementCount),
                links.AsSpan(0, linkCount),
                nodeIndexes,
                indexCapacity,
                productionReachable.AsSpan(0, nodeCount),
                queue.AsSpan(0, nodeCount));

            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                // A packages-dev entry that a production requirement can reach is a stale or hand-merged bundle.
                if (nodes[nodeIndex].Dev && productionReachable[nodeIndex])
                {
                    throw new JsonException("composer.lock packages-dev entry is reachable from a production requirement; the input is inconsistent.");
                }
            }

            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                EnsureCapacity(ref components, nodeIndex);
                components[nodeIndex] = CreateComponent(
                    node,
                    licenses.AsSpan(node.LicenseStart, node.LicenseCount),
                    depths[nodeIndex] switch
                    {
                        0 => DependencyType.Direct,
                        > 0 => DependencyType.Transitive,
                        _ => DependencyType.Unknown,
                    },
                    spdxLicenseIndex);
            }

            var occurrenceVariantCount = 0;
            var developmentOccurrenceCount = 0;
            if (retainGraph)
            {
                for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                {
                    EnsureCapacity(ref occurrences, nodeIndex);
                    occurrences[nodeIndex] = new DependencyOccurrence(0, nodeIndex);

                    // Development-only means the lock places it in packages-dev and no production requirement reaches it.
                    if (nodes[nodeIndex].Dev && !productionReachable[nodeIndex])
                    {
                        EnsureCapacity(ref developmentOccurrences, developmentOccurrenceCount);
                        developmentOccurrences[developmentOccurrenceCount++] = nodeIndex;
                    }

                    if (!nodes[nodeIndex].Dev) continue;
                    EnsureCapacity(ref variants, occurrenceVariantCount);
                    variants[occurrenceVariantCount++] = new DependencyOccurrenceVariant(
                        nodeIndex,
                        Utf8Slice.FromOwnedBytes(DevVariant.ToArray()));
                }

                ProjectEdges(
                    nodes.AsSpan(0, nodeCount),
                    requirements.AsSpan(0, requirementCount),
                    links.AsSpan(0, linkCount),
                    nodeIndexes,
                    indexCapacity,
                    ref edges,
                    ref edgeCount);
            }

            if (projectOrigin.IsEmpty) projectOrigin = Utf8Slice.FromOwnedBytes("composer-project"u8.ToArray());
            var contextVariant = pluginApiVersion.IsEmpty ? default : CreatePrefixedValue("plugin-api="u8, pluginApiVersion);
            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, default),
                [new DependencyResolutionContext(projectOrigin, default, default, default, default, contextVariant)],
                components.AsSpan(0, nodeCount).ToArray(),
                retainGraph ? occurrences.AsSpan(0, nodeCount).ToArray() : [],
                retainGraph ? edges.AsSpan(0, edgeCount).ToArray() : [],
                retainGraph && occurrenceVariantCount != 0 ? variants.AsSpan(0, occurrenceVariantCount).ToArray() : [],
                retainGraph && nodeCount > 0 ? [new DependencyUsageRange(0, nodeCount)] : null,
                retainGraph && developmentOccurrenceCount > 0 ? developmentOccurrences.AsSpan(0, developmentOccurrenceCount).ToArray() : null);
        }
        finally
        {
            ArrayPool<ComposerNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<ComposerRequirement>.Shared.Return(requirements, clearArray: true);
            ArrayPool<ComposerLink>.Shared.Return(links, clearArray: true);
            ArrayPool<Utf8Slice>.Shared.Return(licenses, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyOccurrence>.Shared.Return(occurrences);
            ArrayPool<DependencyOccurrenceVariant>.Shared.Return(variants, clearArray: true);
            ArrayPool<int>.Shared.Return(developmentOccurrences);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (nodeIndexes is not null) ArrayPool<int>.Shared.Return(nodeIndexes);
            if (depths is not null) ArrayPool<int>.Shared.Return(depths);
            if (queue is not null) ArrayPool<int>.Shared.Return(queue);
            if (productionReachable is not null) ArrayPool<bool>.Shared.Return(productionReachable);
        }
    }

    private static void ReadManifest(
        byte[] source,
        ref ComposerRequirement[] requirements,
        ref int requirementCount,
        out Utf8Slice projectOrigin)
    {
        var offset = HasUtf8Bom(source) ? 3 : 0;
        var reader = new Utf8JsonReader(source.AsSpan(offset));
        projectOrigin = default;
        RequireToken(ref reader, JsonTokenType.StartObject, "composer.json root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "composer.json contains an invalid root property.");
            if (reader.ValueTextEquals("name"u8))
            {
                projectOrigin = ReadString(ref reader, source, offset);
                ValidateOptionalPackageName(projectOrigin.Span);
            }
            else if (reader.ValueTextEquals("require"u8))
            {
                ReadRequirementMap(ref reader, source, offset, ProductionRootOwner, ref requirements, ref requirementCount);
            }
            else if (reader.ValueTextEquals("require-dev"u8))
            {
                ReadRequirementMap(ref reader, source, offset, DevelopmentRootOwner, ref requirements, ref requirementCount);
            }
            else
            {
                RequireRead(ref reader, "composer.json root properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new JsonException("composer.json must contain one complete JSON object.");
        }
    }

    private static void ReadLock(
        byte[] source,
        ref ComposerNode[] nodes,
        ref int nodeCount,
        ref ComposerRequirement[] requirements,
        ref int requirementCount,
        ref ComposerLink[] links,
        ref int linkCount,
        ref Utf8Slice[] licenses,
        ref int licenseCount,
        out Utf8Slice pluginApiVersion)
    {
        var offset = HasUtf8Bom(source) ? 3 : 0;
        var reader = new Utf8JsonReader(source.AsSpan(offset));
        pluginApiVersion = default;
        var foundPackages = false;
        var foundDevPackages = false;
        RequireToken(ref reader, JsonTokenType.StartObject, "composer.lock root must be an object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "composer.lock contains an invalid root property.");
            if (reader.ValueTextEquals("packages"u8))
            {
                if (foundPackages) throw new JsonException("composer.lock packages cannot be repeated.");
                foundPackages = true;
                ReadPackages(ref reader, source, offset, false, ref nodes, ref nodeCount, ref requirements, ref requirementCount, ref links, ref linkCount, ref licenses, ref licenseCount);
            }
            else if (reader.ValueTextEquals("packages-dev"u8))
            {
                if (foundDevPackages) throw new JsonException("composer.lock packages-dev cannot be repeated.");
                foundDevPackages = true;
                ReadPackages(ref reader, source, offset, true, ref nodes, ref nodeCount, ref requirements, ref requirementCount, ref links, ref linkCount, ref licenses, ref licenseCount);
            }
            else if (reader.ValueTextEquals("plugin-api-version"u8))
            {
                pluginApiVersion = ReadString(ref reader, source, offset);
            }
            else
            {
                RequireRead(ref reader, "composer.lock root properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (!foundPackages || !foundDevPackages || reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new JsonException("composer.lock requires packages and packages-dev arrays in one complete JSON object.");
        }
    }

    private static void ReadPackages(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        bool dev,
        ref ComposerNode[] nodes,
        ref int nodeCount,
        ref ComposerRequirement[] requirements,
        ref int requirementCount,
        ref ComposerLink[] links,
        ref int linkCount,
        ref Utf8Slice[] licenses,
        ref int licenseCount)
    {
        RequireRead(ref reader, "composer.lock package lists must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.StartArray, "composer.lock package lists must be arrays.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            RequireCurrentToken(ref reader, JsonTokenType.StartObject, "composer.lock package entries must be objects.");
            var nodeIndex = nodeCount;
            var requirementStart = requirementCount;
            var licenseStart = licenseCount;
            Utf8Slice name = default;
            Utf8Slice version = default;
            Utf8Slice repositoryUrl = default;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "composer.lock package entries contain an invalid property.");
                if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader, source, offset);
                else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader, source, offset);
                else if (reader.ValueTextEquals("require"u8)) ReadRequirementMap(ref reader, source, offset, nodeIndex, ref requirements, ref requirementCount);
                else if (reader.ValueTextEquals("provide"u8) || reader.ValueTextEquals("replace"u8)) ReadLinkMap(ref reader, source, offset, nodeIndex, ref links, ref linkCount);
                else if (reader.ValueTextEquals("license"u8)) ReadLicenses(ref reader, source, offset, ref licenses, ref licenseCount);
                else if (reader.ValueTextEquals("source"u8)) repositoryUrl = ReadSource(ref reader, source, offset);
                else
                {
                    RequireRead(ref reader, "composer.lock package properties must have values.");
                    SkipCurrent(ref reader);
                }
            }

            ValidatePackageName(name.Span);
            if (version.IsEmpty) throw new JsonException("composer.lock package entries require a name and version.");
            EnsureCapacity(ref nodes, nodeCount);
            nodes[nodeCount++] = new ComposerNode(
                name,
                version,
                repositoryUrl,
                requirementStart,
                requirementCount - requirementStart,
                licenseStart,
                licenseCount - licenseStart,
                dev);
        }
    }

    private static void ReadRequirementMap(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        int ownerIndex,
        ref ComposerRequirement[] requirements,
        ref int requirementCount)
    {
        RequireRead(ref reader, "Composer requirement maps must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Composer requirements must be objects.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Composer requirement names must be properties.");
            var name = CreateValueSlice(ref reader, source, offset);
            RequireRead(ref reader, "Composer requirements must have constraints.");
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Composer requirement constraints must be strings.");
            EnsureCapacity(ref requirements, requirementCount);
            requirements[requirementCount++] = new ComposerRequirement(ownerIndex, name);
        }
    }

    private static void ReadLinkMap(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        int providerIndex,
        ref ComposerLink[] links,
        ref int linkCount)
    {
        RequireRead(ref reader, "Composer provide and replace maps must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Composer provide and replace values must be objects.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Composer provide and replace names must be properties.");
            var name = CreateValueSlice(ref reader, source, offset);
            RequireRead(ref reader, "Composer provide and replace entries must have constraints.");
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Composer provide and replace constraints must be strings.");
            EnsureCapacity(ref links, linkCount);
            links[linkCount++] = new ComposerLink(providerIndex, name);
        }
    }

    private static void ReadLicenses(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        ref Utf8Slice[] licenses,
        ref int licenseCount)
    {
        RequireRead(ref reader, "Composer license must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.StartArray, "Composer package license must be an array.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            RequireCurrentToken(ref reader, JsonTokenType.String, "Composer package licenses must be strings.");
            EnsureCapacity(ref licenses, licenseCount);
            licenses[licenseCount++] = CreateValueSlice(ref reader, source, offset);
        }
    }

    private static Utf8Slice ReadSource(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Composer package source must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Composer package source must be an object.");
        Utf8Slice url = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Composer package source contains an invalid property.");
            if (reader.ValueTextEquals("url"u8)) url = ReadString(ref reader, source, offset);
            else
            {
                RequireRead(ref reader, "Composer package source properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        return url;
    }

    private static void ResolveDepths(
        ReadOnlySpan<ComposerNode> nodes,
        ReadOnlySpan<ComposerRequirement> requirements,
        ReadOnlySpan<ComposerLink> links,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        Span<int> depths,
        Span<int> queue)
    {
        depths.Fill(int.MinValue);
        var head = 0;
        var tail = 0;
        for (var requirementIndex = 0; requirementIndex < requirements.Length; requirementIndex++)
        {
            var requirement = requirements[requirementIndex];
            if (requirement.OwnerIndex >= 0 || IsPlatformRequirement(requirement.Name.Span)) continue;
            if (!TryResolveRequirement(nodes, links, nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex)) continue;
            if (depths[targetIndex] != int.MinValue) continue;
            depths[targetIndex] = 0;
            queue[tail++] = targetIndex;
        }

        while (head < tail)
        {
            var ownerIndex = queue[head++];
            var node = nodes[ownerIndex];
            var nextDepth = depths[ownerIndex] + 1;
            for (var offset = 0; offset < node.RequirementCount; offset++)
            {
                var requirement = requirements[node.RequirementStart + offset];
                if (IsPlatformRequirement(requirement.Name.Span)
                    || !TryResolveRequirement(nodes, links, nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex)
                    || depths[targetIndex] != int.MinValue)
                {
                    continue;
                }

                depths[targetIndex] = nextDepth;
                queue[tail++] = targetIndex;
            }
        }
    }

    // Marks every node reachable from the project's production `require` closure. The development classification is
    // the packages-dev bucket confirmed by absence from this set, which is fail-closed: a mislabeled production bucket
    // stays runtime, and a dev bucket that turns out production-reachable is rejected as inconsistent input.
    private static void ResolveProductionReach(
        ReadOnlySpan<ComposerNode> nodes,
        ReadOnlySpan<ComposerRequirement> requirements,
        ReadOnlySpan<ComposerLink> links,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        Span<bool> productionReachable,
        Span<int> queue)
    {
        productionReachable.Clear();
        var head = 0;
        var tail = 0;
        for (var requirementIndex = 0; requirementIndex < requirements.Length; requirementIndex++)
        {
            var requirement = requirements[requirementIndex];
            if (requirement.OwnerIndex != ProductionRootOwner || IsPlatformRequirement(requirement.Name.Span)) continue;
            if (!TryResolveRequirement(nodes, links, nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex)) continue;
            if (productionReachable[targetIndex]) continue;
            productionReachable[targetIndex] = true;
            queue[tail++] = targetIndex;
        }

        while (head < tail)
        {
            var ownerIndex = queue[head++];
            var node = nodes[ownerIndex];
            for (var offset = 0; offset < node.RequirementCount; offset++)
            {
                var requirement = requirements[node.RequirementStart + offset];
                if (IsPlatformRequirement(requirement.Name.Span)
                    || !TryResolveRequirement(nodes, links, nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex)
                    || productionReachable[targetIndex])
                {
                    continue;
                }

                productionReachable[targetIndex] = true;
                queue[tail++] = targetIndex;
            }
        }
    }

    private static void ProjectEdges(
        ReadOnlySpan<ComposerNode> nodes,
        ReadOnlySpan<ComposerRequirement> requirements,
        ReadOnlySpan<ComposerLink> links,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ref DependencyEdge[] edges,
        ref int edgeCount)
    {
        for (var requirementIndex = 0; requirementIndex < requirements.Length; requirementIndex++)
        {
            var requirement = requirements[requirementIndex];
            if (IsPlatformRequirement(requirement.Name.Span)
                || !TryResolveRequirement(nodes, links, nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex))
            {
                continue;
            }

            var fromIndex = requirement.OwnerIndex < 0 ? DependencyOccurrence.ContextRoot : requirement.OwnerIndex;
            if (fromIndex == targetIndex || ContainsEdge(edges.AsSpan(0, edgeCount), fromIndex, targetIndex)) continue;
            EnsureCapacity(ref edges, edgeCount);
            edges[edgeCount++] = new DependencyEdge(0, fromIndex, targetIndex);
        }
    }

    private static bool TryResolveRequirement(
        ReadOnlySpan<ComposerNode> nodes,
        ReadOnlySpan<ComposerLink> links,
        ReadOnlySpan<int> nodeIndexes,
        int indexCapacity,
        ReadOnlySpan<byte> name,
        out int nodeIndex)
    {
        if (TryGetNodeIndex(nodes, nodeIndexes, indexCapacity, name, out nodeIndex)) return true;
        nodeIndex = -1;
        for (var linkIndex = 0; linkIndex < links.Length; linkIndex++)
        {
            var link = links[linkIndex];
            if (!link.Name.Span.SequenceEqual(name)) continue;
            if (nodeIndex >= 0 && nodeIndex != link.ProviderIndex)
            {
                nodeIndex = -1;
                return false;
            }

            nodeIndex = link.ProviderIndex;
        }

        return nodeIndex >= 0;
    }

    private static ScanComponent CreateComponent(
        ComposerNode node,
        ReadOnlySpan<Utf8Slice> licenses,
        DependencyType dependencyType,
        SpdxLicenseIndex spdxLicenseIndex)
    {
        var rawLicense = CreateLicenseValue(licenses);
        var candidate = rawLicense.IsEmpty
            ? default
            : LicenseCandidateFactory.Create(
                LicenseCandidateSource.DependencyInput,
                LicenseCandidateKind.License,
                rawLicense,
                spdxLicenseIndex,
                PackageLicenseEvidence);
        var (license, status) = rawLicense.IsEmpty ? (default(Utf8Slice), LicenseStatus.Unknown) : candidate.Status switch
        {
            LicenseStatus.Matched => (candidate.Normalized, LicenseStatus.Matched),
            LicenseStatus.Invalid => (LicenseText.WithUncertainty(candidate.Raw), LicenseStatus.Invalid),
            LicenseStatus.Ambiguous => (LicenseText.WithUncertainty(candidate.Raw), LicenseStatus.Ambiguous),
            _ => (default(Utf8Slice), LicenseStatus.Unknown),
        };
        return new ScanComponent(
            node.Name,
            node.Version,
            license,
            "composer",
            dependencyType,
            status,
            CreatePurl(node.Name, node.Version),
            CreateIdentity(node.Name, node.Version),
            candidate,
            [],
            candidate.Warnings,
            node.RepositoryUrl);
    }

    private static Utf8Slice CreateLicenseValue(ReadOnlySpan<Utf8Slice> licenses)
    {
        if (licenses.IsEmpty) return default;
        if (licenses.Length == 1) return licenses[0];
        var length = (licenses.Length - 1) * 4;
        for (var index = 0; index < licenses.Length; index++) length += licenses[index].Length;
        var bytes = new byte[length];
        var written = 0;
        for (var index = 0; index < licenses.Length; index++)
        {
            if (index != 0)
            {
                " OR "u8.CopyTo(bytes.AsSpan(written));
                written += 4;
            }

            licenses[index].Span.CopyTo(bytes.AsSpan(written));
            written += licenses[index].Length;
        }

        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateIdentity(Utf8Slice name, Utf8Slice version)
    {
        var bytes = new byte[name.Length + 1 + version.Length];
        name.Span.CopyTo(bytes);
        bytes[name.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(name.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var encodedVersionLength = Utf8Purl.GetEncodedLength(version.Span);
        var bytes = new byte[PurlPrefix.Length + name.Length + 1 + encodedVersionLength];
        PurlPrefix.CopyTo(bytes);
        name.Span.CopyTo(bytes.AsSpan(PurlPrefix.Length));
        bytes[PurlPrefix.Length + name.Length] = (byte)'@';
        var versionIndex = 0;
        Utf8Purl.WriteEncoded(version.Span, bytes.AsSpan(PurlPrefix.Length + name.Length + 1), ref versionIndex);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePrefixedValue(ReadOnlySpan<byte> prefix, Utf8Slice value)
    {
        var bytes = new byte[prefix.Length + value.Length];
        prefix.CopyTo(bytes);
        value.Span.CopyTo(bytes.AsSpan(prefix.Length));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static bool AddNodeIndex(ReadOnlySpan<ComposerNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var name = nodes[nodeIndex].Name.Span;
        var slot = (int)(Fnv1a.Hash(name) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (nodes[indexes[slot]].Name.Span.SequenceEqual(name)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetNodeIndex(
        ReadOnlySpan<ComposerNode> nodes,
        ReadOnlySpan<int> indexes,
        int capacity,
        ReadOnlySpan<byte> name,
        out int nodeIndex)
    {
        var slot = (int)(Fnv1a.Hash(name) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (nodes[nodeIndex].Name.Span.SequenceEqual(name)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool IsPlatformRequirement(ReadOnlySpan<byte> name)
        => name.SequenceEqual("php"u8)
        || name.SequenceEqual("hhvm"u8)
        || name.StartsWith("ext-"u8)
        || name.StartsWith("lib-"u8)
        || name.StartsWith("composer-"u8);

    private static void ValidateOptionalPackageName(ReadOnlySpan<byte> name)
    {
        if (!name.IsEmpty) ValidatePackageName(name);
    }

    private static void ValidatePackageName(ReadOnlySpan<byte> name)
    {
        var slash = name.IndexOf((byte)'/');
        if (slash <= 0 || slash == name.Length - 1 || name[(slash + 1)..].Contains((byte)'/'))
        {
            throw new JsonException("Composer package names must contain one vendor/name separator.");
        }

        if (!IsAsciiAlphaNumeric(name[0])
            || !IsAsciiAlphaNumeric(name[slash - 1])
            || !IsAsciiAlphaNumeric(name[slash + 1])
            || !IsAsciiAlphaNumeric(name[^1]))
        {
            throw new JsonException("Composer package name parts must start and end with an ASCII letter or number.");
        }

        for (var index = 0; index < name.Length; index++)
        {
            var value = name[index];
            if (value == (byte)'/' || value is >= (byte)'a' and <= (byte)'z' || value is >= (byte)'0' and <= (byte)'9' || value is (byte)'-' or (byte)'_' or (byte)'.') continue;
            throw new JsonException("Composer package names must use the lowercase Composer name format.");
        }
    }

    private static bool IsAsciiAlphaNumeric(byte value)
        => value is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9';

    private static bool ContainsEdge(ReadOnlySpan<DependencyEdge> edges, int from, int to)
    {
        for (var index = 0; index < edges.Length; index++)
        {
            if (edges[index].FromOccurrenceIndex == from && edges[index].ToOccurrenceIndex == to) return true;
        }

        return false;
    }

    private static bool HasUtf8Bom(ReadOnlySpan<byte> inputUtf8)
        => inputUtf8.Length >= 3 && inputUtf8[0] == 0xEF && inputUtf8[1] == 0xBB && inputUtf8[2] == 0xBF;

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Composer string fields must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.String, "Composer fields must use their documented JSON types.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Composer input contains an incomplete JSON value.");
    }

    private static void RequireToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (!reader.Read() || reader.TokenType != expected) throw new JsonException(message);
    }

    private static void RequireRead(ref Utf8JsonReader reader, string message)
    {
        if (!reader.Read()) throw new JsonException(message);
    }

    private static void RequireCurrentToken(ref Utf8JsonReader reader, JsonTokenType expected, string message)
    {
        if (reader.TokenType != expected) throw new JsonException(message);
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private readonly record struct ComposerNode(
        Utf8Slice Name,
        Utf8Slice Version,
        Utf8Slice RepositoryUrl,
        int RequirementStart,
        int RequirementCount,
        int LicenseStart,
        int LicenseCount,
        bool Dev);

    private readonly record struct ComposerRequirement(int OwnerIndex, Utf8Slice Name);
    private readonly record struct ComposerLink(int ProviderIndex, Utf8Slice Name);
}
