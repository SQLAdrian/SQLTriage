# CI/CD Quick Start Guide

## Yes, It's Automatic! 🚀

Once you push to GitHub, the workflows run automatically.

## Setup (One-Time)

### 1. Push to GitHub
```bash
cd c:\GitHub\LiveMonitor
git init
git add .
git commit -m "Initial commit"
git remote add origin https://github.com/YOUR_USERNAME/SqlHealthAssessment.git
git push -u origin main
```

### 2. That's It!
GitHub Actions will automatically:
- ✅ Build on every push to `main` or `develop`
- ✅ Build on every pull request
- ✅ Create releases when you push version tags

---

## How It Works

### Automatic Builds (Every Push)
**Trigger:** Push to `main` or `develop` branch

**What happens:**
1. GitHub Actions spins up Windows VM
2. Installs .NET 8
3. Runs `dotnet restore`
4. Runs `dotnet build --configuration Release`
5. Uploads build artifacts (kept for 7 days)

**View:** Go to your repo → Actions tab → See build status

### Automatic Releases (Version Tags)
**Trigger:** Push a version tag

```bash
# Create and push a release
git tag v1.0.0
git push origin v1.0.0
```

**What happens:**
1. GitHub Actions builds release version
2. Creates ZIP file: `SqlHealthAssessment-1.0.0.zip`
3. Creates GitHub Release with auto-generated notes
4. Attaches ZIP to release
5. Users can download from Releases page

---

## Daily Workflow

### Regular Development
```bash
# Make changes
git add .
git commit -m "Add new feature"
git push

# CI automatically builds and tests
# Check Actions tab for status
```

### Creating a Release
```bash
# Update version in Config/version.json
# Commit changes
git add .
git commit -m "Release v1.0.1"
git push

# Create and push tag
git tag v1.0.1
git push origin v1.0.1

# Wait 5-10 minutes
# Release appears at: github.com/YOUR_USERNAME/SqlHealthAssessment/releases
```

---

## Monitoring CI/CD

### Check Build Status
1. Go to: `https://github.com/YOUR_USERNAME/SqlHealthAssessment/actions`
2. See all workflow runs
3. Click any run to see logs
4. Green ✅ = success, Red ❌ = failed

### Check Releases
1. Go to: `https://github.com/YOUR_USERNAME/SqlHealthAssessment/releases`
2. See all published releases
3. Download ZIP files
4. View release notes

---

## Troubleshooting

### Build Fails
**Check:** Actions tab → Click failed run → View logs

**Common issues:**
- Missing NuGet packages → Fixed by `dotnet restore`
- Compilation errors → Fix in code, push again
- Timeout → Increase timeout in workflow file

### Release Not Created
**Check:**
- Tag format must be `v*` (e.g., `v1.0.0`, not `1.0.0`)
- Tag must be pushed: `git push origin v1.0.0`
- Check Actions tab for errors

### No Workflows Running
**Check:**
- `.github/workflows/` folder exists
- Workflow files are `.yml` not `.yaml`
- You pushed to `main` or `develop` branch
- GitHub Actions enabled in repo settings

---

## Advanced: Customizing Workflows

### Change Build Trigger
Edit `.github/workflows/build.yml`:
```yaml
on:
  push:
    branches: [ main, develop, feature/* ]  # Add more branches
```

### Add Tests
Edit `.github/workflows/build.yml`:
```yaml
- name: Test
  run: dotnet test --no-build --verbosity normal
```

### Change Release Format
Edit `.github/workflows/release.yml`:
```yaml
- name: Create ZIP
  run: |
    cd publish
    7z a ../SqlHealthAssessment-${{ steps.version.outputs.VERSION }}.zip *
```

---

## Status Badges (Optional)

Add to README.md:
```markdown
[![Build](https://github.com/YOUR_USERNAME/SqlHealthAssessment/actions/workflows/build.yml/badge.svg)](https://github.com/YOUR_USERNAME/SqlHealthAssessment/actions/workflows/build.yml)
```

Shows build status: ![Build](https://img.shields.io/badge/build-passing-brightgreen)

---

## Summary

**Automatic:**
- ✅ Builds on every push
- ✅ Releases on version tags
- ✅ No manual steps needed

**Manual:**
- Create version tags: `git tag v1.0.0 && git push origin v1.0.0`
- Monitor in Actions tab
- Download from Releases page

**That's it!** Push code → CI/CD handles the rest.
