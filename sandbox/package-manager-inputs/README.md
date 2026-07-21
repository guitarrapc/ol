# Package-manager input samples

These deterministic resolved-input samples exercise Ol without installing or invoking NuGet, npm, pnpm, or Yarn and without accessing package registries.

Run all adapters from the repository root:

```powershell
./sandbox/package-manager-inputs/Run-Samples.ps1
```

The script builds Ol once, scans every sample with auto detection and `--skip-enrichment`, writes JSON reports below the ignored `output/` directory, and prints detected format and graph counts.

To run one sample directly:

```powershell
dotnet run -c Release --project src/Ol -- scan `
  --input sandbox/package-manager-inputs/pnpm/pnpm-lock.yaml `
  --skip-enrichment --format json --quiet
```

Yarn Classic and Yarn Berry intentionally use the same `yarn.lock` file name in separate directories. Their content signatures select the correct adapter.
