# Upgrade v0.1.0 → v0.2.0

This upgrade targets the supplied v0.1.0 schema whose first migration is `20261025071227_InitialWalletLedger`. The initial migration files are unchanged. The new `AccountSecurityAndOperations` migration adds account challenges, an email outbox, session stamps/timestamps and audit reasons. No wallet balances or users are deleted.

## Before updating

1. Back up your database and local project changes.
2. Stop the running API and open the complete v0.2.0 project. Do not mix the earlier legacy `Users` migration/snapshot or `DiagnoseModelDifference` into this migration chain.
3. Keep your connection string and JWT key in user-secrets/environment variables. The UserSecretsId is unchanged; secrets are shared by project copies using it.
4. Use the SAME database as v0.1.0, if that database was created from `InitialWalletLedger`. For a new installation, select a new database and apply both included migrations.

```powershell
dotnet restore --locked-mode
dotnet tool restore
dotnet ef migrations list
dotnet ef migrations has-pending-model-changes
```

Expected migration list: `InitialWalletLedger` and `AccountSecurityAndOperations`. The model check must report no pending changes. If you still see a legacy migration or model mismatch, stop and resolve the project/migration history before updating; do not suppress the warning or delete the database.

## Apply

```powershell
dotnet ef database update
dotnet run --launch-profile https
```

Then visit `https://localhost:7081/health/ready`. A root status page is available at `https://localhost:7081/`.

## Intentional authentication changes

- The migration revokes existing sessions, because those sessions were issued before security-stamp binding. Sign in again after verification.
- Login now requires `EmailConfirmed`. Existing accounts are NOT automatically trusted.
- For an existing unverified account, POST its email to `/api/v1/auth/resend-verification`, read the message in Development's `App_Data/mail` folder (or your configured SMTP inbox), and POST email/token to `/api/v1/auth/confirm-email`.
- New demo-seeded accounts are explicitly preverified development fixtures. Old demo accounts also need the verification flow; do not rerun the seed command against a populated database.
- Start with Development pickup to test without an SMTP provider. Production requires SMTP and persistent encrypted Data Protection keys; see ACCOUNT-SECURITY.md.

## Existing databases with a custom migration history

The older schema with a `Users` table and decimal(18,4) balances is not this upgrade baseline. A custom legacy-to-ledger migration must preserve normalized Identity accounts and opening ledger balances. Do not apply the previously generated diagnostic migration unchanged. Preserve the legacy database and use a separate clean database for development, or provide the actual migration history for a reviewed conversion.

## Verification

```powershell
dotnet test Tests/FinTechWalletAPI.Tests.csproj --filter 'Category!=SqlServer'
# Point ONLY at a disposable/development SQL Server (tests create/drop isolated databases).
$env:WALLET_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True'
dotnet test Tests/FinTechWalletAPI.Tests.csproj
dotnet run --launch-profile https -- --reconcile
```

The original application-code guards against negative customer balances and duplicate postings remain. This upgrade does not change Windows Application Control policy; launch approval must be resolved through the operating system's approved policy configuration.
