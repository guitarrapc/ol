# Ol

OpenSource License checker.

## Generate an SBOM

Restore the repository-pinned CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

```powershell
dotnet tool restore
dotnet tool run dotnet-CycloneDX Ol.slnx --output sandbox/sbom --output-format Json --filename cyclonedx-sample.json
```

Scan the generated SBOM with `ol`:

```powershell
dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json
```

```bash
dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json --format markdown --out output.md
```

Use an isolated cache root when a build or CI job must not share the user cache:

```powershell
dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json --cache-dir .tmp/ol-cache
```

Regenerate Ol's committed SBOM and text, Markdown, and JSON report snapshots with:

```powershell
./sandbox/Update-SelfScan.ps1
```

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).
