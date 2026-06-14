# MemiVox — Deployment & Hosting Guide (V1)

---

## 🧭 1. Overview

This document explains how MemiVox is deployed, hosted, and maintained.

### High-level flow

GitHub → Build → Publish → Azure App Service → Live API

---

## ✅ 2. Components

### GitHub
- Repo: MemiVox_V1.0
- Contains full source code
- Triggers deployment via GitHub Actions

---

### Azure App Service
- Name: memivox-live
- Region: Australia East
- OS: Linux
- Runtime: .NET 8
- Plan: Free F1

---

### Live URL

https://memivox-live-ehasebg3edhuahf2.australiaeast-01.azurewebsites.net

---

## 🚀 3. Deployment Process

### Automatic Deployment (CI/CD)

Triggered when code is pushed to main branch.

---

### Workflow File

.github/workflows/main_memivox-live.yml

---

### Build Step

dotnet build MyScorer.Api/MyScorer.Api.csproj --configuration Release

---

### Publish Step

dotnet publish MyScorer.Api/MyScorer.Api.csproj -c Release -o publish

---

### Artifact Handling

Upload → publish  
Download → publish  
Deploy → publish  

This ensures Azure receives the correct app files.

---

### Deploy Step

package: publish

---

## ✅ 4. Azure Configuration

### Startup Command (CRITICAL)

dotnet MyScorer.Api.dll

Without this, Azure shows:
"Your web app is running and waiting for your content"

---

### Restart After Deployment

Recommended after deployment:
Azure Portal → App Service → Restart

---

## ✅ 5. Verification

### Health Check

/health

Expected response:
{
  "status": "healthy"
}

---

### UI Checks

/setup  
/live  
/cheesecake/admin  

All should load correctly.

---

## ⚠️ 6. Common Issues & Fixes

### App shows "Waiting for content"

Cause:
Startup command not set

Fix:
Set startup command
Restart app

---

### Deployment success but app empty

Cause:
Wrong publish path

Fix:
Ensure publish folder is used consistently

---

### Password not working

Cause:
Using appsettings.Development.json

Fix:
Move configuration to appsettings.json

---

### Routes returning 404

Cause:
Missing route mapping

Fix:
Check Program.cs routing

---

## 🔐 7. Configuration Rules

Always use:
appsettings.json

Do NOT rely on:
appsettings.Development.json

Azure ignores it.

---

## 💰 8. Cost & Plan

Current plan:
Free F1 ($0/month)

---

### Limitations

- No custom domain
- Limited CPU usage
- Cold start delay possible

---

### Upgrade When

- App becomes slow
- Multiple matches running
- Users depend on system
- Need custom domain

---

## 🔄 9. Recovery Steps

### Restart App

Azure → App Service → Restart

---

### Force Re-deploy

git commit → git push

---

### View Logs

Azure → App Service → Log stream

---

## 🧠 10. Best Practices

- Always test /health
- Verify UI routes after deployment
- Restart app if unsure
- Keep workflow stable
- Avoid manual Azure changes

---

## ✅ 11. Current System Status

Backend running in Azure ✅  
Automatic deployment working ✅  
API endpoints active ✅  
UI routes configured ✅  
Zero-cost hosting ✅  

---

## 🚀 12. Summary

You have a complete deployment pipeline:

Code → GitHub ✅  
Build + Publish ✅  
Deploy to Azure ✅  
Startup command executes ✅  
API accessible publicly ✅  

---

Focus next on:

- Product usage
- Match testing
- User feedback
