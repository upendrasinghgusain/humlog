# Project Specification: Humlog for LG Smart TV

**Project name:** Humlog  
**Target platform:** Raspberry Pi 5 + LG webOS Smart TV  
**Primary objective:** Build a simple, reliable, remote-controlled family photo and video gallery that parents can access through the TV's existing web browser.

## 1. Background and objectives

I am currently in India, while my Raspberry Pi server is at my home in the UK.

I want to develop an application locally and deploy it remotely to the Raspberry Pi using SSH.

The application will retrieve/import photos and videos from my personal Google Photos account, maintain a local media library, and expose a lightweight website.

My parents should be able to:

1. Switch on the LG television.
2. Open its web browser.
3. Select a bookmarked URL.
4. See a full-screen family gallery.
5. Browse photographs and videos using the TV remote.
6. Start an automatic slideshow.
7. Open and play individual videos.
8. Return to the gallery without needing a phone, keyboard or computer.

The solution should reuse the existing television and Raspberry Pi. Avoid purchasing additional streaming hardware.

## 2. Existing infrastructure

### Raspberry Pi

- Raspberry Pi 5.
- 128 GB storage.
- Located at my home in the UK.
- Connected to the home network and internet.
- Existing applications and services already run on it.
- Cloudflare Tunnel is installed directly on the Pi, outside Docker (believed to be the existing configuration).
- Existing domain: `upendragusain.co.uk`.
- Existing SSH access was previously configured using the Pi's private LAN IP address (`192.168.x.x`).
- Existing Docker/Docker Compose environment.

The agent must inspect the existing infrastructure before making changes. Do not overwrite existing configurations or disrupt existing applications.

### Television

Exact model: **LG 43UM7600PTA**

Known specifications:

- 43-inch LED television.
- 3840 × 2160 (4K UHD).
- webOS 4.5 according to LG's model specifications.
- Built-in Wi-Fi.
- Built-in web browser.
- HDMI, USB and LAN connections.

The TV is older, so assume browser compatibility is constrained until tested.

The application must be tested on the actual TV browser, not merely desktop Chrome.

Official model reference:
https://www.lg.com/nz/tv-soundbars/uhd-4k-tv/43um7600pta/

## 3. Phase 0 — Remote infrastructure and deployment

This phase must be completed before significant application development.

### 3.1 Inspect existing Cloudflare infrastructure

Determine:

- Whether `cloudflared` is running as a system service.
- Whether it is running under systemd.
- Which tunnel is currently active.
- Whether it is locally managed or remotely managed.
- Existing published application routes.
- Existing DNS records.
- Existing Cloudflare Access policies.
- Whether SSH is already exposed through Cloudflare.

Do not expose SSH directly to the public internet.

### 3.2 Establish remote SSH access

Preferred approach: reuse the existing Cloudflare Tunnel.

Configure an SSH route through Cloudflare Tunnel and Cloudflare Access if one does not already exist.

The objective is to enable SSH from my development laptop in India to the Pi in the UK.

Requirements:

- SSH key authentication.
- No inbound router port forwarding.
- Cloudflare Access identity-based protection.
- No passwords or private keys committed to source control.
- Document the SSH client configuration.
- Preserve the existing LAN SSH configuration.

Tailscale is an acceptable fallback if the existing Cloudflare setup proves unsuitable.

Official reference:
https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/use-cases/ssh/

### 3.3 Cloudflare configuration management

I want to be able to inspect and update the Cloudflare Tunnel configuration remotely.

Initially, use the Cloudflare dashboard rather than building a custom Cloudflare administration application.

Document:

- Tunnel identifier and name.
- Connector status.
- Existing hostnames and routes.
- DNS configuration.
- SSH route.
- New gallery hostname.
- Access policies.
- Rollback procedure.

Never expose or log tunnel tokens, API tokens or credentials.

Do not replace the existing tunnel or rewrite its configuration unnecessarily.

### 3.4 Deployment workflow

Create a repeatable deployment process.

Preferred workflow:

1. Develop locally.
2. Build the application.
3. Deploy using SSH.
4. Use Docker Compose on the Pi.
5. Verify application health.
6. Preserve existing application data during upgrades.

Provide scripts such as:

- `deploy.sh`
- `status.sh`
- `logs.sh`
- `backup.sh`

Deployment should be repeatable and idempotent.

## 4. Phase 1 — Google Photos integration feasibility

This is a critical architectural decision and must be investigated before implementation.

### Objective

Use my own Google Photos account as the source of photographs and videos.

I would ideally like the application to retrieve existing photos and videos and subsequently identify new additions.

### Important Google API constraints

Google changed its Photos APIs on 31 March 2025.

The Library API can no longer be used to list and retrieve arbitrary existing media from a user's library. It is restricted to app-created content.

The Google Photos Picker API allows the user to select media from their library and grant the application access to those selections.

