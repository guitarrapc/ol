FROM composer:2.9@sha256:b09bccd91a78fe8a9ab4b33d707b862e8fe54fec17782e32683ad2a69c46867d AS generate
WORKDIR /src
COPY composer.json ./
RUN composer update --no-install --no-interaction --no-plugins --no-scripts --no-audit

FROM scratch AS export
COPY --from=generate /src/composer.lock /
