$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    npm install --package-lock-only --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw "npm dependency preparation failed." }
}
finally {
    Pop-Location
}
