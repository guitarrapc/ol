$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    go mod download
    if ($LASTEXITCODE -ne 0) { throw "Go dependency preparation failed." }
}
finally {
    Pop-Location
}
