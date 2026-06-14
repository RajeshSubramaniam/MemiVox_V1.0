# MemiVox — System Overview (V1)

## 1. High-Level Architecture

MemiVox is a lightweight SaaS system for:
- Match setup
- Live scoring
- Overlay rendering (OBS / ATEM)

### Architecture

- Netlify → Marketing site (`memivox.com.au`)
- Azure App Service → Backend + UI
- GitHub → Source + CI/CD

---

## 2. Hosting Distribution

### Marketing (Netlify)
- Domain: memivox.com.au
- Purpose: landing page

### Backend (Azure)
- URL: https://memivox-live-*.azurewebsites.net
- Handles:
  - API
  - Setup UI
  - Overlay UI
  - Admin UI

### Code (GitHub)
- Repo: MemiVox_V1.0
- Auto deploy via GitHub Actions

---

## 3. Application Structure

### Backend
- ASP.NET Core (.NET 8)
- Project: MyScorer.Api

### Frontend (Static)
Served from:
wwwroot/setup/overlay/cheesecake/admin/

---

## 4. Routing

- `/` → redirects to `/setup`
- `/setup` → client portal
- `/live` → overlay
- `/cheesecake/admin` → admin
- `/health` → health check
- `/api/...` → APIs
- `/swagger` → dev tool

---

## 5. Deployment Flow
Local → GitHub → Actions → Azure → Live
### Build
dotnet build MyScorer.Api.csproj
### Publish
dotnet publish → publish/
### Startup (Azure)
dotnet MyScorer.Api.dll
---

## 6. Configuration

### Source

appsettings.json
### Important
- Azure uses `appsettings.json`
- NOT `appsettings.Development.json`

---

## 7. System Flow

### User Flow

User → /setup → enter password → create match → overlay
### Overlay Flow

/live → fetch API → refresh → display score

---

## 8. Match Lifecycle

### Pre-Match
- Setup match
- Configure teams

### During Match
- Update data
- Overlay auto updates

### Post-Match
- Stop stream
- Review usage

---

## 9. Current System State

- Backend deployed ✅
- CI/CD working ✅
- Routes working ✅
- Health endpoint ✅
- Zero hosting cost ✅

---

## 10. Constraints (F1 Plan)

- No custom domain
- Limited CPU
- Cold start possible

---

## 11. Future Plan

- app.memivox.com.au (custom domain)
- Better UI
- Overlay improvements
- Auth system

---

## 12. Principles

- Keep it simple
- Minimize cost
- Prefer reliability
- Fast iteration

---

## 13. Summary

You have:
- Cloud backend ✅
- Overlay system ✅
- CI/CD pipeline ✅
- Working SaaS base ✅

👉 Next focus:
- Real usage
- Pilot testing
- Feedback
