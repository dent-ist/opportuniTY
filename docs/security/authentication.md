# Authentication: OIDC with a BFF session

Implements `E05-T01` (#46) under [ADR-015](../adr/0015-security-architecture-and-trust-boundaries.md) D3 (identity),
D4 (browser session and HTTP security) and D10.4 (Data Protection keys). Threats addressed:
[threat model](threat-model.md) T-01, T-02, T-03 (CSP), T-06, T-10, T-11, T-12, T-13, T-14.

## How it works

```text
Browser (SPA, no tokens)              API (BFF)                               IdP (one per installation)
  GET /bff/login?returnUrl=/x  ──►  302 to IdP: code + PKCE S256,
                                     state, nonce (PAR when advertised)  ──►  user signs in (MFA per IdP policy)
  GET /bff/callback?code&state ──►  code → tokens (confidential client)  ◄──  ID token (RS/PS/ES only), refresh token
                                     user upserted by (iss, sub)
                                     session row in PostgreSQL, tokens encrypted
                               ◄──  Set-Cookie __Host-opp-session (256-bit random), 302 /x
  GET /api/v1/me               ──►  session lookup by SHA-256(cookie); idle/absolute check;
                                     every 15 min refresh grant (groups, deactivation)
                               ◄──  200 user + Set-Cookie __Host-opp-xsrf (request token)
  POST … + X-XSRF-TOKEN, Origin ──► CSRF check (token bound to user + Origin = public origin)
  POST /bff/logout             ──►  session revoked, refresh token revoked (RFC 7009)
                               ◄──  { endSessionUrl } → SPA navigates there
                                                                IdP ──► POST /bff/backchannel-logout (logout_token)
```

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /bff/login?returnUrl=&stepUp=` | anonymous | Starts sign-in. `returnUrl` must be a local path (`/…`, not `//` or `/\`); anything else becomes `/`. `stepUp=true` asks for the MFA `acr` with `max_age=0` |
| `GET /bff/callback` | (OIDC handler) | Redirect URI to register at the IdP: `https://<public origin>/bff/callback` |
| `POST /bff/logout` | session + CSRF | Ends the session; returns `{ "endSessionUrl": "…" }` (the IdP end-session URL with `client_id` and `post_logout_redirect_uri = <public origin>/`) |
| `POST /bff/backchannel-logout` | IdP-signed `logout_token` | OpenID Connect Back-Channel Logout 1.0. Register `https://<api>/bff/backchannel-logout` at the IdP |
| `GET /api/v1/me` | session | The signed-in user (`userId`, `displayName`, `email`, `groups`, `mfa`, `sessionExpiresAt`); 401 problem otherwise |
| `GET /api/v1/me/preferences`, `PUT`/`DELETE /api/v1/me/preferences/{key}` | session + `OwnProfile` policy (+ CSRF on unsafe methods) | The caller's UI preferences (E15-T03: key map, theme, density, pane sizes). The user id comes only from the session, never from the route or body, so no user can reach another user's preferences. Values are opaque client JSON (≤ 16 KiB each, ≤ 100 keys), carry no workspace data and are not audited |

The `/bff` routes are browser-navigation and protocol endpoints and are not in the versioned OpenAPI document.
Every other endpoint requires a session (authorization fallback policy) unless it is explicitly anonymous (health
probes, the OpenAPI document, login, back-channel logout). Unauthenticated API calls get `401` with
`urn:opportunity:problem:unauthorized`; nothing under `/api` ever redirects to the IdP.

### Cookies

| Cookie | Flags | Content |
|---|---|---|
| `__Host-opp-session` | `HttpOnly; Secure; SameSite=Lax; Path=/` (no Domain, no Expires) | 32 random bytes, base64url. The database stores only its SHA-256 |
| `__Host-opp-csrf` | `HttpOnly; Secure; SameSite=Strict; Path=/` | ASP.NET Core anti-forgery cookie token |
| `__Host-opp-xsrf` | `Secure; SameSite=Strict; Path=/` (readable by script) | Anti-forgery request token; the SPA echoes it in `X-XSRF-TOKEN` |
| `__Host-opp-oidc-correlation.*`, `__Host-opp-oidc-nonce.*` | `HttpOnly; Secure; SameSite=None` | Short-lived sign-in state (15 min) |

Unsafe methods (`POST`, `PUT`, `PATCH`, `DELETE`) from a cookie session must carry a valid `X-XSRF-TOKEN` **and** an
`Origin` equal to `Authentication:PublicOrigin`; otherwise `403 csrf-validation-failed`. The token is bound to the
user and is reissued after every sign-in. GET/HEAD never change state.

### Session store: PostgreSQL

ADR-015 D4.1 puts sessions in a PostgreSQL table (`opportunity.user_session`, migration V0005); baseline §16 rules
out Redis. A self-contained encrypted cookie (ASP.NET Core cookie auth without a ticket store) was rejected because it
cannot be revoked server-side (back-channel logout, deactivation, logout on another device), would carry the IdP
tokens to the browser (encrypted, but still bearer material outside the server) and grows with the group claim. The
cost is one indexed read per request plus a throttled write (activity is recorded at most once per minute, so the
idle timeout is enforced to within a minute). The API itself stays stateless: any replica serves any request.

Ended sessions are kept (without tokens) for a day for diagnostics and then deleted hourly.

### Timeouts and principal refresh

| Setting (`Authentication:Session:*`) | Default | Rule |
|---|---|---|
| `IdleTimeout` | 30 min | No request for this long ends the session |
| `AbsoluteTimeout` | 12 h | Hard limit from sign-in, regardless of activity |
| `PrincipalRefreshInterval` | 15 min (5–60) | Refresh-token grant (or userinfo if the IdP issues no refresh token); new groups are stored on the session and the user. A rejected grant (user disabled, IdP session ended) or an unreachable IdP **ends the session** (D3.5). This is the window in which an IdP-side deactivation or group change takes effect (AR-01); back-channel logout is immediate |
| `TouchInterval` | 1 min | Activity write throttle |
| `AuditHashKey` | none | Base64 HMAC key (≥ 32 bytes) for the audit `SessionIdHash`; without it events carry no session hash |

### Identity mapping

Users are keyed by (`iss`, `sub`) in `opportunity.app_user`; email and name are display-only and never matched. First
sign-in provisions a user with **no** workspace access. Groups come from `Authentication:Oidc:GroupsClaim` (default
`groups`) in the ID token, or from userinfo when `GetClaimsFromUserInfoEndpoint=true`; they feed role assignment
(`E05-T02`) and ethical walls (Q-13). Up to 1,000 groups are kept; providers that truncate (Entra overage) need the
directory lookup adapter (follow-up).

The request principal carries `opp_uid` (user ID), `opp_sid` (session row), `iss`, `sub`, `name`, `email`, one `groups`
claim per group, `acr` and `amr`.

### MFA (D3.6)

`Authentication:Mfa:AcrValues` / `AmrValues` define what counts as MFA (a session satisfies MFA when its `acr` is
listed or any `amr` value is listed). A workspace with `require_mfa = true` answers `403 step-up-required` (with
`stepUpUrl: "/bff/login?stepUp=true"`) to sessions without MFA; endpoints marked `.RequireMfa()` (installation
administration, break-glass activation for Q-45) always do. The step-up request sends `acr_values =
Mfa:StepUpAcrValue` (default: the first `AcrValues` entry) and `max_age=0`. With nothing configured, MFA can never be
satisfied, so MFA-protected resources stay closed.

### Response headers

Every API response: `Strict-Transport-Security: max-age=31536000; includeSubDomains`, `X-Content-Type-Options:
nosniff`, `Referrer-Policy: no-referrer`, `Cross-Origin-Opener-Policy` / `-Resource-Policy: same-origin`,
`Permissions-Policy` (camera, microphone, geolocation, payment off), `Content-Security-Policy: default-src 'none';
frame-ancestors 'none'; base-uri 'none'; form-action 'none'` and `Cache-Control: no-store`. The SPA's CSP (no inline
or eval script) is set by the web image (`deploy/docker/web/security-headers.conf`); both are pinned by tests.

