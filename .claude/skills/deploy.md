---
name: deploy
description: Build, deploy, and hot-swap the plugin DLL on the minix Jellyfin container. Use after implementation is complete and tests pass, before verification. Triggers: "deploy to minix", "push to jellyfin", "hot swap", "deploy the plugin", or when verification is needed.
---

# Deploy to Minix

Build the release DLL, copy it into the running Jellyfin container, and restart.

## Prerequisites

- Uncommitted changes are built and tested (`dotnet test` passes)
- You are on the correct branch

## Steps

### 1. Build Release

```bash
dotnet build Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj -c Release
```

Must show 0 errors. Warnings are advisory.

### 2. Deploy via SCP + Podman

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
DLL=Jellyfin.Plugin.AlexaSkill/bin/Release/net9.0/Jellyfin.Plugin.AlexaSkill.dll
CONTAINER_PATH=/config/data/plugins/AlexaSkill_0.2.0.18/Jellyfin.Plugin.AlexaSkill.dll
scp $SSH_OPTS "$DLL" pantinor@minix:/tmp/Jellyfin.Plugin.AlexaSkill.dll && \
ssh $SSH_OPTS pantinor@minix "podman cp /tmp/Jellyfin.Plugin.AlexaSkill.dll jellyfin:$CONTAINER_PATH && systemctl --user restart jellyfin"
```

### 3. Wait and Verify Running

```bash
sleep 8 && SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa" && \
ssh $SSH_OPTS pantinor@minix 'systemctl --user is-active jellyfin'
```

Must return `active`. If not, check `podman logs --tail 20 jellyfin`.

## Key Facts

- **Container name**: `jellyfin`
- **Plugin path**: `/config/data/plugins/AlexaSkill_0.2.0.18/`
- **SSH key**: `~/.ssh/id_rsa` (NOT `id_ed25519`)
- **SSH config bug**: Always use `-F /dev/null` to bypass broken system ssh_config
- **Only the DLL needs copying** — dependencies don't change between builds
- **API key**: `REDACTED`
- **Wait time**: ~8 seconds after restart

## After Deploy

Run the `verify` skill to systematically test the changes on the live instance.