References:

https://developers.google.com/photos/support/updates

https://developers.google.com/photos/overview/about

### Required investigation

The agent must establish:

1. Whether the Picker API can support the desired import workflow.
2. Whether selected photographs and videos can be retrieved and stored locally on the Pi.
3. How long access to selected media remains valid.
4. Whether the user must repeat selection periodically.
5. Whether new photos can be detected without another user selection.
6. Whether any other officially supported Google mechanism enables incremental access.

Do not use undocumented Google endpoints, browser scraping, cookie extraction or unofficial authentication workarounds.

### Architectural fallback

If automatic synchronisation of the whole Google Photos library is not supported, implement an explicit import workflow:

- I authenticate with my Google account.
- I select photographs and videos using Google Photos Picker.
- The application imports the selected items into its own media library.
- The application retains local copies.
- New selections can be imported later.

The TV gallery must work independently of Google Photos once media has been imported.

**Important:** Do not claim that the application supports automatic Google Photos synchronisation unless this has been demonstrated using an officially supported interface.

## 5. Phase 2 — Humlog application

### Preferred technology stack

Use technologies that are already familiar and practical for the existing environment.

Backend:
- ASP.NET Core Web API.
- C#.
- SQLite for metadata and catalogue.
- Local filesystem for media storage.

Frontend:
- Angular SPA, if compatible with the TV browser.
- Alternatively, a lightweight HTML/CSS/JavaScript application if Angular proves too heavy.
- Responsive design.
- Keyboard and remote-control navigation.

Infrastructure:
- Docker.
- Docker Compose.
- Cloudflare Tunnel.
- HTTPS.
- Persistent Docker volumes.

Avoid introducing unnecessary databases, message brokers or external cloud dependencies.

### Suggested project structure

    humlog/
      src/
        api/
        web/
      data/
        media/
        thumbnails/
        database/
      docker-compose.yml
      Dockerfile
      deploy.sh
      README.md

Keep persistent data outside container filesystems.

### Media catalogue

Store metadata including:

- Media ID.
- Original filename.
- Media type (photo/video).
- Capture date/time.
- Import date/time.
- Original dimensions.
- Thumbnail location.
- Original media location.
- File size.
- Duration for videos.
- Source identifier, where available.
- Checksum for duplicate detection.

Support chronological ordering and grouping by date.

### Media handling

Requirements:

- Preserve original media.
- Generate appropriately sized thumbnails.
- Generate browser-compatible video versions where necessary.
- Avoid repeatedly downloading or processing identical media.
- Support large photographs and videos without loading everything into browser memory.
- Use lazy loading and pagination.
- Handle interrupted imports gracefully.

Do not transcode every video by default. Assess browser compatibility and transcode only when necessary.

## 6. Phase 3 — User experience

The interface is primarily designed for older, non-technical users operating a television remote.

Do not design it as a desktop administration dashboard.

### Home screen

The default screen should be visually simple.

Display:

- A large title: Humlog.
- Recent family photographs.
- Large, clearly visible thumbnails.
- A prominent Play Slideshow option.
- A Videos section.
- A Browse by Date option.
- A clear way to return Home.

Avoid small text, dense menus and complicated navigation.

### Main gallery browsing

The default browsing experience should be a thumbnail grid.

Requirements:

- Large thumbnails.
- 4K television support.
- Visible focus indicator.
- Predictable directional navigation.
- Remote-control friendly controls.
- No mouse dependency.
- No keyboard dependency.
- Smooth transitions.
- Lazy loading.

The focused thumbnail must always be visually obvious.

### Slideshow mode

Requirements:

- Full-screen photographs.
- Automatic progression.
- Configurable display duration.
- Smooth transitions.
- Random or chronological ordering.
- Ability to pause and resume.
- Ability to move to the previous or next photograph.
- Ability to exit slideshow easily.

Provide a simple, unobtrusive overlay showing the date where available.

### Video playback

Requirements:

- Videos displayed separately or clearly distinguished from photographs.
- Selecting a video opens a dedicated playback screen.
- Play/pause.
- Seek forwards and backwards where supported.
- Return to gallery.
- Handle unsupported codecs gracefully.

Prioritise common browser-compatible formats, especially MP4 with H.264 video and AAC audio.

Test actual playback on the LG television.

### Remote-control interaction

Support the physical LG remote.

Expected controls:

| Remote button | Action |
|---|---|
| Up / Down / Left / Right | Move focus |
| OK / Enter | Open selected item |
| Back | Return to previous screen |
| Play / Pause | Control slideshow or video where supported |
| Left / Right during slideshow | Previous / next photo |
| Home | Return to gallery home |

Do not assume all LG remote buttons generate standard browser keyboard events. Inspect actual events on the television.

Provide a fallback interaction model if the TV browser does not expose certain buttons.

### Screen and browser requirements

