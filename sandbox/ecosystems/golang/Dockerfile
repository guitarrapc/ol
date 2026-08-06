FROM golang:1.26-trixie@sha256:87ffdb09b6a2e29ff910748b745395e8a0299aa80b7c0551cdca9b55e3fd2b3e AS generate
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN go mod download
RUN go list -m -json all > go-list-modules.json
RUN go mod graph > go-mod-graph.txt

FROM scratch AS export
COPY --from=generate /src/go.sum /
COPY --from=generate /src/go-list-modules.json /
COPY --from=generate /src/go-mod-graph.txt /
