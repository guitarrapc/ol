param(
    [Parameter(Mandatory)]
    [string] $Report,

    [Parameter(Mandatory)]
    [string] $Ecosystem
)

$ErrorActionPreference = "Stop"
$json = Get-Content -Raw $Report | ConvertFrom-Json
$components = @($json.components | Where-Object { $_.purl -like "pkg:$Ecosystem/*" })
if ($components.Count -eq 0) {
    throw "No pkg:$Ecosystem component was produced."
}

if (@($components | Where-Object { $_.ecosystem -ne $Ecosystem }).Count -ne 0) {
    throw "Ol did not classify every pkg:$Ecosystem component as $Ecosystem."
}

if ($json.metadata.packageMetadata.supportedComponentCount -lt 1) {
    throw "Package metadata enrichment did not schedule $Ecosystem."
}

if ($json.metadata.packageMetadata.fetchErrorCount -ne 0) {
    throw "Package metadata enrichment failed for $Ecosystem."
}
