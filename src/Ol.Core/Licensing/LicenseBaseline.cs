using System.Buffers;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ol.Core.Licensing;

/// <summary>Represents one raw license claim retained for review inside a baseline entry.</summary>
/// <param name="Source">The evidence source token.</param>
/// <param name="Kind">The evidence kind token.</param>
/// <param name="Raw">The raw claim, truncated for display when overlong.</param>
/// <param name="Truncated">Whether <paramref name="Raw"/> was shortened.</param>
public readonly record struct LicenseBaselineEvidence(string Source, string Kind, string Raw, bool Truncated);

/// <summary>Represents one acknowledged unresolved component.</summary>
/// <param name="Ecosystem">The package ecosystem, when known.</param>
/// <param name="Name">The component name.</param>
/// <param name="Version">The component version.</param>
/// <param name="Purl">The versioned package URL, when available.</param>
/// <param name="Status">The acknowledged unresolved status token.</param>
/// <param name="Evidence">The raw claims that produced the status.</param>
/// <param name="Fingerprint">The lowercase hex SHA-256 over the status and untruncated claims.</param>
public sealed record LicenseBaselineEntry(
    string Ecosystem,
    string Name,
    string Version,
    string Purl,
    string Status,
    LicenseBaselineEvidence[] Evidence,
    string Fingerprint);

/// <summary>
/// Holds the unresolved components a reviewer has already accepted, so that only newly unresolved
/// components fail a policy check. Acknowledgement removes a violation; it never alters evidence.
/// </summary>
public sealed class LicenseBaseline
{
    /// <summary>The persisted schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The maximum raw claim length retained in the file. The fingerprint covers the untruncated value.</summary>
    public const int MaxRawLength = 200;

    private const byte FieldSeparator = 0x1f;
    private const byte RecordSeparator = 0x1e;

    private readonly FrozenSet<string> acknowledged;

    private LicenseBaseline(FrozenSet<string> acknowledged) => this.acknowledged = acknowledged;

    /// <summary>Gets the number of acknowledged entries.</summary>
    public int Count => acknowledged.Count;

    /// <summary>Determines whether a component matches an entry by identity and unchanged evidence.</summary>
    public bool IsAcknowledged(in ScanComponent component)
        => acknowledged.Count != 0 && acknowledged.Contains(BuildLookupKey(component, ComputeFingerprint(component)));

