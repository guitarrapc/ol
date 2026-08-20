# SBOM generators

> **The scan output wins over this page.** Generator versions change which catalogers run, what they emit, and what the flags are called. Everything below is a symptom to recognize and a class of fix, illustrated with commands that were correct for one version. Verify against `--help` and against the components the generator actually produced; if they disagree with this page, the generator is right.

An SBOM is a second input beside the resolved package-manager inputs, never a replacement for them. Read the SBOM section of the skill before adding one.

## Did the SBOM earn its place?

`summary.supply` answers it in one line. The `--verbose` scan summary splits the same counts per ecosystem.

```text
Supplied by: 1 sbom only; 189 package-manager only; 0 both
  nuget: 0 sbom only; 189 package-manager only; 0 both
```

- **`sbomOnly` large.** The generator catalogued things the resolvers did not. Sometimes that is a real ecosystem ol has no adapter for; more often it is not a dependency at all. Check what those components are before keeping them.
- **`sbomOnly` near zero and `both` large.** The two inputs describe the same population. The SBOM is confirming, not adding.
- **`sbomOnly` and `both` both zero for an ecosystem.** The SBOM contributed nothing there. Expected when the generator does not read that ecosystem's resolver output.

## Symptom: components that are not packages

A source-tree scan can emit components with four-part assembly versions (`pkg:nuget/Some.Library@2.65.0.0` where the package is `Some.Library@2.65.0`), names that are file paths, or products identified from a committed executable. These have no purl or a purl no registry can answer, so they fail closed and cannot be removed by `--exclude-packages`, which never matches a component without a purl.

**Cause.** Binary catalogers reading committed `.dll`, `.exe`, and native libraries — Unity `Assets/Plugins`, vendored tool directories, sample apps. Excluding `bin` and `obj` does not help when the binaries are committed.

**Fix.** Restrict the generator to catalogers that read declared manifests and lockfiles, and turn binary catalogers off. Verify by rerunning and checking that `sbomOnly` dropped.

Syft 1.50 example:

```bash
syft dir:. \
  --override-default-catalogers 'declared' \
  --select-catalogers '-github-actions,-binary' \
  --exclude './.vs/**' --exclude './**/bin/**' --exclude './**/obj/**' \
  -o cyclonedx-json=sbom.cdx.json
```

`--override-default-catalogers declared` alone was not enough in that version: its `declared` set still included the binary catalogers, so `-binary` had to be named. Confirm the resulting set with `syft cataloger list` and the same flags rather than trusting this line.

## Symptom: CI dependencies appear as components

A generator that reads workflow files emits `pkg:github/...` components for actions. Whether those belong in the same report as application dependencies is a scope decision, not a defect.

Two ways to exclude them, and the difference matters:

- **At the generator** (`-github-actions` above): they never enter the report, and nothing records that they were dropped.
- **At policy** (`ol check --exclude-packages pkg:github/`): they stay in the report and the excluded count is always printed, with per-prefix attribution under `--verbose`.

Prefer the policy option when the components are real and merely out of scope; prefer the generator when they are artifacts of scanning the wrong thing.

## Symptom: a repository's own code appears as a dependency

Workspace members, path dependencies, and Unity package manifests can arrive as components, usually without a purl. They report `package_metadata_no_purl`. ol keeps whatever identity the generator wrote and never guesses that a name means "first-party", because guessing would fail open.

Either narrow the generator's scope, or acknowledge them in a baseline. Note that a name written as a filesystem path carries the generating platform's separators, so a baseline generated on one operating system may not match on another.

## Where a generator is genuinely worth it

- An ecosystem ol has no adapter for.
- An ecosystem whose resolver you do not want to install in CI. A generator that reads a committed lockfile can cover it without the toolchain.

In the second case the evidence is shallower: package-artifact inspection needs the installed location a resolved input records, so licenses that only exist inside the package artifact stay unread, and a disagreement between a declared license and the artifact's own license file stays invisible.
