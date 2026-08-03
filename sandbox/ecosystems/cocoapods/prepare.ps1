$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "Podfile.lock") -PathType Leaf)) {
    throw "CocoaPods lock fixture is missing."
}
