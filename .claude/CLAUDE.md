# Local Project Instructions (not checked in)

## Deployment

The Jellyfin server runs on `minix` via a podman quadlet.

### Build & Deploy

```bash
# 1. Build
dotnet publish Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj -c Release --output /tmp/alexa-release/

# 2. Copy to remote
scp -F /dev/null /tmp/alexa-release/Jellyfin.Plugin.AlexaSkill.dll minix:/tmp/Jellyfin.Plugin.AlexaSkill.dll

# 3. Backup old DLL and deploy new one
ssh -F /dev/null minix "podman exec jellyfin cp /config/data/plugins/AlexaSkill_0.2.0.17/Jellyfin.Plugin.AlexaSkill.dll /config/data/plugins/AlexaSkill_0.2.0.17/Jellyfin.Plugin.AlexaSkill.dll.bak"
ssh -F /dev/null minix "podman cp /tmp/Jellyfin.Plugin.AlexaSkill.dll jellyfin:/config/data/plugins/AlexaSkill_0.2.0.17/Jellyfin.Plugin.AlexaSkill.dll"

# 4. Restart
ssh -F /dev/null minix "systemctl --user restart jellyfin"
```

### Key Details

- **Remote host**: `ssh minix` (SSH config handles connection)
- **Container**: `jellyfin` (linuxserver/jellyfin image, quadlet-managed)
- **Quadlet file**: `~/.config/containers/systemd/jellyfin.container` on minix
- **Plugin path in container**: `/config/data/plugins/AlexaSkill_<version>/` (check with `ssh minix "podman exec jellyfin ls /config/data/plugins/"`)
- **Config volume**: `/home/pantinor/containers_config/jellyfin/library:/config:z`
- **Service management**: `systemctl --user start|stop|restart|status jellyfin`
- **After quadlet changes**: `systemctl --user daemon-reload` first
- **Container is in pod**: `minix.pod`
