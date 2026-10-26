# Build, test and run — v0.4.0

Run commands in the directory containing `FinTechWalletAPI.csproj`. Requirements: .NET 10 SDK and a supported SQL Server instance. The README covers initial connection-string/JWT setup; keep secrets outside source control. Existing connection/JWT/email settings remain valid. Review the new TransferRisk defaults in docs/TRANSFER-RISK.md.

## Restore and build

```powershell
dotnet tool restore
dotnet restore Tests/FinTechWalletAPI.Tests.csproj --locked-mode
dotnet build Tests/FinTechWalletAPI.Tests.csproj -c Release --no-restore
```

Building the test project also builds the API. No solution file is required.

## Tests

Database-independent tests:

```powershell
dotnet test Tests/FinTechWalletAPI.Tests.csproj -c Release --no-build --filter 'Category!=SqlServer'
```

Full suite, including transaction/MFA/approval integration tests:

```powershell
# Example for a development Windows LocalDB instance.
$env:WALLET_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;Encrypt=True;TrustServerCertificate=True'
dotnet test Tests/FinTechWalletAPI.Tests.csproj -c Release --no-build
```

Use a disposable/development SQL Server, never a production server. Tests create/drop uniquely named WalletTests_* databases; the login needs create/drop database permission. Without WALLET_TEST_SQL, the unfiltered SQL tests intentionally fail rather than silently skipping.

## Check and apply migrations

Back up an existing application database before upgrading. Use the configured application connection string, not WALLET_TEST_SQL, for migration commands.

```powershell
dotnet ef migrations list --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --configuration Release --no-build
dotnet ef database update --configuration Release --no-build
```

Expected chain: `InitialWalletLedger`, `AccountSecurityAndOperations`, `MfaAndWalletApprovals`, `RiskBasedTransferAuthorization`. The pending-model check must report no changes. If it does not, stop and inspect your migration/source versions. Do not create another initial migration or suppress the warning.

## Run

```powershell
dotnet dev-certs https --trust
dotnet run --launch-profile https
```

Open `https://localhost:7081/health/ready`; it should return `{"status":"ready"}`. The root URL returns API/version information. OpenAPI JSON is at `/openapi/v1.json` in Development; Swagger UI is not bundled.

See `docs/TRANSFER-RISK.md` for risky-transfer authorization and `docs/UPGRADE-v0.4.0.md` before upgrading. See `docs/MFA-AND-APPROVALS.md` to enroll an authenticator and test operator approvals. See `docs/ACCOUNT-SECURITY.md` for SMTP/pickup and operator role commands. Windows Application Control policy issues must be resolved through approved operating-system policy configuration; these build commands do not alter those policies.

## Optional release output

```powershell
dotnet publish FinTechWalletAPI.csproj -c Release --no-restore -o artifacts/publish
```

Production still requires SMTP, protected persistent Data Protection keys, JWT/SQL secrets, HTTPS hosting and deployment-specific operational controls. A publish folder alone is not a production deployment.

## Git commit message

Review/stage your intended changes, then use the included `COMMIT_MESSAGE.txt`:

```powershell
git commit -F COMMIT_MESSAGE.txt
```

Do not stage user-secrets, development mail, key rings, certificates, bin/obj or test-generated database files.
