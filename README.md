# Ol

OpenSource License checker.

## Scan dependencies

Ol accepts resolved dependency inputs. For a .NET project, restore it and scan NuGet's resolved `project.assets.json` directly:

```powershell
dotnet restore Ol.slnx
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --input-format nuget-assets
```

NuGet resolution can differ by target framework and runtime identifier. Ol preserves each target from `project.assets.json` as a separate resolution context in JSON output.

Ol also accepts CycloneDX and SPDX JSON SBOMs. Restore the repository-pinned CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

```powershell
dotnet tool restore
dotnet tool run dotnet-CycloneDX Ol.slnx --output sandbox/sbom --output-format Json --filename cyclonedx-sample.json
```

Scan the generated SBOM with the generalized input API:

```powershell
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --input-format cyclonedx
```

```bash
dotnet run --project src/Ol -- scan --input bom.spdx.json --input-format spdx
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --input-format cyclonedx --format markdown --out output.md
```

`--sbom <path>` remains available as a compatible shortcut that detects CycloneDX or SPDX JSON from the document. Use `--input` with `--input-format` for an explicit, uniform input contract.

Use an isolated cache root when a build or CI job must not share the user cache:

```powershell
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --input-format cyclonedx --cache-dir .tmp/ol-cache
```

## Repository sandbox

Regenerate Ol's committed SBOM and text, Markdown, and JSON report snapshots through the generalized input API with:

```powershell
./sandbox/Update-SelfScan.ps1
```

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).
