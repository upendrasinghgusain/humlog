# Spec: Humlog — Azure Proof of Concept

**Status:** Phase A deployed 2026-11-14 — <https://humlog.upendragusain.co.uk/> (family, email + PIN) and `/tv/?key=…` (TV). See [docs/how-it-works.md](docs/how-it-works.md).  
**Relates to:** `spec.md` (main project spec). This document does not replace it.  
**Purpose:** Get a frictionless, always-up-to-date family slideshow onto the LG TV now, hosted in Azure, without depending on the Raspberry Pi.

## 1. Requirement

> Open the bookmark on the TV → the latest family photos and videos start playing. Nobody ever has to add, sync, pick or refresh anything.

### How photos reach the album today (unchanged)

Photos sent to the parents in any WhatsApp chat (family group or individual chats) → saved on the phone → backed up to Google Photos → **face recognition adds them to the live shared album "Humlog"**.

The shared album is the single source of truth. The site follows it automatically.

## 2. Decisions

| Decision | Reason |
|---|---|
| Source = the album's **public share link** | The live album already aggregates every chat. |
| **Unofficial method accepted** (reading the public share page), overriding spec.md §4 "no undocumented endpoints" for this PoC | Since 31 March 2025 Google's official APIs cannot read any existing album (shared or not) — only app-created content or items hand-picked via the Picker. A frictionless, auto-updating feed is impossible through official APIs. |
| TV never loads Google pages | The TV's webOS browser cannot load photos.google.com or Google sign-in. The TV only loads our simple page plus plain image/video files. |
| Public URL, no authentication, no metadata stripping | Accepted by the owner. |

### Known risk

Google can change the share page's internal format at any time. When that happens, **new** photos stop appearing (the TV keeps showing the last good catalogue) until the extraction code is updated. The site must fail safe: never replace a good catalogue with an empty or broken one.

## 3. Feasibility test results (2026-11-14)

| Check | Result |
|---|---|
| Share link opens without sign-in | ✅ 200, album "Humlog" |
| Item list embedded in page | ✅ First 300 items in the page data (`AF_initDataCallback`, key `ds:1`) |
| Paging to the rest of the album | ✅ Via the page's own data endpoint (`batchexecute`, RPC `snAcKc`) with continuation token + share key — 4 pages |
| Total items | **1,130** (1,068 photos, **62 videos**), 2013-08-18 → 2026-11-12 (live) |
| Photos load anonymously | ✅ `…=w1920-h1080` → 214 KB JPEG, resized by Google; `…=w480-h480` thumbnail |
| Videos load anonymously | ⚠️ Only the original file via `…=dv` (200, `video/mp4`). **No range requests** (no seeking). Google's transcodes (`=m18/22/37`) return 403 **only when a Referer header is sent**; without one, and with a browser User-Agent, they load (206, ranges, upright H.264). The page sends no referrer; the Sync function's probe sends a browser User-Agent. |
| Sample video format | H.264 + AAC, `moov` at start (plays progressively), 1080×1920, ~4.8 min, **400 MB** |

**Conclusion:** photos are straightforward. Videos work in principle but the originals are very large and not seekable, so they need special handling (§6).

## 4. Architecture

```text
Google shared album (public share link)
        │  every 10 min
        ▼
Azure Function "Sync" (C#, .NET 10, timer)
   - fetches share page + pages 2..n
   - probes each new video once (can the TV stream it?)
   - builds catalogue.json (validated; never overwrite good with bad)
   - (Phase B) caches + transcodes new videos
        │
        ▼
Storage account static website ($web): index.html, app.js, catalogue.json  ◄── LG TV browser
   - photos loaded directly from lh3.googleusercontent.com at TV size
   - re-reads catalogue.json every 10 min; new items appear without reload
```

| Item | Choice |
|---|---|
| Subscription / region | `UpendraPractice` / Central India (close to the TV) |
| Resource group | `rg-family-gallery` |
| Site | Storage account static website (`sthumloge085b6`), firewalled to Cloudflare + the function's subnet, served as `humlog.upendragusain.co.uk` behind Cloudflare Access (family) and a key-checking firewall rule (`/tv`, the TV). Chosen over Static Web Apps: page and catalogue share one origin (no CORS). The function publishes the page files itself. |
| Function storage | `stfamgallerye085b6`: code package and sync state (Azure names kept from before the rename to Humlog) |
| Sync | `func-family-gallery-e085b6`: Azure Functions Flex Consumption (512 MB), C# .NET 10 isolated worker, timer every 10 min + on startup |
| Private state | Container `sync-state`: `video-probes.json` (probe result per video id) |
| Config | Share link stored only as app setting `ALBUM_SHARE_URL` — **not committed to git** (anyone with it can view the album) |
| Deploy / delete | `poc/infra/deploy.ps1 [-ShareUrl …]` (idempotent) / `poc/infra/teardown.ps1` |
| Cost | ~£0–2/month (within the Flex free grant) |

### catalogue.json

```json
{
  "generatedUtc": "2026-11-14T10:00:00Z",
  "items": [
    { "id": "AF1Qip…", "type": "photo", "src": "https://lh3.googleusercontent.com/pw/…", "w": 3060, "h": 4080, "takenUtc": "2026-11-12T…", "addedUtc": "2026-11-15T…" },
    { "id": "AF1Qip…", "type": "video", "src": "…", "w": 1080, "h": 1920, "takenUtc": "…", "addedUtc": "…", "durationMs": 19351, "variant": "=m22" }
  ]
}
```

