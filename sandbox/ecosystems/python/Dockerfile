FROM python:3.14-slim-bookworm@sha256:23c59390fc717bf09f9336908199a0ae75d9c4264bf296123f94ad772fea3b52 AS generate
WORKDIR /src
COPY requirements.txt ./
RUN python -m pip install --disable-pip-version-check -r requirements.txt
RUN python -X utf8 -m pip inspect --local > pip-inspect.json

FROM scratch AS export
COPY --from=generate /src/pip-inspect.json /
