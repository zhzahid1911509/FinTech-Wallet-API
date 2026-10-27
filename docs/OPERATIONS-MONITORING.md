# Operational monitoring — v0.5.0

This release implements scheduled internal reconciliation, durable run history, a protected JSON monitoring snapshot, audit search and failed-email metadata inspection. It does not connect an external alert provider or send alert emails. Passkeys, recovery after total factor loss, distributed rate limiting and production load measurements remain future milestones.

## Scheduled reconciliation

The API hosts a background worker. Every ten seconds it checks whether the shared SQL schedule is due. The first run is due after migration/startup; subsequent runs are scheduled from completion, 15 minutes apart by default. No historical missed intervals are backfilled.

`Reconciliation` settings:

| Setting | Default | Validation |
|---|---|---|
| WorkerEnabled | true | Set false to disable the worker on that instance |
| IntervalSeconds | 900 | 60–86400 |
| TimeoutSeconds | 60 | 5–300 |
| StaleAfterSeconds | 3600 | At least IntervalSeconds + TimeoutSeconds + 30; at most 172800 |
| EmailBacklogMinutes | 15 | 1–1440 |

Override using user-secrets or environment variables, for example `Reconciliation__IntervalSeconds=1800`. All worker instances should use the same configuration and synchronized UTC clocks. Restart after configuration changes. WorkerEnabled=false is allowed for dedicated worker/application layouts; ensure at least one continuously running instance has it enabled. Monitoring reports the local instance's configured value, not a cluster-wide worker inventory.

An atomic database update claims one singleton schedule row with a unique lease ID. Claim and creation of the Running record commit together. The lease lasts TimeoutSeconds + 30 seconds. Checking runs outside the claim transaction; the financial check itself uses its existing SERIALIZABLE read transaction. The cancellation budget applies to the complete check; provider command timeouts can fail earlier.

Only the unexpired current lease owner may publish a result. Completion, release of the lease, scheduling of the next run and the completion audit record commit together. A crash leaves a Running record; after lease expiry, the next claimant marks it Abandoned. A late worker cannot publish over the replacement owner's result. A stalled check may briefly overlap a replacement check after lease expiry if cancellation is delayed; both checks are read-only and stale result publication is fenced. This is not an exactly-once job-execution guarantee.

Run states are Running, Healthy, Unhealthy, Failed and Abandoned. Healthy means the balance/ledger and posting-shape checks passed. Unhealthy means one or more mismatches were found. Failed means the check could not complete; it is not evidence that balances are correct or incorrect. Abandoned means a previous owner's lease expired before completion. Reports retain exact mismatch counts and up to 100 details for each mismatch category. No automatic balance repair occurs.

A run uses SERIALIZABLE reads and can block writers on a large ledger. Tune the interval/timeout after measurement. An always-on host is required; sleeping web hosts do not execute background work. The existing on-demand `/operations/reconciliation` and `--reconcile` command remain read-only checks and do not create scheduled-run history or participate in its lease.

## Operator endpoints

All endpoints below require a valid Bearer session and the current WalletOperator role. Responses use Cache-Control: no-store. Read-only access does not require recent MFA; wallet mutations retain their separate MFA requirements. There are no public financial-monitoring endpoints.

| GET path under /api/v1 | Purpose |
|---|---|
| /operations/monitoring | Snapshot of reconciliation status, email backlog and security counters |
| /operations/reconciliation-runs?page=1&pageSize=20 | Run summaries, newest start first |
| /operations/reconciliation-runs/{id} | Run status and bounded mismatch report |
| /operations/audit-events | Search audit records |
| /operations/email-jobs?state=failed | Inspect delivery metadata |

List endpoints accept page 1–100000 and pageSize 1–100 (default 20), return hasMore and order deterministically by timestamp then ID. Offset pagination is intended for an operator UI, not a lossless streaming export; concurrent inserts can shift pages.

Audit filters: `from` (inclusive), `to` (exclusive), exact `action`, `actorId`, `targetId`. Example: `/api/v1/operations/audit-events?action=Transfer.RiskDenied&pageSize=50`. No filter returns the latest records. Date ranges must have from < to. Background reconciliation events use the all-zero actor GUID to mean the system, with the run ID as target. Operator-provided reasons remain potentially sensitive; restrict access to operator accounts.

Email states: failed (terminal), pending (not sent/failed and not expired), expired (expired and not yet marked terminal), sent. Only IDs, timestamps, attempt counts and exception type are exposed. Recipient addresses, subject/body, encrypted payload, account codes and lease identifiers are not returned. Do not add payload logging for troubleshooting.

There is deliberately no blind retry endpoint for terminal emails: the worker clears terminal payloads, and replaying expired verification/reset tokens would be incorrect. Fix SMTP, then use the existing account resend/forgot-password flow to generate a fresh eligible challenge. Those endpoints retain their rate/cooldown limits. Old failed rows remain inspectable.

## Monitoring and alert integration

`/operations/monitoring` returns checkedAt, attentionRequired, alerts, reconciliation, email and securityLast24Hours. It is a custom JSON snapshot, not a Prometheus scrape endpoint. Queries are read independently, so counters are approximate under concurrent writes rather than one consistent financial snapshot. Use reconciliation for financial consistency.

Alert codes:

- reconciliation_worker_disabled: worker disabled on the serving instance.
- reconciliation_never_completed: no terminal scheduled run exists.
- reconciliation_unhealthy / reconciliation_failed / reconciliation_abandoned: latest terminal run needs review.
- reconciliation_stale: latest terminal completion is older than the configured threshold.
- reconciliation_lease_expired: a Running owner's lease expired before recovery.
- email_failed: at least one terminal failure in the last 24 hours.
- email_expired: expired jobs have not yet been finalized by the email worker.
- email_backlog: unexpired pending jobs are older than EmailBacklogMinutes.

The email section includes total historical failed jobs and failedLast24Hours separately, so historical failures do not trigger an alert forever. Security counters cover known-account login failures, MFA failures, transfer risk denials and refresh-token replay over the last 24 hours. They describe persisted events, not all HTTP errors. Unknown-account login attempts are not persisted as account audit records. No arbitrary request bodies, passwords, tokens or database exception messages are recorded by this implementation.

The API logs structured reconciliation run IDs, status, counts and sanitized error types. Configure your log collector/monitor to route warnings and poll the protected endpoint with an authorized session. External collector setup, credential rotation, dashboards, alert delivery/acknowledgment and escalation policies are deployment work and were not performed here. Monitor the API externally as well: a process or SQL outage cannot reliably report through its own database-backed endpoint.

Health/live continues to indicate process liveness. Health/ready checks SQL connectivity and applied migrations. Neither endpoint exposes wallet mismatches or becomes unhealthy solely because a reconciliation discrepancy exists.

## Incident checklist

1. For Unhealthy, inspect the saved run and identify affected wallets/transfers; preserve evidence.
2. Review audit history and use the existing authenticated freeze workflow if warranted by the incident.
3. Investigate ledger, application and database changes; never edit balances solely to make the report green.
4. For Failed/Abandoned/Stale, check database availability, command timeouts, worker logs and always-on hosting.
5. For email issues, inspect metadata, validate SMTP configuration and generate fresh eligible account challenges after repair.

Reports, audit records and email metadata are retained without automatic cleanup in this release. Establish a reviewed retention policy before large-scale deployment.
