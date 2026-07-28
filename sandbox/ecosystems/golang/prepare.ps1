$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    go mod download
    if ($LASTEXITCODE -ne 0) { throw "Go dependency preparation failed." }

    $modules = go list -m -json all
    if ($LASTEXITCODE -ne 0) { throw "Go selected-module generation failed." }
    [IO.File]::WriteAllText(
        (Join-Path $PSScriptRoot "go-list-modules.json"),
        ($modules -join [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))

    $graph = go mod graph
    if ($LASTEXITCODE -ne 0) { throw "Go module graph generation failed." }
    [IO.File]::WriteAllText(
        (Join-Path $PSScriptRoot "go-mod-graph.txt"),
        ($graph -join [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
}
finally {
    Pop-Location
}
