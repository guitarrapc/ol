$ErrorActionPreference = "Stop"
docker build `
    --target export `
    --output "type=local,dest=$(Join-Path $PSScriptRoot "obj")" `
    $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw "NuGet dependency preparation failed." }
