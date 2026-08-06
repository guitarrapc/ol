FROM node:26-bookworm-slim@sha256:cd565714d4da3e84bfd341e31448f81d47c6362198f152345297c9c1154e6341 AS generate
RUN npm install --global @yarnpkg/cli-dist@4.9.4
WORKDIR /src
COPY package.json ./
RUN yarn install --mode=skip-build

FROM scratch AS export
COPY --from=generate /src/yarn.lock /
