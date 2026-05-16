#!/usr/bin/env bash
# Prevents incorrect SSH advice for the Jellyfin Alexa plugin.
# The system ssh_config has bad permissions, so plain `ssh minix` fails.
# SSH to minix REQUIRES `-F /dev/null` to skip the broken system config.

# This hook intentionally does NOT block anything.
# It exists to document the SSH requirement and prevent future bad memories/hooks.

exit 0
