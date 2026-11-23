#!/bin/sh
set -eu

if [ "${CI_PIPELINE_SOURCE:-}" != "merge_request_event" ] ||
   [ "${CI_MERGE_REQUEST_EVENT_TYPE:-}" != "detached" ] ||
   [ "${CI_MERGE_REQUEST_SOURCE_PROJECT_ID:-}" != "${CI_PROJECT_ID:?}" ] ||
   [ "${CI_MERGE_REQUEST_TARGET_BRANCH_PROTECTED:-}" != "true" ]; then
    echo "Guardian requires a same-project detached merge request targeting a protected branch." >&2
    exit 5
fi

target=${CI_MERGE_REQUEST_TARGET_BRANCH_NAME:?}
case "$target" in
    main|demo/week3-missing-baseline) ;;
    *) echo "Unsupported Guardian demo target." >&2; exit 5 ;;
esac

cd "${CI_PROJECT_DIR:?}"
mkdir -p reports
inputs=$(mktemp -d)
trap 'rm -f "$inputs/base.yaml" "$inputs/candidate.yaml" "$inputs/policy.yml"; rmdir "$inputs"' EXIT

repo_git() {
    git -c "safe.directory=$CI_PROJECT_DIR" "$@"
}

repo_git fetch --no-tags --depth=1 origin "refs/heads/$target"
GUARDIAN_BASELINE_SHA=$(repo_git rev-parse 'FETCH_HEAD^{commit}')
GUARDIAN_CANDIDATE_SHA=$(repo_git rev-parse "${CI_COMMIT_SHA:?}^{commit}")
export GUARDIAN_BASELINE_SHA GUARDIAN_CANDIDATE_SHA
printf 'Guardian target %s at %s; candidate %s\n' "$target" "$GUARDIAN_BASELINE_SHA" "$GUARDIAN_CANDIDATE_SHA"

extract_if_present() {
    if repo_git cat-file -e "$1" 2>/dev/null; then
        repo_git show "$1" > "$2"
    fi
}

extract_if_present "$GUARDIAN_BASELINE_SHA:openapi/demo.yaml" "$inputs/base.yaml"
extract_if_present "$GUARDIAN_BASELINE_SHA:.guardian.yml" "$inputs/policy.yml"
extract_if_present "$GUARDIAN_CANDIDATE_SHA:openapi/demo.yaml" "$inputs/candidate.yaml"

guardian compare --base "$inputs/base.yaml" --candidate "$inputs/candidate.yaml" \
    --policy "$inputs/policy.yml" --out reports