- Design for 3840 × 2160 screens.
- Also support smaller resolutions.
- Use large typography.
- High contrast.
- Avoid hover-only interactions.
- Avoid browser-native controls where they are difficult to operate with a remote.
- No autoplay with sound.
- No unnecessary animations.
- Avoid excessive CPU and memory consumption.

The application must be usable even if the TV browser has limited JavaScript or CSS support.

## 7. Phase 4 — Security

The gallery will expose private family photographs and videos over the internet.

Requirements:

- HTTPS only.
- No public directory listing.
- Authentication or an appropriately protected access mechanism.
- Do not expose the media filesystem directly.
- Protect administrative functions separately from the TV browsing interface.
- Rate-limit authentication attempts.
- Use secure, expiring media URLs where appropriate.
- Protect API endpoints.
- Do not expose Google OAuth refresh tokens to the browser.
- Store secrets outside source control.
- Keep Google access tokens encrypted or otherwise securely protected.
- Provide a way to revoke Google access.

The TV interface should not require parents to complete a complicated Google login.

Investigate whether a simple PIN-based TV login can be implemented securely, with a long-lived TV session and the ability to revoke it remotely.

## 8. Phase 5 — Reliability and operations

The application must:

- Start automatically after Pi reboot.
- Recover after a container restart.
- Persist the media catalogue and original files.
- Provide application health checks.
- Log import failures.
- Log playback/API errors.
- Support backups.
- Avoid exhausting the Pi's storage.
- Provide storage usage information.
- Support remote updates.

Do not allow application deployment to interrupt the existing Cloudflare Tunnel service.

## 9. Acceptance criteria

The project is not complete until the following tests pass.

### Infrastructure

- [ ] SSH works from my laptop in India.
- [ ] No public inbound SSH port is required.
- [ ] Existing Cloudflare-hosted websites continue working.
- [ ] New gallery hostname works over HTTPS.
- [ ] Deployment can be repeated without losing data.

### Google Photos

- [ ] Google OAuth authentication works.
- [ ] The officially supported media-selection/import flow works.
- [ ] Selected photographs can be downloaded and retained locally.
- [ ] Selected videos can be imported and retained locally.
- [ ] Duplicate media is detected.
- [ ] Limitations on automatic synchronisation are documented honestly.

### Television

- [ ] Parents can open the bookmarked URL.
- [ ] Gallery home screen loads on LG webOS browser.
- [ ] Thumbnail grid works with the LG remote.
- [ ] Individual photographs open.
- [ ] Slideshow starts and stops.
- [ ] Previous/next navigation works.
- [ ] Individual videos play.
- [ ] Back navigation works.
- [ ] The application remains usable after extended browsing.

### Reliability

- [ ] Pi reboot does not break the application.
- [ ] Container restart does not lose data.
- [ ] Media remains available if Google Photos is temporarily unavailable.
- [ ] The application does not interfere with existing Pi services.

## 10. Development approach

Work incrementally. Do not attempt the complete system in one implementation.

### Milestone 1 — Remote infrastructure

Establish remote SSH access and document the existing Cloudflare configuration.

### Milestone 2 — TV browser proof of concept

Before implementing Google Photos integration, deploy a static test gallery containing:

- 20 sample photographs.
- 3 sample videos.
- A thumbnail grid.
- Slideshow mode.
- Basic video playback.
- Remote navigation.

Test this on the actual LG television.

This is a critical early milestone. If the browser cannot provide a satisfactory experience, reassess the hosting/client architecture before building the rest.

### Milestone 3 — Google Photos import

Implement and test the supported Picker API workflow.

Persist imported media locally.

### Milestone 4 — Full gallery

Implement the catalogue, thumbnails, browsing, slideshow and video playback.

### Milestone 5 — Production deployment

Configure authentication, backups, health checks, monitoring and automated deployment.

## 11. Explicit non-goals

Do not:

- Replace the LG television.
- Require a Google TV or Fire TV stick.
- Require parents to use a mobile phone.
- Require parents to sign into Google.
- Build a general-purpose cloud photo management platform.
- Attempt to bypass Google Photos API restrictions.
- Replace or disrupt existing Pi services.
- Build a custom Cloudflare administration dashboard.
- Introduce expensive hosting or hardware.

## 12. Expected deliverables

1. Infrastructure assessment and remote SSH setup documentation.
2. Cloudflare Tunnel configuration documentation.
3. Working remote deployment pipeline.
4. Google Photos integration feasibility report.
5. Working family gallery application.
6. Docker Compose deployment.
7. TV remote-control interaction documentation.
8. Backup and recovery instructions.
9. A concise README explaining how I develop, deploy and maintain the application.

## Guiding principle

Build a reliable, deliberately simple family photo appliance.

The technical implementation should be sophisticated where necessary, but the experience for my parents should be extremely simple:

**Open browser → Select bookmark → Browse family photographs and videos.**