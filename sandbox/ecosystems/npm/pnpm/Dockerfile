FROM node:26-bookworm-slim@sha256:cd565714d4da3e84bfd341e31448f81d47c6362198f152345297c9c1154e6341 AS generate
RUN npm install --global pnpm@10.34.5
WORKDIR /src
COPY package.json ./
RUN pnpm install --lockfile-only --ignore-scripts

FROM scratch AS export
COPY --from=generate /src/pnpm-lock.yaml /
