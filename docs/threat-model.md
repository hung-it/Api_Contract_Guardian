# Offline gate threat model

| Boundary | Control |
|---|---|
| Baseline and policy | The GitLab gate fetches one protected target commit and extracts both inputs from regular Git blobs. Candidate contract bytes come from the pipeline commit. |
| Local input files | With `--input-root`, Guardian canonicalizes input paths within a trusted directory and rejects symbolic links, reparse points and directories. The gate creates its own input directory. Treat a workspace writable concurrently by other processes as outside this guarantee. |
| OpenAPI | Specs are bounded to 5 MiB, depth 64 and 2,048 references. References use internal JSON Pointers. The adapter passes validated snapshots in a private directory and explicitly disables external references. |
| Engine | The binary comes from an archive checked against a pinned SHA-256. Guardian checks its version, uses a process argument list, and bounds time, stdout and stderr. The CLI and engine can compare with networking disabled. The GitLab job needs network access to fetch the target commit. |
| CI | A digest pins the container and the gate script it contains. The project CI configuration must also be independently maintained for the gate to be mandatory against candidate CI configuration changes. GitLab merge requirements and trusted configuration are verified separately from the job verdict. |

The GitLab demo accepts same-project detached merge requests targeting the configured protected branches. Maintainers check the report target SHA against the current target before merge. Review GitHub and GitLab source SHAs separately when syncing changes between the two services.

## Report store and viewer

The reporting API authenticates CI writes with an expiring, project-scoped bearer token. PostgreSQL stores the token hash and a unique analysis key per project. The API verifies revocation inside the write transaction, then commits the analysis, findings and audit event together. The protected GitLab publication job generates an isolated token and uses a job-local API and disposable database.

The local reviewer uses a same-origin, HttpOnly session cookie and project membership controls each read. Token changes require Maintainer membership and antiforgery validation. Creation returns the token secret once. The audit includes action, actor, project and entity IDs. Local Compose exposes the viewer on loopback with separate volumes for PostgreSQL and session keys. Treat the host, its Docker access and `.env` as trusted.
