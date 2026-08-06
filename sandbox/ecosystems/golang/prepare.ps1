$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "../../ci/Build-EcosystemFixture.ps1") -Path $PSScriptRoot
