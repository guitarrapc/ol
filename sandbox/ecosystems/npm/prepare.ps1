$ErrorActionPreference = "Stop"
$build = Join-Path $PSScriptRoot "../../ci/Build-EcosystemFixture.ps1"
foreach ($fixture in @($PSScriptRoot, (Join-Path $PSScriptRoot "pnpm"), (Join-Path $PSScriptRoot "yarn-berry"))) {
    & $build -Path $fixture
}
