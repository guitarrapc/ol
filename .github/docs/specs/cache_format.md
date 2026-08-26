# Evidence Cache Format Specification

This document defines the persistent JSON cache contract shared by package metadata and source repository evidence.

The cache is an Ol-managed persistence format, not a public report or a general interchange format. It is nevertheless versioned at specification level because an Ol upgrade must be able to decide whether an existing entry is compatible, stale, corrupt, or requires migration. The format also carries provenance needed to explain cached evidence and privacy properties needed to avoid exposing package or private repository identities through physical entry names.

## Design Basis

This specification derives from the [Ol architecture](../Architecture.md), especially the decisions to [make evidence freshness explicit](../Architecture.md#decision-cache-freshness), [persist evidence with explicit provenance and privacy boundaries](../Architecture.md#decision-provenance-privacy), [confine credentials to their intended authority](../Architecture.md#decision-credential-confinement), and [add evidence sources through one reconciliation model](../Architecture.md#decision-shared-reconciliation).

Those decisions require a cache entry to identify its schema, logical target, fetch time, evidence source, and collection outcome without storing token values. They also require physical names that do not reveal the logical target.

<a id="compatibility-contract"></a>
## Compatibility Contract

- Each entry is one UTF-8 JSON object.
- Property names are case-sensitive and use the names shown by the schema for that cache category.
- JSON object property order is not significant.
- `SchemaVersion` determines the meaning of the complete entry. Version `1` is the initial format.
- A reader must not reinterpret an unsupported schema version as the current version. It may migrate a recognized older version or treat the entry as unusable and recollect evidence.
- Writers emit only the current schema version for their cache category.
- Readers may ignore unknown properties within a supported schema version so additive metadata does not invalidate an otherwise usable entry.
- Required fields must have the specified JSON type. A malformed entry, a missing required field, or a logical-key mismatch makes the entry unusable.
- Cache entries are disposable evidence snapshots. An unusable entry does not become authoritative evidence; Ol should recollect it when the source is available.

The format is not required to preserve byte-for-byte serialization. Semantic compatibility is defined by schema version, field names, field values, and validation rules.

<a id="contract-cache-identity"></a>
## Logical and Physical Identity

`CacheKey` is the category-defined logical identity used to find and validate an entry. `CacheKeySha256` is the lowercase hexadecimal SHA-256 of the UTF-8 `CacheKey`.

Physical entry names use:

```text
<cache-category>/<CacheKeySha256>.json
```

The categories are:

- `package-metadata`
- `source-repository`
- `github-file`

The platform-specific cache root is not part of this format contract. The hash-named entry prevents package and private repository identities from appearing in directory listings. The plain `CacheKey` remains inside the entry so the evidence is auditable by a user who can already read the cache content.

A reader must require the stored `CacheKey` to equal the requested logical key using ordinal comparison. It must also require the stored `CacheKeySha256` to equal the hash derived from that key. Implementations may expose the hash as a derived in-memory property, but its persisted JSON value remains required and validated.

## Common Semantics

Every cache category carries these semantics:

| Property | Type | Required | Meaning |
|---|---|---:|---|
| `SchemaVersion` | integer | yes | Complete entry schema version. Version `1` is defined below. |
| `CacheKey` | string | yes | Canonical logical identity of the fetched target. |
| `CacheKeySha256` | string | yes | Lowercase hexadecimal SHA-256 of the UTF-8 `CacheKey`. It may be emitted from a derived value. |
| `FetchedAt` | string | yes | UTC timestamp in RFC 3339/ISO 8601 form recording when the source response was obtained. |
| `Source` | string | yes | Stable logical evidence-source name, such as `npm-registry` or `github-license-api`. |
| `Warnings` | array of strings | yes | Non-fatal collection or normalization warnings; empty when none. |
| `Errors` | array of strings | yes | Source errors retained for audit; empty when none. |

`FetchedAt` records provenance and does not imply an automatic TTL. Freshness remains controlled by `--refresh` and cache-clear commands.

Token values, authorization headers, absolute local paths, and hidden cache-root paths are forbidden in every cache entry. A non-empty repository reference must be a safe absolute URI and must not contain user information, query strings, or fragments because ambiguous and auxiliary URI positions can carry credentials. Authentication mode may be stored where relevant.

<a id="contract-package-cache-v1"></a>
## Package Metadata Entry — Schema Version 1

Package metadata schema version `1` is implemented in v2. It adds these properties:

| Property | Type | Required | Meaning |
|---|---|---:|---|
| `RawLicense` | string | yes | License value returned by the package source; empty when the source returned no license text. |
| `RepositoryUrl` | string | yes | Repository URL returned by package metadata; empty when unavailable. |
| `RepositoryRef` | string | no | Repository commit or ref mapped to the package version; empty or absent when unavailable. |
| `ResolverVersion` | integer | no | Metadata resolver capability version. Absence means the pre-capability resolver. |
| `DeclaredLicenseReferenceKind` | string | no | `None`, `Location`, `ArtifactPath`, or `InlineText`. Absence means no location was declared. An unrecognized value rejects the entry rather than dropping the fact silently. |
| `DeclaredLicenseReference` | string | no | The [declared license reference](spdx.md#contract-declared-license-reference) exactly as the publisher wrote it. Always empty for `InlineText`: a cache is not a place to keep a license document. |

The package schema-version-1 `CacheKey` is the accepted versioned purl substring before the first `?` qualifier or `#` subpath marker. It preserves the input identity's spelling, casing, and percent encoding. Producers must use this identity directly rather than constructing an alternate spelling for the same package. Changing this identity rule requires migration or a new schema version because it changes the physical lookup hash.

Example:

```json
{
  "CacheKey": "pkg:npm/react@19.0.0",
  "Source": "npm-registry",
  "RawLicense": "MIT",
  "RepositoryUrl": "https://github.com/facebook/react",
  "RepositoryRef": "0123456789abcdef",
  "ResolverVersion": 6,
  "Warnings": [],
  "Errors": [],
  "FetchedAt": "2026-07-08T00:00:00+00:00",
  "SchemaVersion": 1,
  "CacheKeySha256": "..."
}
```

The cache stores the raw source license rather than a final reconciled status. On use, Ol validates the raw value with the active SPDX data and passes the resulting candidate through common reconciliation. This prevents a cached conclusion produced with one SPDX snapshot from silently becoming authoritative under another snapshot. An ecosystem spelling that a registry defines as standing for an SPDX expression, such as Cargo's pre-SPDX `MIT/Apache-2.0`, is likewise resolved on use rather than at write time, so the entry keeps what the registry said.

`ResolverVersion` records which observations the writing build could make, and a newer resolver revisits only the entries whose observation it can improve. An entry with no license is revisited in every ecosystem, because every provider can now state where a publisher said its license is. Resolver version `5` additionally revisits npm entries written earlier even when they carry a license: whether a package occupies one directory of a shared repository decides whether that repository's license describes it, and a resolved license does not make that fact observable. Resolver version `6` reads [Go licenses from package contents](packagemanager.md#contract-go-license); every Go entry written earlier carries an empty license because the module proxy has none to give, so the general no-license rule covers them and no ecosystem-specific rule is needed. Recollection writes the current version, so each affected entry is refetched once rather than on every scan.

A capability that changes what a source can answer needs this bump even though the entry format is unchanged. Without it an upgraded Ol keeps serving entries whose emptiness was a property of the old resolver, and the improvement stays invisible until the entries age out — which is how the Go change first appeared to do nothing at all when measured against a warm cache.

<a id="contract-source-cache-v1"></a>
## Source Repository Entry — Schema Version 1

Source repository schema version `1` is used by v3. Its `CacheKey` is:

- `github:<owner>/<repo>@<ref>` when an explicit ref is used;
- `github:<owner>/<repo>@default` when GitHub resolves the default branch.

In addition to the common fields, a source entry carries:

| Property | Type | Required | Meaning |
|---|---|---:|---|
| `AuthMode` | string | yes | `ol_github_token` or `none`; never a token value. |
| `Repository` | string | yes | Logical `owner/repo` target. |
| `Ref` | string | yes | Requested ref, or `default` when omitted. |
| `HttpStatus` | integer or null | yes | Final HTTP status, or `null` when no response status exists. |
| `License` | object or null | yes | GitHub license result, or `null` when no license was detected or collection failed. |
| `ResolverVersion` | integer | no | Source resolver capability version. Absence means the pre-capability resolver. |

When `License` is not `null`, it has this shape:

| Property | Type | Required | Meaning |
|---|---|---:|---|
| `SpdxId` | string or null | yes | SPDX ID returned by GitHub; `null` when GitHub supplied none. |
| `Key` | string | yes | GitHub license key; empty when unavailable. |
| `Name` | string | yes | GitHub license name; empty when unavailable. |
| `Path` | string | yes | Repository-relative license file path; empty when unavailable. |
| `Sha` | string | yes | Git object SHA; empty when unavailable. |
| `HtmlUrl` | string | yes | Logical GitHub URL; empty when unavailable. |

Example:

```json
{
  "SchemaVersion": 1,
  "CacheKey": "github:owner/repo@ref",
  "CacheKeySha256": "...",
  "FetchedAt": "2026-07-08T00:00:00+00:00",
  "Source": "github-license-api",
  "AuthMode": "ol_github_token",
  "Repository": "owner/repo",
  "Ref": "ref",
  "HttpStatus": 200,
  "License": {
    "SpdxId": "MIT",
    "Key": "mit",
    "Name": "MIT License",
    "Path": "LICENSE",
    "Sha": "...",
    "HtmlUrl": "https://github.com/owner/repo/blob/ref/LICENSE"
  },
  "ResolverVersion": 3,
  "Warnings": [],
  "Errors": []
}
```

Resolver version `3` revisits entries whose `Ref` is not `default` and whose `HttpStatus` is `404`. An earlier resolver stopped at that answer, while the current one repeats the lookup at the repository default ref before concluding that no license file exists; see the [ref fallback contract](source.md#contract-source-ref-fallback). Recollection writes the current version, so each affected entry is refetched once rather than on every scan.

HTTP 404 and a successful response with no identified license are cacheable unknown outcomes, not malformed entries. Retry-exhausted or non-retryable source failures may also be retained when needed for audit, but cache use must continue to follow the best-effort and refresh behavior defined by the source specification.

<a id="contract-github-file-cache-v1"></a>
## Declared GitHub File Entry — Schema Version 1

The `github-file` category stores the bounded raw bytes returned for one exact declared GitHub file. Its `CacheKey` is `github-file:<owner>/<repo>@<ref>/<path>` with the case-insensitive GitHub owner and repository normalized to lowercase; ref and path retain their declared casing. In addition to the common identity and provenance fields, an entry carries:

| Property | Type | Required | Meaning |
|---|---|---:|---|
| `HttpStatus` | integer | yes | Always `200`; negative and error responses are not persisted. |
| `ContentSha256` | string | yes | Lowercase SHA-256 of decoded `Content`. |
| `Content` | string | yes | Base64-encoded raw document bytes. |

The decoded content is capped at 1 MiB and the complete entry is bounded before rental or parsing. A reader validates schema, logical key, key digest, source, UTC fetch time, status/content consistency, Base64 encoding, and the recomputed content SHA-256. Any failure makes the entry unusable and causes recollection when network access is enabled. A hit is reclassified with the active SPDX corpus; the cache never persists `MIT` or another final matcher conclusion. HTTP `404` remains deduplicated within one scan but is not persisted because evidence caches have no automatic TTL and GitHub may also use `404` for visibility or authorization changes.

The content digest is an integrity check, not a signature or MAC. It detects accidental corruption and unsynchronized edits but cannot authenticate an entry against an actor able to rewrite both content and digest. Filesystem access control is the trust boundary for intentional local modification; `--refresh`, `cache clear github-file`, and an isolated `--cache-dir` let callers decline existing entries.

<a id="contract-cache-archive-v1"></a>
## Cache Archive — Format Version 1

An `.olcache` file is the transport form of an Ol-managed cache, not its runtime lookup format. It is a gzip-compressed USTAR stream. Scan continues to read and write the category directories above; callers explicitly cross the archive boundary with `ol cache pack` and `ol cache unpack` so a scan never rewrites a complete archive as a side effect.

The archive begins with exactly one root manifest:

```json
{"FormatVersion":1}
```

All other entries are regular files named exactly `<cache-category>/<CacheKeySha256>.json`. Directories, links, devices, duplicate names, additional path segments, uppercase digests, and unknown root entries are invalid. Version `1` permits only the three categories defined by this specification.

`pack` writes the manifest first, then categories in the specification order and physical names in ordinal order. USTAR ownership, permissions, and modification time are fixed, and gzip output carries no run-specific timestamp. Equal input bytes therefore produce equal archive bytes. Every included entry must satisfy the common schema and identity contract. An optional maximum age omits entries whose UTC `FetchedAt` precedes the calculated cutoff before the archive entry-count limit is applied; it does not change or reinterpret the entry. Existing symbolic links and reparse points in a cache path, including linked cache entries, are rejected rather than followed.

The archive path must be outside all managed cache category directories for both `pack` and `unpack` and must not contain symbolic links or reparse points. This prevents archive replacement from overwriting a source cache entry or the input archive itself, including through a linked parent directory. `unpack` requires the manifest as its first entry so it validates the format before staging cache content. It accepts only USTAR entries emitted by this format; GNU, pax, and other tar variants are rejected.

`cache prune` is the explicit destructive retention operation for a persistent cache directory. It applies the same maximum-age cutoff across all managed categories, deletes only validated hash-named entries older than the cutoff, and preserves unknown sibling files. Archive packing never deletes its source entries.

`unpack` limits compressed, expanded, per-entry, and entry-count work before committing staged files. It derives every destination from the recognized category and validated opaque name rather than joining an archive-supplied path. The manifest and every entry are staged and validated before replacement begins. Existing symbolic links and reparse points in the cache-root path, category paths, and destination entry paths are rejected and rechecked before replacement, so a pre-existing link cannot redirect a write outside `--cache-dir`. The archive is neither signed nor authoritative: category readers apply their complete schema validation when an entry is requested, exactly as they do for a locally written cache.

An archive has the same privacy content as the directory it packs. Opaque physical names prevent identities from appearing in its entry listing, but each JSON entry still carries its logical package or repository key. A public seed must therefore be built only from evidence whose package and repository identities may be disclosed; packing a cache populated from private repositories does not make that evidence public-safe.

## Evolution and Migration

A schema version changes when an existing field is removed, renamed, changes type, or changes meaning, or when a newly required field cannot be safely defaulted. Adding an optional property that older readers may ignore does not by itself require a new version.

When the current Ol version cannot read an entry safely, it may:

1. migrate a recognized schema to the current schema;
2. ignore the entry and recollect evidence; or
3. report a component-scoped cache error when recollection cannot proceed.

It must not silently reinterpret incompatible fields. Cache incompatibility alone must not erase other valid evidence for the component.

## Related Specifications

- [Package metadata evidence and cache behavior](packagemanager.md)
- [Source repository evidence and cache behavior](source.md)
- [CLI cache commands and privacy contract](cli.md)

## Lessons Learned

- Native AOT cache serialization requires a source-generated `JsonSerializerContext`; reflection-based `JsonSerializer` paths can pass Debug tests but fail at runtime.
