using Ol.Core.Licensing;

namespace Ol.Core.Reporting;

/// <summary>Identifies what changed for one component between two scan reports.</summary>
public enum ScanReportChangeKind : byte
{
    /// <summary>The component is present only in the current report.</summary>
    Added,

    /// <summary>The component is present only in the previous report.</summary>
    Removed,

    /// <summary>The component's resolved version set changed.</summary>
    VersionChanged,

    /// <summary>The reconciled status changed.</summary>
    StatusChanged,

    /// <summary>The reconciled license expression changed.</summary>
    LicenseChanged,

    /// <summary>Underlying evidence changed while the conclusion stayed the same.</summary>
    EvidenceChanged,
}

/// <summary>Describes one license-relevant change between two scan reports.</summary>
public readonly record struct ScanReportChange(
    ScanReportChangeKind Kind,
    string Ecosystem,
    string Name,
    string PreviousVersion,
    string CurrentVersion,
    string PreviousLicense,
    string CurrentLicense,
    string PreviousStatus,
    string CurrentStatus);

/// <summary>
/// Compares two persisted scan reports and reports only license-relevant changes.
/// </summary>
/// <remarks>
/// This is a pure transform over restored components. It exists so a reviewer can see what changed about
/// licensing between two runs instead of re-reading a whole report.
/// </remarks>
public static class ScanReportDiff
{
    /// <summary>Compares two component sets and returns changes in deterministic order.</summary>
    public static ScanReportChange[] Compare(ReadOnlySpan<ScanComponent> previous, ReadOnlySpan<ScanComponent> current)
    {
        var previousByKey = GroupByIdentity(previous);
        var currentByKey = GroupByIdentity(current);
        var changes = new List<ScanReportChange>();

        foreach (var (key, currentGroup) in currentByKey)
        {
            if (!previousByKey.TryGetValue(key, out var previousGroup))
            {
                changes.Add(Create(ScanReportChangeKind.Added, currentGroup[0], null, currentGroup[0]));
                continue;
            }

            CompareGroup(previousGroup, currentGroup, changes);
        }

        foreach (var (key, previousGroup) in previousByKey)
        {
            if (!currentByKey.ContainsKey(key))
            {
                changes.Add(Create(ScanReportChangeKind.Removed, previousGroup[0], previousGroup[0], null));
            }
        }

        var result = changes.ToArray();
        Array.Sort(result, CompareChanges);
        return result;
    }

    private static void CompareGroup(List<ScanComponent> previous, List<ScanComponent> current, List<ScanReportChange> changes)
    {
        var previousVersions = Versions(previous);
        var currentVersions = Versions(current);
        var versionChanged = !string.Equals(previousVersions, currentVersions, StringComparison.Ordinal);
        if (versionChanged)
        {
            changes.Add(Create(ScanReportChangeKind.VersionChanged, current[0], previous[0], current[0]) with
            {
                PreviousVersion = previousVersions,
                CurrentVersion = currentVersions,
            });
        }

        // Compare like-for-like versions so evidence drift is not hidden behind a version bump.
        for (var i = 0; i < current.Count; i++)
        {
            var currentComponent = current[i];
            var previousComponent = Find(previous, currentComponent.Version);
            if (previousComponent is not { } match) continue;

            CompareConclusions(match, currentComponent, changes);
        }

        if (versionChanged) CompareUnmatchedConclusions(previous, current, changes);
    }

    private static void CompareConclusions(
        in ScanComponent previous,
        in ScanComponent current,
        List<ScanReportChange> changes)
    {
        var statusChanged = previous.Status != current.Status;
        var licenseChanged = !previous.License.Equals(current.License);
        if (statusChanged) changes.Add(Create(ScanReportChangeKind.StatusChanged, current, previous, current));
        if (licenseChanged) changes.Add(Create(ScanReportChangeKind.LicenseChanged, current, previous, current));
        if (!statusChanged && !licenseChanged &&
            !string.Equals(LicenseBaseline.ComputeFingerprint(previous), LicenseBaseline.ComputeFingerprint(current), StringComparison.Ordinal))
        {
            changes.Add(Create(ScanReportChangeKind.EvidenceChanged, current, previous, current));
        }
    }

