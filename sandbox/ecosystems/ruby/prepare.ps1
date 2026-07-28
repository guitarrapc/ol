$ErrorActionPreference = "Stop"
$bundle = Get-Command bundle -ErrorAction Stop

Push-Location $PSScriptRoot
try {
    & $bundle.Source lock --update
    if ($LASTEXITCODE -ne 0) { throw "Bundler dependency preparation failed." }
}
finally {
    Pop-Location
}
