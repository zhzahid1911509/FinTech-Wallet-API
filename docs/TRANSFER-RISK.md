# Risk-based transfer authorization — v0.4.0

This release implements the risk-based verification part of the roadmap. It uses explicit server-side rules, not machine learning, IP geolocation or a device-fingerprinting service. TOTP is not phishing-resistant. Passkeys and recovery when every factor is lost remain unimplemented.

## Default policy

| Signal | Requires a transaction-specific authorization |
|---|---|
| Amount | At least 5,000 BDT |
| Rolling outgoing value | Posted outgoing customer transfers in the preceding 24 hours plus this request reach 15,000 BDT |
| New recipient | No prior posted customer transfer from this wallet to this recipient, and amount at least 1,000 BDT |
| Recovery-code session | Every amount, until a successful authenticator session step-up changes the session assurance |

Boundaries are inclusive. Demo seed postings are excluded. Incoming transfers do not reduce the outgoing total. A recipient becomes known after a successful transfer of any amount; this is a simple heuristic, not a trusted-beneficiary feature. Large-value and cumulative rules still apply to known recipients. Several rules can apply together.

Configure `TransferRisk:SingleTransferThreshold`, `Rolling24HoursThreshold`, `NewRecipientThreshold` and `AuthorizationSeconds` in appsettings or environment variables (use `__` separators). Defaults are 5000, 15000, 1000 and 120 seconds. Thresholds must be positive, at most 1,000,000 BDT and have no more than two decimal places; validity is checked at startup. Authorization lifetime must be 30–300 seconds. Restart all instances with the same policy. These are development defaults requiring business risk review, not regulatory limits. There is no client-controlled bypass or disable switch.

## Client sequence

Use an authenticated session and keep one Idempotency-Key for the complete transfer attempt. All endpoints below use `Authorization: Bearer <access-token>` and `Content-Type: application/json`.

1. Optional advisory assessment: `POST /api/v1/transfers/assess`

```json
{"recipientWalletId":"<recipient-wallet-guid>","amount":5000}
```

The response includes `authorizationRequired`, `reasons`, `outgoingLast24Hours`, `mfaEnrolled` and `policyVersion`. Assessment does not reserve funds or guarantee execution. The transfer endpoint recalculates risk inside its serializable posting transaction; do not trust a cached assessment.

2. If required, enroll MFA using MFA-AND-APPROVALS.md, save the recovery codes, and sign in again. Then call `POST /api/v1/transfers/authorizations` with `Idempotency-Key: transfer-example-001`:

```json
{
  "recipientWalletId":"<recipient-wallet-guid>",
  "amount":5000,
  "currentPassword":"<current-password>",
  "authenticatorCode":"<fresh-six-digit-code>"
}
```

A successful response contains `id`, canonical recipient/amount/currency/key and `expiresAt`. Show these details to the user in a trusted client. The server verifies password and a fresh TOTP even if login or operator step-up happened recently. Recovery codes cannot authorize transfers. The code used for login cannot be reused; wait for the next authenticator code.

3. Submit `POST /api/v1/transfers` with the SAME Idempotency-Key, amount and recipient:

```json
{"recipientWalletId":"<recipient-wallet-guid>","amount":5000,"authorizationId":"<authorization-id>"}
```

Low-risk transfers can omit `authorizationId`. If provided, an authorization must be valid even for a low-risk transfer. An authorization is bound to one session, key and request fingerprint. A different session (including another login by the same user), user, amount, recipient or key cannot use it. Refresh within the same valid session preserves the binding. Logout, password reset, MFA changes and session revocation prevent use of grants from invalidated sessions.

Authorization does not reserve money and does not override frozen-wallet, currency, balance or other transfer validation. Its consumption, debit, credit, posting record and audit events commit together. A posting failure rolls back consumption, so a still-valid authorization can be retried with the same details/key. Issuing an authorization consumes the TOTP independently; if that response is lost, wait for a fresh code to request another authorization.

## Retry and error behavior

- `403 transfer_mfa_enrollment_required`: a risky transfer requires an enrolled authenticator.
- `403 transfer_authorization_required`: obtain a transaction authorization.
- `403 transfer_authorization_invalid`: expired, mismatched, consumed or unavailable authorization; assess and authorize again if no posting exists.
- `403 mfa_invalid` / `mfa_locked`: existing MFA replay/lockout rules apply; password success never resets this counter.
- `409 transfer_already_posted` from authorization issuance: replay the original transfer request/key to obtain its committed result.
- Concurrency can return 409; retry using the SAME key and original details.

A completed transfer can be replayed without a fresh authorization, including after the original authorization expires. The API still requires a currently valid session for the same actor. An idempotency mismatch remains 409. Denials do not reserve keys. Invalid or expired grants are never accepted because an advisory assessment previously said low risk.

## Audit and operational limits

`Transfer.AuthorizationCreated`, `Transfer.AuthorizationConsumed`, `Transfer.RiskAccepted` and `Transfer.RiskDenied` record actor/target IDs and bounded server-generated reasons. No password, TOTP or request body is recorded. Only denials at the risk gate create RiskDenied; earlier input, funds or authentication errors use their existing handling. Audit insertion failure fails the operation rather than posting unaudited funds.

The rolling-value query runs in the same SERIALIZABLE transaction as posting, so concurrent requests cannot both rely on a stale outgoing total. Whole-transaction retries handle supported SQL conflicts. This favors correctness; sustained load and distributed rate-limit work remain on the roadmap.

Authorizations and audits are retained in this release; a reviewed retention worker remains future work. No funds are transferred to external providers. Neither this policy nor TOTP detects all fraud or resists real-time phishing. Transaction binding follows the server-side authorization, execution-gate and one-operation credential principles described in [OWASP Transaction Authorization](https://cheatsheetseries.owasp.org/cheatsheets/Transaction_Authorization_Cheat_Sheet.html).
