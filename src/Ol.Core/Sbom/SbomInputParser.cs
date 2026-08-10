using Ol.Core.Licensing;
using Ol.Core.Spdx;
using System.Buffers;

using System.Text.Json;

namespace Ol.Core.Sbom;

/// <summary>
/// Parses SBOM JSON documents into dependency inventories.
/// </summary>
internal static class SbomInputParser
{
    internal static DependencyInventory ParseCycloneDxInventory(byte[] sbomUtf8, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var reader = new Utf8JsonReader(sbomUtf8.AsSpan(offset), isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var dependencyRefs = ArrayPool<DependencyEdge>.Shared.Rent(16);
        var componentCount = 0;
        var dependencyCount = 0;
        var hasRoot = false;
        var rootRef = default(Utf8Slice);
        var specVersion = default(Utf8Slice);

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("metadata"u8))
                {
                    var rootComponent = ReadCycloneDxMetadataComponent(ref reader, sbomUtf8, offset, spdxLicenseIndex);
                    if (!rootComponent.SourceId.IsEmpty)
                    {
                        hasRoot = true;
                        rootRef = rootComponent.SourceId;
                        EnsureComponentCapacity(ref components, componentCount);
                        components[componentCount] = rootComponent with { DependencyType = DependencyType.Root };
                        componentCount++;
                    }

                    continue;
                }

                if (reader.ValueTextEquals("specVersion"u8))
                {
                    specVersion = ReadUtf8Slice(ref reader, sbomUtf8, offset);
                    continue;
                }

                if (reader.ValueTextEquals("dependencies"u8))
                {
                    ReadCycloneDxDependencies(ref reader, sbomUtf8, offset, ref dependencyRefs, ref dependencyCount);
                    continue;
                }

                if (!reader.ValueTextEquals("components"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    var component = ReadCycloneDxComponent(ref reader, sbomUtf8, offset, spdxLicenseIndex);
                    EnsureComponentCapacity(ref components, componentCount);
                    components[componentCount] = component;
                    componentCount++;
                }
            }

            var result = new ScanComponent[componentCount];
            components.AsSpan(0, componentCount).CopyTo(result);
            if (hasRoot)
            {
                ApplyDependencyTypes(result, rootRef, dependencyRefs.AsSpan(0, dependencyCount));
            }

            return CreateInventory(specVersion, result, dependencyRefs.AsSpan(0, dependencyCount), retainGraph);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(dependencyRefs, clearArray: true);
        }
    }

    internal static DependencyInventory ParseSpdxInventory(byte[] sbomUtf8, int offset, SpdxLicenseIndex spdxLicenseIndex, bool retainGraph)
    {
        var reader = new Utf8JsonReader(sbomUtf8.AsSpan(offset), isFinalBlock: true, state: default);
        var components = ArrayPool<ScanComponent>.Shared.Rent(16);
        var dependencyRefs = ArrayPool<DependencyEdge>.Shared.Rent(16);
        var componentCount = 0;
        var dependencyCount = 0;
        var rootRef = default(Utf8Slice);
        var specVersion = default(Utf8Slice);

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("relationships"u8))
                {
                    ReadSpdxRelationships(ref reader, sbomUtf8, offset, ref dependencyRefs, ref dependencyCount, ref rootRef);
                    continue;
                }

                if (reader.ValueTextEquals("spdxVersion"u8))
                {
                    specVersion = ReadUtf8Slice(ref reader, sbomUtf8, offset);
                    continue;
                }

                if (!reader.ValueTextEquals("packages"u8))
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    continue;
                }

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    var component = ReadSpdxPackage(ref reader, sbomUtf8, offset, spdxLicenseIndex);
                    if (componentCount == components.Length)
                    {
                        var expanded = ArrayPool<ScanComponent>.Shared.Rent(components.Length * 2);
                        components.AsSpan(0, componentCount).CopyTo(expanded);
                        ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
                        components = expanded;
                    }

                    components[componentCount] = component;
                    componentCount++;
                }
            }

            var result = new ScanComponent[componentCount];
            components.AsSpan(0, componentCount).CopyTo(result);
            if (!rootRef.IsEmpty)
            {
                ApplyDependencyTypes(result, rootRef, dependencyRefs.AsSpan(0, dependencyCount));
            }

            return CreateInventory(specVersion, result, dependencyRefs.AsSpan(0, dependencyCount), retainGraph);
        }
        finally
        {
            ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
            ArrayPool<DependencyEdge>.Shared.Return(dependencyRefs, clearArray: true);
        }
    }

    private static ScanComponent ReadCycloneDxComponent(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var sourceId = default(Utf8Slice);
        var name = default(Utf8Slice);
        var group = default(Utf8Slice);
        var version = default(Utf8Slice);
        var purl = default(Utf8Slice);
        var repositoryUrl = default(Utf8Slice);
        var license = LicenseText.Unknown;
        var status = LicenseStatus.Unknown;
        var primaryCandidate = default(LicenseCandidate);
        var additionalCandidates = Array.Empty<LicenseCandidate>();
        var observedLicense = LicenseText.Unknown;
        var observedStatus = LicenseStatus.Unknown;
        var observedPrimary = default(LicenseCandidate);
        var observedAdditional = Array.Empty<LicenseCandidate>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("bom-ref"u8))
            {
                sourceId = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("name"u8))
            {
                name = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("group"u8))
            {
                group = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("version"u8))
            {
                version = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("purl"u8))
            {
                purl = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("externalReferences"u8))
            {
                repositoryUrl = ReadCycloneDxRepositoryUrl(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("licenses"u8))
            {
                (license, status, primaryCandidate, additionalCandidates) = ReadCycloneDxLicenses(ref reader, source, offset, spdxLicenseIndex, SbomLicenseField.CycloneDxLicenses);
                continue;
            }

            if (reader.ValueTextEquals("evidence"u8))
            {
                (observedLicense, observedStatus, observedPrimary, observedAdditional) = ReadCycloneDxEvidenceLicenses(ref reader, source, offset, spdxLicenseIndex);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        (license, status, primaryCandidate, additionalCandidates) = CombineObservedLicenses(
            license,
            status,
            primaryCandidate,
            additionalCandidates,
            observedLicense,
            observedStatus,
            observedPrimary,
            observedAdditional);

        var ecosystem = OlDefaults.PackageMetadataProviders.GetEcosystem(purl, out var packageNameIncludesNamespace);
        return CreateScanComponent(QualifyName(group, name, packageNameIncludesNamespace), version, license, ecosystem, DependencyType.Unknown, status, purl, sourceId, primaryCandidate, additionalCandidates, repositoryUrl);
    }

    /// <summary>
    /// Rejoins a CycloneDX <c>group</c> and <c>name</c> into the package name the ecosystem itself uses.
    /// </summary>
    /// <remarks>
    /// A generator that splits <c>@scope/pkg</c> into two fields leaves a name that is neither unique nor
    /// installable: three different scopes all arrive as <c>node</c>, and one of them collides with the real
    /// <c>node</c> package. Rejoining restores the identity the lockfile adapters already produce for the
    /// same dependency. Generators disagree about whether <c>name</c> repeats the group, so a name that is
    /// already qualified is kept as it is rather than qualified twice.
    /// </remarks>
    private static Utf8Slice QualifyName(Utf8Slice group, Utf8Slice name, bool packageNameIncludesNamespace)
    {
        if (!packageNameIncludesNamespace || group.IsEmpty || name.IsEmpty) return name;
        var groupSpan = group.Span;
        var nameSpan = name.Span;
        if (nameSpan.Length > groupSpan.Length
            && nameSpan[groupSpan.Length] == (byte)'/'
            && nameSpan[..groupSpan.Length].SequenceEqual(groupSpan))
        {
            return name;
        }

        var bytes = new byte[checked(groupSpan.Length + 1 + nameSpan.Length)];
        groupSpan.CopyTo(bytes);
        bytes[groupSpan.Length] = (byte)'/';
        nameSpan.CopyTo(bytes.AsSpan(groupSpan.Length + 1));
        return Utf8Slice.FromOwnedBytes(bytes);
    }

    private static ScanComponent ReadCycloneDxMetadataComponent(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return default;
        }

        var depth = reader.CurrentDepth;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("component"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    return ReadCycloneDxComponent(ref reader, source, offset, spdxLicenseIndex);
                }

                reader.Skip();
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        return default;
    }

    private static ScanComponent ReadSpdxPackage(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex)
    {
        var depth = reader.CurrentDepth;
        var sourceId = default(Utf8Slice);
        var name = default(Utf8Slice);
        var version = default(Utf8Slice);
        var purl = default(Utf8Slice);
        var repositoryUrl = default(Utf8Slice);
        var declared = default(Utf8Slice);
        var concluded = default(Utf8Slice);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("SPDXID"u8))
            {
                sourceId = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("name"u8))
            {
                name = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("versionInfo"u8))
            {
                version = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("licenseDeclared"u8))
            {
                declared = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("licenseConcluded"u8))
            {
                concluded = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            if (reader.ValueTextEquals("externalRefs"u8))
            {
                (purl, repositoryUrl) = ReadSpdxExternalReferences(ref reader, source, offset);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        var declaredCandidate = LicenseCandidateFactory.Create(
            LicenseCandidateSource.Sbom,
            LicenseCandidateKind.Declared,
            declared,
            spdxLicenseIndex,
            new LicenseEvidence(LicenseEvidenceKind.Sbom, SbomLicenseField.SpdxLicenseDeclared));
        var concludedCandidate = LicenseCandidateFactory.Create(
            LicenseCandidateSource.Sbom,
            LicenseCandidateKind.Concluded,
            concluded,
            spdxLicenseIndex,
            new LicenseEvidence(LicenseEvidenceKind.Sbom, SbomLicenseField.SpdxLicenseConcluded));
        var (license, status) = ReconcileLicenses(declaredCandidate, concludedCandidate);
        var additionalCandidates = new[] { concludedCandidate };
        return CreateScanComponent(name, version, license, OlDefaults.PackageMetadataProviders.GetEcosystem(purl), DependencyType.Unknown, status, purl, sourceId, declaredCandidate, additionalCandidates, repositoryUrl);
    }

    private static void ReadSpdxRelationships(ref Utf8JsonReader reader, byte[] source, int offset, ref DependencyEdge[] dependencies, ref int dependencyCount, ref Utf8Slice rootRef)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var element = default(Utf8Slice);
            var related = default(Utf8Slice);
            var relationshipType = SpdxRelationshipType.None;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("spdxElementId"u8))
                {
                    element = ReadUtf8Slice(ref reader, source, offset);
                    continue;
                }

                if (reader.ValueTextEquals("relationshipType"u8))
                {
                    reader.Read();
                    relationshipType = reader.TokenType == JsonTokenType.String
                        ? ValueTextEqualsAsciiIgnoreCase(ref reader, "describes") ? SpdxRelationshipType.Describes
                        : ValueTextEqualsAsciiIgnoreCase(ref reader, "depends_on") ? SpdxRelationshipType.DependsOn
                        : ValueTextEqualsAsciiIgnoreCase(ref reader, "dependency_of") ? SpdxRelationshipType.DependencyOf
                        : SpdxRelationshipType.None
                        : SpdxRelationshipType.None;
                    continue;
                }

                if (reader.ValueTextEquals("relatedSpdxElement"u8))
                {
                    related = ReadUtf8Slice(ref reader, source, offset);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }

            if (relationshipType == SpdxRelationshipType.Describes)
            {
                rootRef = related;
            }
            else if (relationshipType == SpdxRelationshipType.DependsOn)
            {
                AddDependencyEdge(ref dependencies, ref dependencyCount, element, related);
            }
            else if (relationshipType == SpdxRelationshipType.DependencyOf)
            {
                AddDependencyEdge(ref dependencies, ref dependencyCount, related, element);
            }
        }
    }

    private static (Utf8Slice Purl, Utf8Slice RepositoryUrl) ReadSpdxExternalReferences(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return default;
        }

        var purl = default(Utf8Slice);
        var repositoryUrl = default(Utf8Slice);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var referenceLocator = default(Utf8Slice);
            var isPurl = false;
            var isVcs = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("referenceType"u8))
                {
                    reader.Read();
                    isPurl = reader.TokenType == JsonTokenType.String && ValueTextEqualsAsciiIgnoreCase(ref reader, "purl");
                    isVcs = reader.TokenType == JsonTokenType.String && ValueTextEqualsAsciiIgnoreCase(ref reader, "vcs");
                    continue;
                }

                if (reader.ValueTextEquals("referenceLocator"u8))
                {
                    referenceLocator = ReadUtf8Slice(ref reader, source, offset);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }

            if (isPurl)
            {
                purl = referenceLocator;
            }

            if (isVcs)
            {
                repositoryUrl = referenceLocator;
            }
        }

        return (purl, repositoryUrl);
    }

    private static (Utf8Slice License, LicenseStatus Status, LicenseCandidate PrimaryCandidate, LicenseCandidate[] AdditionalCandidates) ReadCycloneDxLicenses(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, SbomLicenseField sbomField)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return (LicenseText.Unknown, LicenseStatus.Unknown, default, []);
        }

        var candidateBuffer = ArrayPool<LicenseCandidate>.Shared.Rent(4);
        var candidateCount = 0;
        var validCount = 0;
        var ambiguousCount = 0;
        var invalidCount = 0;
        var firstValue = default(Utf8Slice);
        var secondValue = default(Utf8Slice);

        try
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var candidate = ReadCycloneDxLicenseCandidate(ref reader, source, offset, spdxLicenseIndex, sbomField);
                if (candidateCount == candidateBuffer.Length)
                {
                    var expanded = ArrayPool<LicenseCandidate>.Shared.Rent(candidateBuffer.Length * 2);
                    candidateBuffer.AsSpan(0, candidateCount).CopyTo(expanded);
                    ArrayPool<LicenseCandidate>.Shared.Return(candidateBuffer, clearArray: true);
                    candidateBuffer = expanded;
                }

                candidateBuffer[candidateCount] = candidate;
                candidateCount++;
                if (candidate.Status == LicenseStatus.Unknown)
                {
                    continue;
                }

                if (candidate.Status == LicenseStatus.Matched)
                {
                    validCount++;
                    if (validCount == 1)
                    {
                        firstValue = candidate.Normalized;
                    }
                    else if (!firstValue.Equals(candidate.Normalized))
                    {
                        secondValue = candidate.Normalized;
                    }
                }
                else if (candidate.Status == LicenseStatus.Invalid)
                {
                    invalidCount++;
                    if (firstValue.IsEmpty)
                    {
                        firstValue = candidate.Raw;
                    }
                }
                else
                {
                    ambiguousCount++;
                    if (firstValue.IsEmpty)
                    {
                        firstValue = candidate.Raw;
                    }
                }
            }

            var primaryCandidate = candidateCount == 0 ? default : candidateBuffer[0];
            var additionalCandidates = candidateCount < 2 ? [] : candidateBuffer.AsSpan(1, candidateCount - 1).ToArray();
            if (validCount == 1)
            {
                return (firstValue, LicenseStatus.Matched, primaryCandidate, additionalCandidates);
            }

            if (validCount > 1)
            {
                // The collection resolves to no single expression, so no entry in it resolves one
                // either. Leaving each entry as a resolved claim would let a later evidence source see
                // them as separate sources disagreeing, and report a conflict this document never
                // stated. The individual claims stay readable; only what they conclude changes.
                Demote(ref primaryCandidate);
                for (var i = 0; i < additionalCandidates.Length; i++)
                {
                    Demote(ref additionalCandidates[i]);
                }

                return (secondValue.IsEmpty ? firstValue : LicenseText.Conflict(firstValue, secondValue), LicenseStatus.Ambiguous, primaryCandidate, additionalCandidates);
            }

            if (invalidCount > 0)
            {
                return (LicenseText.WithUncertainty(firstValue), LicenseStatus.Invalid, primaryCandidate, additionalCandidates);
            }

            if (ambiguousCount > 0)
            {
                return (LicenseText.WithUncertainty(firstValue), LicenseStatus.Ambiguous, primaryCandidate, additionalCandidates);
            }

            return (LicenseText.Unknown, LicenseStatus.Unknown, primaryCandidate, additionalCandidates);
        }
        finally
        {
            ArrayPool<LicenseCandidate>.Shared.Return(candidateBuffer, clearArray: true);
        }
    }

    /// <summary>Marks a resolved entry as unresolved because the collection it belongs to is.</summary>
    private static void Demote(ref LicenseCandidate candidate)
    {
        if (candidate.Status == LicenseStatus.Matched)
        {
            candidate = candidate with { Status = LicenseStatus.Ambiguous };
        }
    }

    /// <summary>Reads <c>component.evidence.licenses</c>, ignoring the other evidence collections.</summary>
    /// <remarks>
    /// <c>evidence</c> also carries identity, occurrence, and call-stack data that says nothing about a
    /// license, so this walks the object and reads only the one collection rather than parsing it whole.
    /// </remarks>
    private static (Utf8Slice License, LicenseStatus Status, LicenseCandidate PrimaryCandidate, LicenseCandidate[] AdditionalCandidates) ReadCycloneDxEvidenceLicenses(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return (LicenseText.Unknown, LicenseStatus.Unknown, default, []);
        }

        var depth = reader.CurrentDepth;
        var result = (License: LicenseText.Unknown, Status: LicenseStatus.Unknown, PrimaryCandidate: default(LicenseCandidate), AdditionalCandidates: Array.Empty<LicenseCandidate>());
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("licenses"u8))
            {
                result = ReadCycloneDxLicenses(ref reader, source, offset, spdxLicenseIndex, SbomLicenseField.CycloneDxEvidenceLicenses);
                continue;
            }

            reader.Read();
            reader.Skip();
        }

        return result;
    }

    /// <summary>
    /// Combines a detected license collection with the component's declared one.
    /// </summary>
    /// <remarks>
    /// The observed licenses contract in spdx.md. A producer writes under <c>evidence</c> when it detected a license
    /// rather than being told one, so a detection supplies a license only where nothing was declared and never
    /// replaces one. Keep a multi-entry collection as one unresolved observation: splitting it would make two
    /// detected identifiers read as two sources contradicting each other.
    /// </remarks>
    private static (Utf8Slice License, LicenseStatus Status, LicenseCandidate PrimaryCandidate, LicenseCandidate[] AdditionalCandidates) CombineObservedLicenses(
        Utf8Slice declaredLicense,
        LicenseStatus declaredStatus,
        LicenseCandidate declaredPrimary,
        LicenseCandidate[] declaredAdditional,
        Utf8Slice observedLicense,
        LicenseStatus observedStatus,
        LicenseCandidate observedPrimary,
        LicenseCandidate[] observedAdditional)
    {
        if (observedPrimary.Source == LicenseCandidateSource.None)
        {
            return (declaredLicense, declaredStatus, declaredPrimary, declaredAdditional);
        }

        var candidates = Combine(declaredPrimary, declaredAdditional, observedPrimary, observedAdditional);
        if (declaredPrimary.Source == LicenseCandidateSource.None)
        {
            return (observedLicense, observedStatus, candidates.Primary, candidates.Additional);
        }

        var (license, status) = declaredStatus == LicenseStatus.Matched && observedStatus == LicenseStatus.Matched
            ? SpdxExpressionRelation.IsAccountedFor(observedLicense.Span, declaredLicense.Span)
                ? (declaredLicense, LicenseStatus.Matched)
                : (LicenseText.Conflict(declaredLicense, observedLicense), LicenseStatus.Conflict)
            : (declaredLicense, declaredStatus);

        return (license, status, candidates.Primary, candidates.Additional);
    }

    private static (LicenseCandidate Primary, LicenseCandidate[] Additional) Combine(
        LicenseCandidate firstPrimary,
        LicenseCandidate[] firstAdditional,
        LicenseCandidate secondPrimary,
        LicenseCandidate[] secondAdditional)
    {
        if (firstPrimary.Source == LicenseCandidateSource.None)
        {
            return (secondPrimary, secondAdditional);
        }

        var additional = new LicenseCandidate[firstAdditional.Length + 1 + secondAdditional.Length];
        firstAdditional.CopyTo(additional, 0);
        additional[firstAdditional.Length] = secondPrimary;
        secondAdditional.CopyTo(additional, firstAdditional.Length + 1);
        return (firstPrimary, additional);
    }

    private static (Utf8Slice License, LicenseStatus Status) ReconcileLicenses(LicenseCandidate firstCandidate, LicenseCandidate secondCandidate)
    {
        var firstStatus = firstCandidate.Status;
        var secondStatus = secondCandidate.Status;
        var firstValue = firstCandidate.Normalized;
        var secondValue = secondCandidate.Normalized;

        if (firstStatus == LicenseStatus.Matched && secondStatus == LicenseStatus.Matched)
        {
            if (firstValue.Equals(secondValue))
            {
                return (firstValue, LicenseStatus.Matched);
            }

            return (LicenseText.Conflict(firstValue, secondValue), LicenseStatus.Conflict);
        }

        if (firstStatus == LicenseStatus.Matched)
        {
            return (firstValue, LicenseStatus.Matched);
        }

        if (secondStatus == LicenseStatus.Matched)
        {
            return (secondValue, LicenseStatus.Matched);
        }

        if (firstStatus == LicenseStatus.Invalid)
        {
            return (LicenseText.WithUncertainty(firstCandidate.Raw), LicenseStatus.Invalid);
        }

        if (secondStatus == LicenseStatus.Invalid)
        {
            return (LicenseText.WithUncertainty(secondCandidate.Raw), LicenseStatus.Invalid);
        }

        if (firstStatus == LicenseStatus.Ambiguous)
        {
            return (LicenseText.WithUncertainty(firstCandidate.Raw), LicenseStatus.Ambiguous);
        }

        if (secondStatus == LicenseStatus.Ambiguous)
        {
            return (LicenseText.WithUncertainty(secondCandidate.Raw), LicenseStatus.Ambiguous);
        }

        return (LicenseText.Unknown, LicenseStatus.Unknown);
    }

    private static ScanComponent CreateScanComponent(
        Utf8Slice name,
        Utf8Slice version,
        Utf8Slice license,
        string ecosystem,
        DependencyType dependencyType,
        LicenseStatus status,
        Utf8Slice purl,
        Utf8Slice sourceId,
        LicenseCandidate primaryCandidate,
        LicenseCandidate[] additionalCandidates,
        Utf8Slice repositoryUrl = default)
    {
        var hasDeprecatedWarning = false;
        if (primaryCandidate.Source != LicenseCandidateSource.None)
        {
            hasDeprecatedWarning = primaryCandidate.Deprecated;
        }

        for (var i = 0; i < additionalCandidates.Length; i++)
        {
            hasDeprecatedWarning |= additionalCandidates[i].Deprecated;
        }

        return new ScanComponent(
            name,
            version,
            license,
            ecosystem,
            dependencyType,
            status,
            purl,
            sourceId,
            primaryCandidate,
            additionalCandidates,
            hasDeprecatedWarning ? ["deprecated_spdx_identifier"] : [],
            repositoryUrl);
    }

    private static Utf8Slice ReadCycloneDxRepositoryUrl(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return default;
        }

        var repositoryUrl = default(Utf8Slice);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var url = default(Utf8Slice);
            var isVcs = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    isVcs = reader.TokenType == JsonTokenType.String && ValueTextEqualsAsciiIgnoreCase(ref reader, "vcs");
                    continue;
                }

                if (reader.ValueTextEquals("url"u8))
                {
                    url = ReadUtf8Slice(ref reader, source, offset);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }

            if (isVcs && !url.IsEmpty) repositoryUrl = url;
        }

        return repositoryUrl;
    }

    private static LicenseCandidate ReadCycloneDxLicenseCandidate(ref Utf8JsonReader reader, byte[] source, int offset, SpdxLicenseIndex spdxLicenseIndex, SbomLicenseField sbomField)
    {
        var depth = reader.CurrentDepth;
        var kind = LicenseCandidateKind.None;
        var candidate = LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, kind, default(Utf8Slice), spdxLicenseIndex);
        var acknowledgement = LicenseAcknowledgement.None;
        var declaredUrl = default(Utf8Slice);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("id"u8) || reader.ValueTextEquals("expression"u8) || reader.ValueTextEquals("name"u8))
            {
                kind = reader.ValueTextEquals("id"u8) ? LicenseCandidateKind.Id : reader.ValueTextEquals("expression"u8) ? LicenseCandidateKind.Expression : LicenseCandidateKind.Name;
                reader.Read();
                candidate = CreateLicenseCandidate(ref reader, source, offset, kind, spdxLicenseIndex);
                continue;
            }

            if (reader.ValueTextEquals("acknowledgement"u8))
            {
                reader.Read();
                acknowledgement = reader.TokenType == JsonTokenType.String
                    ? ValueTextEqualsAsciiIgnoreCase(ref reader, "declared") ? LicenseAcknowledgement.Declared
                    : ValueTextEqualsAsciiIgnoreCase(ref reader, "concluded") ? LicenseAcknowledgement.Concluded
                    : LicenseAcknowledgement.None
                    : LicenseAcknowledgement.None;
                continue;
            }

            // `url` names where the license is, not what it is, so it is retained as a declared
            // location and never read as a license value.
            if (reader.ValueTextEquals("url"u8))
            {
                declaredUrl = ReadUtf8Slice(ref reader, source, offset);
                continue;
            }

            reader.Read();
        }

        DeclaredLicenseReference? reference = declaredUrl.IsEmpty ? null : new(DeclaredLicenseReferenceKind.Location, declaredUrl);
        return LicenseCandidateFactory.ResolveDeclaredLocation(
            candidate with { Evidence = new LicenseEvidence(LicenseEvidenceKind.Sbom, sbomField, acknowledgement, DeclaredReference: reference) },
            spdxLicenseIndex);
    }

    private static LicenseCandidate CreateLicenseCandidate(ref Utf8JsonReader reader, byte[] source, int offset, LicenseCandidateKind kind, SpdxLicenseIndex spdxLicenseIndex)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, kind, default(Utf8Slice), spdxLicenseIndex);
        }

        return !reader.HasValueSequence && !reader.ValueIsEscaped
            ? LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, kind, CreateValueSlice(ref reader, source, offset), spdxLicenseIndex)
            : LicenseCandidateFactory.Create(LicenseCandidateSource.Sbom, kind, Utf8Slice.FromString(reader.GetString() ?? string.Empty), spdxLicenseIndex);
    }

    private static Utf8Slice ReadUtf8Slice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.String
            ? CreateValueSlice(ref reader, source, offset)
            : default;
    }

    private static Utf8Slice CreateValueSlice(ref Utf8JsonReader reader, byte[] source, int offset)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return Utf8Slice.FromString(reader.GetString() ?? string.Empty);
        }

        return new Utf8Slice(source, checked(offset + (int)reader.TokenStartIndex + 1), reader.ValueSpan.Length);
    }

    private static bool ValueTextEqualsAsciiIgnoreCase(ref Utf8JsonReader reader, string expectedLowercase)
    {
        if (reader.HasValueSequence || reader.ValueIsEscaped)
        {
            return string.Equals(reader.GetString(), expectedLowercase, StringComparison.OrdinalIgnoreCase);
        }

        var value = reader.ValueSpan;
        if (value.Length != expectedLowercase.Length)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (current is >= (byte)'A' and <= (byte)'Z')
            {
                current = (byte)(current | 0x20);
            }

            if (current != expectedLowercase[i])
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureComponentCapacity(ref ScanComponent[] components, int componentCount)
    {
        if (componentCount != components.Length)
        {
            return;
        }

        var expanded = ArrayPool<ScanComponent>.Shared.Rent(components.Length * 2);
        components.AsSpan(0, componentCount).CopyTo(expanded);
        ArrayPool<ScanComponent>.Shared.Return(components, clearArray: true);
        components = expanded;
    }

    private static void ReadCycloneDxDependencies(ref Utf8JsonReader reader, byte[] source, int offset, ref DependencyEdge[] dependencies, ref int dependencyCount)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var depth = reader.CurrentDepth;
            var parentRef = default(Utf8Slice);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == depth)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("ref"u8))
                {
                    parentRef = ReadUtf8Slice(ref reader, source, offset);
                    continue;
                }

                if (reader.ValueTextEquals("dependsOn"u8))
                {
                    ReadCycloneDxDependsOn(ref reader, source, offset, parentRef, ref dependencies, ref dependencyCount);
                    continue;
                }

                reader.Read();
                reader.Skip();
            }
        }
    }

    private static void ReadCycloneDxDependsOn(ref Utf8JsonReader reader, byte[] source, int offset, Utf8Slice parentRef, ref DependencyEdge[] dependencies, ref int dependencyCount)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                continue;
            }

            if (dependencyCount == dependencies.Length)
            {
                var expanded = ArrayPool<DependencyEdge>.Shared.Rent(dependencies.Length * 2);
                dependencies.AsSpan(0, dependencyCount).CopyTo(expanded);
                ArrayPool<DependencyEdge>.Shared.Return(dependencies, clearArray: true);
                dependencies = expanded;
            }

            dependencies[dependencyCount] = new DependencyEdge(parentRef, CreateValueSlice(ref reader, source, offset));
            dependencyCount++;
        }
    }

    private static void AddDependencyEdge(ref DependencyEdge[] dependencies, ref int dependencyCount, Utf8Slice parentRef, Utf8Slice childRef)
    {
        if (parentRef.IsEmpty || childRef.IsEmpty)
        {
            return;
        }

        if (dependencyCount == dependencies.Length)
        {
            var expanded = ArrayPool<DependencyEdge>.Shared.Rent(dependencies.Length * 2);
            dependencies.AsSpan(0, dependencyCount).CopyTo(expanded);
            ArrayPool<DependencyEdge>.Shared.Return(dependencies, clearArray: true);
            dependencies = expanded;
        }

        dependencies[dependencyCount] = new DependencyEdge(parentRef, childRef);
        dependencyCount++;
    }

    private static DependencyInventory CreateInventory(Utf8Slice specVersion, ScanComponent[] components, ReadOnlySpan<DependencyEdge> dependencyRefs, bool retainGraph)
    {
        var input = new ScanInputDescriptor(default, default, string.Empty, string.Empty, specVersion);
        if (!retainGraph)
        {
            return new DependencyInventory(input, [], components, [], []);
        }

        var occurrences = new DependencyOccurrence[components.Length];
        for (var i = 0; i < occurrences.Length; i++)
        {
            occurrences[i] = new DependencyOccurrence(DependencyOccurrence.UnspecifiedContext, i);
        }

        var edges = ProjectDependencyEdges(components, dependencyRefs);
        return new DependencyInventory(input, [], components, occurrences, edges);
    }

    private static Core.DependencyEdge[] ProjectDependencyEdges(ReadOnlySpan<ScanComponent> components, ReadOnlySpan<DependencyEdge> dependencyRefs)
    {
        if (dependencyRefs.IsEmpty || components.IsEmpty)
        {
            return [];
        }

        var resolved = ArrayPool<Core.DependencyEdge>.Shared.Rent(dependencyRefs.Length);
        var resolvedCount = 0;
        try
        {
            for (var i = 0; i < dependencyRefs.Length; i++)
            {
                var from = FindComponentIndex(components, dependencyRefs[i].ParentRef);
                var to = FindComponentIndex(components, dependencyRefs[i].ChildRef);
                if (from >= 0 && to >= 0)
                {
                    resolved[resolvedCount++] = new Core.DependencyEdge(DependencyOccurrence.UnspecifiedContext, from, to);
                }
            }

            if (resolvedCount == 0)
            {
                return [];
            }

            var result = new Core.DependencyEdge[resolvedCount];
            resolved.AsSpan(0, resolvedCount).CopyTo(result);
            return result;
        }
        finally
        {
            ArrayPool<Core.DependencyEdge>.Shared.Return(resolved);
        }
    }

    private static int FindComponentIndex(ReadOnlySpan<ScanComponent> components, Utf8Slice sourceId)
    {
        if (sourceId.IsEmpty)
        {
            return -1;
        }

        for (var i = 0; i < components.Length; i++)
        {
            if (components[i].SourceId.Equals(sourceId))
            {
                return i;
            }
        }

        return -1;
    }

    private static void ApplyDependencyTypes(ScanComponent[] components, Utf8Slice rootRef, ReadOnlySpan<DependencyEdge> dependencies)
    {
        var directRefs = ArrayPool<Utf8Slice>.Shared.Rent(dependencies.Length);
        var transitiveRefs = ArrayPool<Utf8Slice>.Shared.Rent(dependencies.Length);
        var queue = ArrayPool<Utf8Slice>.Shared.Rent(dependencies.Length);
        var directCount = 0;
        var transitiveCount = 0;
        var queueHead = 0;
        var queueTail = 0;

        try
        {
            for (var i = 0; i < dependencies.Length; i++)
            {
                if (dependencies[i].ParentRef.Equals(rootRef) && !Contains(directRefs, directCount, dependencies[i].ChildRef))
                {
                    directRefs[directCount++] = dependencies[i].ChildRef;
                    queue[queueTail++] = dependencies[i].ChildRef;
                }
            }

            while (queueHead != queueTail)
            {
                var current = queue[queueHead++];
                for (var i = 0; i < dependencies.Length; i++)
                {
                    var child = dependencies[i].ChildRef;
                    if (!dependencies[i].ParentRef.Equals(current) || Contains(directRefs, directCount, child) || Contains(transitiveRefs, transitiveCount, child))
                    {
                        continue;
                    }

                    transitiveRefs[transitiveCount++] = child;
                    queue[queueTail++] = child;
                }
            }

            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component.SourceId.Equals(rootRef))
                {
                    components[i] = component with { DependencyType = DependencyType.Root };
                }
                else if (Contains(directRefs, directCount, component.SourceId))
                {
                    components[i] = component with { DependencyType = DependencyType.Direct };
                }
                else if (Contains(transitiveRefs, transitiveCount, component.SourceId))
                {
                    components[i] = component with { DependencyType = DependencyType.Transitive };
                }
            }
        }
        finally
        {
            ArrayPool<Utf8Slice>.Shared.Return(directRefs);
            ArrayPool<Utf8Slice>.Shared.Return(transitiveRefs);
            ArrayPool<Utf8Slice>.Shared.Return(queue);
        }
    }

    private static bool Contains(ReadOnlySpan<Utf8Slice> values, int count, Utf8Slice value)
    {
        for (var i = 0; i < count; i++)
        {
            if (values[i].Equals(value))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct DependencyEdge(Utf8Slice ParentRef, Utf8Slice ChildRef);

    private enum SpdxRelationshipType : byte
    {
        None,
        Describes,
        DependsOn,
        DependencyOf,
    }
}
