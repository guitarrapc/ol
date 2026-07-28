param(
    [Parameter(Mandatory)]
    [string] $Sbom,

    [Parameter(Mandatory)]
    [string] $Output
)

$ErrorActionPreference = "Stop"
$document = Get-Content -Raw $Sbom | ConvertFrom-Json
$rootRef = $document.metadata.component.'bom-ref'
if ([string]::IsNullOrWhiteSpace($rootRef)) {
    throw "CycloneDX metadata.component.bom-ref is required."
}

$document.metadata.PSObject.Properties.Remove("component")
$document.dependencies = @($document.dependencies | Where-Object { $_.ref -ne $rootRef })

$json = ($document | ConvertTo-Json -Depth 100).Replace("`r`n", "`n") + "`n"
[IO.File]::WriteAllText($Output, $json, [Text.UTF8Encoding]::new($false))
