using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal static class SwiftPackageResolvedInputParser
{
    private const byte IdentityField = 1 << 0;
    private const byte KindField = 1 << 1;
    private const byte LocationField = 1 << 2;
    private const byte StateField = 1 << 3;
    private const byte RequiredPinFields = IdentityField | KindField | LocationField | StateField;
    private static readonly Utf8Slice ProjectOrigin = Utf8Slice.FromOwnedBytes("Package.resolved"u8.ToArray());
    private static readonly Utf8Slice FormatVersion2 = Utf8Slice.FromOwnedBytes("2"u8.ToArray());
    private static readonly Utf8Slice FormatVersion3 = Utf8Slice.FromOwnedBytes("3"u8.ToArray());
    private static ReadOnlySpan<byte> PurlPrefix => "pkg:swift/"u8;

    internal static DependencyInventory Parse(byte[] source, int offset, SpdxLicenseIndex _, bool retainGraph)
    {
        var pins = ArrayPool<SwiftPin>.Shared.Rent(16);
        var pinCount = 0;
        try
        {
            var reader = new Utf8JsonReader(source.AsSpan(offset), new JsonReaderOptions { MaxDepth = 32 });
            RequireToken(ref reader, JsonTokenType.StartObject, "Package.resolved must be a JSON object.");
            var formatVersion = 0;
            var foundVersion = false;
            var foundPins = false;
            var foundOriginHash = false;
            var originHash = default(Utf8Slice);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Package.resolved must contain JSON properties.");
                if (reader.ValueTextEquals("version"u8))
                {
                    if (foundVersion) throw new JsonException("Package.resolved version cannot be repeated.");
                    foundVersion = true;
                    RequireRead(ref reader, "Package.resolved version must have a value.");
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out formatVersion) || formatVersion is not (2 or 3))
                    {
                        throw new JsonException("Package.resolved version must be 2 or 3.");
                    }
                }
                else if (reader.ValueTextEquals("pins"u8))
                {
                    if (foundPins) throw new JsonException("Package.resolved pins cannot be repeated.");
                    foundPins = true;
                    RequireToken(ref reader, JsonTokenType.StartArray, "Package.resolved pins must be an array.");
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        RequireCurrentToken(ref reader, JsonTokenType.StartObject, "Package.resolved pins must be objects.");
                        EnsureCapacity(ref pins, pinCount);
                        pins[pinCount++] = ReadPin(ref reader, source, offset);
                    }
                    RequireCurrentToken(ref reader, JsonTokenType.EndArray, "Package.resolved pins array is incomplete.");
                }
                else if (reader.ValueTextEquals("originHash"u8))
                {
                    if (foundOriginHash) throw new JsonException("Package.resolved originHash cannot be repeated.");
                    foundOriginHash = true;
                    originHash = ReadNullableString(ref reader, source, offset);
                }
                else
                {
                    RequireRead(ref reader, "Package.resolved property must have a value.");
                    SkipCurrent(ref reader);
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || !foundVersion || !foundPins)
            {
                throw new JsonException("Package.resolved requires one version and pins array.");
            }
            if (formatVersion == 2 && foundOriginHash) throw new JsonException("Package.resolved version 2 cannot contain originHash.");

            ValidateUniquePins(pins.AsSpan(0, pinCount));
            var components = new ScanComponent[pinCount];
            var occurrences = retainGraph ? new DependencyOccurrence[pinCount] : [];
            var variants = retainGraph && pinCount != 0 ? new DependencyOccurrenceVariant[pinCount] : [];
            for (var pinIndex = 0; pinIndex < pinCount; pinIndex++)
            {
                var pin = pins[pinIndex];
                var resolved = !pin.Version.IsEmpty ? pin.Version : !pin.Branch.IsEmpty ? pin.Branch : pin.Revision;
                var purl = pin.Kind.Span.SequenceEqual("remoteSourceControl"u8)
                    ? CreatePurl(pin.Location, resolved)
                    : default;
                components[pinIndex] = new ScanComponent(
                    pin.Identity,
                    resolved,
                    default,
                    purl.IsEmpty ? "-" : "swift",
                    DependencyType.Unknown,
                    LicenseStatus.Unknown,
                    purl,
                    CreateSourceId(pin.Identity, resolved),
                    default,
                    [],
                    LicenseCandidateWarnings.None,
                    purl.IsEmpty ? default : pin.Location);
                if (retainGraph)
                {
                    occurrences[pinIndex] = new DependencyOccurrence(0, pinIndex);
                    variants[pinIndex] = new DependencyOccurrenceVariant(pinIndex, CreateVariant(pin.Kind, pin.Revision));
                }
            }

            return new DependencyInventory(
                new ScanInputDescriptor(default, default, string.Empty, string.Empty, formatVersion == 2 ? FormatVersion2 : FormatVersion3),
                [new DependencyResolutionContext(ProjectOrigin, default, default, default, default, CreateOriginVariant(originHash))],
                components,
                occurrences,
                [],
                variants);
        }
        finally
        {
            ArrayPool<SwiftPin>.Shared.Return(pins, clearArray: true);
        }
    }

    private static SwiftPin ReadPin(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        var pin = default(SwiftPin);
        byte fields = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Package.resolved pin must contain JSON properties.");
            if (reader.ValueTextEquals("identity"u8))
            {
                RequireUnique(ref fields, IdentityField);
                pin.Identity = ReadString(ref reader, source, offset, "Package.resolved pin identity must be a string.");
            }
            else if (reader.ValueTextEquals("kind"u8))
            {
                RequireUnique(ref fields, KindField);
                pin.Kind = ReadString(ref reader, source, offset, "Package.resolved pin kind must be a string.");
            }
            else if (reader.ValueTextEquals("location"u8))
            {
                RequireUnique(ref fields, LocationField);
                pin.Location = ReadString(ref reader, source, offset, "Package.resolved pin location must be a string.");
            }
            else if (reader.ValueTextEquals("state"u8))
            {
                RequireUnique(ref fields, StateField);
                ReadState(ref reader, source, offset, ref pin);
            }
            else
            {
                RequireRead(ref reader, "Package.resolved pin property must have a value.");
                SkipCurrent(ref reader);
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject
            || fields != RequiredPinFields
            || pin.Identity.IsEmpty
            || pin.Kind.IsEmpty
            || (pin.Location.IsEmpty && !pin.Kind.Span.SequenceEqual("registry"u8))
            || (pin.Version.IsEmpty && pin.Branch.IsEmpty && pin.Revision.IsEmpty)
            || (!pin.Version.IsEmpty && !pin.Branch.IsEmpty)
            || (!pin.Branch.IsEmpty && pin.Revision.IsEmpty)
            || (!pin.Kind.Span.SequenceEqual("remoteSourceControl"u8)
                && !pin.Kind.Span.SequenceEqual("localSourceControl"u8)
                && !pin.Kind.Span.SequenceEqual("registry"u8)))
        {
            throw new JsonException("Package.resolved pins require a supported identity, kind, location, and resolved state.");
        }

        ValidateIdentity(pin.Identity.Span);
        return pin;
    }

    private static void ReadState(ref Utf8JsonReader reader, byte[] source, int offset, ref SwiftPin pin)
    {
        RequireToken(ref reader, JsonTokenType.StartObject, "Package.resolved pin state must be an object.");
        byte fields = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            RequireCurrentToken(ref reader, JsonTokenType.PropertyName, "Package.resolved pin state must contain JSON properties.");
            if (reader.ValueTextEquals("version"u8))
            {
                RequireUnique(ref fields, 1);
                pin.Version = ReadNullableString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("branch"u8))
            {
                RequireUnique(ref fields, 2);
                pin.Branch = ReadNullableString(ref reader, source, offset);
            }
            else if (reader.ValueTextEquals("revision"u8))
            {
                RequireUnique(ref fields, 4);
                pin.Revision = ReadNullableString(ref reader, source, offset);
            }
            else
            {
                RequireRead(ref reader, "Package.resolved pin state property must have a value.");
                SkipCurrent(ref reader);
            }
        }
        RequireCurrentToken(ref reader, JsonTokenType.EndObject, "Package.resolved pin state is incomplete.");
    }

    private static void ValidateUniquePins(ReadOnlySpan<SwiftPin> pins)
    {
        var capacity = 2;
        if (pins.Length > 1 << 29) throw new JsonException("Package.resolved contains too many pins.");
        while (capacity < pins.Length * 2) capacity *= 2;
        var indexes = ArrayPool<int>.Shared.Rent(capacity);
        try
        {
            indexes.AsSpan(0, capacity).Fill(-1);
            for (var pinIndex = 0; pinIndex < pins.Length; pinIndex++)
            {
                var slot = (int)(Hash(pins[pinIndex].Identity.Span) & (uint)(capacity - 1));
                while (indexes[slot] >= 0)
                {
                    if (pins[indexes[slot]].Identity.Equals(pins[pinIndex].Identity))
                    {
                        throw new JsonException("Package.resolved cannot contain duplicate pin identities.");
                    }
                    slot = (slot + 1) & (capacity - 1);
                }
                indexes[slot] = pinIndex;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(indexes);
        }
    }

    private static Utf8Slice CreatePurl(Utf8Slice location, Utf8Slice version)
    {
        var value = location.Span;
        var scheme = value.StartsWith("https://"u8) ? 8 : value.StartsWith("http://"u8) ? 7 : 0;
        if (scheme == 0) return default;
        value = value[scheme..];
        if (value.IndexOfAny((byte)'?', (byte)'#') >= 0) return default;
        while (!value.IsEmpty && value[^1] == (byte)'/') value = value[..^1];
        if (value.EndsWith(".git"u8)) value = value[..^4];
        var slash = value.IndexOf((byte)'/');
        if (slash <= 0
            || slash == value.Length - 1
            || value[..slash].Contains((byte)'@')
            || value[(slash + 1)..].IndexOf((byte)'/') < 0)
        {
            return default;
        }
        var encodedLength = GetEncodedLength(value, keepSlash: true);
        var bytes = new byte[checked(PurlPrefix.Length + encodedLength + 1 + GetEncodedLength(version.Span, keepSlash: false))];
        PurlPrefix.CopyTo(bytes);
        var index = PurlPrefix.Length;
        WriteEncoded(value, bytes, ref index, keepSlash: true);
        bytes[index++] = (byte)'@';
        WriteEncoded(version.Span, bytes, ref index, keepSlash: false);
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateSourceId(Utf8Slice identity, Utf8Slice version)
    {
        var bytes = new byte[checked(identity.Length + 1 + version.Length)];
        identity.Span.CopyTo(bytes);
        bytes[identity.Length] = (byte)'@';
        version.Span.CopyTo(bytes.AsSpan(identity.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateVariant(Utf8Slice kind, Utf8Slice revision)
    {
        var length = checked("kind=".Length + kind.Length + (revision.IsEmpty ? 0 : ";revision=".Length + revision.Length));
        var bytes = new byte[length];
        "kind="u8.CopyTo(bytes);
        kind.Span.CopyTo(bytes.AsSpan("kind=".Length));
        if (!revision.IsEmpty)
        {
            var index = "kind=".Length + kind.Length;
            ";revision="u8.CopyTo(bytes.AsSpan(index));
            revision.Span.CopyTo(bytes.AsSpan(index + ";revision=".Length));
        }
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static Utf8Slice CreateOriginVariant(Utf8Slice originHash)
    {
        if (originHash.IsEmpty) return default;
        var bytes = new byte[checked("origin-hash=".Length + originHash.Length)];
        "origin-hash="u8.CopyTo(bytes);
        originHash.Span.CopyTo(bytes.AsSpan("origin-hash=".Length));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static void ValidateIdentity(ReadOnlySpan<byte> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] <= 0x20 || value[index] is (byte)'/' or (byte)'@')
            {
                throw new JsonException("Package.resolved pin identity contains an unsupported character.");
            }
        }
    }

    private static int GetEncodedLength(ReadOnlySpan<byte> value, bool keepSlash)
    {
        var length = 0;
        for (var index = 0; index < value.Length; index++) length = checked(length + (IsPurlSafe(value[index], keepSlash) ? 1 : 3));
        return length;
    }

    private static void WriteEncoded(ReadOnlySpan<byte> value, Span<byte> destination, ref int index, bool keepSlash)
    {
        ReadOnlySpan<byte> hex = "0123456789ABCDEF"u8;
        for (var valueIndex = 0; valueIndex < value.Length; valueIndex++)
        {
            var item = value[valueIndex];
            if (IsPurlSafe(item, keepSlash)) destination[index++] = item;
            else
            {
                destination[index++] = (byte)'%';
                destination[index++] = hex[item >> 4];
                destination[index++] = hex[item & 0x0f];
            }
        }
    }

    private static bool IsPurlSafe(byte value, bool keepSlash)
        => value is >= (byte)'a' and <= (byte)'z'
        || value is >= (byte)'A' and <= (byte)'Z'
        || value is >= (byte)'0' and <= (byte)'9'
        || value is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~'
        || (keepSlash && value == (byte)'/');

    private static uint Hash(ReadOnlySpan<byte> value)
    {
        var hash = 2166136261u;
        for (var index = 0; index < value.Length; index++) hash = (hash ^ value[index]) * 16777619;
        return hash;
    }

    private static Utf8Slice ReadString(ref Utf8JsonReader reader, byte[] source, int offset, string message)
    {
        RequireRead(ref reader, message);
        RequireCurrentToken(ref reader, JsonTokenType.String, message);
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static Utf8Slice ReadNullableString(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        RequireRead(ref reader, "Package.resolved optional string field must have a value.");
        if (reader.TokenType == JsonTokenType.Null) return default;
        RequireCurrentToken(ref reader, JsonTokenType.String, "Package.resolved optional string values must be strings or null.");
        if (reader.HasValueSequence || reader.ValueIsEscaped) return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static void RequireUnique(ref byte fields, byte field)
    {
        if ((fields & field) != 0) throw new JsonException("Package.resolved properties cannot be repeated.");
        fields |= field;
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

    private static void SkipCurrent(ref Utf8JsonReader reader)
    {
        if (!reader.TrySkip()) throw new JsonException("Package.resolved contains an incomplete JSON value.");
    }

    private static void EnsureCapacity<T>(ref T[] values, int count)
    {
        if (count < values.Length) return;
        var expanded = ArrayPool<T>.Shared.Rent(values.Length * 2);
        values.AsSpan(0, count).CopyTo(expanded);
        ArrayPool<T>.Shared.Return(values, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        values = expanded;
    }

    private struct SwiftPin
    {
        public Utf8Slice Identity;
        public Utf8Slice Kind;
        public Utf8Slice Location;
        public Utf8Slice Version;
        public Utf8Slice Branch;
        public Utf8Slice Revision;
    }
}
