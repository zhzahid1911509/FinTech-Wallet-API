# FinTech Wallet API — v0.4.0

An ASP.NET Core 10 / EF Core / SQL Server API implementing the wallet foundation, internal transfers and the account-security, MFA and operational-approval milestones. BDT only. This is a development release, not approval to handle real funds.

## Implemented

- ASP.NET Core Identity registration, password hashing and login lockout (5 failures / 15 minutes).
- Atomic account + zero-balance wallet creation; unique email and one customer wallet per user.
- Signed JWT access tokens, hashed rotating refresh tokens, refresh reuse detection and session revocation.
- Every authenticated request checks session validity. Logout invalidates access and refresh tokens for that session.
- Authenticated wallet balance and participant-only transfer details/history.
- Atomic internal transfer with SQL Server serializable transactions, rowversion checks and bounded whole-transaction retries.
- Caller-scoped durable idempotency keys and request fingerprints; replays return the original transfer.
- Two-sided ledger postings, application-level append-only financial/audit records, database constraints and indexes.
- Amount/precision checks, insufficient-funds prevention, self-transfer and frozen-wallet checks.
- Problem Details errors, basic rate limiting, liveness/readiness and development OpenAPI JSON.
- Initial EF migration, local demo seeding, tests and a GitHub Actions SQL Server test workflow.

## New in v0.4.0

- Server-side risk checks for large amounts, cumulative spending, new recipients and recovery-code sessions.
- Single-use, expiring transfer authorization with current-password and fresh-TOTP proof.
- Authorization bound to session, recipient, amount and idempotency key; consumed atomically with posting.
- Advisory risk assessment, persisted risk audit events and concurrency regression tests.
- Additive migration; existing idempotent replay remains supported.

**Transfer behavior has changed:** see [docs/TRANSFER-RISK.md](docs/TRANSFER-RISK.md) and [docs/UPGRADE-v0.4.0.md](docs/UPGRADE-v0.4.0.md).

## New in v0.3.0

- Authenticator-app TOTP enrollment, code replay prevention and independent MFA lockout.
- One-time recovery codes stored only as hashes; encrypted authenticator secrets.
- MFA-aware login and recent-authenticator step-up for operator mutations.
- Session invalidation and email notifications for MFA configuration changes.
- Two-person wallet unfreezing with expiry, state/security checks and atomic audit records.
- Additive migration, security regression tests and explicit build instructions.

**Build/test/run commands:** [BUILD.md](BUILD.md). **Upgrade:** [docs/UPGRADE-v0.4.0.md](docs/UPGRADE-v0.4.0.md).
**MFA and approval API examples:** [docs/MFA-AND-APPROVALS.md](docs/MFA-AND-APPROVALS.md).
**GitHub commit text:** [COMMIT_MESSAGE.txt](COMMIT_MESSAGE.txt).

## New in v0.2.0

- Email verification required for login; durable verification messages queued during registration.
- Single-use, expiring verification/recovery codes, resend cooldown and generic recovery responses.
- Password reset invalidates all sessions; session list, individual revocation and logout-all.
- Encrypted SQL email outbox, bounded retries, expiring worker leases, SMTP with required TLS and Development-only `.eml` pickup.
- Database-checked WalletOperator permission, password-confirmed freeze/unfreeze with audit reasons.
- Read-only ledger reconciliation through CLI or protected endpoint; email delivery status counters.
- Transaction history date, type and incoming/outgoing filters.
- Root status endpoint, runtime/design-time migration consistency tests and dependency lock files.

**Upgrading an existing v0.1.0 database? Read [docs/UPGRADE-v0.2.0.md](docs/UPGRADE-v0.2.0.md) first.** The initial migration is preserved; the second migration is additive and revokes pre-upgrade sessions. Existing unverified users must verify their email. No users or balances are deleted or automatically marked verified.

**New account verification, password recovery, SMTP setup and operator commands:** [docs/ACCOUNT-SECURITY.md](docs/ACCOUNT-SECURITY.md).

## Requirements

