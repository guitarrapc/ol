FROM ruby:4.0-slim-trixie@sha256:b6505477e3b9409e6786c9cc2facc0e7b3e0aab575c53ef4a92b4e61c0cdd983 AS generate
RUN gem install bundler --version 4.0.16 --no-document
WORKDIR /src
COPY Gemfile ./
RUN bundle lock --update

FROM scratch AS export
COPY --from=generate /src/Gemfile.lock /
