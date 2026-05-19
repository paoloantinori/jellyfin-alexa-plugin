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

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln -c Release -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests -c Release
python3 scripts/validate_interaction_models.py
python3 scripts/validate_versions.py
```

All must pass with 0 errors.

## Step 5: Deploy and Rebuild Models

Run the `deploy` skill to push the new DLL. The version change triggers automatic model deployment on startup via `SkillStartup`, but also run the `rebuild-models` skill to push immediately without waiting for the next restart.

## Step 6: Commit, Tag, Push

```bash
git add Directory.Build.props build.yaml
git commit -m "Release v${NEW_VERSION}: <one-line summary>"
git tag "${NEW_VERSION}"
git push origin main --tags
```

**Tag must match the version exactly** (no `v` prefix — the version string `0.4.0.0` is the tag).

## Step 7: Monitor CI

```bash
gh run list --workflow=release-build.yml --limit 3
```

Wait for the release build to complete. Check:
- GitHub Releases page has the new version with zip artifact
- `manifest.json` was auto-updated with new version + checksum
- Jellyfin plugin catalog will detect the new version automatically

## Files That Change

| File | What to update |
|------|---------------|
| `Directory.Build.props` | `<Version>`, `<AssemblyVersion>`, `<FileVersion>` |
| `build.yaml` | `version` field |

`manifest.json` is updated automatically by the release CI — do NOT edit it manually.
