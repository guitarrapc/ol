$ErrorActionPreference = "Stop"
$composer = Get-Command composer -ErrorAction Stop

Push-Location $PSScriptRoot
try {
    & $composer.Source update --no-install --no-interaction --no-plugins --no-scripts --no-audit
    if ($LASTEXITCODE -ne 0) { throw "Composer dependency preparation failed." }
}
finally {
    Pop-Location
}
