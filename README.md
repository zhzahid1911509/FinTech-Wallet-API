# FinTech Wallet API — v0.1.0

An ASP.NET Core 10 / EF Core / SQL Server API implementing the first foundation and internal-transfer milestone. BDT only. This is a development release, not approval to handle real funds.

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

This creates `alice@example.test` and `bob@example.test`, each with 10,000 BDT of simulated funds, using your chosen password. The offsetting system wallet and ledger entries preserve the accounting balance. Seeding refuses non-Development environments and databases containing users/wallets. It never tops up existing users. There is no public deposit/credit endpoint.

## 4. Start

```powershell
dotnet run --launch-profile https
```

- API: `https://localhost:7081`
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

Expected balances: Alice 9,500 BDT; Bob 10,500 BDT. New registered accounts start at zero.

## API summary

| Method | Path | Authorization | Purpose |
|---|---|---|---|
| POST | `/api/v1/auth/register` | Public, limited | Register + create wallet |
| POST | `/api/v1/auth/login` | Public, limited | Create a session/token pair |
| POST | `/api/v1/auth/refresh` | Refresh token | Rotate token; reject reuse |
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
