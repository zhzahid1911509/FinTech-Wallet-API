# Upgrade to v0.5.0

Back up the database, preserve user-secrets/environment settings and Data Protection keys, stop the API and replace the complete source. Follow BUILD.md to restore, build, check migrations and update the database before startup. The UserSecretsId is unchanged.

`OperationalMonitoring` is an additive migration. It creates ReconciliationRuns and the singleton ReconciliationSchedules row, plus audit-query indexes. It preserves accounts, sessions, balances, ledger entries, MFA, transaction authorizations and wallet approvals. Earlier migration files are unchanged.

Expected chain: InitialWalletLedger → AccountSecurityAndOperations → MfaAndWalletApprovals → RiskBasedTransferAuthorization → OperationalMonitoring.

Do not recreate initial migrations, suppress PendingModelChangesWarning or import the legacy DiagnoseModelDifference migration. Custom legacy schemas need a separately reviewed upgrade path. Index creation can take time on a large audit table; use a maintenance window appropriate to your dataset.

The reconciliation worker is enabled by default. On first startup it runs when it next polls, then 15 minutes after each completion. Configure Reconciliation settings as documented in OPERATIONS-MONITORING.md. The check can block writers; measure and tune its frequency and timeout. Worker and API instances must use consistent settings and synchronized UTC clocks.

Existing customer API requests are compatible. New endpoints are read-only and operator-protected. Existing ad-hoc reconciliation routes/CLI are unchanged. Monitoring does not add external notifications or automatic financial repair.

Tests disable hosted workers to prevent background activity from changing test fixtures, and exercise the scheduler explicitly against SQL Server. Production worker configuration is independent of those test settings.

Rollback removes persisted run history; use a reviewed recovery plan rather than automatically downgrading a deployed schema.
