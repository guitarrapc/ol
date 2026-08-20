# Ecosystem inputs

Use only the rows matching the audited subject. Confirm exact supported versions with `ol scan --help` because the installed ol may differ from this bundled guidance.

| Ecosystem | Input accepted by ol | Preparation and cautions |
|---|---|---|
| Any / mixed | One CycloneDX or SPDX JSON SBOM | Generate it from the resolved build with an ecosystem-native or build-level tool. Prefer this for a release-spanning, polyglot inventory. |
| .NET / NuGet | `project.assets.json` v3/v4 | Run `dotnet restore`; select current assets for each in-scope project/target, not every historical `obj` directory. |
| npm | `package-lock.json` v2/v3 | Run the project's locked install workflow. Include each in-scope workspace/root lock; do not substitute `package.json`. |
| pnpm | `pnpm-lock.yaml` v9 | Run the project's locked install workflow. Preserve workspace importer coverage. |
| Yarn | Classic v1 or Berry metadata v8 `yarn.lock` | Run the project's install workflow and verify the lock format/version. |
| Rust / Cargo | Cargo metadata JSON | Run `cargo metadata --format-version 1 --locked > cargo-metadata.json`, omitting `--locked` when the repository does not commit `Cargo.lock`. Neither `Cargo.toml` nor `Cargo.lock` is accepted directly. Include the intended features/targets when they change the graph. |
| Go modules | `go-list-modules.json` plus `go-mod-graph.txt` in one directory | Run `go list -m -json all` and `go mod graph`, saving the outputs with those names. Run them from the intended module/workspace context. |
| Python | pip inspect JSON v1 | Activate the audited environment, then run `python -m pip inspect --local > pip-inspect.json`. A requirements file alone is not resolved evidence. |
| PHP / Composer | `composer.json` plus `composer.lock` in one directory | Keep the pair together. Include or exclude `packages-dev` according to the audited install scope; do not scan `composer.lock` without its root requirements. |
| Ruby / Bundler | `Gemfile.lock` | Generate it with the project's Bundler workflow and correct platform set. |
| Java / Maven | Maven Dependency Plugin 3.7+ tree JSON | Run `mvn dependency:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json` for each in-scope module/configuration. |
| Java / Gradle | CycloneDX/SPDX JSON SBOM | Generate an SBOM from the resolved Gradle configurations; ol does not consume a Gradle lock as a portable resolved graph. |
| SwiftPM | `Package.resolved` v2/v3 | Run `swift package resolve` for the audited package/workspace and platform context. |
| CocoaPods | `Podfile.lock` | Run the project's locked CocoaPods workflow; keep the lock aligned with the intended target definitions. |

## Repository discovery

Search both tracked and ignored/generated paths. Use repository-native workspace files and CI/release commands to determine ownership and scope; filenames alone are insufficient. Typical candidates include:

```text
bom*.json, *.cdx.json, *.spdx.json
project.assets.json, package-lock.json, pnpm-lock.yaml, yarn.lock
Cargo.toml, Cargo.lock, go.mod, go.work, pyproject.toml, requirements*.txt
composer.json, composer.lock, Gemfile.lock, pom.xml, build.gradle*
Package.swift, Package.resolved, Podfile, Podfile.lock
```

Manifests in this list are discovery signals, not automatically valid ol inputs. Generate the accepted resolved form shown in the table.

## Polyglot alignment checklist

- Cover every package manager used by the release path, including dependencies built in nested frontend, generator, native, or deployment subtrees.
- Distinguish independently shipped products from one product assembled from several ecosystems.
- Match SBOM and resolver outputs to the same revision, build flags, platform, features, and development/production selection.
- Avoid scanning vendored examples, fixtures, documentation sites, and developer tools unless they are part of the declared subject.
- Record an explicit blocker for each in-scope ecosystem that lacks an aligned resolved input.
- Compare per-context component and dependency counts before trusting the aggregate result.
