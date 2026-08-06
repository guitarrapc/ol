FROM ruby:4.0-slim-trixie@sha256:b6505477e3b9409e6786c9cc2facc0e7b3e0aab575c53ef4a92b4e61c0cdd983 AS generate
RUN apt-get update \
    && apt-get install -y --no-install-recommends git libcurl4 rsync \
    && rm -rf /var/lib/apt/lists/*
RUN gem install cocoapods --version 1.16.2 --no-document
WORKDIR /src
COPY Podfile ./
RUN pod install --allow-root

FROM scratch AS export
COPY --from=generate /src/Podfile.lock /
