# ReKindle — Social Feature API Reference

Covers the server-side API surface behind ReKindle's social apps (KindleChat, Neighbourhood, Topics, Flipbook, Pixel, Moderation). All endpoints are **Firebase Cloud Functions v2 `onCall` callables**, defined in `firebase-functions/index.js`, invoked from the client via the Firebase SDK:

```js
const result = await firebase.functions().httpsCallable('functionName')(payload);
```

All calls are restricted by CORS to: `beta.rekindle.ink`, `rekindle.ink`, `lite.rekindle.ink`, `legacy.rekindle.ink`.

## 1. Architecture

ReKindle runs **two separate Firebase projects**:

| Project | Used by | Config |
|---|---|---|
| **Main** (default) | Core app, IMAP mail, RSS, account auth | — |
| **`rekindle-socials`** | KindleChat, Neighbourhood, Topics, Flipbook, Pixel, Moderation | `firebase-social.json` / `firestore-social.rules` / `rtdb-social-rules.json` |

Both are backed by the same `firebase-functions/index.js` codebase. A secondary Admin SDK app (`socialAdminApp`) is initialized server-side from a `SOCIAL_SERVICE_ACCOUNT_JSON` env var, giving functions admin access to the social project's Auth, RTDB, and Firestore. If that env var is unset, every social endpoint fails closed with `failed-precondition`.

Client pages that read/write social-project data must:
1. Init a secondary client app pointed at `firebase-social.json`.
2. Call **`getSocialToken`** (below) against the *main* project to mint a custom token for the *social* project.
3. Sign in to the social app with that token before touching social RTDB/Firestore.

## 2. Auth & Account Functions

### `registerUser`
Creates a new account. Runs server-side so the IP-ban check can't be bypassed client-side.

- **Auth:** none required (this *is* the sign-up call)
- **Request:** `{ username: string, password: string }`
  - Username: ≤20 chars, alphanumeric only
- **Behavior:** creates a Firebase Auth user with email `{username}@rekindle.ink`; rejects the request if the caller's IP is in `banned_ips`; writes `users_public/{uid}` and `user_cards/{uid}` profile records with a random `avatarSeed`.
- **Response:** `{ customToken: string }` — client signs in via `signInWithCustomToken()`
- **Errors:** `invalid-argument` (bad username/password), `already-exists` (username taken), `permission-denied` (banned IP), `internal`

### `getUserAuthStatus`
Admin/moderator lookup of whether a **main-project** account is disabled.

- **Auth:** signed in; caller must be an admin (`ukiyo@rekindle.ink`) or listed under `moderators/{uid}` in the main RTDB
- **Request:** `{ uid: string }`
- **Response:** `{ disabled: boolean }`, or `{ disabled: null, notFound: true }` if the user doesn't exist
- **Errors:** `unauthenticated`, `invalid-argument`, `permission-denied`, `internal`

### `setUserAuthStatus`
Admin-only: enable/disable a **main-project** account.

- **Auth:** signed in; caller must be `ukiyo@rekindle.ink`
- **Request:** `{ uid: string, disabled: boolean }`
- **Response:** `{ success: true, disabled: boolean }`
- **Errors:** `unauthenticated`, `invalid-argument`, `permission-denied`, `internal`

### `getSocialUserAuthStatus` / `setSocialUserAuthStatus`
Same as the two functions above, but operate on the user's account in the **`rekindle-socials`** Auth project instead of the main one. Same admin/moderator gating; `setSocialUserAuthStatus` is admin-only.

- **Request:** `{ uid }` (get) / `{ uid, disabled }` (set)
- **Response:** `{ disabled }` / `{ success: true, disabled }`
- **Extra error:** `failed-precondition` if `socialAdminApp` isn't configured; `not-found` if the uid has no social-project record

### `checkIPOnLogin`
Called immediately after a successful sign-in to catch IP bans that occurred after the account was created.

- **Auth:** signed in
- **Request:** `{}` (IP is read server-side from `x-forwarded-for` / the raw request, never trusted from the client)
- **Behavior:** if the caller's IP is in `banned_ips`, disables their account, revokes all refresh tokens, and logs a `mod_actions` entry. Otherwise refreshes the stored IP at `users_private/{uid}/ipAddress`.
- **Response:** `{ banned: boolean }`

