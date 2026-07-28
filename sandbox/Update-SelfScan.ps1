param(
    [switch] $NoBuild,
    [switch] $ReportsOnly,
    [string] $OutputDirectory
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $PSScriptRoot "self"
}
elseif ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$sbom = Join-Path $output "ol.cdx.json"
$generatedSbom = Join-Path $output "bom.json"
$ol = Join-Path $root "src/Ol/bin/Release/net10.0/ol.dll"

New-Item -ItemType Directory -Force $output | Out-Null
Push-Location $root
try {
    if (!$NoBuild) {
        dotnet build Ol.slnx -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
    }

    if (!$ReportsOnly) {
        dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

        dotnet tool run dotnet-CycloneDX src/Ol/Ol.csproj --runtime linux-x64 --output $output --output-format Json --filename (Split-Path -Leaf $generatedSbom) --no-serial-number
        if ($LASTEXITCODE -ne 0) { throw "CycloneDX SBOM generation failed." }

        $document = Get-Content -Raw $generatedSbom | ConvertFrom-Json
        $document.PSObject.Properties.Remove("serialNumber")
        if ($null -ne $document.metadata) {
            $document.metadata.PSObject.Properties.Remove("timestamp")
        }

        $normalized = ($document | ConvertTo-Json -Depth 100).Replace("`r`n", "`n") + "`n"
        [IO.File]::WriteAllText($sbom, $normalized, [Text.UTF8Encoding]::new($false))
        Remove-Item -LiteralPath $generatedSbom
    }
    elseif (!(Test-Path -LiteralPath $sbom -PathType Leaf)) {
        throw "Committed self-scan SBOM was not found: $sbom"
    }

    foreach ($format in @("text", "markdown", "json")) {
        $extension = switch ($format) {
            "text" { "txt" }
            "markdown" { "md" }
            default { $format }
        }
        $report = Join-Path $output "ol.$extension"
        dotnet $ol scan --input $sbom --format $format --skip-enrichment --quiet --out $report | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Ol $format self-scan failed." }
    }
}
finally {
    Pop-Location
}
