$ErrorActionPreference = "Stop"
$python = Get-Command python3 -ErrorAction SilentlyContinue
if ($null -eq $python) { $python = Get-Command python -ErrorAction Stop }

Push-Location $PSScriptRoot
try {
    & $python.Source -m venv .venv
    if ($LASTEXITCODE -ne 0) { throw "Python virtual environment creation failed." }

    $venvPython = if ($IsWindows) { Join-Path $PSScriptRoot ".venv\Scripts\python.exe" } else { Join-Path $PSScriptRoot ".venv/bin/python" }
    & $venvPython -m pip install --disable-pip-version-check -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw "Python dependency preparation failed." }

    $inspect = & $venvPython -X utf8 -m pip inspect --local
    if ($LASTEXITCODE -ne 0) { throw "pip inspect generation failed." }
    [IO.File]::WriteAllText(
        (Join-Path $PSScriptRoot "pip-inspect.json"),
        ($inspect -join [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false))
}
finally {
    Pop-Location
}
