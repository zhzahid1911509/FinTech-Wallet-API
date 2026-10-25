# Changelog

## 0.2.0 — 2026-10-25

- Add mandatory email verification, single-use recovery codes and password reset with session invalidation.
- Add session listing, owned revocation and logout-all with security-stamp binding.
- Add encrypted durable email jobs, multi-worker leases, bounded retries, SMTP/TLS and Development-only pickup.
- Add WalletOperator permission, trusted-console role management and password-confirmed audited wallet freeze/unfreeze.
- Add read-only internal ledger reconciliation and email status counters.
- Add transaction date/type/direction filters and root API status.
- Preserve the initial migration and add AccountSecurityAndOperations; revoke old sessions on upgrade.
- Add model consistency/security tests, locked package dependencies and upgrade documentation.
- Fix transfer retries for SQL deadlocks wrapped by EF Core execution-strategy exceptions, found by running the real SQL Server suite.

## 0.1.0 — 2026-10-25

First functional foundation/internal-transfer milestone from the supplied scaffold.

- Replaced the weather demo with authenticated versioned wallet APIs.
- Added EF Core SQL Server persistence, Identity accounts, migrations and constraints.
- Added JWT sessions, refresh rotation/reuse detection and logout revocation.
- Added atomic BDT transfers, ledger postings, durable idempotency, rowversion protection and whole-transaction retries.
- Added owned history, validation/errors, rate limits, health checks and basic audit records.
- Added empty-database Development-only demo seeding with balanced funding entries.
- Added unit, HTTP smoke and SQL Server integration tests plus CI.
- Corrected README claims and documented deferred features and validation limits.
- Updated Microsoft packages to 10.0.13; pinned System.Security.Cryptography.Pkcs to 10.0.13 to resolve vulnerable transitive 9.0.11 dependency.
