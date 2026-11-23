#!/bin/sh
set -eu

export DOCKER_CONFIG="$(mktemp -d)"
trap 'rm -f "$DOCKER_CONFIG/config.json"' EXIT
umask 077
auth="$(printf '%s:%s' "$CI_REGISTRY_USER" "$CI_REGISTRY_PASSWORD" | base64 | tr -d '\n')"
printf '{"auths":{"%s":{"auth":"%s"}}}\n' "$CI_REGISTRY" "$auth" > "$DOCKER_CONFIG/config.json"

mkdir -p artifacts
buildctl-daemonless.sh build \
    --frontend dockerfile.v0 --local context=. --local dockerfile=. \
    --opt platform=linux/amd64 --opt "build-arg:GUARDIAN_SOURCE_SHA=$CI_COMMIT_SHA" \
    --output "type=image,name=$CI_REGISTRY_IMAGE/guardian-cli:cli-$CI_COMMIT_SHA,push=true" \
    --metadata-file artifacts/image-metadata.json
