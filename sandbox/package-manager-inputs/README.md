# Package-manager input samples

These deterministic resolved-input samples exercise Ol without installing or invoking NuGet, npm, pnpm, Yarn, or Cargo and without accessing package registries.

Run all adapters from the repository root:

```bash
./sandbox/package-manager-inputs/Run-Samples.ps1
```

The script builds Ol once, scans every sample with auto detection and `--skip-enrichment`, then scans this whole directory as one mixed package-manager collection. It writes JSON reports below the ignored `output/` directory and prints detected format and graph counts.

To run one sample directly:

```bash
dotnet run -c Release --project src/Ol -- scan --input sandbox/package-manager-inputs/pnpm/pnpm-lock.yaml --skip-enrichment --format json --quiet
```

The Cargo sample is the checked output of `cargo metadata --format-version 1 --locked`, not `Cargo.toml` or `Cargo.lock` itself:

```bash
dotnet run -c Release --project src/Ol -- scan --input sandbox/package-manager-inputs/cargo/cargo-metadata.json --skip-enrichment --format json --quiet
```

Yarn Classic and Yarn Berry intentionally use the same `yarn.lock` file name in separate directories. Their content signatures select the correct adapter.

The final `all` row demonstrates the polyglot repository workflow:

```bash
dotnet run -c Release --project src/Ol -- scan --input sandbox/package-manager-inputs --skip-enrichment --format json --quiet
```

Do not specify `--input-format` for a mixed directory. Each discovered file is content-detected and the combined report format is `collection`.
