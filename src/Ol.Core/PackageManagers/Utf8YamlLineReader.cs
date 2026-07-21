using System.Text.Json;

namespace Ol.Core.PackageManagers;

internal readonly record struct Utf8YamlLine(
    int Indent,
    Utf8Slice Key,
    Utf8Slice Value,
    bool HasValue,
    bool IsSequence);

internal ref struct Utf8YamlLineReader
{
    private readonly byte[] source;
    private readonly int sourceOffset;
    private readonly ReadOnlySpan<byte> input;
    private int position;

    internal Utf8YamlLineReader(byte[] source, int offset)
    {
        this.source = source;
        sourceOffset = offset;
        input = source.AsSpan(offset);
        position = 0;
    }

    internal bool Read(out Utf8YamlLine line)
    {
        while (position < input.Length)
        {
            var lineStart = position;
            var newline = input[position..].IndexOf((byte)'\n');
            var lineEnd = newline < 0 ? input.Length : position + newline;
            position = newline < 0 ? input.Length : lineEnd + 1;
            if (lineEnd > lineStart && input[lineEnd - 1] == (byte)'\r')
            {
                lineEnd--;
            }

            var bytes = input[lineStart..lineEnd];
            var indent = 0;
            while (indent < bytes.Length && bytes[indent] == (byte)' ')
            {
                indent++;
            }

            if (indent < bytes.Length && bytes[indent] == (byte)'\t')
            {
                throw new JsonException("Dependency lock YAML indentation cannot contain tabs.");
            }

            bytes = bytes[indent..];
            if (bytes.IsEmpty || bytes[0] == (byte)'#' || bytes.SequenceEqual("---"u8))
            {
                continue;
            }

            var contentLength = FindCommentStart(bytes);
            bytes = TrimEnd(bytes[..contentLength]);
            if (bytes.IsEmpty)
            {
                continue;
            }

            if (bytes[0] == (byte)'-' && (bytes.Length == 1 || bytes[1] == (byte)' '))
            {
                var valueStart = bytes.Length == 1 ? bytes.Length : 2;
                var value = Unquote(lineStart + indent + valueStart, Trim(bytes[valueStart..]));
                line = new Utf8YamlLine(indent, default, value, !value.IsEmpty, true);
                return true;
            }

            var colon = FindMappingColon(bytes);
            if (colon < 0)
            {
                throw new JsonException("Dependency lock YAML contains an unsupported scalar line.");
            }

            var rawKey = TrimEnd(bytes[..colon]);
            var rawValue = Trim(bytes[(colon + 1)..]);
            if (rawKey.IsEmpty)
            {
                throw new JsonException("Dependency lock YAML contains an empty mapping key.");
            }

            var keyStart = lineStart + indent;
            var valueStartInBytes = colon + 1;
            while (valueStartInBytes < bytes.Length && bytes[valueStartInBytes] == (byte)' ')
            {
                valueStartInBytes++;
            }
            line = new Utf8YamlLine(
                indent,
                Unquote(keyStart, rawKey),
                rawValue.IsEmpty ? default : Unquote(lineStart + indent + valueStartInBytes, rawValue),
                !rawValue.IsEmpty,
                false);
            return true;
        }

        line = default;
        return false;
    }

    private Utf8Slice Unquote(int relativeStart, ReadOnlySpan<byte> value)
    {
        if (value.Length >= 2 && ((value[0] == (byte)'\'' && value[^1] == (byte)'\'') || (value[0] == (byte)'"' && value[^1] == (byte)'"')))
        {
            var quote = value[0];
            var content = value[1..^1];
            if (quote == (byte)'\'' ? content.IndexOf("''"u8) >= 0 : content.Contains((byte)'\\'))
            {
                throw new JsonException("Escaped YAML lock scalars are not supported.");
            }

            return content.IsEmpty ? default : new Utf8Slice(source, sourceOffset + relativeStart + 1, content.Length);
        }

        return value.IsEmpty ? default : new Utf8Slice(source, sourceOffset + relativeStart, value.Length);
    }

    private static int FindMappingColon(ReadOnlySpan<byte> value)
    {
        var quote = (byte)0;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quote != 0)
            {
                if (current == quote)
                {
                    if (quote == (byte)'\'' && i + 1 < value.Length && value[i + 1] == quote)
                    {
                        i++;
                    }
                    else
                    {
                        quote = 0;
                    }
                }
                else if (quote == (byte)'"' && current == (byte)'\\')
                {
                    i++;
                }

                continue;
            }

            if (current is (byte)'\'' or (byte)'"')
            {
                quote = current;
            }
            else if (current == (byte)':')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindCommentStart(ReadOnlySpan<byte> value)
    {
        var quote = (byte)0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quote != 0)
            {
                if (current == quote)
                {
                    if (quote == (byte)'\'' && i + 1 < value.Length && value[i + 1] == quote)
                    {
                        i++;
                    }
                    else
                    {
                        quote = 0;
                    }
                }
                else if (quote == (byte)'"' && current == (byte)'\\')
                {
                    i++;
                }

                continue;
            }

            switch (current)
            {
                case (byte)'\'':
                case (byte)'"':
                    quote = current;
                    break;
                case (byte)'[':
                    bracketDepth++;
                    break;
                case (byte)']':
                    bracketDepth--;
                    break;
                case (byte)'{':
                    braceDepth++;
                    break;
                case (byte)'}':
                    braceDepth--;
                    break;
                case (byte)'#' when bracketDepth == 0 && braceDepth == 0 && (i == 0 || value[i - 1] == (byte)' '):
                    return i;
            }
        }

        return value.Length;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == (byte)' ') start++;
        return TrimEnd(value[start..]);
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> value)
    {
        var length = value.Length;
        while (length > 0 && value[length - 1] == (byte)' ') length--;
        return value[..length];
    }
}
