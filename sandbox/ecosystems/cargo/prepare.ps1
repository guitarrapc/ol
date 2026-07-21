$ErrorActionPreference = "Stop"
$manifestPath = Join-Path $PSScriptRoot "Cargo.toml"
cargo generate-lockfile --manifest-path $manifestPath
if ($LASTEXITCODE -ne 0) { throw "Cargo dependency preparation failed." }

$metadata = cargo metadata --format-version 1 --locked --manifest-path $manifestPath
if ($LASTEXITCODE -ne 0) { throw "Cargo metadata generation failed." }
[IO.File]::WriteAllText(
    (Join-Path $PSScriptRoot "cargo-metadata.json"),
    ($metadata -join [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false))
