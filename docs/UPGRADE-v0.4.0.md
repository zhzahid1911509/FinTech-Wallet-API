# Upgrade to v0.4.0

Back up the database, preserve user-secrets/environment configuration and Data Protection keys, and stop the API before replacing the source. Use the complete release and follow BUILD.md. The UserSecretsId is unchanged.

This release adds `RiskBasedTransferAuthorization`, creating TransferAuthorizations with session/transfer foreign keys, rowversion, an expiry constraint and a unique consumed-transfer index. Earlier migrations remain byte-for-byte unchanged. Existing accounts, MFA, sessions, wallet balances, ledger and approval records are preserved.

Expected migration chain:

1. InitialWalletLedger
2. AccountSecurityAndOperations
3. MfaAndWalletApprovals
4. RiskBasedTransferAuthorization

Do not delete migration history, regenerate initial migrations, suppress pending-model warnings or import legacy DiagnoseModelDifference migrations. Custom legacy schemas need a separately reviewed migration path.

## Client behavior change

Previously valid transfer requests may now receive 403 when the risk policy requires authorization. Update clients to assess the transfer, enroll MFA if necessary, obtain transaction-specific authorization with password/TOTP, and submit the returned authorizationId with the same idempotency key. See TRANSFER-RISK.md for defaults and complete examples. A recent MFA login or operator step-up alone does not authorize a risky transfer.

Existing valid sessions remain valid. Recovery-code sessions require transaction authorization for all outgoing transfers until TOTP step-up. Existing low-risk requests keep their two-field request shape. Successful historical transfers remain idempotently replayable without new proof; new outgoing risk calculations include historical posted transfers within the last 24 hours.

Coordinate a maintenance deployment: older application instances do not enforce the new rules, so do not serve traffic through a mix of old and new versions. Configure identical TransferRisk settings on all instances. The API does not automatically migrate at startup.

Downgrading the schema deletes authorization evidence. Use a reviewed recovery plan and backups rather than automatically reverting a deployed database.
