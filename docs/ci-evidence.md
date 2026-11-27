# Demo HTTPS and CI evidence

## Local HTTPS viewer

Create the normal Compose environment first. Use a certificate trusted by the browser for the selected hostname. Store `fullchain.pem` and `privkey.pem` outside the repository, then start the HTTPS overlay.

```powershell
$env:GUARDIAN_HTTPS_HOST = 'localhost'
$env:GUARDIAN_TLS_DIR = (Resolve-Path 'path/to/tls').Path
docker compose -f compose.yaml -f compose.https.yaml up -d --build --wait
```

Open `https://localhost:19443`. Only the proxy publishes a port. The app accepts the HTTPS scheme and peer address from the configured edge proxy, issues Secure cookies, and limits reviewer login to ten attempts per peer IP each minute. If a different edge subnet is needed, set `GUARDIAN_HTTPS_SUBNET` and `GUARDIAN_HTTPS_PROXY_IP` together and reserve the proxy address.

## Verify a published report

Publishing saves the report and its claimed provenance. In the report detail, a project Maintainer can select **Verify CI evidence** for reports with a CI provider. Guardian checks that provider independently and records the timestamp, report hash, image digest and commit SHAs if the evidence matches. A verified report retains its original verdict. The report stays unverified if the artifact is missing, expired, or differs from the uploaded report. Reports published before provider metadata was added remain readable.

Configure a trusted source separately for each Guardian project in the Guardian API process. In the names below, replace `<project-id>` with the Guardian project UUID.

| Environment variable suffix under `Guardian__Ci__Projects__<project-id>__` | Value |
|---|---|
| `GitHub__Repository` | GitHub repository in `owner/repository` form |
| `GitHub__AppId` | Numeric ID of the App that attests the `guardian-contract` check |
| `GitHub__ImageDigest` | Expected Guardian OCI digest in `sha256:…` form |
| `GitHub__Token` | Repository-scoped read credential with Actions and Checks read access |
| `GitLab__ProjectId` | Numeric GitLab project ID |
| `GitLab__ImageDigest` | Expected Guardian OCI digest in `sha256:…` form |
| `GitLab__Token` | Project-scoped `read_api` credential |

Inject credentials into the API service through a private Compose override or the runtime secret mechanism. Enable the entries for the providers used by the project. Guardian trusts the configured GitHub App to bind its check on the candidate commit to one artifact ID. For GitLab, protect `main` and its CI definition. Guardian checks the fixed `guardian_release_evidence` job, pipeline source, ref and commit. Keep the expected digest synchronized with the reviewed pipeline configuration and verify reports during the artifact's retention window.
