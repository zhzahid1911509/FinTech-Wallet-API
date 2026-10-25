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

SERIALIZABLE provides the isolation here; this release does not enable or claim RCSI. Rowversion is an additional stale-write guard. Contention can reduce throughput; load testing and workload measurements precede alternative locking strategies.

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

- No email verification/recovery delivery, MFA, role-based operations endpoints or session management UI.
- Account-registration conflicts currently reveal that an account exists; align this with the eventual onboarding/privacy policy.
- Login timing is not equalized for unknown accounts; rate limiting is a basic single-process control.
- Rate limiting is per remote IP for auth and per user for wallet operations, in memory. Configure trusted reverse proxies explicitly before using forwarded client IPs; do not trust arbitrary forwarded headers. Multi-instance deployments need shared enforcement.
- Audit records cover successful registration/session and financial actions, not every rejected request or administrative event.
- No distributed outbox, payment provider, reconciliation worker, holds, refunds or fraud engine.
- No production deployment or live-money certification has been performed.
