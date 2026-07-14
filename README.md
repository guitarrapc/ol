# Ol

OpenSource License checker.

## Generate an SBOM

Install the CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

```powershell
dotnet tool install --global CycloneDX
dotnet CycloneDX Ol.slnx --json --output sandbox/sbom --filename cyclonedx-sample.json
```

Scan the generated SBOM with `ol`:

```powershell
dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json
```

```bash
$ dotnet run --project src/Ol -- scan --sbom sandbox/sbom/cyclonedx-sample.json --format markdown > output.md
```
