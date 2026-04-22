# Publishing Guide

Step-by-step walkthrough to push this project to https://github.com/nicoliu0212/Ekahau-Revit-Transfer and ship the v2.4.0 release.

---

## What's already prepared

```
✓ .gitignore                              ignores bin/obj/.wix/Output/.user/...
✓ LICENSE                                 MIT (Copyright 2026 Nico Liu)
✓ README.md                               main page — badges, ToC, install steps, architecture
✓ USER_GUIDE.md                           comprehensive English user manual
✓ CHANGELOG.md                            full version history v1.0 → v2.4.0
✓ RELEASE_NOTES_v2.4.0.md                 first release notes (paste into GitHub Release page)
✓ PUBLISH.md                              this file
✓ .github/workflows/release.yml           CI: push v* tag → auto-build + create release + upload MSI
✓ .github/ISSUE_TEMPLATE/bug_report.md    bug template
✓ .github/ISSUE_TEMPLATE/feature_request.md
✓ .github/ISSUE_TEMPLATE/config.yml       disables blank issues + links to USER_GUIDE
✓ .github/PULL_REQUEST_TEMPLATE.md        PR checklist
✓ Installer/EkahauWiFiTools-v2.4.0.msi    latest build (per-user, AppData scope)
```

Git is already initialised with the initial commit.

---

## Step 1 — Push to GitHub

### Prepare the GitHub repo

1. Open https://github.com/nicoliu0212/Ekahau-Revit-Transfer
2. **Settings → Default branch** confirm `main`
3. **Settings → Danger Zone → Change visibility → Make public** (do this whenever you're ready — must be public before others can see the release)

> If the repo doesn't exist yet:
> - Create an **empty** repo on GitHub (don't tick README / .gitignore / license — we already have them)
> - Name = `Ekahau-Revit-Transfer`
> - Private is fine, you can flip to Public later

### Push the code

From `D:\Claude\EkahauRevitPlugin\`:

```powershell
git remote add origin https://github.com/nicoliu0212/Ekahau-Revit-Transfer.git
git push -u origin main
```

First push will ask you to authenticate (recommend [GitHub CLI](https://cli.github.com/) or a Personal Access Token).

---

## Step 2 — First Release (v2.4.0)

Two ways. Pick one.

### Option A — git tag triggers automatic release (recommended)

The `release.yml` workflow watches for `v*` tags. On push, CI builds all 3 runtime targets, packages the MSI, creates the GitHub Release, and uploads the MSI as an asset.

```powershell
git tag -a v2.4.0 -m "Release v2.4.0 — first public release"
git push origin v2.4.0
```

Watch progress at https://github.com/nicoliu0212/Ekahau-Revit-Transfer/actions
After ~3-5 minutes, https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases will show v2.4.0 with `EkahauWiFiTools-v2.4.0.msi` attached.

**.NET 10 SDK is still in RC** — the first CI build may fail to find it. If so:
- Edit `.github/workflows/release.yml`
- Change `dotnet-quality: preview` to `dotnet-quality: rc`, OR
- Temporarily drop the net10 target (edit `.csproj`, change `TargetFrameworks` to `net48;net8.0-windows` only)

### Option B — manual upload

```
1. On GitHub: Releases → "Draft a new release"
2. Tag: v2.4.0   (Create new tag on publish)
3. Title: v2.4.0
4. Description: paste contents of RELEASE_NOTES_v2.4.0.md
5. Drag-and-drop:  D:\Claude\EkahauRevitPlugin\Installer\EkahauWiFiTools-v2.4.0.msi
6. Publish release
```

---

## Step 3 — Subsequent releases

For each new version:

```powershell
# 1. Edit code + add a new section at the top of CHANGELOG.md
# 2. Bump Version= in Installer/Package.wxs
# 3. Commit
git add -A
git commit -m "feat: <description>"
git push

# 4. Tag — CI auto-builds + releases + uploads MSI
git tag -a v2.4.1 -m "Release v2.4.1"
git push origin v2.4.1
```

---

## Step 4 — Polish (optional)

### Topics
On the repo home page, click the gear icon next to "About" and add topics:
```
revit, ekahau, wifi, wifi-planning, autodesk, revit-addin, dotnet, csharp, wpf, wifi-survey
```

### About description
Same gear → Description:
> Bi-directional bridge between Autodesk Revit and Ekahau AI Pro for WiFi planning. Supports Revit 2023-2027.

### Screenshots
The README has no screenshots yet. High-quality projects all have them. Suggested shots:
1. **Ribbon screenshot** — showing the WiFi Tools tab + 5 buttons
2. **Param Config mapping dialog**
3. **ESX Export view selection + mapping check**
4. **ESX Read view with placed AP markers**
5. **AP Place output — WiFi Plan view + AP Schedule**

Save them under `docs/images/` and reference at the top of README:
```markdown
![Ribbon](docs/images/ribbon.png)
```

A short GIF (use [ScreenToGif](https://www.screentogif.com/)) is even more effective.

### Pin to your profile
GitHub profile → Pinned repositories → add this repo.

---

## Step 5 — Ongoing maintenance

| Trigger | Action |
|---------|--------|
| Issue received | Reply using the template, label as bug / enhancement / question |
| PR received | Walk through the PR template checklist |
| New release | Update CHANGELOG.md, bump `Installer/Package.wxs` Version, push tag |
| New Revit version | Add a new target framework + SDK reference, update install.ps1 / WiX |

---

## Repo state

```
D:\Claude\EkahauRevitPlugin\
├── .git/                                 ← initialised
├── .github/                              ← CI + templates
├── .gitignore
├── EkahauRevitPlugin/                    ← source code
├── Installer/                            ← WiX MSI definition
├── USER_GUIDE.md
├── CHANGELOG.md
├── LICENSE
├── README.md
├── RELEASE_NOTES_v2.4.0.md
├── PUBLISH.md                            ← this file
├── fix-revit-2027.bat
└── install.ps1

Branch: main
```

---

## Pre-push checklist

- [ ] `LICENSE` says `Copyright (c) 2026 Nico Liu` — name correct?
- [ ] All `nicoliu0212/Ekahau-Revit-Transfer` URLs in README, CHANGELOG, ISSUE_TEMPLATE/config.yml correct?
- [ ] `.github/workflows/release.yml` uses `${{ github.repository }}` which GitHub auto-fills — no manual change needed
- [ ] Installer is per-user (no UAC) — confirmed in `Installer/Package.wxs` line `Scope="perUser"`

If you want to change the username / repo path, global search-and-replace `nicoliu0212/Ekahau-Revit-Transfer`.

---

Done. After publishing successfully, the download link appears at:
https://github.com/nicoliu0212/Ekahau-Revit-Transfer/releases/tag/v2.4.0