- .NET 10 SDK.
- SQL Server 2022+ (Developer edition or LocalDB is suitable for local use), or Docker for the supplied SQL Server Compose service.
- PowerShell examples below run from the directory containing `FinTechWalletAPI.csproj`.

## 1. Configure local secrets

For Windows SQL Server LocalDB:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" 'Server=(localdb)\MSSQLLocalDB;Database=FinTechWalletDev;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True'

# Generate a fresh random signing key; do not put it in source control.
$keyBytes = New-Object byte[] 64
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
dotnet user-secrets set "Jwt:SigningKey" ([Convert]::ToBase64String($keyBytes))
```

Or start the local SQL Server Docker service:

```powershell
$env:MSSQL_SA_PASSWORD = Read-Host 'Choose a strong local SQL Server password'
docker compose up -d
docker compose logs sqlserver
# Wait until SQL Server reports that it is ready for client connections.
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=FinTechWalletDev;User Id=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
```

Use the signing-key commands above for either database option. `TrustServerCertificate=True` and the SA login are local-development choices only. In deployment, use a trusted certificate and a dedicated least-privilege database login. User-secrets are a development convenience, not an encrypted production vault.

## 2. Restore and migrate

```powershell
dotnet restore
dotnet tool restore
dotnet ef database update
dotnet dev-certs https --trust
```

The migration is included. Do not generate another initial migration. The API does not auto-migrate on startup. Existing scaffolds have no migration history; use a new `FinTechWalletDev` database. A database manually created with other schemas requires a separately reviewed migration path.

## 3. Optional demo accounts and test funds

Run this BEFORE registering any users, against an empty migrated database:

```powershell
$env:Demo__Password = Read-Host 'Choose a demo password (12+ characters, upper/lowercase, digit and symbol)'
dotnet run --launch-profile https -- --seed-demo
```

This creates explicitly preverified development fixtures `alice@example.test` and `bob@example.test`, each with 10,000 BDT of simulated funds, using your chosen password. The offsetting system wallet and ledger entries preserve the accounting balance. Seeding refuses non-Development environments and databases containing users/wallets. It never tops up existing users. There is no public deposit/credit endpoint.

## 4. Start

```powershell
dotnet run --launch-profile https
```

- API/status: `https://localhost:7081`
- Liveness: `https://localhost:7081/health/live`
- Readiness: `https://localhost:7081/health/ready`
- Development OpenAPI JSON: `https://localhost:7081/openapi/v1.json`

Import OpenAPI into your API client or use `FinTechWalletAPI.http`. Swagger UI is not bundled. The old `/weatherforecast` sample has been removed.

## 5. Try a transfer in PowerShell

Keep the API running in one terminal. In a second terminal:

```powershell
$base = 'https://localhost:7081'
$demoPassword = Read-Host 'Enter the demo password used for seeding'
$alice = Invoke-RestMethod "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{ email='alice@example.test'; password=$demoPassword } | ConvertTo-Json)
$bob = Invoke-RestMethod "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{ email='bob@example.test'; password=$demoPassword } | ConvertTo-Json)
$aliceHeaders = @{ Authorization="Bearer $($alice.accessToken)" }
$bobHeaders = @{ Authorization="Bearer $($bob.accessToken)" }
$bobWallet = Invoke-RestMethod "$base/api/v1/wallets/me" -Headers $bobHeaders
$transferHeaders = @{ Authorization="Bearer $($alice.accessToken)"; 'Idempotency-Key'=[guid]::NewGuid().ToString() }
$body = @{ recipientWalletId=$bobWallet.id; amount=500.00 } | ConvertTo-Json
Invoke-RestMethod "$base/api/v1/transfers" -Method Post -Headers $transferHeaders -ContentType 'application/json' -Body $body
# Repeat exactly the same request: same transfer ID, no second debit.
Invoke-RestMethod "$base/api/v1/transfers" -Method Post -Headers $transferHeaders -ContentType 'application/json' -Body $body
Invoke-RestMethod "$base/api/v1/wallets/me" -Headers $aliceHeaders
Invoke-RestMethod "$base/api/v1/transactions?page=1&pageSize=20" -Headers $aliceHeaders
```

