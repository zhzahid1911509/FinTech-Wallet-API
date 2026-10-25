# Roadmap after v0.2.0

## Completed milestones

v0.1.0: wallet/Identity database, authenticated internal BDT transfers, paired ledger records, idempotency, concurrency retries, history, demo seeding, tests and CI.

v0.2.0: email verification and password recovery, expiring single-use codes, encrypted durable email delivery with retries, session management and stamp-based invalidation, operator freeze/unfreeze with password confirmation/audit, on-demand internal reconciliation, filtered history, additive migration and dependency locks.

## Next priority

1. MFA enrollment, challenge/recovery codes and step-up verification; dual approval for sensitive operational changes.
2. Observability: structured failure audit, metrics/alerts, email dead-letter operations and scheduled reconciliation.
3. Distributed rate limiting, trusted proxy configuration and measured concurrency/load testing.
4. Key rotation, least-privilege database grants, append-only storage controls, backups and restore drills.
5. Retention/cleanup of used challenges, expired sessions, refresh tokens and completed email records; old tokens must remain detectable for the required replay window.
6. Statements/receipts and support/admin workflow improvements.

## External payments milestone

Provider abstraction, authenticated deduplicated webhooks, pending/settled state machines, available/held balances, a financial transactional outbox and idempotent consumers, settlement reconciliation and ambiguity resolution. No public endpoint may credit funds based on a client assertion.

## Later product expansion

Beneficiaries, payment requests, merchant/QR payments, authorized reversals/refunds, configurable fees/limits, scheduled payments and multi-currency accounting. Each requires specific authorization and accounting tests. Real-funds operation requires deployment, operational and applicable compliance work beyond these code milestones.
