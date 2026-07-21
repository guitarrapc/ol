param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$olProject = Join-Path $repositoryRoot "src\Ol\Ol.csproj"

dotnet build $olProject -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Ol build failed with exit code $LASTEXITCODE."
}

$olAssembly = Join-Path $repositoryRoot "src\Ol\bin\$Configuration\net10.0\ol.dll"
$outputDirectory = Join-Path $PSScriptRoot "output"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$samples = @(
    [pscustomobject]@{ Name = "nuget"; Input = "nuget\project.assets.json" },
    [pscustomobject]@{ Name = "npm"; Input = "npm\package-lock.json" },
    [pscustomobject]@{ Name = "pnpm"; Input = "pnpm\pnpm-lock.yaml" },
    [pscustomobject]@{ Name = "yarn-classic"; Input = "yarn-classic\yarn.lock" },
    [pscustomobject]@{ Name = "yarn-berry"; Input = "yarn-berry\yarn.lock" },
    [pscustomobject]@{ Name = "cargo"; Input = "cargo\cargo-metadata.json" }
)

$results = foreach ($sample in $samples) {
    $inputPath = Join-Path $PSScriptRoot $sample.Input
    $outputPath = Join-Path $outputDirectory "$($sample.Name).json"
    $null = dotnet $olAssembly scan --input $inputPath --skip-enrichment --format json --out $outputPath --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Sample '$($sample.Name)' failed with exit code $LASTEXITCODE."
    }

    $report = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
    [pscustomobject]@{
        Sample = $sample.Name
        Format = $report.metadata.input.format
        Contexts = @($report.inventory.contexts).Count
        Components = @($report.inventory.components).Count
        Occurrences = @($report.inventory.occurrences).Count
        Edges = @($report.inventory.edges).Count
        Report = $outputPath
    }
}

$collectionOutputPath = Join-Path $outputDirectory "collection.json"
$null = dotnet $olAssembly scan --input $PSScriptRoot --skip-enrichment --format json --out $collectionOutputPath --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Mixed package-manager collection failed with exit code $LASTEXITCODE."
}

$collectionReport = Get-Content -LiteralPath $collectionOutputPath -Raw | ConvertFrom-Json
$results += [pscustomobject]@{
    Sample = "all"
    Format = $collectionReport.metadata.input.format
    Contexts = @($collectionReport.inventory.contexts).Count
    Components = @($collectionReport.inventory.components).Count
    Occurrences = @($collectionReport.inventory.occurrences).Count
    Edges = @($collectionReport.inventory.edges).Count
    Report = $collectionOutputPath
}

$results | Format-Table -AutoSize
