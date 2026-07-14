# Evidence Cache Format Specification

This document defines the persistent JSON cache contract shared by package metadata and source repository evidence.

The cache is an Ol-managed persistence format, not a public report or a general interchange format. It is nevertheless versioned at specification level because an Ol upgrade must be able to decide whether an existing entry is compatible, stale, corrupt, or requires migration. The format also carries provenance needed to explain cached evidence and privacy properties needed to avoid exposing package or private repository identities through physical entry names.

## Design Basis

This specification derives from the [Ol design](../DESIGN.md), especially the decisions to [make evidence freshness explicit](../DESIGN.md#decision-cache-freshness), [persist evidence with explicit provenance and privacy boundaries](../DESIGN.md#decision-provenance-privacy), [confine credentials to their intended authority](../DESIGN.md#decision-credential-confinement), and [add evidence sources through one reconciliation model](../DESIGN.md#decision-shared-reconciliation).

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

The package schema-version-1 `CacheKey` is the accepted versioned purl substring before the first `?` qualifier or `#` subpath marker. It preserves the input identity's spelling, casing, and percent encoding. Producers must use this identity directly rather than constructing an alternate spelling for the same package. Changing this identity rule requires migration or a new schema version because it changes the physical lookup hash.

Example:

```json
{
  "CacheKey": "pkg:npm/react@19.0.0",
  "Source": "npm-registry",
  "RawLicense": "MIT",
  "RepositoryUrl": "https://github.com/facebook/react",
  "RepositoryRef": "0123456789abcdef",
  "Warnings": [],
  "Errors": [],
  "FetchedAt": "2026-07-08T00:00:00+00:00",
  "SchemaVersion": 1,
  "CacheKeySha256": "..."
}
```

The cache stores the raw source license rather than a final reconciled status. On use, Ol validates the raw value with the active SPDX data and passes the resulting candidate through common reconciliation. This prevents a cached conclusion produced with one SPDX snapshot from silently becoming authoritative under another snapshot.

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
  "Warnings": [],
  "Errors": []
}
```

HTTP 404 and a successful response with no identified license are cacheable unknown outcomes, not malformed entries. Retry-exhausted or non-retryable source failures may also be retained when needed for audit, but cache use must continue to follow the best-effort and refresh behavior defined by the source specification.

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
