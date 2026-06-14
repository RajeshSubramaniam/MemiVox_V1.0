# MemiVox — Match Day Runbook (V1)

This document is a **practical checklist for running MemiVox during a live match**.

Use this before, during, and after every match.

---

# 🧭 1. Pre-Match Setup (15–20 mins before start)

## ✅ Backend Health

- [ ] Open `/health` endpoint
- [ ] Status = `healthy`
- [ ] Response time < 2 seconds

Example:
https://memivox-live-*.azurewebsites.net/health

---

## ✅ App Access

- [ ] `/setup` loads correctly
- [ ] Password works
- [ ] No console errors in browser

---

## ✅ Match Setup

- [ ] Create new match
- [ ] Enter team names
- [ ] Verify match created successfully

---

## ✅ Overlay Page

- [ ] Open `/live`
- [ ] Overlay renders correctly
- [ ] Field elements visible

---

## ✅ OBS / ATEM Integration

- [ ] Overlay URL added to OBS browser source
- [ ] Resolution correct (e.g. 1080p)
- [ ] Transparency working (if implemented)

---

## ✅ Internet Check

- [ ] Stable connection
- [ ] Backup hotspot available

---

# 🎥 2. During Match

---

## ✅ Live Update Check

- [ ] Scores update correctly
- [ ] Overlay refresh works (no freezing)
- [ ] No lag > 2–3 seconds

---

## ✅ Backend Monitoring

- [ ] `/health` still accessible
- [ ] No errors in UI

---

## ✅ Observations to capture

- Any lag?
- Any refresh issues?
- Any display problems?

👉 Capture feedback for improvement

---

# ⚠️ 3. Emergency Actions

---

## ❌ Overlay not updating

👉 Do:

- Refresh overlay page
- Refresh `/live` in browser
- Restart OBS browser source

---

## ❌ Backend not responding

👉 Do:

1. Check `/health`
2. Restart Azure App Service
3. Reload `/setup` + `/live`

---

## ❌ Password/login not working

- Check correct environment (Azure)
- Retry using known password
- Restart app if needed

---

# ✅ 4. Post-Match

---

## ✅ Closeout

- [ ] Stop OBS streaming
- [ ] Save match data if needed

---

## ✅ Quick review

- [ ] Did overlay perform smoothly?
- [ ] Any delays?
- [ ] Any crashes?

---

## ✅ Notes

Write down:

- Issues experienced
- Improvements required

---

# ✅ 5. Golden Rule

👉 If something breaks:

Always check /health first ✅

---

# ✅ Summary

This runbook ensures:

- Stable match experience
- Quick recovery from issues
- Consistent testing process