    /// <summary>
    /// Computes the fingerprint that makes an acknowledgement expire by itself. It covers the status and
    /// every raw claim, so a version bump, a corrected registry record, or a changed license file drops
    /// the entry and the component fails again until it is reviewed anew.
    /// </summary>
    public static string ComputeFingerprint(in ScanComponent component)
    {
        var candidateCount = component.CandidateCount;
        var buffer = new ArrayBufferWriter<byte>(64 + (candidateCount * 32));
        Write(buffer, component.Status.ToUtf8());
        buffer.Write([RecordSeparator]);

        // Sorting keeps the fingerprint independent of evidence completion order.
        var candidates = candidateCount == 0 ? [] : new LicenseCandidate[candidateCount];
        for (var i = 0; i < candidateCount; i++) candidates[i] = component.GetCandidate(i);
        Array.Sort(candidates, CandidateComparison);

        for (var i = 0; i < candidateCount; i++)
        {
            var candidate = candidates[i];
            Write(buffer, candidate.Source.ToUtf8());
            buffer.Write([FieldSeparator]);
            Write(buffer, candidate.Kind.ToUtf8());
            buffer.Write([FieldSeparator]);
            Write(buffer, candidate.Raw.Span);
            buffer.Write([RecordSeparator]);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buffer.WrittenSpan, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Creates an in-memory baseline from entries, so a freshly written snapshot can be applied without re-reading it.</summary>
    public static LicenseBaseline FromEntries(ReadOnlySpan<LicenseBaselineEntry> entries)
    {
        var keys = new HashSet<string>(entries.Length, StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            keys.Add(BuildLookupKey(entry.Ecosystem, entry.Name, entry.Version, entry.Purl, entry.Fingerprint));
        }

        return new LicenseBaseline(keys.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>Builds the deterministic snapshot of every non-root component the policy allows to be acknowledged.</summary>
    public static LicenseBaselineEntry[] CreateEntries(ReadOnlySpan<ScanComponent> components, LicenseAllowPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (components.IsEmpty) return [];

        var entries = new List<LicenseBaselineEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < components.Length; i++)
        {
            ref readonly var component = ref components[i];
            if (component.DependencyType == DependencyType.Root) continue;
            if (!policy.CanAcknowledge(component)) continue;

            var fingerprint = ComputeFingerprint(component);
            if (!seen.Add(BuildLookupKey(component, fingerprint))) continue;

            entries.Add(CreateEntry(component, fingerprint));
        }

        var result = entries.ToArray();
        Array.Sort(result, CompareEntries);
        return result;
    }

    /// <summary>
    /// Writes the baseline as deterministic UTF-8 JSON. No generation timestamp is written so that
    /// regenerating an unchanged baseline produces no diff.
    /// </summary>
    public static byte[] Serialize(ReadOnlySpan<LicenseBaselineEntry> entries, string toolVersion, string licenseListVersion)
    {
        var buffer = new ArrayBufferWriter<byte>(256 + (entries.Length * 192));
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion"u8, SchemaVersion);

            writer.WriteStartObject("tool"u8);
            writer.WriteString("version"u8, toolVersion);
            writer.WriteEndObject();

            writer.WriteStartObject("spdx"u8);
            writer.WriteString("licenseListVersion"u8, licenseListVersion);
            writer.WriteEndObject();

            writer.WriteStartArray("acknowledged"u8);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                writer.WriteStartObject();
                if (entry.Ecosystem.Length != 0) writer.WriteString("ecosystem"u8, entry.Ecosystem);
                writer.WriteString("name"u8, entry.Name);
                writer.WriteString("version"u8, entry.Version);
                if (entry.Purl.Length != 0) writer.WriteString("purl"u8, entry.Purl);
                writer.WriteString("status"u8, entry.Status);

                writer.WriteStartArray("evidence"u8);
                for (var j = 0; j < entry.Evidence.Length; j++)
                {
                    var evidence = entry.Evidence[j];
                    writer.WriteStartObject();
                    writer.WriteString("source"u8, evidence.Source);
                    writer.WriteString("kind"u8, evidence.Kind);
                    writer.WriteString("raw"u8, evidence.Raw);
                    if (evidence.Truncated) writer.WriteBoolean("truncated"u8, true);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteString("fingerprint"u8, entry.Fingerprint);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses a persisted baseline. An unusable document is an error rather than a silently empty
    /// baseline, so a mistyped path is reported instead of changing which components fail.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out LicenseBaseline baseline, out string error)
    {
        baseline = null!;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                error = "The baseline must be a JSON object.";
                return false;
            }

            var schemaVersion = -1;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("schemaVersion"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out schemaVersion))
                    {
                        error = "The baseline schemaVersion must be a number.";
                        return false;
                    }
                }
                else if (reader.ValueTextEquals("acknowledged"u8))
                {
                    if (!TryReadEntries(ref reader, keys, out error)) return false;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (schemaVersion < 0)
            {
                error = "The baseline is missing schemaVersion.";
                return false;
            }

            if (schemaVersion != SchemaVersion)
            {
                error = $"Unsupported baseline schemaVersion {schemaVersion}; this build supports {SchemaVersion}.";
                return false;
            }

            baseline = new LicenseBaseline(keys.ToFrozenSet(StringComparer.Ordinal));
            error = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"The baseline is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadEntries(ref Utf8JsonReader reader, HashSet<string> keys, out string error)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            error = "The baseline acknowledged value must be an array.";
            return false;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string ecosystem = string.Empty, name = string.Empty, version = string.Empty, purl = string.Empty, fingerprint = string.Empty;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("ecosystem"u8)) ecosystem = ReadString(ref reader);
                else if (reader.ValueTextEquals("name"u8)) name = ReadString(ref reader);
                else if (reader.ValueTextEquals("version"u8)) version = ReadString(ref reader);
                else if (reader.ValueTextEquals("purl"u8)) purl = ReadString(ref reader);
                else if (reader.ValueTextEquals("fingerprint"u8)) fingerprint = ReadString(ref reader);
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (fingerprint.Length == 0 || (purl.Length == 0 && name.Length == 0))
            {
                error = "A baseline entry must have a fingerprint and an identity.";
                return false;
            }

            keys.Add(BuildLookupKey(ecosystem, name, version, purl, fingerprint));
        }

        error = string.Empty;
        return true;
    }

    private static string ReadString(ref Utf8JsonReader reader)
        => reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;

    private static LicenseBaselineEntry CreateEntry(in ScanComponent component, string fingerprint)
    {
        var candidateCount = component.CandidateCount;
        var evidence = candidateCount == 0 ? [] : new LicenseBaselineEvidence[candidateCount];
        for (var i = 0; i < candidateCount; i++)
        {
            var candidate = component.GetCandidate(i);
            var raw = candidate.Raw.ToString();
            var truncated = raw.Length > MaxRawLength;
            evidence[i] = new LicenseBaselineEvidence(
                Encoding.UTF8.GetString(candidate.Source.ToUtf8()),
                Encoding.UTF8.GetString(candidate.Kind.ToUtf8()),
                truncated ? raw[..MaxRawLength] : raw,
                truncated);
        }

        Array.Sort(evidence, static (left, right) =>
        {
            var result = string.CompareOrdinal(left.Source, right.Source);
            if (result != 0) return result;
            result = string.CompareOrdinal(left.Kind, right.Kind);
            return result != 0 ? result : string.CompareOrdinal(left.Raw, right.Raw);
        });

        return new LicenseBaselineEntry(
            component.Ecosystem ?? string.Empty,
            component.Name.ToString(),
            component.Version.ToString(),
            component.Purl.ToString(),
            Encoding.UTF8.GetString(component.Status.ToUtf8()),
            evidence,
            fingerprint);
    }

    private static int CompareEntries(LicenseBaselineEntry left, LicenseBaselineEntry right)
    {
        var result = string.CompareOrdinal(left.Ecosystem, right.Ecosystem);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.Name, right.Name);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.Version, right.Version);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.Purl, right.Purl);
        return result != 0 ? result : string.CompareOrdinal(left.Fingerprint, right.Fingerprint);
    }

    private static readonly Comparison<LicenseCandidate> CandidateComparison = CompareCandidates;

    private static int CompareCandidates(LicenseCandidate left, LicenseCandidate right)
    {
        var result = left.Source.ToUtf8().SequenceCompareTo(right.Source.ToUtf8());
        if (result != 0) return result;
        result = left.Kind.ToUtf8().SequenceCompareTo(right.Kind.ToUtf8());
        return result != 0 ? result : Utf8Slice.CompareOrdinal(left.Raw, right.Raw);
    }

    private static string BuildLookupKey(in ScanComponent component, string fingerprint)
        => BuildLookupKey(component.Ecosystem ?? string.Empty, component.Name.ToString(), component.Version.ToString(), component.Purl.ToString(), fingerprint);

    private static string BuildLookupKey(string ecosystem, string name, string version, string purl, string fingerprint)
        => purl.Length != 0
            ? string.Concat(purl, " ", fingerprint)
            : string.Join(' ', ecosystem, name, version, fingerprint);

    private static void Write(ArrayBufferWriter<byte> buffer, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return;
        buffer.Write(value);
    }
}
