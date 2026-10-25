# Changelog

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
