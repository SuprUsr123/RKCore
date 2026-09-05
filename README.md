# RKCore — KindleChat Console Client

A cross-platform (.NET 8, VB.NET) console client for the ReKindle platform: KindleChat, Topics, and Neighbourhood. Talks to the same Firebase projects and moderation worker as the web app, but from your terminal.

## Features

### Account
- **Register** — creates an account and auto-logs in (no token hunting)
- **Login** — username/password (accounts are `<username>@rekindle.ink`), custom token, or paste a Firebase ID token
- **Session persistence** — refresh tokens are saved after login, so the app doesn't ask for credentials again on the next launch (logout clears the session)
- **Diagnostics screen** — shows whether the config/API keys loaded, pings the Firebase Identity Toolkit and the Cloud Functions base

### KindleChat
- **Send / view messages** with usernames resolved from `user_cards`, reactions, replies, and message IDs shown for every message
- **Pixel art** — browse your saved library (main-project Firestore), enter a grid manually, or **import any image** (center-cropped, scaled to 256×256, rendered to a PNG data URL) with an ASCII preview before sending
- **Flipnotes** — browse your saved library, enter JSON manually, or **import an animated GIF** (frames coalesced, scaled to 256×256, fps derived from the GIF delay)
- **Live mode** — polls for new messages every 3 s, silent send that doesn't clobber your input line
- **Reactions** and **message deletion** (author or moderator)

### Topics
- Browse topics page-by-page (`[S]ee More` = 20 topics per fetch) with search, poll marks, comment counts
- View a topic: poll with **live vote counts and percentages**, comments with author names
- Create topics (with optional 2–4 option polls), comment, vote, report, delete your own topic

### Neighbourhood
- Browse posts page-by-page (`[S]ee More` = 10 posts per fetch, matching the web app's page size)
- View a post with comments and author names
- Create posts, comment, report, delete your own post

### Translation
- Background translation of sent messages via the translation worker, with silent retry/backoff (no output bleeding into your typing)

## Build

Requires the .NET 8 SDK.

**Linux / macOS**
```sh
./build.sh          # build (Debug)
./run.sh            # run
```

**Windows**
```bat
build.cmd           :: build (Debug)
run.cmd             :: run
```

Release: `./build.sh Release` or `build.cmd Release`.

## Configuration

`rekindle-config.json` (copied to the output dir on build) holds the endpoint bases and the public Firebase web API keys for both projects. It is loaded at startup from the app directory, the working directory, or the assembly directory — whichever finds it first.

## Session storage

`.rk-session` (next to the executable) stores the main/social refresh tokens after login and is deleted on logout. Add it to any sync/backup exclusions — treat it like a password.

## Architecture notes

- `ServerReader.vb` — the adaptable API core (`ReKindleSocialClient`): Firebase auth + refresh-token rotation, cloud-function callable protocol (v2 `onCall` wrapping), RTDB reads/writes (`?auth=` token), Firestore structured queries (`:runQuery` RPC, same as the SDK), moderation/translation workers, per-collection caches with TTL and invalidation
- `KindleChatConsole.vb` — console UI and flows

## Known issues

- **Firestore 429 "Quota exceeded"** — the Firestore REST API has a far lower queries-per-second ceiling than the Firebase SDK's streaming connection the web app uses. Mitigations already in place: `:runQuery` RPC instead of the heavily limited ListDocuments endpoint, cursor-based "See More" pagination (20 topics / 10 posts per fetch), 5-minute caches for lists/comments/poll votes, and automatic exponential-backoff retry (2s→32s) on 429/500/503. If you still hit it, wait ~60s for the burst window to reset; persistent 429s mean the project's daily read quota (Spark plan: 50k/day) is shared with the live web app and is genuinely exhausted — check the Firebase console usage page.
- **Rate-limited actions on the moderation worker** — posting topics/messages/posts is subject to server-side token buckets (e.g. 5 neighbourhood posts per 5 min). Errors surface with the server's message; wait and retry.
- **Topic comment pagination** — topic comments load up to 50 in one page (no "see more" yet); neighbourhood comments load 20.
- **Translation output** — retries are silent, so a failed translation just means the message shows untranslated in the web app.
- **Login on first launch only** — if the session file is stale (revoked refresh token), you'll be dropped to the login menu once, then stay logged in again.
