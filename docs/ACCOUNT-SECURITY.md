# Account security and operations — updated for v0.3.0

MFA enrollment and the current two-person unfreeze workflow are documented in [MFA-AND-APPROVALS.md](MFA-AND-APPROVALS.md).

## Local email delivery (no provider account required)

The default Development mode writes MIME email files under `App_Data/mail` every few seconds. That folder is excluded from git and is not served over HTTP. Open `.eml` files in a mail viewer, or a text editor, and copy the code on the `Token:` line. Treat these files as credentials: use only test accounts locally, restrict folder access, and delete messages when finished testing. Do not upload them to GitHub or support tickets.

The API returns no verification/reset token. Its root `/` now returns API/version information. `/openapi/v1.json` is available in Development; no Swagger UI is bundled.

### Register and verify

```http
POST /api/v1/auth/register
Content-Type: application/json

{"email":"you@example.com","password":"Your-Strong-Password-239!"}
```

Registration returns 201 with the wallet ID and `emailVerificationRequired: true`. Open the verification email, then:

```http
POST /api/v1/auth/confirm-email
Content-Type: application/json

{"email":"you@example.com","token":"CODE_FROM_EMAIL"}
```

A successful confirmation returns 204. You can now log in normally. Login to an unverified account returns the same general 401 as other unavailable accounts.

To request another verification email:

```http
POST /api/v1/auth/resend-verification
Content-Type: application/json

{"email":"you@example.com"}
```

Verification codes expire after 60 minutes. Resends have a persisted one-minute cooldown. A resend invalidates earlier verification codes; use the newest email. The response is always the same 202 message for eligible, ineligible and missing accounts; delivery failures are handled by the worker.

### Recover a password

```http
POST /api/v1/auth/forgot-password
Content-Type: application/json

{"email":"you@example.com"}
```

Only a verified account can receive a password-reset code. An unverified account must complete email verification first. The response does not reveal eligibility.

```http
POST /api/v1/auth/reset-password
Content-Type: application/json

{"email":"you@example.com","token":"CODE_FROM_EMAIL","newPassword":"A-New-Strong-Password-239!"}
```

A successful reset returns 204. Codes expire after 15 minutes, are single-use and are bound to the account, purpose and security stamp. All previous access/refresh sessions become invalid; sign in with the new password. Reset does not remove an existing login lockout: wait for its expiry if the account is locked.

### Manage sessions

Authenticated endpoints:

- `GET /api/v1/auth/sessions` — latest 100 active sessions, including the current-session flag.
- `DELETE /api/v1/auth/sessions/{id}` — revoke one owned session. Other users' IDs return 404.
- `POST /api/v1/auth/logout-all` — revoke all sessions, including the caller's current session.

Logout-all also rotates the security stamp, invalidating outstanding recovery codes. Request another email if needed. Session checks apply to new requests; an already executing request is not cancelled retroactively.

## SMTP configuration

Use any SMTP provider for which you have credentials. Set these from the project directory using user-secrets for local development:

```powershell
dotnet user-secrets set "Email:Mode" "Smtp"
dotnet user-secrets set "Email:Host" "YOUR_SMTP_HOST"
dotnet user-secrets set "Email:Port" "587"
dotnet user-secrets set "Email:Tls" "StartTls"
dotnet user-secrets set "Email:From" "YOUR_VERIFIED_SENDER_ADDRESS"
dotnet user-secrets set "Email:Username" "YOUR_SMTP_USERNAME"
dotnet user-secrets set "Email:Password" "YOUR_SMTP_PASSWORD"
```

For providers requiring implicit TLS, use port 465 and `Email:Tls=SslOnConnect`. Certificate validation remains enabled; plaintext and opportunistic TLS are not supported. Restart the API after configuration. Do not use the default `wallet@example.test` sender with a real provider.

To return to local pickup:

```powershell
dotnet user-secrets set "Email:Mode" "Pickup"
```

Messages are persisted atomically with account operations. Each job has a worker lease, a 45-second send deadline, bounded exponential retry delays and a five-attempt default. Delivered or terminally failed messages have their encrypted payload cleared. Failed/expired job counts are available to operators. After resolving a delivery problem, request a fresh code; there is no public job-retry endpoint.

SMTP delivery is at least once: a process failure after server acceptance but before recording success can deliver the same message twice. The same single-use code and stable Message-ID make duplicates harmless to the account workflow. SMTP acceptance does not guarantee delivery to the inbox.

## Production configuration

Pickup is forbidden outside Development. Production requires SMTP and an enabled worker, plus durable encrypted Data Protection keys:

```text
Email__Mode=Smtp
Email__Host=<provider host>
Email__From=<verified sender>
Email__Username=<secret>
Email__Password=<secret>
DataProtection__KeyDirectory=<persistent restricted directory>
DataProtection__CertificatePath=<path to PFX certificate with private key>
DataProtection__CertificatePassword=<secret, if required>
```

The PFX protects the key ring used to encrypt queued email payloads. Keep the key directory and private key restricted, durable and backed up. Multi-instance deployments must share the same key ring/application name and certificate access. Retain older certificates during rotation to decrypt old keys. This is separate from HTTPS and JWT signing. Do not put certificates or passwords in the repository. A complete production deployment/rotation plan remains a later milestone.

## Operator permissions

No registration field or public API can grant permissions. On the trusted server console, grant the role to an existing, verified account:

```powershell
$env:Operations__Email = 'operator@example.com'
dotnet run --launch-profile https -- --grant-operator
```

Remove it with:

```powershell
dotnet run --launch-profile https -- --revoke-operator
```

These commands exit after the change and append an audit event. The audit identifies the affected account and labels the action as console-originated; OS operator identity is not inferred. Access to the server console is privileged. HTTP authorization checks role membership in the database on every request, so removal takes effect without waiting for JWT expiry.

### Freeze or unfreeze

```http
PUT /api/v1/operations/wallets/WALLET_ID/state
Authorization: Bearer OPERATOR_ACCESS_TOKEN
Content-Type: application/json

{"isFrozen":true,"reason":"Review requested by support","currentPassword":"OPERATOR_CURRENT_PASSWORD"}
```

The current password and recent authenticator verification are required in addition to operator permission. Direct `isFrozen:false` is rejected in v0.3.0; use the two-person unfreeze-request workflow. System wallets cannot be changed through this endpoint. Repeating an already-applied state is a no-op; changed states and reasons are audited. Transfers involving a frozen wallet are rejected, while replay of an already-posted transfer still returns its original result. MFA and two-person unfreezing are implemented in v0.3.0.

## Reconciliation

```powershell
dotnet run --launch-profile https -- --reconcile
```

The command is read-only. Exit code 0 means no discrepancies; 2 means discrepancies were found. It checks every wallet projection against its ledger and every transfer's paired postings. Results include total mismatch counts and up to 100 details of each kind. It never edits balances or creates correction entries.

Operators can also call `GET /api/v1/operations/reconciliation`. The query uses a SERIALIZABLE transaction for a consistent view and may block transfers on large ledgers; run off-peak and add operational scheduling/load validation before scaling. It does not reconcile external bank/provider statements because no external provider is connected.

## History filters

```text
GET /api/v1/transactions?page=1&pageSize=20&kind=Transfer&direction=outgoing&from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z
```

- `from`: inclusive timestamp; `to`: exclusive timestamp. Prefer UTC (`Z`).
- `kind`: `Transfer` or `DemoFunding`.
- `direction`: `incoming` or `outgoing`, relative to the caller's wallet.
- Existing page/pageSize limits remain in effect. Filters never broaden account ownership.
