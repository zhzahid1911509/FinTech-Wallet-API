# Validation — v0.4.0

Validated on 2026-10-26 with .NET SDK 10.0.402 / runtime 10.0.13 and a temporary SQL Server 2025 Developer instance (17.0.4065.4). Test databases were isolated and deleted by the suite.

| Check | Result |
|---|---|
| Release build of API and tests | Passed; 0 warnings, 0 errors |
| Complete test suite | 101 passed; 0 failed; 0 skipped |
| Database-independent tests | 51 passed |
| Real SQL Server integration tests | 50 passed |
| Runtime and design-time models vs snapshot | Both match |
| EF CLI pending model check | No pending changes |
| Existing v0.1.0/v0.2.0/v0.3.0 migration files | Byte-for-byte unchanged |
| Existing-data upgrade test | Passed: balances/ledger preserved, old sessions revoked |
| Idempotent SQL migration script | Generated in `docs/schema.sql` |
| NuGet vulnerability check including transitive API dependencies | No vulnerable packages reported by the configured feed at check time |

## What was exercised

Financial integration: balanced transfers, stable idempotent replay, simultaneous duplicate requests, simultaneous spending without overdraft, rejected transfers, participant-only reads, database-error rollback and migration preservation.

Account integration: registration/verification, account/purpose/expiry checks, single-use tokens, reset with access/refresh-session invalidation, generic recovery responses, persisted resend cooldown, ownership of session revocation, logout-all and persisted login lockout.

Operations integration: operator role/current-password enforcement, freeze/unfreeze effects and audit, immediate role-revocation enforcement, reconciliation detection without automatic repair and owned history filters.

Email integration: encrypted outbox, retry scheduling, successful payload clearing and competing worker claims. Development MIME pickup and encrypted-envelope tamper rejection were tested without an SMTP provider.

The first SQL run exposed EF-wrapped deadlock exceptions escaping the original retry classifier. The handler now inspects the exception chain; the final full suite passes with this correction. An operator test also needed a fresh DI scope for a separate console invocation to avoid stale tracked Identity state.

MFA integration: encrypted enrollment, session revocation, TOTP replay prevention, independent factor lockout, single-use recovery codes, disable/regeneration, refresh freshness and password reset preserving MFA.

Approval integration: distinct reviewer enforcement, expiry, requester role/stamp and wallet-state invalidation, reject/cancel, concurrent TOTP use and atomic rollback.

Transfer risk integration: enrollment requirements, current-password and single-use TOTP proof, binding to amount/key/recipient/session/actor, expiry, recovery-session restrictions, database-failure rollback of approval consumption, completed idempotent replay, session revocation, concurrent approved duplicates, rolling-window expiry and simultaneous spending at the cumulative threshold. Policy unit tests cover inclusive boundaries and invalid settings.

## Limits

- SMTP code compiled and the queue/transport interface was tested; no live provider email was sent or inbox delivery verified.
- SQL Server 2025 was exercised locally. The supplied CI targets SQL Server 2022, but that remote workflow was not executed here.
- No production deployment, sustained load test, external payment integration or disaster-recovery drill was performed.
- Passing tests is not a claim that the application is ready to handle real funds.

## Reproduce

```powershell
dotnet restore Tests/FinTechWalletAPI.Tests.csproj --locked-mode
dotnet build Tests/FinTechWalletAPI.Tests.csproj -c Release --no-restore
# Set WALLET_TEST_SQL to a disposable SQL Server; tests need create/drop database permission.
dotnet test Tests/FinTechWalletAPI.Tests.csproj -c Release --no-build
dotnet ef migrations has-pending-model-changes
dotnet list FinTechWalletAPI.csproj package --vulnerable --include-transitive
```

Without SQL Server, run `dotnet test Tests/FinTechWalletAPI.Tests.csproj --filter 'Category!=SqlServer'`. Do not represent that filtered run as full financial validation.