## 3. Age Verification

Social features are gated behind self-declared age verification, enforced server-side against a per-country minimum age table (`DEFAULT_SOCIAL_MEDIA_MIN_AGE` in `index.js`, overridable via Firestore `config/age_requirements`).

### `verifyAgeSelfDeclaration`
- **Auth:** signed in
- **Request:** `{ dob: { day: number, month: number, year: number }, country: string }` (ISO 3166-1 alpha-2)
- **Behavior:** validates the date is real and not in the future, computes age, and compares against the country's minimum. On success, sets `ageVerified`, `ageVerifiedAt`, `ageVerificationCountry`, `ageVerificationThreshold`, `ageVerificationMethod: 'self_declaration'` as **custom claims on both the main and social Auth projects**, and writes an audit record to `users/{uid}/ageVerification/latest` in Firestore.
- **Response (pass):** `{ success: true, country, age, minimumAge }`
- **Response (fail):** `{ success: false, reason, country, age, minimumAge }` (not an error — the call still resolves)
- **Errors:** `unauthenticated`, `invalid-argument` (missing/invalid DOB or country), `internal`

### `startAgeVerification`, `completeAgeVerification`, `verifyAge`
**Deprecated.** These backed a removed third-party AgeVerif OAuth2 integration. All three now unconditionally throw `failed-precondition` directing callers to `verifyAgeSelfDeclaration`. Kept only so old clients get a clear error instead of a missing-function crash.

## 4. Social Token Exchange

### `getSocialToken`
Mints the custom auth token a client uses to sign in to the `rekindle-socials` project. This is the bridge between "you're a valid main-project user" and "you're allowed into chat/topics/neighbourhood."

- **Auth:** signed in
- **Request:** `{}`
- **Behavior:**
  1. Reads moderator status from main RTDB `moderators/{uid}`.
  2. Reads Pro status from the caller's auth token claim (`pro`) or admin email.
  3. Reads `ageVerified` from the caller's main-project auth claims.
  4. Syncs the user's email into the social project's Auth record (creating it if missing) so they're visible in the Firebase Console for that project.
  5. Signs a custom token embedding `moderator`, `pro`, `ageVerified`, and `email` as claims — these are what `rtdb-social-rules.json` / `firestore-social.rules` check.
- **Response:** `{ token: string }`
- **Errors:** `unauthenticated`, `failed-precondition` (social project not configured)

**Client pattern:**
```js
const { data } = await firebase.functions().httpsCallable('getSocialToken')({});
await socialAuth.signInWithCustomToken(data.token);
```

## 5. Data Written Directly (not via Cloud Functions)

Once signed in to the social project, apps read/write RTDB and Firestore directly (subject to the security rules below) rather than through callables:

| Data | Location | Rules file |
|---|---|---|
| Chat messages, translations, rate-limit state | RTDB `kindlechat/*` | `rtdb-social-rules.json` |
| Topics, neighbourhood posts/comments | Firestore | `firestore-social.rules` |
| User-generated reports (spam, harassment, etc.) | RTDB `/reports` | `rtdb-social-rules.json` (write: reporting user or service account) |
| Public profile / avatar | RTDB `users_public/{uid}`, `user_cards/{uid}` | `rtdb-social-rules.json` |

Reports are submitted client-side (see `js/reports.js`) with a shared modal used across KindleChat, Neighbourhood, Topics, and Suggestions; report categories are `spam`, `harassment`, `inappropriate`, `hate_speech`, `self_harm`, `violence`, `other`.

Message-level rate limiting and abuse moderation for chat happens in a separate component (`workers/rekindle-moderate`), not in these Cloud Functions.

## 6. Content APIs — sending, posting, commenting, reporting

Unlike the account/token functions above, **content creation does not go through Cloud Functions callables.** It goes through a single Cloudflare Worker, `rekindle-moderate`, at:

```
https://rekindle-moderate.timjarnott.workers.dev
```

This is a plain `POST` endpoint (not a Firebase callable), authenticated with a **Firebase ID token** (not a callable context):

```js
var token = await (socialAuth.currentUser || currentUser).getIdToken(true);
var result = await fetch(MODERATION_WORKER_URL, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + token },
  body: JSON.stringify({ type: '<action>', /* ...fields */ })
}).then(r => r.json());
```
