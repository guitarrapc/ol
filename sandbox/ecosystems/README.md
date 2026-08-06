# Ecosystem fixtures

Each fixture is generated for `linux/amd64` with the toolchain pinned in its `Dockerfile`. Run its `prepare.ps1`; no ecosystem SDK or runtime is required on the host.

Docker is used when available, followed by Podman. Set `CONTAINER_RUNTIME` to explicitly select either tool.

The npm fixture also regenerates its pnpm and Yarn Berry subfixtures.
