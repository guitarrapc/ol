param(
    [Parameter(Mandatory)]
    [string] $Path
)

$ErrorActionPreference = "Stop"
$containerRuntime = $env:CONTAINER_RUNTIME
if ([string]::IsNullOrWhiteSpace($containerRuntime)) {
    $containerRuntime = if (Get-Command docker -ErrorAction SilentlyContinue) {
        "docker"
    }
    elseif (Get-Command podman -ErrorAction SilentlyContinue) {
        "podman"
    }
    else {
        throw "Docker or Podman is required to prepare ecosystem fixtures."
    }
}

$runtime = Get-Command $containerRuntime -ErrorAction Stop
& $runtime.Source build `
    --platform linux/amd64 `
    --target export `
    --output "type=local,dest=$Path" `
    $Path
if ($LASTEXITCODE -ne 0) { throw "Ecosystem fixture preparation failed." }
