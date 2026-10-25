# Roadmap after v0.1.0

This delivery starts the previously proposed plan; it does not complete every phase.

## Implemented milestone

Database foundation, Identity-backed registration/login, rotating token sessions, own-wallet retrieval, atomic internal transfers, ledger entries, idempotency, concurrency retries, history, basic audit and operational endpoints. Initial migration, demo data, meaningful tests and CI are included.

## Next: complete account and operational foundations

1. Run the supplied SQL Server integration suite and fix any environment-specific failures; require it in branch protection.
2. Email verification and password recovery using an actual delivery provider, one-time tokens, expiry, abuse controls and session invalidation after reset.
3. MFA/step-up verification, session listing/revoke-all and explicit admin/support permissions.
4. Controlled wallet freeze/unfreeze workflow with audited permissions; current transfer code respects stored freeze flags but there is no admin endpoint yet.
5. Formal ledger reconciliation command/report and operational alerts.
6. Date/type transaction filters and statements; current history supports bounded pagination only.
7. Dependency lock files and update policy, secret scanning, safe deployment migrations and managed secret rotation.

## Then: operational readiness

Structured telemetry, service-level metrics, trusted proxy configuration, distributed rate limiting, failure-event audit, retention policy, backup/restore drills, load tests, immutable storage/database access policy and dual approval for financial adjustments.

## Then: external payments

Provider abstraction, authenticated deduplicated webhooks, pending/settled state machines, balance holds, transactional outbox and idempotent inbox/consumers, settlement reconciliation and ambiguity resolution. No public endpoint should credit a balance based on a client assertion.

## Later product expansion

Beneficiaries, payment requests, merchant/QR payments, authorized reversals/refunds, configurable fees/limits, scheduled payments and multi-currency accounting. Each requires its own authorization and accounting tests.
