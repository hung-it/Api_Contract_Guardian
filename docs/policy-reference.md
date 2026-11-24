# Compatibility policy v1

```yaml
version: 1
compatibility:
  fail_on: [ERR]
  fail_on_unknown: true
```

Run `guardian compare --base base.json --candidate candidate.json --policy .guardian.yml --out reports`. Without `--policy`, Guardian uses the policy above. An explicit missing or invalid policy returns exit `5` and writes `INVALID_POLICY` when an output directory is supplied.

`fail_on` accepts `INFO`, `WARN` and `ERR`. The lowest severity in the list is the threshold. For example, `[WARN, ERR]` blocks WARN and ERR findings. `rules` optionally maps known rule IDs to a severity before the threshold is evaluated. The supported IDs are:

- `endpoint-added`
- `api-path-removed-without-deprecation`
- `api-removed-without-deprecation`
- `request-parameter-became-required`
- `new-required-request-property`
- `request-property-enum-value-removed`
- `response-property-type-changed`
- `response-required-property-removed`

An unmapped finding stays UNKNOWN. With `fail_on_unknown: true`, UNKNOWN yields `INDETERMINATE` and exit `2`, even if there are ERR findings. With `false`, known findings decide the verdict. A known finding at or above the effective threshold yields `FAIL` and exit `2`. Otherwise the report is `PASS` and the exit code is `0`.

An invalid spec yields `INDETERMINATE` and exit `3`. Engine errors yield `INDETERMINATE` and exit `4`. The JSON and Markdown reports include the effective findings and the SHA-256 of the policy bytes used. In CI, the baseline and policy come from a single protected target commit.
