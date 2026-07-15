$ErrorActionPreference = "Stop"
dotnet restore (Join-Path $PSScriptRoot "Ol.Ci.NuGet.csproj")
if ($LASTEXITCODE -ne 0) { throw "NuGet dependency preparation failed." }
