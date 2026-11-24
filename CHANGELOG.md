# Changelog

## 1.0.0

- Added offline OpenAPI validation and backward-compatibility checks with a pinned oasdiff engine and versioned policy.
- Added reproducible JSON and Markdown reports with findings, stable fingerprints and source provenance.
- Added a GitLab contract gate that evaluates merge requests against the protected baseline and policy. The FlowForge integration exercises a breaking response change and a compatible fix.
- Added separate publication into PostgreSQL with project-scoped tokens and idempotent retries.
- Added project-scoped analysis history, a React reviewer, token revocation, audit events and Docker Compose for local evaluation.
- Matched the publication timestamp to PostgreSQL precision so retries return the same receipt.
