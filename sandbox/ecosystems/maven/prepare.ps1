$ErrorActionPreference = "Stop"
$maven = Get-Command mvn -ErrorAction Stop

Push-Location $PSScriptRoot
try {
    & $maven.Source --batch-mode org.apache.maven.plugins:maven-dependency-plugin:3.11.0:copy-dependencies "-DoutputDirectory=target/dependency"
    if ($LASTEXITCODE -ne 0) { throw "Maven dependency preparation failed." }

    & $maven.Source --batch-mode org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree "-DoutputType=json" "-DoutputFile=target/maven-dependency-tree.json"
    if ($LASTEXITCODE -ne 0) { throw "Maven dependency-tree preparation failed." }
}
finally {
    Pop-Location
}
