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
3. Recheck the session and security stamp inside the transaction. Look up `(authenticated actor ID, idempotency key)`. If found, compare the request fingerprint; return the original transfer or 409 on mismatch.
4. Load the sender from the authenticated user, never from a client-provided sender ID.
5. Load the recipient and reject system wallets, self-transfers, frozen wallets, currency mismatches, insufficient funds or balance overflow.
6. Evaluate current risk from posted transfers and session assurance. Require a valid transaction-bound authorization if risky (and validate any supplied authorization). Record risk denials without reserving the idempotency key.
7. Update both balances; create a transfer, one debit, one credit, risk/posting audit events and consume the authorization if supplied.
8. Save and commit as one database transaction.
9. On rowversion conflict, deadlock or unique-key contention, roll back, clear tracked state, and retry the whole transaction up to four total attempts with jitter. Exhaustion returns 409.

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

- Email verification/recovery, session APIs and the WalletOperator role are implemented in v0.2.0. MFA and two-person unfreezing are added in v0.3.0; a session-management UI remains.
- Account-registration conflicts currently reveal that an account exists; align this with the eventual onboarding/privacy policy.
- Login timing is not equalized for unknown accounts; rate limiting is a basic single-process control.
- Rate limiting is per remote IP for auth and per user for wallet operations, in memory. Configure trusted reverse proxies explicitly before using forwarded client IPs; do not trust arbitrary forwarded headers. Multi-instance deployments need shared enforcement.
- Audit records cover successful registration/session and financial actions, not every rejected request or administrative event.
- A scoped durable email outbox and on-demand internal reconciliation are implemented. A general financial-event outbox, payment provider, holds, refunds and fraud engine remain. Scheduled reconciliation is added in v0.5.0.
- No production deployment or live-money certification has been performed.


## v0.2.0 security additions

See ACCOUNT-SECURITY.md for the current account, email and operator behavior. Login requires confirmed email. Sessions are bound to the Identity security stamp, checked during access-token and refresh-token validation. Password reset and logout-all revoke sessions and rotate the stamp, preventing concurrently issued stale sessions from remaining usable. Failed-login counters are committed even when login returns 401.

Email challenge hashes, account changes and encrypted outbox payloads share the same SQL transaction. Queue workers use conditional atomic claims and expiring leases. Email transport is outside the transaction; delivery is at least once. Terminal payloads are cleared. Keys are managed through ASP.NET Core Data Protection, separate from JWT signing.

Account recovery returns generic eligibility-independent messages, but timings are not equalized. Registration still exposes duplicate accounts via 409. Rate limiting remains per-process and needs a distributed strategy before multi-instance production use. Recovery is email-only; Single-use MFA recovery codes are added in v0.3.0; recovery without any factor and account compromise review remain later work.

Reconciliation checks ledger-to-balance projections and per-transfer posting structure; it is an internal consistency check, not external settlement reconciliation. Operator wallet state changes use current-password confirmation, database role checks and an audited reason. Console role changes identify the affected account; OS administrative identity must be captured by deployment audit infrastructure.


## v0.3.0 authenticator and approval security

MFA uses Otp.NET TOTP verification with SHA-1/6 digits/30 seconds and an adjacent-window tolerance of one step. Secrets are protected with a separate Data Protection purpose. The last accepted step is persisted and strictly increases; recovery codes have 128 bits of entropy and are stored as hashes. Factor failures have a separate persisted lockout so successful password verification cannot clear the factor's failed-attempt counter. Factor consumption and session creation share a serializable transaction.

Session `MfaVerifiedAt` and `AuthenticationMethod` are authoritative server-side values; clients cannot provide them. Recovery login does not satisfy operator step-up. Refresh never renews the MFA timestamp. Password reset preserves enrolled MFA. Enabling/disabling MFA or replacing recovery codes rotates the security stamp and revokes sessions.

Wallet unfreezing uses a persisted request bound to the requester security stamp and wallet state version. A distinct current operator with recent TOTP must approve within 30 minutes. The request decision, wallet state and audit records commit in one serializable transaction. A unique filtered index allows one Pending request per wallet; a SQL constraint prevents an Approved request from naming its requester as reviewer. Mutable approval state has rowversion protection, while audit events remain append-only through the application context.

Freeze remains immediate for a recently authenticated operator. Two-person approval protects unfreezing only; future adjustments and payouts need their own approval designs. TOTP remains susceptible to real-time phishing; passkeys and device-bound credentials are future options.


## v0.4.0 transfer risk

See TRANSFER-RISK.md. Approval creation verifies current password and a fresh TOTP, then stores only session/key/fingerprint/expiry. Execution rechecks the live session, balance/freeze rules and current risk; approval consumption shares the posting transaction. An expired or mismatched approval cannot be substituted by recent login assurance. Posted replay is resolved before risk evaluation so response loss does not require a second approval or debit. This is a rules-based development policy, not a fraud-detection engine.


## v0.5.0 operational monitoring

A BackgroundService creates a scope for each scheduler poll. SQL conditionally claims one schedule lease and creates a Running record atomically. The check runs read-only; only an unexpired lease token may atomically store results, completion audit, release and next due time. Expired owners become Abandoned when replaced. Late results cannot overwrite the replacement. Cancellation is cooperative, so expired checks may overlap briefly without financial writes.

Monitoring endpoints are role-protected and return explicit projections. Email payloads are never exposed. Snapshot counters are approximate under concurrency; reconciliation retains its consistent financial read. A synthetic all-zero actor ID identifies background completion audit events. Known-account login failures are persisted without passwords or detailed credential-failure reasons. No external alert delivery is configured. See OPERATIONS-MONITORING.md for operational limits and setup.
