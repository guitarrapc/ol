#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REFERENCES_DIR="${ROOT_DIR}/.references"

mkdir -p "${REFERENCES_DIR}"

repo_specs=(
  "licensee/licensed"
  "google/go-licenses"
  "oss-review-toolkit/ort"
  "RSeidelsohn/license-checker-rseidelsohn"
  "pivotal/LicenseFinder"
  "raimon49/pip-licenses"
)

for spec in "${repo_specs[@]}"; do
  IFS='|' read -r repo name branch <<< "${spec}"
  name="${name:-${repo#*/}}"
  target="${REFERENCES_DIR}/${name}"
  url="https://github.com/${repo}.git"

  if [[ -d "${target}/.git" ]]; then
    echo "[pull] ${repo}${branch:+ @ ${branch}}"
    if [[ -n "${branch:-}" ]]; then
      git -C "${target}" pull --ff-only origin "${branch}"
    else
      git -C "${target}" pull --ff-only
    fi
  elif [[ -d "${target}" ]]; then
    echo "[skip] ${target} exists but is not a git repository"
  else
    echo "[clone] ${repo}${branch:+ @ ${branch}} -> ${name}"
    if [[ -n "${branch:-}" ]]; then
      git clone --branch "${branch}" --single-branch "${url}" "${target}"
    else
      git clone "${url}" "${target}"
    fi
  fi
done