### Audit

`Auth.SignIn`, `SignInFailed`, `SignOut`, `SessionExpired`, `SessionRevoked`, `StepUp` and `AuthZ.Denied`
(`MfaRequired`) are written through the `IAuditEventWriter` port (ADR-013 envelope, system chain). Until the audit
store lands (`E14-T01`, #115) the API registers a no-op writer; tests use the in-memory writer.

## Configuration

All settings live under `Authentication` (environment variables use `__`, e.g. `Authentication__Oidc__ClientId`).
The API refuses to start without the required ones.

| Key | Required | Example / default |
|---|---|---|
| `PublicOrigin` | yes | `https://review.example.com` (the origin users browse to; no path) |
| `Oidc:Authority` | yes | Issuer URL, e.g. `https://login.example.com/realms/opportunity` |
| `Oidc:MetadataAddress` | no | Discovery URL when the API reaches the IdP on another address (containers) |
| `Oidc:ClientId` | yes | `opportunity` |
| `Oidc:ClientSecret` | for confidential clients | from a secret store (`*_FILE`, E05-T09); never committed |
| `Oidc:Scopes` | no | `openid profile email` |
| `Oidc:GroupsClaim` | no | `groups` |
| `Oidc:GetClaimsFromUserInfoEndpoint` | no | `false` |
| `Oidc:RequireHttpsMetadata` | no | `true` (set `false` only for the local developer IdP) |
| `Mfa:AcrValues`, `Mfa:AmrValues`, `Mfa:StepUpAcrValue` | for MFA | e.g. `AcrValues:0=gold`, or `AmrValues:0=mfa` |

Register at the IdP: a **confidential** web client, authorization code flow with PKCE (S256), redirect URI
`<PublicOrigin>/bff/callback`, post-logout redirect URI `<PublicOrigin>/`, back-channel logout URI
`<API base>/bff/backchannel-logout` with "session required", and a groups claim in the ID token (or userinfo).

- **Keycloak**: client authentication on; standard flow only; *Advanced → PKCE method S256*; a *Group Membership*
  mapper (`groups`, full path off, ID token + userinfo); back-channel logout URL + session required. For step-up, map
  ACR levels (`acr.loa.map`, e.g. `{"mfa":2}`) and add a conditional OTP step in the browser flow; set
  `Mfa:AcrValues:0=mfa`. The developer realm is a ready example: `deploy/docker-compose/keycloak/opportunity-realm.json`.
- **Microsoft Entra ID**: authority `https://login.microsoftonline.com/<tenant>/v2.0`; web platform redirect URI; a
  client secret or certificate; token configuration → *groups claim* (security groups, emitted as object IDs); front
  channel logout is not used. Entra emits `amr` (`mfa`) in ID tokens: set `Mfa:AmrValues:0=mfa`; step-up with
  Conditional Access authentication contexts is a follow-up. Users in more than 200 groups need the overage adapter.
- **Okta**: OIDC web app, PKCE required, a `groups` claim (filter) on the authorization server, back-channel logout is
  not offered by every Okta plan (refresh-based deactivation still applies). MFA via `amr` (`mfa`).
- **Authentik**: OAuth2/OpenID provider, confidential client, signing key RS256, a `groups` scope mapping;
  back-channel logout supported on recent versions.

Behind a TLS-terminating proxy the API must see the public scheme and host (forwarded headers, `E19-T06`) so that
the redirect URI it sends matches the registered one.

## Developer profile

The Compose dev profile runs Keycloak with realm `opportunity` (see `deploy/docker-compose/README.md`): demo users
`admin.dev`, `reviewer.dev`, `privilege.dev`, `walled.dev` (member of `wall-project-falcon`) and `auditor.dev`, all with
password `opportunity`. Sign in at <http://localhost:8081/bff/login> (browsers accept `Secure`/`__Host-` cookies over
plain HTTP only for `localhost`). Never use this realm or its credentials outside a local machine.

## Not done here / follow-ups

- `private_key_jwt` client authentication (preferred by D3.2) can now take its key from `ISecretProvider` (E05-T09) but
  is not implemented; only `client_secret_post` is. The client secret comes from `Authentication__Oidc__ClientSecret_FILE`
  (or `.env` in the developer profile, where `./opportunity.sh init` generates it and the dev realm imports it).
- Data Protection keys are stored in PostgreSQL (`opportunity.data_protection_key`) but not yet encrypted with a KEK
  (ADR-015 D10.4). E05-T09 delivered the KEK provider but left sealing the key ring out of its scope; until a follow-up
  does it they are protected by database access control.
- Session ID rotation at break-glass activation (D4.2) lands with break-glass (`E05-T06`).
- Per-workspace *tightening* of session timeouts (D4.2) is not implemented; the installation values apply.
- Audit events are not yet persisted (`E14-T01`).
