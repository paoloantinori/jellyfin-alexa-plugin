#!/bin/bash
export PLAYWRIGHT_BROWSERS_PATH="/home/pantinor/data/repo/personal/jellyfin-alexa-plugin/.claude/pw-cache"
exec npx @playwright/mcp@latest \
  --user-data-dir "/home/pantinor/data/repo/personal/jellyfin-alexa-plugin/.claude/playwright-data" \
  "$@"
