# Upgrade to v0.3.0

This release preserves both earlier migration files and adds `MfaAndWalletApprovals`. It adds MFA profiles/recovery hashes, session assurance fields, wallet state versions and approval requests. Users, balances, posted ledger records and prior audit records remain intact.

1. Back up the database and your local code changes; stop the running API.
2. Use the complete v0.3.0 source with your existing connection string and JWT configuration. The UserSecretsId is unchanged.
3. Do not import the earlier legacy `Users` schema migration or `DiagnoseModelDifference`. The supported chain is InitialWalletLedger → AccountSecurityAndOperations → MfaAndWalletApprovals.
4. Follow [../BUILD.md](../BUILD.md) to restore, build, check and apply migrations.

## Behavior changes

- Customers who have not enrolled MFA can continue logging in with email/password. Email verification from v0.2.0 remains required.
- Existing sessions have no recent-MFA assurance. Operators must enroll and authenticate with TOTP before changing wallet state.
- Existing verified operator accounts retain their roles, but a role alone no longer permits mutation.
- Direct `isFrozen:false` is no longer supported. Update clients to request unfreezing and obtain approval from a different operator.
- MFA enrollment confirmation, disable and recovery-code regeneration revoke all sessions for that account. Password reset continues to revoke sessions and does NOT disable MFA.
- Refresh tokens preserve the original MFA timestamp instead of renewing it.
- Existing Data Protection configuration now also protects authenticator secrets. Preserve the key ring/certificate and its backups across deploys.

See [MFA-AND-APPROVALS.md](MFA-AND-APPROVALS.md) for complete request examples. Development email pickup and SMTP setup remain in ACCOUNT-SECURITY.md. The migration adds no automatically verified accounts and no default operator.

## Data and migration safety

The new migration is additive. Do not delete existing migration history, suppress PendingModelChangesWarning or regenerate an initial migration. If the model check fails or your migration list differs from the chain above, resolve that difference before applying updates. The tests exercise the supported migrations on isolated databases; they do not migrate a custom legacy schema.

Rolling back this release's schema would remove MFA and approval records, including security history. Use backups and a reviewed recovery plan rather than an automatic downgrade in a deployed environment.
