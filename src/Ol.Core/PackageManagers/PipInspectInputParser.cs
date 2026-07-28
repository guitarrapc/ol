using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class PipInspectInputParser
{
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:pypi/"u8;
    private static readonly Utf8Slice ProjectOrigin = Utf8Slice.FromOwnedBytes("pip-environment"u8.ToArray());
    private static readonly Utf8Slice DirectSourceVariant = Utf8Slice.FromOwnedBytes("source=direct"u8.ToArray());
    private static readonly LicenseEvidence LicenseExpressionEvidence = new(
        LicenseEvidenceKind.DependencyInput,
        DependencyInput: new DependencyInputEvidence("pip-inspect", "installed[].metadata.license_expression"));
    private static readonly LicenseEvidence LicenseEvidence = new(
        LicenseEvidenceKind.DependencyInput,
        DependencyInput: new DependencyInputEvidence("pip-inspect", "installed[].metadata.license"));

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var nodes = ArrayPool<PythonNode>.Shared.Rent(16);
        var requirements = ArrayPool<PythonRequirement>.Shared.Rent(32);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var edges = ArrayPool<DependencyEdge>.Shared.Rent(32);
        var nodeCount = 0;
        var requirementCount = 0;
        var componentCount = 0;
        var edgeCount = 0;
        int[]? nodeIndexes = null;
        try
        {
            ReadReport(
                source,
                offset,
                ref nodes,
                ref nodeCount,
                ref requirements,
                ref requirementCount,
                out var specificationVersion,
                out var pipVersion,
                out var implementation,
                out var pythonVersion,
                out var platform,
                out var architecture);
            var indexCapacity = GetIndexCapacity(nodeCount);
            nodeIndexes = ArrayPool<int>.Shared.Rent(indexCapacity);
            nodeIndexes.AsSpan(0, indexCapacity).Fill(-1);
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                if (!AddNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, nodeIndex))
                {
                    throw new JsonException("pip inspect installed distribution names must be unique after normalization.");
                }

                EnsureCapacity(ref components, componentCount);
                components[componentCount++] = CreateComponent(nodes[nodeIndex], spdxLicenseIndex);
            }

            var contexts = new[]
            {
                new DependencyResolutionContext(ProjectOrigin, pythonVersion, implementation, platform, architecture, CreatePipVariant(pipVersion)),
            };
            if (!retainGraph)
            {
                return new DependencyInventory(
                    new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                    contexts,
                    components.AsSpan(0, componentCount).ToArray(),
                    [],
                    [],
                    []);
            }

            var occurrences = new DependencyOccurrence[nodeCount];
            var variantCount = 0;
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                occurrences[nodeIndex] = new DependencyOccurrence(0, nodeIndex);
                if (nodes[nodeIndex].HasDirectUrl) variantCount++;
                if (nodes[nodeIndex].Requested != 1) continue;
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(0, DependencyOccurrence.ContextRoot, nodeIndex);
            }

            for (var requirementIndex = 0; requirementIndex < requirementCount; requirementIndex++)
            {
                var requirement = requirements[requirementIndex];
                if (!TryGetNodeIndex(nodes.AsSpan(0, nodeCount), nodeIndexes, indexCapacity, requirement.Name.Span, out var targetIndex)) continue;
                if (ContainsEdge(edges.AsSpan(0, edgeCount), requirement.OwnerIndex, targetIndex)) continue;
                EnsureCapacity(ref edges, edgeCount);
                edges[edgeCount++] = new DependencyEdge(0, requirement.OwnerIndex, targetIndex);
            }

            var variants = variantCount == 0 ? [] : new DependencyOccurrenceVariant[variantCount];
            var variantIndex = 0;
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                if (nodes[nodeIndex].HasDirectUrl) variants[variantIndex++] = new DependencyOccurrenceVariant(nodeIndex, DirectSourceVariant);
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, specificationVersion),
                contexts,
                components.AsSpan(0, componentCount).ToArray(),
                occurrences,
                edges.AsSpan(0, edgeCount).ToArray(),
                variants);
        }
        finally
        {
            ArrayPool<PythonNode>.Shared.Return(nodes, clearArray: true);
            ArrayPool<PythonRequirement>.Shared.Return(requirements, clearArray: true);
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(edges);
            if (nodeIndexes is not null) ArrayPool<int>.Shared.Return(nodeIndexes);
        }
    }

    private static void ReadReport(
        byte[] source,
        int offset,
        ref PythonNode[] nodes,
        ref int nodeCount,
        ref PythonRequirement[] requirements,
        ref int requirementCount,
        out Utf8Slice specificationVersion,
        out Utf8Slice pipVersion,
        out Utf8Slice implementation,
        out Utf8Slice pythonVersion,
        out Utf8Slice platform,
        out Utf8Slice architecture)
    {
        specificationVersion = default;
        pipVersion = default;
        implementation = default;
        pythonVersion = default;
        platform = default;
        architecture = default;
        var foundInstalled = false;
        var foundEnvironment = false;
        var reader = new Utf8JsonReader(source.AsSpan(offset), isFinalBlock: true, state: default);
        RequireToken(ref reader, JsonTokenType.StartObject, "pip inspect must be a JSON object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "pip inspect contains an invalid property.");
            if (reader.ValueTextEquals("version"u8)) specificationVersion = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("pip_version"u8)) pipVersion = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("installed"u8))
            {
                if (foundInstalled) throw new JsonException("pip inspect installed cannot be repeated.");
                foundInstalled = true;
                RequireRead(ref reader, "pip inspect installed must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "pip inspect installed must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    EnsureCapacity(ref nodes, nodeCount);
                    nodes[nodeCount] = ReadInstalledItem(ref reader, source, offset, nodeCount, ref requirements, ref requirementCount);
                    nodeCount++;
                }
            }
            else if (reader.ValueTextEquals("environment"u8))
            {
                if (foundEnvironment) throw new JsonException("pip inspect environment cannot be repeated.");
                foundEnvironment = true;
                ReadEnvironment(ref reader, source, offset, out implementation, out pythonVersion, out platform, out architecture);
            }
            else
            {
                RequireRead(ref reader, "pip inspect properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (!specificationVersion.Span.SequenceEqual("1"u8)
            || pipVersion.IsEmpty
            || !foundInstalled
            || !foundEnvironment
            || implementation.IsEmpty
            || pythonVersion.IsEmpty
            || platform.IsEmpty
            || architecture.IsEmpty)
        {
            throw new JsonException("pip inspect requires format version 1, pip_version, installed, and a complete resolution environment.");
        }
    }

    private static PythonNode ReadInstalledItem(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        int ownerIndex,
        ref PythonRequirement[] requirements,
        ref int requirementCount)
    {
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "pip inspect installed entries must be objects.");
        Utf8Slice name = default;
        Utf8Slice version = default;
        Utf8Slice licenseExpression = default;
        Utf8Slice license = default;
        var requested = (sbyte)-1;
        var hasDirectUrl = false;
        var isPipInstaller = false;
        var foundMetadata = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "pip inspect installed entries contain an invalid property.");
            if (reader.ValueTextEquals("metadata"u8))
            {
                if (foundMetadata) throw new JsonException("pip inspect installed metadata cannot be repeated.");
                foundMetadata = true;
                ReadMetadata(ref reader, source, offset, ownerIndex, ref requirements, ref requirementCount, out name, out version, out licenseExpression, out license);
            }
            else if (reader.ValueTextEquals("requested"u8)) requested = ReadBoolean(ref reader) ? (sbyte)1 : (sbyte)0;
            else if (reader.ValueTextEquals("installer"u8)) isPipInstaller = ReadString(ref reader, source, offset).Span.SequenceEqual("pip"u8);
            else if (reader.ValueTextEquals("direct_url"u8))
            {
                RequireRead(ref reader, "pip inspect direct_url must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartObject, "pip inspect direct_url must be an object.");
                hasDirectUrl = true;
                SkipCurrent(ref reader);
            }
            else
            {
                RequireRead(ref reader, "pip inspect installed properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        ValidateName(name.Span);
        if (!foundMetadata || version.IsEmpty) throw new JsonException("pip inspect installed metadata requires a valid name and version.");
        return new PythonNode(name, version, licenseExpression, license, requested, hasDirectUrl, isPipInstaller);
    }

    private static void ReadMetadata(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        int ownerIndex,
        ref PythonRequirement[] requirements,
        ref int requirementCount,
        out Utf8Slice name,
        out Utf8Slice version,
        out Utf8Slice licenseExpression,
        out Utf8Slice license)
    {
        RequireRead(ref reader, "pip inspect metadata must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "pip inspect metadata must be an object.");
        name = default;
        version = default;
        licenseExpression = default;
        license = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "pip inspect metadata contains an invalid property.");
            if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("license_expression"u8)) licenseExpression = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("license"u8))
            {
                RequireRead(ref reader, "pip inspect metadata license must have a value.");
                if (reader.TokenType == JsonTokenType.String) license = CreateValueSlice(ref reader, source, offset);
                else SkipCurrent(ref reader);
            }
            else if (reader.ValueTextEquals("requires_dist"u8))
            {
                RequireRead(ref reader, "pip inspect metadata requires_dist must have a value.");
                RequireCurrentToken(ref reader, JsonTokenType.StartArray, "pip inspect metadata requires_dist must be an array.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    RequireCurrentToken(ref reader, JsonTokenType.String, "pip inspect requires_dist entries must be strings.");
                    var requirement = CreateValueSlice(ref reader, source, offset);
                    if (!TryReadUnconditionalRequirementName(requirement, out var dependencyName)) continue;
                    EnsureCapacity(ref requirements, requirementCount);
                    requirements[requirementCount++] = new PythonRequirement(ownerIndex, dependencyName);
                }
            }
            else
            {
                RequireRead(ref reader, "pip inspect metadata properties must have values.");
                SkipCurrent(ref reader);
            }
        }
    }

    private static void ReadEnvironment(
        ref Utf8JsonReader reader,
        byte[] source,
        int offset,
        out Utf8Slice implementation,
        out Utf8Slice pythonVersion,
        out Utf8Slice platform,
        out Utf8Slice architecture)
    {
        RequireRead(ref reader, "pip inspect environment must have a value.");
        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "pip inspect environment must be an object.");
        implementation = default;
        pythonVersion = default;
        platform = default;
        architecture = default;
        Utf8Slice shortPythonVersion = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "pip inspect environment contains an invalid property.");
            if (reader.ValueTextEquals("implementation_name"u8)) implementation = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("python_full_version"u8)) pythonVersion = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("python_version"u8)) shortPythonVersion = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("sys_platform"u8)) platform = ReadString(ref reader, source, offset);
            else if (reader.ValueTextEquals("platform_machine"u8)) architecture = ReadString(ref reader, source, offset);
            else
            {
                RequireRead(ref reader, "pip inspect environment properties must have values.");
                SkipCurrent(ref reader);
            }
        }

        if (pythonVersion.IsEmpty) pythonVersion = shortPythonVersion;
    }

    private static ScanComponent CreateComponent(PythonNode node, SpdxLicenseIndex spdxLicenseIndex)
    {
        var rawLicense = node.LicenseExpression.IsEmpty ? node.License : node.LicenseExpression;
        var evidence = node.LicenseExpression.IsEmpty ? LicenseEvidence : LicenseExpressionEvidence;
        var candidate = rawLicense.IsEmpty
            ? default
            : LicenseCandidateFactory.Create(LicenseCandidateSource.DependencyInput, LicenseCandidateKind.License, rawLicense, spdxLicenseIndex, evidence);
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
            "pypi",
            node.Requested == 1 ? DependencyType.Direct
                : node.Requested == 0 && node.IsPipInstaller ? DependencyType.Transitive
                : DependencyType.Unknown,
            status,
            node.HasDirectUrl ? default : CreatePurl(node.Name, node.Version),
            CreateIdentity(node.Name, node.Version),
            candidate,
            [],
            candidate.Warnings.ToStrings());
    }

    private static bool TryReadUnconditionalRequirementName(Utf8Slice requirement, out Utf8Slice name)
    {
        var value = requirement.Span;
        if (value.Contains((byte)';'))
        {
            name = default;
            return false;
        }

        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t') start++;
        var end = start;
        while (end < value.Length && IsNameByte(value[end])) end++;
        if (end == start)
        {
            name = default;
            return false;
        }

        name = requirement.Slice(start, end - start);
        ValidateName(name.Span);
        return true;
    }

    private static Utf8Slice CreatePipVariant(Utf8Slice pipVersion)
    {
        var bytes = new byte[4 + pipVersion.Length];
        "pip="u8.CopyTo(bytes);
        pipVersion.Span.CopyTo(bytes.AsSpan(4));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateIdentity(Utf8Slice name, Utf8Slice version)
    {
        var normalizedLength = GetNormalizedLength(name.Span);
        var bytes = new byte[normalizedLength + 1 + version.Length];
        var index = WriteNormalized(name.Span, bytes);
        bytes[index++] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(index));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreatePurl(Utf8Slice name, Utf8Slice version)
    {
        var normalizedLength = GetNormalizedLength(name.Span);
        var bytes = new byte[PurlPrefix.Length + normalizedLength + 1 + version.Length];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length + WriteNormalized(name.Span, bytes.AsSpan(PurlPrefix.Length));
        bytes[index++] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(index));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void ValidateName(ReadOnlySpan<byte> name)
    {
        if (name.IsEmpty || !IsAsciiAlphaNumeric(name[0]) || !IsAsciiAlphaNumeric(name[^1]))
        {
            throw new JsonException("Python distribution names must use the PyPA name format.");
        }

        for (var index = 1; index < name.Length - 1; index++)
        {
            if (!IsNameByte(name[index])) throw new JsonException("Python distribution names must use the PyPA name format.");
        }
    }

    private static int GetNormalizedLength(ReadOnlySpan<byte> name)
    {
        var length = 0;
        var separator = false;
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current is (byte)'-' or (byte)'_' or (byte)'.')
            {
                if (!separator) length++;
                separator = true;
            }
            else
            {
                length++;
                separator = false;
            }
        }

        return length;
    }

    private static int WriteNormalized(ReadOnlySpan<byte> name, Span<byte> destination)
    {
        var written = 0;
        var separator = false;
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current is (byte)'-' or (byte)'_' or (byte)'.')
            {
                if (!separator) destination[written++] = (byte)'-';
                separator = true;
            }
            else
            {
                destination[written++] = current is >= (byte)'A' and <= (byte)'Z' ? (byte)(current | 0x20) : current;
                separator = false;
            }
        }

        return written;
    }

    private static bool ContainsEdge(ReadOnlySpan<DependencyEdge> edges, int from, int to)
    {
        for (var index = 0; index < edges.Length; index++)
        {
            if (edges[index].FromOccurrenceIndex == from && edges[index].ToOccurrenceIndex == to) return true;
        }

        return false;
    }

    private static int GetIndexCapacity(int count)
    {
        var capacity = 2;
        while (capacity < count * 2) capacity *= 2;
        return capacity;
    }

    private static bool AddNodeIndex(ReadOnlySpan<PythonNode> nodes, Span<int> indexes, int capacity, int nodeIndex)
    {
        var name = nodes[nodeIndex].Name.Span;
        var slot = (int)(HashNormalized(name) & (uint)(capacity - 1));
        while (indexes[slot] >= 0)
        {
            if (NormalizedEquals(nodes[indexes[slot]].Name.Span, name)) return false;
            slot = (slot + 1) & (capacity - 1);
        }

        indexes[slot] = nodeIndex;
        return true;
    }

    private static bool TryGetNodeIndex(ReadOnlySpan<PythonNode> nodes, ReadOnlySpan<int> indexes, int capacity, ReadOnlySpan<byte> name, out int nodeIndex)
    {
        var slot = (int)(HashNormalized(name) & (uint)(capacity - 1));
        while ((nodeIndex = indexes[slot]) >= 0)
        {
            if (NormalizedEquals(nodes[nodeIndex].Name.Span, name)) return true;
            slot = (slot + 1) & (capacity - 1);
        }

        return false;
    }

    private static uint HashNormalized(ReadOnlySpan<byte> name)
    {
        var hash = 2166136261u;
        var separator = false;
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current is (byte)'-' or (byte)'_' or (byte)'.')
            {
                if (separator) continue;
                current = (byte)'-';
                separator = true;
            }
            else
            {
                if (current is >= (byte)'A' and <= (byte)'Z') current = (byte)(current | 0x20);
                separator = false;
            }

            hash = (hash ^ current) * 16777619;
        }

        return hash;
    }

    private static bool NormalizedEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (TryReadNormalized(left, ref leftIndex, out var leftValue))
        {
            if (!TryReadNormalized(right, ref rightIndex, out var rightValue) || leftValue != rightValue) return false;
        }

        return !TryReadNormalized(right, ref rightIndex, out _);
    }

    private static bool TryReadNormalized(ReadOnlySpan<byte> value, ref int index, out byte normalized)
    {
        if (index >= value.Length)
        {
            normalized = 0;
            return false;
        }

        var current = value[index++];
        if (current is (byte)'-' or (byte)'_' or (byte)'.')
        {
            while (index < value.Length && value[index] is (byte)'-' or (byte)'_' or (byte)'.') index++;
            normalized = (byte)'-';
            return true;
        }

        normalized = current is >= (byte)'A' and <= (byte)'Z' ? (byte)(current | 0x20) : current;
        return true;
    }

    private static bool IsNameByte(byte value) => IsAsciiAlphaNumeric(value) || value is (byte)'-' or (byte)'_' or (byte)'.';

    private static bool IsAsciiAlphaNumeric(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9';

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "pip inspect string fields must have values.");
        RequireCurrentToken(ref reader, JsonTokenType.String, "pip inspect fields must use their documented JSON types.");
        return CreateValueSlice(ref reader, source, offset);
    }

    private static bool ReadBoolean(ref Utf8JsonReader reader)
    {
        RequireRead(ref reader, "pip inspect boolean fields must have values.");
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        throw new JsonException("pip inspect fields must use their documented JSON types.");
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("pip inspect contains an incomplete JSON value.");
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

    private readonly record struct PythonNode(
        Utf8Slice Name,
        Utf8Slice Version,
        Utf8Slice LicenseExpression,
        Utf8Slice License,
        sbyte Requested,
        bool HasDirectUrl,
        bool IsPipInstaller);

    private readonly record struct PythonRequirement(int OwnerIndex, Utf8Slice Name);
}
