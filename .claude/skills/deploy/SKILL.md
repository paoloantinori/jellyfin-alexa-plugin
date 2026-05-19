---
name: deploy
description: Build, deploy, and verify the plugin DLL on the minix Jellyfin container with config backup/restore and optional model rebuild. Triggers: "deploy to minix", "push to jellyfin", "hot swap", "deploy the plugin", "deploy without release".
---

# Deploy to Minix

Build the release DLL, hot-swap it into the running Jellyfin container, verify config survived, and optionally rebuild interaction models.

## Prerequisites

- Uncommitted changes are built and tested (`dotnet test` passes)
- You are on the correct branch

## Steps

### 1. Backup Plugin Config

**NEVER skip this.** Jellyfin resets plugin config when the DLL changes.

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
ssh $SSH_OPTS pantinor@minix "curl -sf 'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3'" > /tmp/alexa_plugin_config_backup.json
python3 -c "import json; cfg=json.load(open('/tmp/alexa_plugin_config_backup.json')); print(f'Users: {len(cfg.get(\"Users\",[]))}, Simulator: {cfg.get(\"SimulatorEnabled\")}')"
```

**If 0 users**: STOP. Config is already lost. Ask user before proceeding.

### 2. Build Release

```bash
dotnet build Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj -c Release
```

Must show 0 errors. Warnings are advisory.

### 3. Deploy DLL via SCP + Podman

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
PLUGIN_DIR=$(ssh $SSH_OPTS pantinor@minix "podman exec jellyfin ls /config/data/plugins/" | grep AlexaSkill | tr -d ' ')
scp $SSH_OPTS Jellyfin.Plugin.AlexaSkill/bin/Release/net9.0/Jellyfin.Plugin.AlexaSkill.dll pantinor@minix:/tmp/
ssh $SSH_OPTS pantinor@minix "podman exec jellyfin cp /config/data/plugins/${PLUGIN_DIR}/Jellyfin.Plugin.AlexaSkill.dll /config/data/plugins/${PLUGIN_DIR}/Jellyfin.Plugin.AlexaSkill.dll.bak && \
  podman cp /tmp/Jellyfin.Plugin.AlexaSkill.dll jellyfin:/config/data/plugins/${PLUGIN_DIR}/Jellyfin.Plugin.AlexaSkill.dll && \
  systemctl --user restart jellyfin"
```

### 4. Wait for Server Boot

Poll the config endpoint until Jellyfin is responsive (up to ~60 seconds):

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
for i in $(seq 1 20); do
  if ssh $SSH_OPTS pantinor@minix "curl -sf 'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3'" > /dev/null 2>&1; then
    echo "Jellyfin is up (attempt $i)"
    break
  fi
  echo "Waiting... (attempt $i/20)"
  sleep 3
done
```

Must print `Jellyfin is up`. If it reaches attempt 20 without success, check logs:
```bash
ssh $SSH_OPTS pantinor@minix "podman logs jellyfin --tail 30"
```

### 5. Verify Config Survived

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
ssh $SSH_OPTS pantinor@minix "curl -sf 'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3'" | python3 -c "import json, sys; cfg=json.load(sys.stdin); print(f'Users after deploy: {len(cfg.get(\"Users\",[]))}')"
```

**If 0 users** — restore from backup:
```bash
cat /tmp/alexa_plugin_config_backup.json | ssh $SSH_OPTS pantinor@minix "curl -sf -X POST \
  'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3' -H 'Content-Type: application/json' -d @-"
```

### 6. Rebuild Interaction Models (if models changed)

If the deploy includes interaction model changes (new slot values, new utterances, new intents),
rebuild all models so Alexa's NLU picks up the changes without requiring a version bump:

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
USER_ID=$(ssh $SSH_OPTS pantinor@minix "curl -sf 'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3'" | python3 -c "import json,sys; print(json.load(sys.stdin)['Users'][0]['Id'])")
ssh $SSH_OPTS pantinor@minix "curl -s -X POST 'http://localhost:8096/alexaskill/api/custom-model/rebuild' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3' -H 'Content-Type: application/json' \
  -d '{\"userId\":\"$USER_ID\"}'"
```

The endpoint now polls SMAPI and waits for all locale model builds to complete.
Must return `{"success":true,"message":"Rebuilt 17 models — 17 succeeded, 0 failed",...}`.
If any locale failed, the response includes per-locale error details.

## Key Facts

- **Container name**: `jellyfin`
- **Plugin path**: discovered dynamically via `podman exec jellyfin ls /config/data/plugins/`
- **SSH key**: `~/.ssh/id_rsa` with `-F /dev/null` (bypasses broken system ssh_config)
- **Only the DLL needs copying** — dependencies don't change between builds
- **Plugin ID**: `c5df7de087774b3ca70d5c3dae359c9e`
- **Boot verification**: polling loop checks API responsiveness (step 4), no fixed sleep
- **Model build verification**: rebuild endpoint polls SMAPI until all locales report SUCCEEDED/FAILED

## After Deploy

Run the `verify` skill to systematically test the changes on the live instance.
