# MFA and two-person wallet approvals — v0.3.0

## Enrollment

MFA is optional for customer login. All operator wallet-state mutations require a verified authenticator within the last five minutes. Read-only operator endpoints still require the WalletOperator role but do not require recent MFA.

1. Log in with your verified email/password and keep the access token.
2. Request an enrollment secret:

```http
POST /api/v1/auth/mfa/enrollment
Authorization: Bearer ACCESS_TOKEN
Content-Type: application/json

{"currentPassword":"YOUR_PASSWORD"}
```

The response contains `sharedKey`, `authenticatorUri` and `expiresAt`. Add the shared key manually to a TOTP authenticator app, or let a trusted local client render the `otpauth://` URI as a QR code. Use time-based codes, SHA-1, six digits and a 30-second interval. Do not submit the URI/secret to an online QR-generation service, screenshot it for support, or commit it to git.

The pending enrollment expires after ten minutes. Starting enrollment again before confirmation replaces the previous secret. An already-enabled authenticator cannot be overwritten by this endpoint.

3. Confirm using the current six-digit code:

```http
POST /api/v1/auth/mfa/confirm
Authorization: Bearer ACCESS_TOKEN
Content-Type: application/json

{"code":"123456"}
```

The response contains ten recovery codes. Save them offline or in a password manager: they are returned once and cannot be retrieved later. Confirmation enables MFA and revokes all existing sessions. **Wait for your authenticator to display its next code before logging in**, because confirmation has consumed the current code.

## Login and recovery login

```http
POST /api/v1/auth/login
Content-Type: application/json

{"email":"you@example.com","password":"YOUR_PASSWORD","authenticatorCode":"123456"}
```

A valid password without the required factor returns 403 with `code: mfa_required`; no session or tokens are issued. Without MFA enabled, the original email/password request still works.

When the authenticator is unavailable, use one recovery code INSTEAD of an authenticator code:

```json
{"email":"you@example.com","password":"YOUR_PASSWORD","recoveryCode":"ONE_UNUSED_RECOVERY_CODE"}
```

Recovery codes are also single-use. A recovery login permits normal account access, but it does not establish the recent authenticator assurance required for operator mutations. Supplying both factor fields is rejected.

Authenticator validation accepts at most one adjacent 30-second window in either direction. Each accepted time step can be used only once per account, including across login, enrollment confirmation and step-up. Keep the server and phone clocks synchronized. Wait for the next code after a successful factor check. Five invalid factor attempts lock MFA verification for 15 minutes, independently of the password lockout counter; a correct password does not reset that MFA counter.

## Status and step-up

`GET /api/v1/auth/mfa/status` returns enabled state, lockout expiry and the number of remaining recovery codes; it returns no secret or recovery-code values.

For a sensitive operation, if the last successful authenticator verification is more than five minutes old:

```http
POST /api/v1/auth/mfa/step-up
Authorization: Bearer ACCESS_TOKEN
Content-Type: application/json

{"currentPassword":"YOUR_PASSWORD","authenticatorCode":"NEW_UNUSED_CODE"}
```

Success returns 204 and updates the current server-side session. You can continue using its access token. Refreshing an access token does not extend the MFA window. Recovery codes cannot satisfy step-up.

## Replace recovery codes or disable MFA

`POST /api/v1/auth/mfa/recovery-codes` accepts the same password/authenticator body as step-up. It returns ten new codes, invalidates every old code and revokes all sessions. Wait for a new authenticator code before signing in again.

`POST /api/v1/auth/mfa/disable` requires current password plus either an unused authenticator code or an unused recovery code. It deletes the encrypted authenticator profile and recovery-code hashes, disables MFA and revokes all sessions. If you used a recovery code to log in, disabling MFA requires a DIFFERENT unused recovery code. Email security notifications are queued when MFA is enabled/disabled or recovery codes are replaced.

Password reset does not disable MFA. If both the authenticator and every recovery code are lost, this release has no self-service MFA bypass; a separately designed identity-recovery workflow is future work.

## Operator workflow

Grant/revoke WalletOperator through the existing trusted-console commands. Operator privileges alone do not permit wallet mutation: the operator must enroll MFA, sign in with an authenticator code or complete step-up, and supply their current password in the mutation request.

### Freeze immediately

```http
PUT /api/v1/operations/wallets/WALLET_ID/state
Authorization: Bearer OPERATOR_ACCESS_TOKEN
Content-Type: application/json

{"isFrozen":true,"reason":"Security review required","currentPassword":"OPERATOR_PASSWORD"}
```

Freezing is immediate and audited. Repeating the same frozen state is a no-op. Sending `isFrozen:false` to this endpoint now returns 409 `two_person_approval_required`.

### Request unfreezing

Operator A:

```http
POST /api/v1/operations/wallets/WALLET_ID/unfreeze-requests
Authorization: Bearer OPERATOR_A_TOKEN
Content-Type: application/json

{"currentPassword":"OPERATOR_A_PASSWORD","reason":"Customer review completed"}
```

The 201 response contains the approval request ID. The wallet remains frozen. At most one pending request exists per wallet; a second pending request returns 409. The request expires after 30 minutes. Creating a new request after expiration marks the previous one Expired.

Operator B (a DIFFERENT user with current operator permission and recent authenticator verification):

```http
POST /api/v1/operations/unfreeze-requests/REQUEST_ID/approve
Authorization: Bearer OPERATOR_B_TOKEN
Content-Type: application/json

{"currentPassword":"OPERATOR_B_PASSWORD","reason":"Independent review completed"}
```

Approval atomically unfreezes the wallet, records the reviewer, changes the request status and appends audit events. It does not move money. Self-approval is forbidden. The requester must still be a verified, MFA-enabled operator with the same security stamp. A changed wallet state version, removed requester role, reset password, MFA change or logout-all invalidates the pending request when approval is attempted.

Use `/reject` instead of `/approve` to reject as a different operator. The requester can use `/cancel`. Both require current password and recent authenticator verification and leave the wallet frozen. Terminal decisions cannot be replayed; they return 409.

Read requests with:

- `GET /api/v1/operations/unfreeze-requests?status=Pending` — latest 100 matching requests.
- `GET /api/v1/operations/unfreeze-requests/REQUEST_ID` — one request.

Pending items may have passed their `expiresAt`; expiry is enforced on decision/new-request operations rather than by a cleanup worker. Read the timestamp as well as the status. No API accepts client-supplied requester/reviewer IDs, security stamps or wallet state versions.

## Storage and operational requirements

Authenticator secrets use ASP.NET Core Data Protection with a dedicated purpose, sharing the existing protected key-ring infrastructure. Recovery codes contain 128 bits of randomness and only SHA-256 hashes are stored. MFA counters, replay state and recovery consumption are transactional and use SQL Server rowversions. Keep the Data Protection key ring and encryption certificate backed up: losing them can make enrolled secrets unreadable. This is distinct from JWT signing keys.

MFA and approval changes are audited. Sensitive responses use `Cache-Control: no-store`; request bodies and secrets are not logged. TOTP is not phishing-resistant; passkeys/hardware-backed factors and stronger risk-based verification remain future work. Dual approval currently covers wallet unfreezing only, not future financial adjustments or external payouts. Database/server administrators remain privileged outside the HTTP authorization boundary.
