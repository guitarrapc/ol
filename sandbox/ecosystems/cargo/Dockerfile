FROM rust:1.97-bookworm@sha256:14bc9c5966e7b3a385794b3d5389a8765668342025fbcc7b2e3d2866ac4bd8c3 AS generate
WORKDIR /src
COPY Cargo.toml ./
COPY src ./src
RUN cargo generate-lockfile
RUN cargo metadata --format-version 1 --locked > cargo-metadata.json

FROM scratch AS export
COPY --from=generate /src/Cargo.lock /
COPY --from=generate /src/cargo-metadata.json /
