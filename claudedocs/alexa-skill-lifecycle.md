# Alexa Skill Update Lifecycle

How the plugin keeps each user's Alexa skill in sync with the installed plugin version.

## Version Detection

- Plugin version is defined in `Directory.Build.props` (`<Version>` element)
- `Util.GetVersion()` reads it at runtime from the assembly's `AssemblyInformationalVersionAttribute`
- `ManifestSkill.AddVersionTag()` appends the version to the skill's display name (e.g. "Jellyfin Player v0.2.0.2")
- `ManifestSkill.GetVersionTag()` extracts the version back out of the display name

## Auto-Update on Startup

`SkillStartup` (`EntryPoints/SkillStartup.cs`) is a hosted service that runs on plugin startup:

1. Iterates all configured users in `PluginConfiguration.Users`
2. For each user with a ready skill (`SkillId` != null, `SmapiManagement` != null):
   - Fetches the cloud skill manifest via SMAPI
   - Compares `cloudManifestSkill.GetVersionTag()` with `Util.GetVersion()`
   - If they differ: calls `SmapiManagement.UpdateSkillAsync(skillId, manifestSkill, interactionModels)`
   - Also checks account linking data (endpoint URL, client ID) and updates if stale

`UpdateSkillAsync()` (`Alexa/SmapiManagement.cs`):
1. Uploads the updated manifest (skill name, endpoints, etc.)
2. Waits for SMAPI to process the manifest update
3. Iterates all locales and uploads each interaction model via `InteractionModel.Update()`
4. Logs success/failure per locale (failures don't block other locales)

## Interaction Model Loading

- Models are embedded resources: `Alexa/InteractionModel/model_*.json`
- `Util.GetLocalInteractionModels()` scans assembly resources for matching files
- `Plugin.BuildSkillInteractionModels(invocationName)` creates `SkillInteractionModel` objects
- Each model is deserialized and assigned the user's invocation name

## What Triggers a Skill Update

Any change to `Directory.Build.props` version triggers an update on next Jellyfin restart:

- New/modified interaction model utterances (any locale)
- Manifest changes (skill metadata, endpoints)
- Account linking configuration changes

## Release Workflow

1. Bump version in `Directory.Build.props`
2. Update `build.yaml` changelog
3. Commit and push to `main`
4. Create and push git tag matching the version (e.g. `git tag 0.2.0.2 && git push origin 0.2.0.2`)
5. GitHub Actions release workflow:
   - Verifies tag matches `Directory.Build.props` version
   - Builds Release configuration, runs all tests
   - Creates ZIP with required DLLs
   - Computes MD5 checksum
   - Creates GitHub release with the ZIP artifact
   - Appends new version entry to `manifest.json` (checksum, sourceUrl, changelog, targetAbi)
   - Commits updated `manifest.json` back to `main`

## User Update Flow

1. Jellyfin polls the plugin repository manifest periodically
2. Finds new version entry in `manifest.json`
3. Downloads ZIP from GitHub release URL
4. Verifies MD5 checksum
5. Installs updated plugin files
6. Restarts Jellyfin
7. `SkillStartup` detects version mismatch → pushes updated interaction models to Amazon
8. Alexa picks up new models within minutes

## Key Files

| File | Purpose |
|------|---------|
| `Directory.Build.props` | Plugin version (single source of truth) |
| `build.yaml` | Release metadata (owner, changelog, targetAbi, artifacts) |
| `manifest.json` | Plugin catalog entries (one per release, with checksums) |
| `Alexa/InteractionModel/model_*.json` | Interaction models per locale (12 locales) |
| `Alexa/Manifest/ManifestSkill.cs` | Skill manifest construction, version tagging |
| `Alexa/SmapiManagement.cs` | SMAPI calls: create, update, get skill |
| `EntryPoints/SkillStartup.cs` | Startup service: version check, auto-update |
| `Configuration/PluginConfiguration.cs` | User/skill storage, persisted as XML |
| `.github/workflows/release-build.yml` | CI release pipeline |
| `.github/workflows/add_release_to_manifest.py` | Post-release manifest update script |
