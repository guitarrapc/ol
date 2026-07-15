param(
    [Parameter(Mandatory)]
    [string] $Report,

    [Parameter(Mandatory)]
    [string] $TextReport,

    [Parameter(Mandatory)]
    [string] $MarkdownReport,

    [Parameter(Mandatory)]
    [string] $Ecosystem,

    [Parameter(Mandatory)]
    [string] $InputKind,

    [Parameter(Mandatory)]
    [string] $InputFormat
)

$ErrorActionPreference = "Stop"
$json = Get-Content -Raw $Report | ConvertFrom-Json
$expectedInput = "$InputKind/$InputFormat"
if ($json.metadata.input.kind -ne $InputKind -or $json.metadata.input.format -ne $InputFormat) {
    throw "Expected input $expectedInput, got $($json.metadata.input.kind)/$($json.metadata.input.format)."
}

if ((Get-Content $TextReport -TotalCount 1) -ne "Input: $expectedInput") {
    throw "Text report did not identify input $expectedInput."
}

if ((Get-Content $MarkdownReport -TotalCount 1) -ne ('Input: `' + $expectedInput + '`')) {
    throw "Markdown report did not identify input $expectedInput."
}

if ($null -eq $json.inventory -or @($json.inventory.components).Count -lt 1 -or @($json.inventory.occurrences).Count -lt 1) {
    throw "Complete dependency inventory was not emitted."
}

if ($InputKind -eq "package-manager" -and @($json.inventory.contexts).Count -lt 1) {
    throw "Package-manager resolution contexts were not emitted."
}

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

if ($json.metadata.packageMetadata.targetCount -lt 1) {
    throw "Package metadata enrichment did not report a deduplicated $Ecosystem target."
}

if ($json.metadata.packageMetadata.fetchErrorCount -ne 0) {
    throw "Package metadata enrichment failed for $Ecosystem."
}
