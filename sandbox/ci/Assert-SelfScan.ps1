param(
    [Parameter(Mandatory)]
    [string] $Sbom,

    [Parameter(Mandatory)]
    [string] $Report
)

$ErrorActionPreference = "Stop"
$sbomJson = Get-Content -Raw $Sbom | ConvertFrom-Json
if ($sbomJson.bomFormat -ne "CycloneDX" -or @($sbomJson.components).Count -lt 1) {
    throw "Live self-scan did not produce a non-empty CycloneDX SBOM."
}

$reportJson = Get-Content -Raw $Report | ConvertFrom-Json
if ($reportJson.metadata.input.kind -ne "sbom" -or $reportJson.metadata.input.format -ne "cyclonedx") {
    throw "Live self-scan report did not identify a CycloneDX SBOM input."
}

if ($null -eq $reportJson.inventory -or @($reportJson.inventory.components).Count -lt 1 -or @($reportJson.inventory.occurrences).Count -lt 1) {
    throw "Live self-scan report did not contain a complete dependency inventory."
}

if (@($reportJson.components).Count -lt 1) {
    throw "Live self-scan report did not contain dependency components."
}
