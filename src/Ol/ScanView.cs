using System.Buffers;
using System.Numerics;
using Ol.Core;
using Ol.Core.Licensing;

internal static class ScanView
{
    public static void Validate(string? dependency, string sort, string? groupBy)
    {
        if (dependency is not null and not "")
        {
            var tokens = dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                throw new ArgumentException("Dependency filter must contain at least one value.");
            }

            foreach (var token in tokens)
            {
                ParseDependency(token);
            }
        }

        ParseSortFields(sort);
        if (groupBy is not null and not "")
        {
            ParseGroupFields(groupBy);
        }
    }

    public static int Apply(ScanComponent[] components, string? dependency, string sort, SortOrder sortOrder)
    {
        var count = FilterByDependency(components, dependency);
        SortView(components, null, count, sort, sortOrder);
        return count;
    }

    /// <summary>Filters and sorts <paramref name="components"/> while keeping <paramref name="usages"/> positionally aligned.</summary>
    public static int Apply(ScanComponent[] components, DependencyUsage[] usages, string? dependency, string sort, SortOrder sortOrder)
    {
        var count = FilterByDependency(components, usages, dependency);
        SortView(components, usages, count, sort, sortOrder);
        return count;
    }

    /// <summary>
    /// Orders the view by sorting component positions rather than the components themselves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A component is a 216-byte struct, and sorting the array directly moved one per swap and copied two
    /// per comparison, because a <see cref="Comparison{T}"/> takes both operands by value. Sorting an
    /// index moves four bytes per swap and reads each component through a reference, so the work the
    /// comparison does is the key comparison and nothing else. Applying the result costs one gather and
    /// one bulk copy, which is linear against the sort's n log n.
    /// </para>
    /// <para>
    /// Ties resolve to input order, which makes a view reproducible from the inventory alone instead of
    /// depending on what introsort happened to do with equal keys.
    /// </para>
    /// </remarks>
    private static void SortView(ScanComponent[] components, DependencyUsage[]? usages, int count, string sort, SortOrder sortOrder)
    {
        var keys = ParseSortFields(sort);
        if (count < 2)
        {
            return;
        }

        var order = ArrayPool<int>.Shared.Rent(count);
        var ordered = ArrayPool<ScanComponent>.Shared.Rent(count);
        var orderedUsages = usages is null ? [] : ArrayPool<DependencyUsage>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
            }

            order.AsSpan(0, count).Sort(new ComponentOrderComparer(components, keys, sortOrder));

            for (var i = 0; i < count; i++)
            {
                ordered[i] = components[order[i]];
            }

            ordered.AsSpan(0, count).CopyTo(components);
            if (usages is not null)
            {
                for (var i = 0; i < count; i++)
                {
                    orderedUsages[i] = usages[order[i]];
                }

                orderedUsages.AsSpan(0, count).CopyTo(usages);
            }
        }
        finally
        {
            if (orderedUsages.Length != 0)
            {
                ArrayPool<DependencyUsage>.Shared.Return(orderedUsages);
            }

            ArrayPool<ScanComponent>.Shared.Return(ordered, clearArray: true);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>Compares two component positions by the requested keys, reading each component by reference.</summary>
    private readonly struct ComponentOrderComparer(ScanComponent[] components, SortField[] keys, SortOrder sortOrder) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            ref var left = ref components[x];
            ref var right = ref components[y];
            for (var i = 0; i < keys.Length; i++)
            {
                var comparison = CompareByKey(in left, in right, keys[i]);
                if (comparison != 0)
                {
                    return sortOrder == SortOrder.Desc ? -comparison : comparison;
                }
            }

            return x.CompareTo(y);
        }
    }

    public static GroupRow[] Group(ScanComponent[] components, DependencyUsage[]? usages, int count, string groupBy)
    {
        var fields = ParseGroupFields(groupBy);
        if (count == 0)
        {
            return [];
        }

        // Sized so the table never rehashes and stays under half full, which is what lets the index be a
        // pooled array of row numbers instead of a dictionary that allocates per entry.
        var capacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(count * 2, 4));
        var mask = capacity - 1;
        var table = ArrayPool<int>.Shared.Rent(capacity);
        var componentGroups = ArrayPool<int>.Shared.Rent(count);
        var representatives = ArrayPool<int>.Shared.Rent(count);
        var starts = ArrayPool<int>.Shared.Rent(count);
        var counts = ArrayPool<int>.Shared.Rent(count);
        try
        {
            table.AsSpan(0, capacity).Fill(-1);
            var groupCount = 0;
            for (var i = 0; i < count; i++)
            {
                var slot = GetGroupHash(components[i], fields) & mask;
                int group;
                while (true)
                {
                    group = table[slot];
                    if (group < 0)
                    {
                        group = groupCount++;
                        table[slot] = group;
                        representatives[group] = i;
                        counts[group] = 0;
                        break;
                    }

                    if (GroupEquals(components[i], components[representatives[group]], fields))
                    {
                        break;
                    }

                    slot = (slot + 1) & mask;
                }

                componentGroups[i] = group;
                counts[group]++;
            }

            // Built from the original positions, before the components move.
            var result = new GroupRow[groupCount];
            var offset = 0;
            for (var i = 0; i < groupCount; i++)
            {
                starts[i] = offset;
                result[i] = new GroupRow(CreateGroupValues(components[representatives[i]], fields), counts[i], components.AsMemory(offset, counts[i]));
                offset += counts[i];
            }

            Reorder(components, usages, count, componentGroups, starts, groupCount);
            Array.Sort(result, CompareGroupRows);
            return result;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(counts);
            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<int>.Shared.Return(representatives);
            ArrayPool<int>.Shared.Return(componentGroups);
            ArrayPool<int>.Shared.Return(table);
        }
    }

    /// <summary>Moves the view into group order so each row can be a window onto it.</summary>
    /// <remarks>
    /// The usages travel with their components. Nothing reads them once a view is grouped, because grouped
    /// output states counts rather than per-component reachability, but leaving two positionally paired
    /// arrays disagreeing would make the next reader of that pairing wrong rather than merely unlucky.
    /// </remarks>
    private static void Reorder(
        ScanComponent[] components,
        DependencyUsage[]? usages,
        int count,
        ReadOnlySpan<int> componentGroups,
        ReadOnlySpan<int> starts,
        int groupCount)
    {
        var positions = ArrayPool<int>.Shared.Rent(groupCount);
        var ordered = ArrayPool<ScanComponent>.Shared.Rent(count);
        var orderedUsages = usages is null ? [] : ArrayPool<DependencyUsage>.Shared.Rent(count);
        try
        {
            starts[..groupCount].CopyTo(positions);
            for (var i = 0; i < count; i++)
            {
                var target = positions[componentGroups[i]]++;
                ordered[target] = components[i];
                if (usages is not null)
                {
                    orderedUsages[target] = usages[i];
                }
            }

            ordered.AsSpan(0, count).CopyTo(components);
            if (usages is not null)
            {
                orderedUsages.AsSpan(0, count).CopyTo(usages);
            }
        }
        finally
        {
            if (orderedUsages.Length != 0)
            {
                ArrayPool<DependencyUsage>.Shared.Return(orderedUsages);
            }

            ArrayPool<ScanComponent>.Shared.Return(ordered, clearArray: true);
            ArrayPool<int>.Shared.Return(positions);
        }
    }

    public static int CountExcludedUnknown(ReadOnlySpan<ScanComponent> components, string dependency)
    {
        var includesUnknown = false;
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseDependency(token) == DependencyType.Unknown)
            {
                includesUnknown = true;
                break;
            }
        }

        if (includesUnknown)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i].DependencyType == DependencyType.Unknown)
            {
                count++;
            }
        }

        return count;
    }

    private static int FilterByDependency(Span<ScanComponent> components, string? dependency)
    {
        if (dependency is null or "")
        {
            return components.Length;
        }

        Span<bool> allowed = stackalloc bool[4];
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            allowed[(int)ParseDependency(token)] = true;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (allowed[(int)components[i].DependencyType])
            {
                components[count] = components[i];
                count++;
            }
        }

        return count;
    }

    private static int FilterByDependency(Span<ScanComponent> components, Span<DependencyUsage> usages, string? dependency)
    {
        if (dependency is null or "")
        {
            return components.Length;
        }

        Span<bool> allowed = stackalloc bool[4];
        foreach (var token in dependency.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            allowed[(int)ParseDependency(token)] = true;
        }

        var count = 0;
        for (var i = 0; i < components.Length; i++)
        {
            if (allowed[(int)components[i].DependencyType])
            {
                components[count] = components[i];
                usages[count] = usages[i];
                count++;
            }
        }

        return count;
    }

    private static DependencyType ParseDependency(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "root" => DependencyType.Root,
            "direct" => DependencyType.Direct,
            "transitive" => DependencyType.Transitive,
            "unknown" => DependencyType.Unknown,
            _ => throw new ArgumentException($"Unknown dependency value: {value}"),
        };
    }

    private static SortField[] ParseSortFields(string sort)
    {
        var tokens = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Sort must contain at least one key.");
        }

        var fields = new SortField[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            fields[i] = ParseSortField(tokens[i]);
        }

        return fields;
    }

    private static SortField ParseSortField(string value)
    {
        if (value.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Name;
        }

        if (value.Equals("version", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Version;
        }

        if (value.Equals("license", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.License;
        }

        if (value.Equals("ecosystem", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Ecosystem;
        }

        if (value.Equals("dependency", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Dependency;
        }

        if (value.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Status;
        }

        if (value.Equals("purl", StringComparison.OrdinalIgnoreCase))
        {
            return SortField.Purl;
        }

        throw new ArgumentException($"Unknown sort key: {value}");
    }

    private static int CompareByKey(in ScanComponent left, in ScanComponent right, SortField key)
    {
        return key switch
        {
            SortField.Name => Utf8Slice.CompareOrdinal(left.Name, right.Name),
            SortField.Version => Utf8Slice.CompareOrdinal(left.Version, right.Version),
            SortField.License => Utf8Slice.CompareOrdinal(left.License, right.License),
            SortField.Ecosystem => string.CompareOrdinal(left.Ecosystem, right.Ecosystem),
            SortField.Dependency => left.DependencyType.CompareTo(right.DependencyType),
            SortField.Status => left.Status.CompareTo(right.Status),
            SortField.Purl => Utf8Slice.CompareOrdinal(left.Purl, right.Purl),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
    }

    private static GroupField[] ParseGroupFields(string groupBy)
    {
        var tokens = groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new ArgumentException("Group-by must contain at least one key.");
        }

        var fields = new GroupField[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            fields[i] = tokens[i].ToLowerInvariant() switch
            {
                "name" => GroupField.Name,
                "version" => GroupField.Version,
                "license" => GroupField.License,
                "ecosystem" => GroupField.Ecosystem,
                "dependency" => GroupField.Dependency,
                "status" => GroupField.Status,
                _ => throw new ArgumentException($"Unknown group key: {tokens[i]}"),
            };
        }

        return fields;
    }

    // The tokens a grouped row displays for the two fields that are not source-backed text. Static UTF-8
    // storage, so a row naming one of them holds a slice rather than a value it had to encode.
    private static readonly byte[] GroupTokens = "unknownroottransitiveerrormatchedconflictambiguousinvaliddirect"u8.ToArray();
    private static readonly Utf8Slice UnknownToken = new(GroupTokens, 0, 7);
    private static readonly Utf8Slice RootToken = new(GroupTokens, 7, 4);
    private static readonly Utf8Slice TransitiveToken = new(GroupTokens, 11, 10);
    private static readonly Utf8Slice ErrorToken = new(GroupTokens, 21, 5);
    private static readonly Utf8Slice MatchedToken = new(GroupTokens, 26, 7);
    private static readonly Utf8Slice ConflictToken = new(GroupTokens, 33, 8);
    private static readonly Utf8Slice AmbiguousToken = new(GroupTokens, 41, 9);
    private static readonly Utf8Slice InvalidToken = new(GroupTokens, 50, 7);
    private static readonly Utf8Slice DirectToken = new(GroupTokens, 57, 6);

    /// <summary>Builds one row's displayed values. Runs once per row, never once per component.</summary>
    private static Utf8Slice[] CreateGroupValues(in ScanComponent component, GroupField[] fields)
    {
        var values = new Utf8Slice[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            values[i] = fields[i] switch
            {
                GroupField.Name => component.Name,
                GroupField.Version => component.Version,
                GroupField.License => component.License,
                GroupField.Ecosystem => Utf8Slice.FromString(component.Ecosystem),
                GroupField.Dependency => GetDependencyTypeToken(component.DependencyType),
                GroupField.Status => GetStatusToken(component.Status),
                _ => throw new ArgumentOutOfRangeException(nameof(fields)),
            };
        }

        return values;
    }

    private static Utf8Slice GetDependencyTypeToken(DependencyType value) => value switch
    {
        DependencyType.Root => RootToken,
        DependencyType.Direct => DirectToken,
        DependencyType.Transitive => TransitiveToken,
        _ => UnknownToken,
    };

    private static Utf8Slice GetStatusToken(LicenseStatus value) => value switch
    {
        LicenseStatus.Matched => MatchedToken,
        LicenseStatus.Conflict => ConflictToken,
        LicenseStatus.Ambiguous => AmbiguousToken,
        LicenseStatus.Invalid => InvalidToken,
        LicenseStatus.Error => ErrorToken,
        _ => UnknownToken,
    };

    /// <summary>Hashes a component by the grouped fields alone, reading UTF-8 without decoding it.</summary>
    private static int GetGroupHash(in ScanComponent component, GroupField[] fields)
    {
        var hash = new HashCode();
        for (var i = 0; i < fields.Length; i++)
        {
            switch (fields[i])
            {
                case GroupField.Name: hash.AddBytes(component.Name.Span); break;
                case GroupField.Version: hash.AddBytes(component.Version.Span); break;
                case GroupField.License: hash.AddBytes(component.License.Span); break;
                case GroupField.Ecosystem: hash.Add(component.Ecosystem, StringComparer.Ordinal); break;
                case GroupField.Dependency: hash.Add((int)component.DependencyType); break;
                case GroupField.Status: hash.Add((int)component.Status); break;
                default: throw new ArgumentOutOfRangeException(nameof(fields));
            }
        }

        return hash.ToHashCode();
    }

    /// <summary>Reports whether two components belong in the same row, comparing the grouped fields only.</summary>
    private static bool GroupEquals(in ScanComponent left, in ScanComponent right, GroupField[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            var equal = fields[i] switch
            {
                GroupField.Name => left.Name.Equals(right.Name),
                GroupField.Version => left.Version.Equals(right.Version),
                GroupField.License => left.License.Equals(right.License),
                GroupField.Ecosystem => string.Equals(left.Ecosystem, right.Ecosystem, StringComparison.Ordinal),
                GroupField.Dependency => left.DependencyType == right.DependencyType,
                GroupField.Status => left.Status == right.Status,
                _ => throw new ArgumentOutOfRangeException(nameof(fields)),
            };
            if (!equal)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareGroupRows(GroupRow left, GroupRow right)
    {
        for (var i = 0; i < left.Values.Length; i++)
        {
            var comparison = Utf8Slice.CompareOrdinal(left.Values[i], right.Values[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}

internal enum GroupField
{
    Name,
    Version,
    License,
    Ecosystem,
    Dependency,
    Status,
}

internal enum SortField
{
    Name,
    Version,
    License,
    Ecosystem,
    Dependency,
    Status,
    Purl,
}

/// <summary>
/// One grouped row: the values that name it, how many components it holds, and which ones.
/// </summary>
/// <remarks>
/// <see cref="Components"/> is a window onto the view array rather than a copy of it. Copying cost one
/// component struct per component — the row arrays together weighed as much as the whole view — for data
/// the caller already had in hand. The window is valid for as long as that array is, which for the scan
/// command is the rest of the report.
/// </remarks>
internal readonly record struct GroupRow(Utf8Slice[] Values, int Count, ReadOnlyMemory<ScanComponent> Components);
