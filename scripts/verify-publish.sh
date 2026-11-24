#!/bin/sh
set -eu

: "${PGHOST:?}" "${PGDATABASE:?}" "${PGUSER:?}" "${PGPASSWORD:?}"
export ConnectionStrings__Guardian="Host=$PGHOST;Database=$PGDATABASE;Username=$PGUSER;Password=$PGPASSWORD"
export Guardian__ProjectId=6bc296f9-06f9-468c-9b77-e605ca9103a1
export GUARDIAN_REPORT_TOKEN="$(od -An -v -tx1 -N32 /dev/urandom | tr -d ' \n')"
export ASPNETCORE_URLS=http://127.0.0.1:5189
out=artifacts/publish-acceptance
mkdir -p "$out"

for attempt in $(seq 1 30); do
    if pg_isready -q; then break; fi
    sleep 1
done
pg_isready -q
api=src/Guardian.Api/bin/Release/net10.0/Guardian.Api.dll
cli=artifacts/guardian/guardian.dll
report=artifacts/policy-tests/strict-err/report.json
publish() {
    dotnet "$cli" publish --report "$report" --api-url "$ASPNETCORE_URLS" --project "$Guardian__ProjectId"
}

if publish > "$out/unavailable.json" 2> "$out/unavailable.log"; then
    echo "Expected publish to fail before the API starts." >&2
    exit 1
else
    test "$?" -eq 6
fi

dotnet "$api" migrate
Guardian__BootstrapToken="$GUARDIAN_REPORT_TOKEN" dotnet "$api" bootstrap
dotnet "$api" > "$out/api.log" 2>&1 &
api_pid=$!
trap 'kill "$api_pid" 2>/dev/null || true; wait "$api_pid" 2>/dev/null || true' EXIT
for attempt in $(seq 1 30); do
    if curl -fsS "$ASPNETCORE_URLS/health/ready" >/dev/null 2>&1; then break; fi
    sleep 1
done
curl -fsS "$ASPNETCORE_URLS/health/ready" >/dev/null
publish > "$out/created.json"
publish > "$out/retried.json"
pwsh -NoProfile -Command '
    $created = Get-Content artifacts/publish-acceptance/created.json -Raw | ConvertFrom-Json
    $retry = Get-Content artifacts/publish-acceptance/retried.json -Raw | ConvertFrom-Json
    if (-not $created.analysis_id -or $created.analysis_id -ne $retry.analysis_id -or
        $created.created_at -ne $retry.created_at -or $created.status -ne "FAIL" -or $retry.status -ne "FAIL") { exit 1 }
'
test "$(psql -Atq -v ON_ERROR_STOP=1 -c "SELECT count(*) FROM analyses WHERE status = 'FAIL'")" -eq 1
test "$(psql -Atq -v ON_ERROR_STOP=1 -c "SELECT count(*) FROM analyses")" -eq 1
echo "PASS publish FAIL from artifact, offline failure exit 6, and retry deduplication in PostgreSQL"
