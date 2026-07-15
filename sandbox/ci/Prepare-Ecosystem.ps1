param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$fixture = (Resolve-Path (Join-Path $root $Path)).Path
$ecosystemRoot = (Resolve-Path (Join-Path $root "sandbox/ecosystems")).Path
if (!$fixture.StartsWith($ecosystemRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fixture path must be under sandbox/ecosystems."
}

$prepare = Join-Path $fixture "prepare.ps1"
if (!(Test-Path -LiteralPath $prepare -PathType Leaf)) {
    throw "Fixture must provide prepare.ps1."
}

& $prepare
