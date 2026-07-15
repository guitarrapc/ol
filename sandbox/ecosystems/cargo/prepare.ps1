$ErrorActionPreference = "Stop"
cargo generate-lockfile --manifest-path (Join-Path $PSScriptRoot "Cargo.toml")
if ($LASTEXITCODE -ne 0) { throw "Cargo dependency preparation failed." }
