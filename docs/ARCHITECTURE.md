# Architecture and financial behavior

## Modules

- `Program.cs`: composition, HTTP endpoints, authentication, authorization and rate limiting.
- `Contracts`: explicit public DTOs; persistence entities and password hashes are never returned.
- `Services/AuthService`: Identity-backed account authentication and session lifecycle.
- `Services/TransferService`: the single internal money-movement path.
- `Data`: SQL Server EF mappings, constraints, migrations and financial-write validation.
- `Models`: persistence types.
- `Infrastructure`: validation and Problem Details errors.

This is a single deployable application with module boundaries. It is not a distributed or microservice system.

## Transfer algorithm

1. Validate amount, recipient ID and `Idempotency-Key`.
2. Start a SQL Server SERIALIZABLE transaction.
3. Look up `(authenticated actor ID, idempotency key)`. If found, compare the request fingerprint; return the original transfer or 409 on mismatch.
4. Load the sender from the authenticated user, never from a client-provided sender ID.
5. Load the recipient and reject system wallets, self-transfers, frozen wallets, currency mismatches, insufficient funds or balance overflow.
6. Update both balances; create a transfer, one debit, one credit and a posting audit event.
7. Save and commit as one database transaction.
8. On rowversion conflict, deadlock or unique-key contention, roll back, clear tracked state, and retry the whole transaction up to four total attempts with jitter. Exhaustion returns 409.

SERIALIZABLE provides the transfer isolation here; this algorithm does not depend on RCSI. EF SQL Server database creation may enable RCSI for default read-committed operations. Rowversion is an additional stale-write guard. Contention can reduce throughput; load testing and workload measurements precede alternative locking strategies.

After a timeout or ambiguous result, repeat the SAME payload with the SAME idempotency key. Do not generate a new key for a retry. Idempotency records are durable and are not expired in this release. Rejected requests that never commit do not reserve the key.

The same key is scoped to one actor and the transfer operation. Keys are case-sensitive. Amounts are decimal BDT with two places; numerically equivalent amounts such as 1.0 and 1.00 share a fingerprint. JSON amounts are numbers, not strings.

## Ledger

Customer wallet balances represent liabilities: debit reduces the balance and credit increases it. Each transfer has exactly two same-currency entries whose debit/credit amounts match its amount. Balance is a materialized projection updated with the entries.

EF SaveChanges rejects edits/deletions to transfers, ledger entries and audit events, orphan ledger additions, and malformed pairs. SQL constraints enforce positive amounts, distinct transfer wallets, nonnegative customer balances, and one-sided ledger lines. Database administrators can bypass application protections; configure least-privilege database grants before deployment. Cross-row ledger balancing is enforced by the application, not a database-wide trigger.

This version posts internal transfers immediately. `Posted` is the only exposed successful status; external pending settlement, holds and reversals are not implemented. There is no API to edit a balance.

Demo seeding creates a system offset account with -20,000 BDT and two customer accounts with +10,000 BDT each. It is a development fixture, not a real deposit or settlement mechanism.

## Authentication

ASP.NET Core Identity owns password hashing, password policy and account lockout. Access tokens are HS256-signed JWTs with validated issuer, audience, algorithm and expiration. The signed `sub` and `sid` claims identify the user/session. JWT validation queries the session database so revocation is checked on the next request (already executing requests are not retroactively cancelled).

Refresh tokens are 64 random bytes; only SHA-256 hashes are stored. Refresh consumes the old token and creates a new token inside a serializable transaction. Used tokens are retained to detect replay; replay revokes the session. Sessions expire after seven days by default and rotation does not extend that absolute expiry. Access tokens last ten minutes by default.

Clients must serialize refresh calls. Retrying a refresh whose response was lost may trigger replay revocation; sign in again. Logout revokes only the current session. Token responses use `Cache-Control: no-store`.

## Limitations requiring follow-up

- Email verification/recovery, session APIs and the WalletOperator role are implemented in v0.2.0. MFA, dual approval and a session-management UI remain.
- Account-registration conflicts currently reveal that an account exists; align this with the eventual onboarding/privacy policy.
- Login timing is not equalized for unknown accounts; rate limiting is a basic single-process control.
- Rate limiting is per remote IP for auth and per user for wallet operations, in memory. Configure trusted reverse proxies explicitly before using forwarded client IPs; do not trust arbitrary forwarded headers. Multi-instance deployments need shared enforcement.
- Audit records cover successful registration/session and financial actions, not every rejected request or administrative event.
- A scoped durable email outbox and on-demand internal reconciliation are implemented. A general financial-event outbox, payment provider, scheduled reconciliation worker, holds, refunds and fraud engine remain.
- No production deployment or live-money certification has been performed.


## v0.2.0 security additions

See ACCOUNT-SECURITY.md for the current account, email and operator behavior. Login requires confirmed email. Sessions are bound to the Identity security stamp, checked during access-token and refresh-token validation. Password reset and logout-all revoke sessions and rotate the stamp, preventing concurrently issued stale sessions from remaining usable. Failed-login counters are committed even when login returns 401.

Email challenge hashes, account changes and encrypted outbox payloads share the same SQL transaction. Queue workers use conditional atomic claims and expiring leases. Email transport is outside the transaction; delivery is at least once. Terminal payloads are cleared. Keys are managed through ASP.NET Core Data Protection, separate from JWT signing.

Account recovery returns generic eligibility-independent messages, but timings are not equalized. Registration still exposes duplicate accounts via 409. Rate limiting remains per-process and needs a distributed strategy before multi-instance production use. Recovery is email-only; MFA recovery and account compromise review are later work.

Reconciliation checks ledger-to-balance projections and per-transfer posting structure; it is an internal consistency check, not external settlement reconciliation. Operator wallet state changes use current-password confirmation, database role checks and an audited reason. Console role changes identify the affected account; OS administrative identity must be captured by deployment audit infrastructure.
