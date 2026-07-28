$ErrorActionPreference = "Stop"
dotnet build (Join-Path $PSScriptRoot "Ol.Ci.NuGet.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "NuGet dependency preparation failed." }