Expected balances: Alice 9,500 BDT; Bob 10,500 BDT. New registered accounts start at zero and must verify their email before login. In Development, open the queued message in `App_Data/mail` and submit its token to `/api/v1/auth/confirm-email`; see the account-security guide.

## API summary

| Method | Path | Authorization | Purpose |
|---|---|---|---|
| POST | `/api/v1/auth/register` | Public, limited | Register + create wallet |
| POST | `/api/v1/auth/login` | Public, limited | Create a session/token pair |
| POST | `/api/v1/auth/refresh` | Refresh token | Rotate token; reject reuse |
| POST | `/api/v1/auth/resend-verification` | Public, limited | Queue another verification email |
| POST | `/api/v1/auth/confirm-email` | Single-use code | Confirm email ownership |
| POST | `/api/v1/auth/forgot-password` | Public, limited | Queue recovery email for an eligible account |
| POST | `/api/v1/auth/reset-password` | Single-use code | Reset password and revoke sessions |
| GET | `/api/v1/auth/sessions` | Bearer | List up to 100 active own sessions |
| DELETE | `/api/v1/auth/sessions/{id}` | Owner | Revoke a session |
| POST | `/api/v1/auth/logout-all` | Bearer | Revoke all own sessions |
| PUT | `/api/v1/operations/wallets/{id}/state` | Operator + password + recent TOTP | Freeze with an audit reason |
| POST | `/api/v1/operations/wallets/{id}/unfreeze-requests` | Operator + password + recent TOTP | Request unfreezing |
| POST | `/api/v1/operations/unfreeze-requests/{id}/{decision}` | Operator + password + recent TOTP | Approve/reject as a different operator, or cancel as requester |
| GET | `/api/v1/auth/mfa/status` | Bearer | MFA status and remaining-code count |
| POST | `/api/v1/auth/mfa/enrollment` | Bearer + password | Begin authenticator setup |
| POST | `/api/v1/auth/mfa/confirm` | Bearer + TOTP | Enable MFA; return recovery codes once |
| POST | `/api/v1/auth/mfa/step-up` | Bearer + password + TOTP | Verify recent MFA |
| POST | `/api/v1/auth/mfa/recovery-codes` | Bearer + password + TOTP | Replace recovery codes |
| POST | `/api/v1/auth/mfa/disable` | Bearer + password + factor | Disable MFA and revoke sessions |
| GET | `/api/v1/operations/reconciliation` | Operator | Check ledger consistency |
| GET | `/api/v1/operations/email-status` | Operator | Delivery counts, never message content |
| POST | `/api/v1/auth/logout` | Bearer | Revoke current session |
| GET | `/api/v1/wallets/me` | Bearer | Own wallet ID and balance |
| POST | `/api/v1/transfers` | Bearer + `Idempotency-Key` | Send BDT |
| GET | `/api/v1/transfers/{id}` | Participant | Transfer details |
| GET | `/api/v1/transactions` | Bearer | Own paginated history |
| GET | `/health/live` | Public | Process liveness |
| GET | `/health/ready` | Public | Database connectivity + migrations |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for transaction guarantees and [docs/ROADMAP.md](docs/ROADMAP.md) for remaining work.

## Tests

Fast tests (no database):

```powershell
dotnet test Tests/FinTechWalletAPI.Tests.csproj --filter 'Category!=SqlServer'
```

Full suite using a disposable SQL Server instance:

```powershell
$env:WALLET_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True'
dotnet test Tests/FinTechWalletAPI.Tests.csproj
```

The SQL tests create and delete uniquely named `WalletTests_<guid>` databases. The test login needs create/drop database permissions. Never point this suite at a production server. Omitting `WALLET_TEST_SQL` when running all tests intentionally fails the SQL tests instead of silently skipping financial verification.

The GitHub Actions workflow starts SQL Server and runs the full suite. Its fixed password belongs only to that ephemeral CI database, not to an application account.

See [docs/VALIDATION.md](docs/VALIDATION.md) for what was actually verified in this delivery.
