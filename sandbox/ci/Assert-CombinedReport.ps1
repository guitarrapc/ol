#Requires -Version 7.0
<#
.SYNOPSIS
Asserts the input-combination contract for a scan that read an SBOM together with a package-manager input.

.DESCRIPTION
Checks properties rather than counts. Resolution rates move with registry responses and SPDX license-list versions,
so asserting them would fail on days Ol did not change. What must hold whatever the registries answer is that the
collection identifies itself as one, that every component states which inputs supplied it, and that combining inputs
never loses a component either input reported on its own.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CombinedReport,
    [Parameter(Mandatory)][string] $SbomReport,
    [Parameter(Mandatory)][string] $PackageManagerReport
)

$ErrorActionPreference = 'Stop'

function Read-Report([string] $Path) {
    if (-not (Test-Path $Path)) { throw "Report not found: $Path" }
    return Get-Content $Path -Raw | ConvertFrom-Json
}

# Containment is checked on purl identity rather than the printed purl. A folded row keeps the package-manager
# spelling, so the same component can appear as "pkg:nuget/Direct.Package@1.0.0" in the collection and
# "pkg:nuget/direct.package@1.0.0" in the SBOM-only scan. Comparing raw strings would read that as a loss.
function Get-PurlIdentity([string] $Purl) {
    $end = $Purl.IndexOfAny([char[]]@('?', '#'))
    $identity = if ($end -lt 0) { $Purl } else { $Purl.Substring(0, $end) }
    return $identity.ToLowerInvariant()
}

function Get-PurlSet($Report) {
    $set = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($component in $Report.components) {
        if (-not [string]::IsNullOrEmpty($component.purl)) { [void]$set.Add((Get-PurlIdentity $component.purl)) }
    }
    return $set
}

$combined = Read-Report $CombinedReport
$sbom = Read-Report $SbomReport
$packageManager = Read-Report $PackageManagerReport

if ($combined.metadata.input.kind -ne 'collection') {
    throw "Combined report input kind is '$($combined.metadata.input.kind)', expected 'collection'."
}

if ($null -ne $combined.metadata.input.PSObject.Properties['sbomSha256']) {
    throw 'Combined report must not publish the collection hash as the SBOM identity.'
}

foreach ($component in $combined.components) {
    if ($null -eq $component.suppliedBy -or $component.suppliedBy.Count -eq 0) {
        throw "Component '$($component.name)' does not state which input supplied it."
    }
}

$combinedPurls = Get-PurlSet $combined
$suppliedByBoth = 0
foreach ($component in $combined.components) {
    if ($component.suppliedBy.Count -eq 2) { $suppliedByBoth++ }
}

foreach ($pair in @(@{ Name = 'SBOM'; Report = $sbom }, @{ Name = 'package-manager'; Report = $packageManager })) {
    foreach ($purl in (Get-PurlSet $pair.Report)) {
        if (-not $combinedPurls.Contains($purl)) {
            throw "Combining inputs dropped '$purl', which the $($pair.Name) scan reported on its own."
        }
    }
}

if ($suppliedByBoth -eq 0) {
    throw 'No component was supplied by both inputs, so this fixture proves nothing about matching.'
}

Write-Host "Combined report OK: $($combined.components.Count) components, $suppliedByBoth matched across both inputs."
