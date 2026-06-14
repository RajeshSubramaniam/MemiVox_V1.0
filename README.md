# MemiVox Live Scorer (MyScorer)

Live cricket scoreboard overlay system for OBS/ATEM streaming. Pulls live scores from **PlayHQ** and **CricHeroes** and displays them as a transparent overlay on your video stream.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Project Structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Local Development Setup](#local-development-setup)
- [Running the Application](#running-the-application)
- [URL Routing](#url-routing)
- [API Endpoints](#api-endpoints)
- [How It Works (End to End)](#how-it-works-end-to-end)
- [Configuration](#configuration)
- [Testing](#testing)
- [Deployment (Azure App Service)](#deployment-azure-app-service)
- [Custom Domain Setup](#custom-domain-setup)
- [Device Agent (Raspberry Pi / Mini PC)](#device-agent-raspberry-pi--mini-pc)
- [Troubleshooting](#troubleshooting)

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────┐
│                   Azure App Service                  │
│                                                      │
│  ┌─────────────────────────────────────────────────┐ │
│  │              MyScorer.Api (ASP.NET Core)        │ │
│  │                                                 │ │
│  │  Controllers:                                   │ │
│  │    AdminController    → /api/admin/*            │ │
│  │    SetupController    → /api/setup/*            │ │
│  │    MatchController    → /api/match/*            │ │
│  │    DeviceController   → /api/device/*           │ │
│  │                                                 │ │
│  │  Static Frontend (wwwroot/):                    │ │
│  │    /cheesecake/admin/ → Admin Panel             │ │
│  │    /{setupId}         → Client Setup Page       │ │
│  │    /{setupId}/live    → OBS Overlay             │ │
│  └──────────┬──────────────────────────────────────┘ │
│             │                                        │
│  ┌──────────▼──────────┐  ┌──────────────────────┐  │
│  │ MyScorer.Application│  │   MyScorer.Core      │  │
│  │                     │  │                      │  │
│  │  Score Extraction   │  │  Models:             │  │
│  │  PlayHQ Scraper     │  │   SetupRecord        │  │
│  │  CricHeroes Scraper │  │   LiveScoreData      │  │
│  │  EF Core Services   │  │   MatchSnapshot      │  │
│  │  SQLite Database    │  │   DeviceCommand      │  │
│  └─────────────────────┘  │   StreamingDevice    │  │
│                           └──────────────────────┘  │
└──────────────────────────────────────────────────────┘

External APIs:
  ├── PlayHQ GraphQL API (api.playhq.com/graphql)
  ├── PlayHQ Spectator WebSocket (spectator.playhq.com)
  └── CricHeroes Next.js API (_next/data/{buildId}/...)

┌──────────────────────────────────┐
│  MyScorer.DeviceAgent            │
│  (Runs on Mini PC at the ground) │
│  Sends heartbeats to backend     │
│  Receives streaming commands     │
└──────────────────────────────────┘
```

## Project Structure

```
MyScorer.sln                          ← Main solution file
│
├── MyScorer.Api/                     ← ASP.NET Core Web API (main deployable)
│   ├── Controllers/
│   │   ├── AdminController.cs        ← Admin CRUD (setups, clients, matches)
│   │   ├── SetupController.cs        ← Client setup + live score endpoint
│   │   ├── MatchController.cs        ← Match state management
│   │   └── DeviceController.cs       ← Device heartbeat + command queue
│   ├── Services/
│   │   └── MaintenanceService.cs     ← Background cleanup (stale data, stuck commands)
│   ├── wwwroot/                      ← Static frontend files
│   │   ├── index.html                ← Root redirect → admin panel
│   │   ├── setup/index.html          ← Client setup page (password gate + match URL)
│   │   ├── overlay/live.html         ← OBS overlay (transparent scoreboard)
│   │   └── cheesecake/admin/index.html ← Admin panel
│   ├── Program.cs                    ← App startup, middleware, health endpoints
│   └── appsettings.json              ← Configuration (admin password, PlayHQ config)
│
├── MyScorer.Application/             ← Business logic layer
│   ├── Data/
│   │   └── MyScorerDbContext.cs      ← EF Core context (SQLite)
│   └── Services/
│       ├── ScoreExtractionService.cs ← Score fetching orchestrator (7s cache)
│       ├── InMemoryMatchStateService.cs ← Live match state (in-memory)
│       ├── EfCoreAdminStateService.cs   ← Admin data (DB-backed)
│       ├── EfCoreMatchRegistrationService.cs ← Match registrations (DB-backed)
│       └── Providers/
│           ├── PlayHqScraper.cs          ← PlayHQ GraphQL API scraper
│           ├── PlayHqSpectatorService.cs ← PlayHQ WebSocket live feed
│           ├── CricHeroesScraper.cs      ← CricHeroes Next.js scraper
│           └── IProviderScraper.cs       ← Provider interface
│
├── MyScorer.Core/                    ← Shared models and validation
│   └── Models/
│       ├── SetupRecord.cs            ← Setup configuration
│       ├── LiveScoreData.cs          ← Unified score data model
│       ├── MatchSnapshot.cs          ← Point-in-time match state
│       ├── MatchRegistrationRecord.cs ← Match URL registration
│       ├── DeviceCommand.cs          ← Remote device commands
│       ├── DeviceHeartbeat.cs        ← Device health ping
│       └── StreamingDevice.cs        ← Registered streaming device
│
├── MyScorer.Infrastructure/          ← Infrastructure (minimal, placeholder)
│
├── MyScorer.DeviceAgent/             ← Separate app for field devices
│   ├── Services/
│   │   ├── HeartbeatWorker.cs        ← Sends heartbeats every 5s
│   │   ├── BackendApiClient.cs       ← HTTP client to backend API
│   │   └── AtemDetector.cs           ← ATEM switcher detection
│   └── appsettings.json              ← BackendUrl, DeviceId, AtemIp
│
├── myscorer-platform/
│   └── MyScorer.Tests/               ← xUnit unit tests (26 tests)
│
├── regression-test.ps1               ← Integration test suite (68 tests, needs pwsh)
└── .gitignore
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell/releases) (for regression tests only)
- Any IDE: Visual Studio 2022, VS Code, or Rider

## Local Development Setup

```bash
# 1. Clone the repository
git clone https://github.com/YOUR_ORG/MyScorer-main.git
cd MyScorer-main

# 2. Restore dependencies
dotnet restore

# 3. Build the solution
dotnet build
```

No database setup needed — SQLite creates `myscorer.db` automatically on first run.

## Running the Application

```bash
cd MyScorer.Api
dotnet run
```

The server starts at **http://localhost:5201** (configured in `Properties/launchSettings.json`).

### Quick Smoke Test

Open a browser:
- http://localhost:5201/health → Should return JSON with `"status": "healthy"`
- http://localhost:5201/cheesecake/admin/ → Admin panel

## URL Routing

The application uses custom middleware for URL routing. Understanding this is critical:

| URL Pattern | What It Serves | Example |
|---|---|---|
| `/` | Redirects to admin panel | → `/cheesecake/admin/` |
| `/cheesecake/admin/` | Admin panel (manage setups, matches) | Direct access |
| `/{setupId}` | Client setup page (enter match URL) | `/23082201` |
| `/{setupId}/live` | OBS overlay (transparent scoreboard) | `/23082201/live` |
| `/api/*` | REST API endpoints | `/api/admin/setups` |
| `/health` | Health check | Returns JSON |
| `/health/detail` | Detailed diagnostics (DB stats) | Returns JSON |

**Blocked routes** (return 404 by design):
- `/setup` — must use `/{setupId}` instead
- `/overlay` — must use `/{setupId}/live` instead

**How `/{setupId}` works**: The middleware checks if the first URL segment is alphanumeric (1-20 chars) and not a reserved word. If so, it serves `wwwroot/setup/index.html`. The JavaScript on that page reads the setupId from the URL path and uses it for API calls.

## API Endpoints

### Admin (`/api/admin`) — Requires `X-Admin-Password` header

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/admin/validate-password` | Validate admin password |
| GET | `/api/admin/setups` | List all setups |
| POST | `/api/admin/setups` | Create a new setup |
| GET | `/api/admin/clients` | List all clients |
| POST | `/api/admin/clients/{setupId}` | Update client settings |
| GET | `/api/admin/matches/{setupId}` | List matches for a setup |
| GET | `/api/admin/cricheroes-buildid` | Get current CricHeroes build ID |
| POST | `/api/admin/cricheroes-buildid` | Set CricHeroes build ID manually |

### Setup (`/api/setup`) — Requires `X-Admin-Password` or `X-Setup-Password` header

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/setup/{setupId}/validate-password` | Validate setup password (rate-limited: 5/min) |
| POST | `/api/setup/{setupId}/change-password` | Change setup password |
| GET | `/api/setup/{setupId}/active-match` | Get active match registration |
| GET | `/api/setup/{setupId}/matches` | List all match registrations |
| POST | `/api/setup/{setupId}/matches` | Register a new match URL |
| GET | `/api/setup/{setupId}/live-score` | **Get live score data** (used by overlay) |

### Match (`/api/match`)

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/match/test-extract?matchUrl=...` | Test score extraction from a URL |
| GET | `/api/match/{setupId}` | Get current match state |
| POST | `/api/match/{setupId}/update` | Update match state snapshot |

### Device (`/api/device`)

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/device/heartbeat` | Record device heartbeat |
| GET | `/api/device/{deviceId}/status` | Check device online/offline status |
| POST | `/api/device/command` | Queue command (START_STREAM/STOP_STREAM) |
| GET | `/api/device/{deviceId}/next-command` | Poll for next pending command |
| POST | `/api/device/command/ack` | Acknowledge command delivery/completion |
| GET | `/api/device/devices` | List all streaming devices |

### Health (Minimal API — no auth)

| Method | Endpoint | Description |
|---|---|---|
| GET | `/health` | Basic health (uptime, memory, GC) |
| GET | `/health/detail` | Detailed diagnostics (DB counts, maintenance stats) |

## How It Works (End to End)

### 1. Admin creates a Setup

Admin opens `/cheesecake/admin/`, enters the admin password, and creates a **Setup** (e.g., SetupId: `23082201`). Each setup gets a default client password.

### 2. Client configures their match

The client opens `/{setupId}` (e.g., `/23082201`), enters their setup password, and pastes a match URL from:
- **PlayHQ**: `https://www.playhq.com/cricket-australia/org/.../game/bf90b123`
- **CricHeroes**: `https://cricheroes.in/scorecard/25357111`

This registers the match URL in the database.

### 3. Live score extraction

When anyone opens `/{setupId}/live`, the overlay page polls `GET /api/setup/{setupId}/live-score` every 8 seconds.

The backend:
1. Looks up the active match registration for that setupId
2. Determines the provider (PlayHQ or CricHeroes) from the URL
3. Calls the appropriate scraper to fetch live data
4. Returns a unified `LiveScoreData` JSON response
5. Caches results for 7 seconds to avoid hammering external APIs

### 4. OBS overlay displays the score

The overlay page (`live.html`) is a fully transparent HTML page. Only the scoreboard card is visible — everything else is see-through. This is designed to be added as a **Browser Source** in OBS Studio or loaded in a Chromium browser captured by an ATEM switcher.

### 5. Match auto-completion

When the score provider reports a match as "Completed", the backend automatically deactivates the match registration. The overlay shows the final score with a "COMPLETED" badge.

## Configuration

### `appsettings.json`

```json
{
  "Admin": {
    "Password": "admin@myscorer2026"    ← Change this in production!
  },
  "PlayHQ": {
    "SpectatorWsUrl": "wss://spectator.playhq.com/graphql"
  }
}
```

**Important**: For production, set the admin password via environment variable:
```
Admin__Password=your-secure-password
```

In Azure App Service, set this under **Settings → Environment variables**.

### Database

SQLite database (`myscorer.db`) is created automatically on first run. No migrations needed — `EnsureCreated()` handles schema creation.

**Tables**: Setups, Clients, Matches, StreamingDevices, DeviceCommands

## Testing

### Unit Tests (26 tests)

```bash
dotnet test myscorer-platform/MyScorer.Tests/MyScorer.Tests.csproj
```

Test coverage:
- `ValidationTests` — Input validation (URLs, setupIds, passwords)
- `CacheEvictionTests` — Score cache TTL and eviction
- `CommandLifecycleTests` — Device command state machine
- `DeviceHeartbeatTests` — Heartbeat processing
- `MaintenanceDiagnosticsTests` — Background maintenance stats

### Integration Tests (68 tests)

Requires the server to be running and **PowerShell 7** (`pwsh`):

```powershell
# Terminal 1: Start the server
cd MyScorer.Api
dotnet run

# Terminal 2: Run integration tests (must use pwsh, not powershell)
pwsh -File regression-test.ps1
```

> **Note**: Windows PowerShell 5.1 (`powershell`) will NOT work — the tests use `-SkipHttpErrorCheck` which requires PowerShell 7+.

## Deployment (Azure App Service)

The application is deployed as a single Azure App Service (Linux).

### What gets deployed

Only `MyScorer.Api` — it includes the Application, Core, and Infrastructure projects as dependencies, plus all static frontend files in `wwwroot/`.

### Deploy via GitHub

1. Push code to GitHub
2. In Azure Portal → App Service → **Deployment Center**
3. Connect to your GitHub repo
4. Azure will build and deploy automatically on push

### Deploy via CLI

```bash
# Publish
dotnet publish MyScorer.Api/MyScorer.Api.csproj -c Release -o ./publish

# Deploy using Azure CLI
az webapp deploy --resource-group YOUR_RG --name memivox-live --src-path ./publish --type zip
```

### Azure Configuration

Set these in **Azure Portal → App Service → Settings → Environment variables**:

| Name | Value | Purpose |
|---|---|---|
| `Admin__Password` | (your secure password) | Override default admin password |
| `ASPNETCORE_ENVIRONMENT` | `Production` | .NET environment |

### Health Check

Configure Azure health probe to hit `/health` — it returns `200 OK` with JSON when the app is running.

### Current Production URL

`https://memivox-live-ehasebg3edhuahf2.australiaeast-01.azurewebsites.net`

Custom domain: `https://live.memivox.com.au` (if DNS is configured)

## Custom Domain Setup

The production domain is `live.memivox.com.au` (subdomain of the marketing site `memivox.com.au`).

### DNS Setup (at Netlify — where memivox.com.au DNS is managed)

| Type | Name | Value |
|---|---|---|
| CNAME | `live` | `memivox-live-ehasebg3edhuahf2.australiaeast-01.azurewebsites.net` |
| TXT | `asuid.live` | *(Azure domain verification ID)* |

### Azure Setup

1. Azure Portal → App Service → **Custom domains** → Add `live.memivox.com.au`
2. Enable free managed SSL certificate

### Domain Registrar

- **Registrar**: VectorIP
- **Nameservers**: Netlify (NS1 — `dns1.p09.nsone.net` through `dns4.p09.nsone.net`)
- **DNS records managed at**: [app.netlify.com](https://app.netlify.com) → Domain management → DNS settings

> Do NOT add DNS records at VectorIP — the nameservers point to Netlify, so only Netlify DNS records are active.

## Device Agent (Raspberry Pi / Mini PC)

The Device Agent is a separate .NET Worker Service that runs on the streaming device at the cricket ground.

### What it does

- Sends heartbeats to the backend every 5 seconds
- Reports OBS/camera/ATEM status
- Receives remote commands (START_STREAM, STOP_STREAM)

### Configuration (`MyScorer.DeviceAgent/appsettings.json`)

```json
{
  "BackendUrl": "https://live.memivox.com.au",
  "DeviceId": "SETUP001",
  "DeviceName": "Pi-Field-01",
  "AtemIp": "192.168.1.100"
}
```

### Running

```bash
cd MyScorer.DeviceAgent
dotnet run
```

The Device Agent has its own solution file (`MyScorer.DeviceAgent.sln`) and can be built/deployed independently.

## Troubleshooting

### Common Issues

| Problem | Cause | Fix |
|---|---|---|
| `/health` works but UI returns 404 | URL routing mismatch | Use `/{setupId}` not `/setup`. Use `/cheesecake/admin/` (with trailing slash) |
| Admin panel won't load | Missing trailing slash | Navigate to `/cheesecake/admin/` (not `/cheesecake/admin`) |
| "Waiting for match data" on overlay | No active match registered | Go to `/{setupId}`, enter password, and register a match URL |
| CricHeroes returns empty scores | Stale buildId | Go to admin panel → Settings → update CricHeroes build ID |
| PlayHQ spectator WS fails | Geo-restriction | PlayHQ spectator only works from Australian IPs |
| Regression tests fail | Using wrong PowerShell | Use `pwsh` (PowerShell 7), not `powershell` (Windows PowerShell 5.1) |
| SQLite errors after schema change | Schema drift | Delete `myscorer.db` — it will be recreated on next startup |
| Build warnings about NuGet sources | Corporate NuGet proxy unreachable | Safe to ignore — packages are cached locally |

### Useful Diagnostic URLs

- `/health` — Quick uptime check
- `/health/detail` — Full diagnostics (memory, DB counts, maintenance stats, command queue)
- `/api/match/test-extract?matchUrl=URL` — Test if a match URL can be scraped

### Logs

- **Local**: Console output from `dotnet run`
- **Azure**: Portal → App Service → **Log stream** (real-time) or **Diagnose and solve problems**

---

## Score Providers

### PlayHQ

- **Method**: GraphQL API (`POST https://api.playhq.com/graphql`)
- **Auth**: None required for public match data
- **Tenant**: Extracted from URL (e.g., `cricket-australia`)
- **Live feed**: WebSocket spectator (`wss://spectator.playhq.com/graphql`) — AU IPs only
- **Supported**: Multi-innings (2-day matches), finals, all formats

### CricHeroes

- **Method**: Next.js `_next/data/{buildId}` API (bypasses Cloudflare WAF)
- **Build ID**: Changes when CricHeroes deploys. Auto-heals when stale, or set manually via admin panel
- **Cache**: 7-second TTL to avoid rate limiting
- **Fallback**: Serves stale cache during transient errors

---

*Built by MemiVox — Live cricket streaming overlay system for Australian community cricket.*
