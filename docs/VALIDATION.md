# Validation — v0.1.0

Validation performed on 2026-10-25 with .NET SDK 10.0.402 and runtime 10.0.13.

| Check | Result |
|---|---|
| Release build of API and test project | Passed; 0 warnings, 0 errors |
| Database-independent tests | 18 passed; 0 failed; 0 skipped |
| EF model vs migration snapshot | No pending model changes |
| SQL migration script generation | Passed; `docs/schema.sql` included |
| NuGet vulnerability query including transitive API packages | No vulnerable packages reported by the configured feed at validation time |
| SQL Server integration tests | 9 supplied and compiled; NOT executed in this environment |
| Live deployment, provider integration or load test | Not performed |

The database-independent suite covers amount limits/precision, key validation, culture-independent request fingerprints, append-only/paired ledger guards, application startup, unauthenticated access denial, registration validation and OpenAPI generation.

The SQL Server suite covers registration/duplicate accounts, balanced transfers and replay, concurrent spending, concurrent duplicates, rejected transfers, transaction ownership, refresh replay, logout revocation, and rollback after an injected database constraint failure. It uses actual SQL Server transactions and rowversion semantics. It requires `WALLET_TEST_SQL`; there was no SQL Server service available during this delivery.

Build/unit success does not establish that the SQL integration suite passes. Run the full suite before deployment or accepting the financial milestone. The supplied CI workflow automates that step but has not been executed remotely by this delivery.

Commands used:

```text
dotnet build Tests/FinTechWalletAPI.Tests.csproj -c Release
dotnet test Tests/FinTechWalletAPI.Tests.csproj -c Release --no-build --filter Category!=SqlServer
dotnet ef migrations has-pending-model-changes --no-build
dotnet ef migrations script --idempotent --no-build --output docs/schema.sql
dotnet list FinTechWalletAPI.csproj package --vulnerable --include-transitive --format json
```