`src` is Google's base URL; the page appends `=w{W}-h{H}` (photo, poster) or the video's `variant` (`=m37`, `=m22` or `=dv`; the page uses `=dv` until the video has been probed). `addedUtc` is when the item was added to the album (entry field 5 of the share page data; falls back to `takenUtc`). Most recently added first, then newest taken first.

Sync safety rules: write only if the parse succeeded and the item count is ≥ 50% of the previous catalogue; keep the previous version as `catalogue.prev.json`; log every failure.

## 5. The experience (minimal UX)

One full-screen slideshow. No menus.

| Remote button | Photo showing | Video playing |
|---|---|---|
| *(nothing)* | Advances after **8 s** | Plays to the end, then advances |
| **Right** | Next | Skip to next |
| **Left** | Previous | Previous |
| **OK** / Play-Pause | Pause / resume | Pause / resume |
| **Up** / Red | Back to the normal order | Back to the normal order |
| **Down** / Green | Shuffle everything | Shuffle everything |
| **Back** | Nothing (stays in app) | Nothing |

- Crossfade between items (`?fx=slide` for a horizontal slide; fade was smoothest on the TV).
- Every remote press shows a short label ("Next", "Previous", "Newest first", "Shuffled") straight away.
- "⏸ Paused" badge while paused.
- Portrait photos/videos shown whole, centred, with a blurred copy of the same image filling the sides.
- **Order: added in the last 7 days first** (most recently added first), then the rest shuffled, looping. When the sync finds new items, they are played next. Up/Red returns to this order; Down/Green shuffles everything.
- Videos autoplay with sound. If the browser blocks sound, play muted with "🔇 Press OK for sound"; once unmuted, stays unmuted.
- If a video fails to load or play within 15 s, skip it.

URL overrides for testing only: `?secs=12`, `?newDays=7`, `?fx=fade|none`, `?photosOnly=1`.

## 6. Videos — phased

**Phase A (deployed):** play Google's own transcode (`=m37`, else `=m22`), falling back to the original via `=dv`, directly on the TV. Videos longer than **3 minutes** are skipped.

Finding after deploy: originals vary wildly. WhatsApp-sourced clips are small H.264 with the index (`moov`) first (~1 Mbit/s) and stream fine. Clips uploaded straight from phones are often enormous (e.g. 19 s = 990 MB, ~430 Mbit/s) with the index at the end; without range support a browser must download the whole file before playing. So the Sync function **probes each video once** (first 256 KB: `moov` before `mdat`, codec `avc1`, size ≤ 200 MB) and marks the rest `"playable": false`; the page skips those instantly. Probing is slow (Google takes seconds to start each original), so it has a 2-minute budget per run and carries on next run.

Update (2026-11-15): the TV ignores CSS rotation on video (it draws video on a separate hardware layer), so the page's rotate-it-ourselves fix only cropped sideways videos. Google's own transcodes turned out to be usable after all (see §3), and they are upright. The probe now picks, per video, the first variant that streams: `=m37` (1080p), `=m22` (720p), then `=dv`. Result: 68 of 70 videos play a transcode, 2 tiny old ones the original (still sideways), none skipped. The page falls back to `=dv` once if a transcode won't load on the TV.

**Phase B (no longer needed; Google's transcodes do the job):** the Sync function downloads each new video once, transcodes it with ffmpeg to 720p H.264/AAC (~2–4 Mbps, `+faststart`), stores it in Blob Storage (seekable, ~10–30× smaller), and the catalogue points to that copy. Runs on a Container Apps job (ffmpeg in a container). One-off cost to process the existing 62 videos; then only new ones.

## 7. TV browser constraints

- Old Chromium base: plain ES5-style JavaScript, no frameworks, no modules, no `async/await`, no optional chaining.
- One HTML, one CSS, one JS file; no third-party CDNs.
- Only current + next item in the DOM; release `<video>` sources after use.
- Keys: 37/39 Left/Right, 38/403 Up/Red, 40/404 Down/Green, 13 OK, 415/19 Play/Pause, 461 Back — **to be confirmed** on the TV with `/keys.html` (shows every key event in large text).
- The page must take keyboard focus itself on load: opening a bookmark can leave focus on the browser's toolbar.
- Video is drawn on a hardware layer: CSS transforms (rotation) aren't applied to it.

## 8. Project layout

```text
humlog/
  spec.md                 # main spec (unchanged)
  spec-azure-poc.md       # this document
  poc/
    Humlog.slnx
    web/                  # static site: index.html, keys.html, app.js, app.css
    sync/                 # Azure Function (C# .NET 10): album → catalogue.json
    sync.Tests/           # xUnit tests against synthetic page/response/MP4 fixtures
    infra/                # deploy.ps1 / teardown.ps1 (az CLI)
  docs/
    tv-remote.md          # observed LG remote key codes
```

## 9. Acceptance criteria

- [ ] Site live over HTTPS; opens straight into the slideshow.
- [ ] Catalogue contains all album items (currently 1,130) and updates within 10 min of a new photo appearing in the album.
- [ ] A broken or empty sync never wipes the existing catalogue.
- [ ] Left/Right/OK work on laptop keyboard and on the LG remote; key codes recorded.
- [ ] Photos display sharp and correctly oriented on the TV.
- [ ] At least one video plays with sound on the TV (Phase A); otherwise Phase B is implemented.
- [ ] Runs for 1 hour on the TV without slowing down or crashing.
- [ ] Everything can be deleted with one command (`az group delete -n rg-family-gallery`).

## 10. Open questions

1. ~~Order~~ — decided: added in the last 7 days first, then shuffled; Down/Green shuffles everything.
2. Who tests on the TV, and when?