    private static void CompareUnmatchedConclusions(
        List<ScanComponent> previous,
        List<ScanComponent> current,
        List<ScanReportChange> changes)
    {
        var removed = new List<ScanComponent>();
        var added = new List<ScanComponent>();
        for (var i = 0; i < previous.Count; i++)
        {
            if (Find(current, previous[i].Version) is null) removed.Add(previous[i]);
        }

        for (var i = 0; i < current.Count; i++)
        {
            if (Find(previous, current[i].Version) is null) added.Add(current[i]);
        }

        var previousLicense = Licenses(removed);
        var currentLicense = Licenses(added);
        var previousStatus = Statuses(removed);
        var currentStatus = Statuses(added);
        var licenseChanged = !string.Equals(Licenses(previous), Licenses(current), StringComparison.Ordinal);
        var statusChanged = !string.Equals(Statuses(previous), Statuses(current), StringComparison.Ordinal);
        if (statusChanged)
        {
            changes.Add(CreateUnmatchedChange(
                ScanReportChangeKind.StatusChanged,
                removed,
                added,
                previousLicense,
                currentLicense,
                previousStatus,
                currentStatus));
        }

        if (licenseChanged)
        {
            changes.Add(CreateUnmatchedChange(
                ScanReportChangeKind.LicenseChanged,
                removed,
                added,
                previousLicense,
                currentLicense,
                previousStatus,
                currentStatus));
        }
    }

    private static ScanReportChange CreateUnmatchedChange(
        ScanReportChangeKind kind,
        List<ScanComponent> previous,
        List<ScanComponent> current,
        string previousLicense,
        string currentLicense,
        string previousStatus,
        string currentStatus)
    {
        var identity = current.Count != 0 ? current[0] : previous[0];
        ScanComponent? previousComponent = previous.Count != 0 ? previous[0] : null;
        ScanComponent? currentComponent = current.Count != 0 ? current[0] : null;
        return Create(kind, identity, previousComponent, currentComponent) with
        {
            PreviousVersion = VersionsOrEmpty(previous),
            CurrentVersion = VersionsOrEmpty(current),
            PreviousLicense = previousLicense,
            CurrentLicense = currentLicense,
            PreviousStatus = previousStatus,
            CurrentStatus = currentStatus,
        };
    }

    private static ScanComponent? Find(List<ScanComponent> components, Utf8Slice version)
    {
        for (var i = 0; i < components.Count; i++)
        {
            if (components[i].Version.Equals(version)) return components[i];
        }

        return null;
    }

    private static string Versions(List<ScanComponent> components)
    {
        if (components.Count == 1) return components[0].Version.ToString();

        var values = new string[components.Count];
        for (var i = 0; i < components.Count; i++) values[i] = components[i].Version.ToString();
        Array.Sort(values, StringComparer.Ordinal);
        return string.Join(", ", values);
    }

    private static string VersionsOrEmpty(List<ScanComponent> components)
        => components.Count == 0 ? string.Empty : Versions(components);

    private static string Licenses(List<ScanComponent> components)
    {
        if (components.Count == 0) return string.Empty;

        var values = new string[components.Count];
        for (var i = 0; i < components.Count; i++) values[i] = LicenseOf(components[i]);
        return JoinSortedUnique(values);
    }

    private static string Statuses(List<ScanComponent> components)
    {
        if (components.Count == 0) return string.Empty;

        var values = new string[components.Count];
        for (var i = 0; i < components.Count; i++) values[i] = StatusOf(components[i]);
        return JoinSortedUnique(values);
    }

    private static string JoinSortedUnique(string[] values)
    {
        Array.Sort(values, StringComparer.Ordinal);
        var count = 1;
        for (var i = 1; i < values.Length; i++)
        {
            if (string.Equals(values[i - 1], values[i], StringComparison.Ordinal)) continue;
            values[count++] = values[i];
        }

        return string.Join(", ", values, 0, count);
    }

    private static ScanReportChange Create(ScanReportChangeKind kind, in ScanComponent identity, in ScanComponent? previous, in ScanComponent? current)
        => new(
            kind,
            identity.Ecosystem ?? string.Empty,
            identity.Name.ToString(),
            previous?.Version.ToString() ?? string.Empty,
            current?.Version.ToString() ?? string.Empty,
            LicenseOf(previous),
            LicenseOf(current),
            StatusOf(previous),
            StatusOf(current));

    private static string LicenseOf(in ScanComponent? component)
        => component is { } value && !value.License.IsEmpty ? value.License.ToString() : string.Empty;

    private static string StatusOf(in ScanComponent? component)
        => component is { } value ? System.Text.Encoding.UTF8.GetString(value.Status.ToUtf8()) : string.Empty;

    private static Dictionary<string, List<ScanComponent>> GroupByIdentity(ReadOnlySpan<ScanComponent> components)
    {
        var result = new Dictionary<string, List<ScanComponent>>(components.Length, StringComparer.Ordinal);
        for (var i = 0; i < components.Length; i++)
        {
            var key = string.Concat(components[i].Ecosystem ?? string.Empty, " ", components[i].Name.ToString());
            if (!result.TryGetValue(key, out var group))
            {
                group = [];
                result[key] = group;
            }

            group.Add(components[i]);
        }

        return result;
    }

    private static int CompareChanges(ScanReportChange left, ScanReportChange right)
    {
        var result = string.CompareOrdinal(left.Name, right.Name);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.Ecosystem, right.Ecosystem);
        return result != 0 ? result : ((int)left.Kind).CompareTo((int)right.Kind);
    }
}
