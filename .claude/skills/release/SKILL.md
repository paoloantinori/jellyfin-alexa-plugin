---
name: release
description: Prepare and publish a new plugin release. Analyzes changes since last tag, recommends a version bump, validates, tags, and pushes. Triggers: "release", "publish", "cut a release", "ship it", "new version", "bump version and release".
---

# Release

Prepare a versioned release by analyzing what changed, recommending the right semver bump, and publishing.

## Step 1: Analyze Changes Since Last Release

```bash
LAST_TAG=$(git describe --tags --abbrev=0)
echo "Last release: $LAST_TAG"
echo ""
echo "Changes since $LAST_TAG:"
git log $LAST_TAG..HEAD --oneline
echo ""
echo "Diff stat:"
git diff $LAST_TAG..HEAD --stat
```

Classify each commit into categories:

| Category | Examples |
|----------|----------|
| **New feature** | New intent handler, new slot type, new UI section, new API endpoint |
| **Enhancement** | Improved matching, better error messages, UI polish, APL upgrades |
| **Bug fix** | Crash fix, wrong behavior, null reference, edge case |
| **Refactor** | Code reorganization, extracted helpers, no behavior change |
| **Infra** | CI changes, validation scripts, build config |
| **Docs** | Comments, README, skill files |

## Step 2: Recommend Version

Current version format: `MAJOR.MINOR.PATCH.BUILD` (e.g. `0.3.2.0`).

Read current version:
```bash
grep '<Version>' Directory.Build.props | sed 's/.*>\(.*\)<.*/\1/'
```

### Version Rules

| Change Scope | Bump | Example |
|---|---|---|
| Bug fixes only, no new behavior | PATCH + BUILD | `0.3.2.0` → `0.3.3.0` |
| New features or enhancements (most releases) | MINOR + BUILD | `0.3.2.0` → `0.4.0.0` |
| Breaking changes to interaction models or API | MAJOR + BUILD | `0.3.2.0` → `1.0.0.0` |
| Pre-1.0 with new features | MINOR + BUILD | `0.3.2.0` → `0.4.0.0` |

**BUILD always resets to 0 on any bump.** The 4th segment exists for Jellyfin plugin catalog compatibility.

Present the recommendation to the user with rationale:
```
Current version: 0.3.2.0
Recommended:     0.4.0.0

Rationale: MINOR bump — 5 new features (carousel APL, SlotMappings refactor,
rebuild endpoint, BrowseCategory expansion, LibraryQueryType synonyms),
2 enhancements, 0 breaking changes.
```

Wait for user confirmation before proceeding.

## Step 3: Bump Version

Update version in **all three locations**:

```bash
NEW_VERSION="0.4.0.0"  # use the agreed version

# Directory.Build.props (3 fields: Version, AssemblyVersion, FileVersion)
sed -i "s/<Version>.*<\/Version>/<Version>${NEW_VERSION}<\/Version>/" Directory.Build.props
sed -i "s/<AssemblyVersion>.*<\/AssemblyVersion>/<AssemblyVersion>${NEW_VERSION}<\/AssemblyVersion>/" Directory.Build.props
sed -i "s/<FileVersion>.*<\/FileVersion>/<FileVersion>${NEW_VERSION}<\/FileVersion>/" Directory.Build.props

# build.yaml
sed -i "s/^version:.*/version: ${NEW_VERSION}/" build.yaml

# Verify
grep '<Version>' Directory.Build.props
grep '^version:' build.yaml
```

## Step 4: Validate

### 4a. Local build + validators
```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln -c Release -warnaserror
python3 scripts/validate_interaction_models.py
python3 scripts/validate_versions.py   # MUST PASS: props == build.yaml == manifest latest
```

### 4b. Run tests in a CI-matching container (REQUIRED before tagging)
A green local `dotnet test` is **not** sufficient. The dev box (Fedora: `/bin/sh`=bash, ffmpeg installed) differs from the ubuntu runner (`/bin/sh`=dash, **no** ffmpeg). Environment-dependent failures — bash-only shell syntax in spawned scripts, missing system binaries — only surface on the runner, *after* the tag is pushed. Run the suite under the runner's failure surface first:
```bash
podman run --rm -v "$PWD":/src:z -w /src \
  mcr.microsoft.com/dotnet/sdk:9.0 \
  dotnet test Jellyfin.Plugin.AlexaSkill.Tests -c Release
```
(`docker` works the same if that's what you have; `:z` handles SELinux labels on Fedora.) The Debian image gives `/bin/sh`=dash and ships no ffmpeg — matching the ubuntu runner's failure surface (not identical to `ubuntu-latest`, but far closer than Fedora). All tests must pass here before tagging. (Origin: 0.7.0.0 release — 2382 passed on Fedora, 4 failed in CI. See `feedback_ci_env_tests` memory.) If podman/docker is unavailable, at minimum `dash -n` any shell scripts the tests generate.

## Step 5: Deploy and Rebuild Models

Run the `deploy` skill to push the new DLL. The version change triggers automatic model deployment on startup via `SkillStartup`, but also run the `rebuild-models` skill to push immediately without waiting for the next restart.

## Step 6: Commit, Tag, Push

Before committing, append a **placeholder** `manifest.json` entry for the new version (`checksum: "placeholder"`, changelog from `build.yaml`) so `validate_versions.py` passes — release CI's `add_release_to_manifest.py` replaces it with the real MD5 computed from the built zip. Without the placeholder, release CI fails at the validate-versions step.

```bash
# Stage ONLY the release files — NEVER `git add -A` (lots of untracked noise in this tree)
git add Directory.Build.props build.yaml manifest.json
git commit -m "Release v${NEW_VERSION}: <one-line summary>"
git tag "${NEW_VERSION}"
git push origin main --tags
```

**Tag must match the version exactly** (no `v` prefix — the version string `0.4.0.0` is the tag).

**If the release run fails** (e.g. tests): the release was never published, so move the unreleased tag to the fix commit and force-push rather than bumping the version for a test-only fix:
```bash
git tag -f "${NEW_VERSION}" <fix-commit> && git push -f origin "${NEW_VERSION}"
```

## Step 7: Monitor CI + Verify (MANDATORY — recurring failure points)

```bash
gh run watch <run-id> --exit-status     # wait for green
```

Then verify **all three**:
- **GitHub release** exists with the zip: `gh release view ${NEW_VERSION}`
- **Manifest checksum was replaced** — must be a real 32-char MD5, NOT `"placeholder"`: `grep placeholder manifest.json` → empty. Pull the bot's commit first: `git pull --ff-only origin main`.
- **Set curated release notes** — `generate_release_notes` yields near-empty notes for this direct-to-main repo (no PRs to summarize). Write a Features/Fixes body from `build.yaml`'s changelog and set it:
  ```bash
  gh release edit ${NEW_VERSION} --notes-file <curated-notes.md>
  ```

Jellyfin's plugin catalog auto-detects the new version from `manifest.json`.

## Files That Change

| File | What to update |
|------|---------------|
| `Directory.Build.props` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `build.yaml` | `version` field |

`manifest.json`: append a `placeholder` entry before tagging (Step 6) — release CI overwrites it with the real checksum. Never hand-write the checksum; CI computes it from the zip.
