# Playwright MCP Server in Claude Code Sandbox

## The Problem

Claude Code's sandbox mounts `~/.cache/ms-playwright/` as **read-only**. The Playwright MCP server fails at startup with:

```
Error: async initializeServer: ENOENT: no such file or directory, mkdir '/home/user/.cache/ms-playwright/b'
```

This happens because Playwright has **two separate cache mechanisms** that both write to `~/.cache/ms-playwright/`:

| Env Var | What It Controls | Default Path |
|---|---|---|
| `PLAYWRIGHT_BROWSERS_PATH` | Where browser binaries are found/installed | `~/.cache/ms-playwright/` |
| `PLAYWRIGHT_SERVER_REGISTRY` | MCP server runtime registry (browser watcher) | `~/.cache/ms-playwright/b` |

Both must be redirected to writable directories.

## The Solution

### Step 1 — Create writable directories in your project

```bash
mkdir -p .claude/pw-cache
mkdir -p .claude/pw-server-registry
```

### Step 2 — Copy the browser cache from the read-only location

```bash
cp -r ~/.cache/ms-playwright/* .claude/pw-cache/
```

This copies Chromium, Firefox, WebKit, and all supporting files (~500MB).

### Step 3 — Create `.mcp.json` in your project root with all three env vars

```json
{
  "mcpServers": {
    "playwright": {
      "command": "npx",
      "args": ["@playwright/mcp@latest"],
      "env": {
        "PLAYWRIGHT_BROWSERS_PATH": "/absolute/path/to/your/project/.claude/pw-cache",
        "PLAYWRIGHT_SERVER_REGISTRY": "/absolute/path/to/your/project/.claude/pw-server-registry",
        "XDG_CACHE_HOME": "/absolute/path/to/your/project/.claude/pw-cache-xdg"
      }
    }
  }
}
```

Replace `/absolute/path/to/your/project` with your actual project path.

### Step 4 — Add the writable dirs to `.git/info/exclude` or `.gitignore`

```
.claude/pw-cache/
.claude/pw-server-registry/
.claude/pw-cache-xdg/
```

### Step 5 — Reconnect MCP

In Claude Code, run:

```
/mcp
```

The Playwright MCP server should now start successfully.

## Verifying It Works

Navigate to any URL. If you see page content instead of `ENOENT` errors, it is working.

## Troubleshooting

| Error | Cause | Fix |
|---|---|---|
| `ENOENT: mkdir '.../ms-playwright/b'` | `PLAYWRIGHT_SERVER_REGISTRY` not set | Add it to `.mcp.json` `env` |
| `ENOENT: mkdir '.../ms-playwright/mcp-chrome-...'` | Stale browser profile lock file | `rm -rf .claude/pw-cache/mcp-chrome-*` |
| `Browser is already in use for ...` | Previous browser session didn't close cleanly | Remove stale lock files or restart MCP |
| Env vars not taking effect | `.mcp.json` not reloaded after edit | Run `/mcp` or restart Claude Code |
| 404 on navigation | Target server not reachable | Verify URL and network connectivity |

## Why Three Env Vars

- **`PLAYWRIGHT_BROWSERS_PATH`** — Tells Playwright where to find browser binaries. Without this, it tries to install them to the read-only cache.
- **`PLAYWRIGHT_SERVER_REGISTRY`** — The MCP server's `_browsersDir()` method uses this as its runtime registry. Without it, it tries to create `~/.cache/ms-playwright/b` which is read-only. This is the env var that causes the `ENOENT: mkdir` error.
- **`XDG_CACHE_HOME`** — Fallback cache directory on Linux. Set as defense-in-depth in case Playwright falls back to computing the default path from `os.homedir() + .cache`.

## Alternative: Wrapper Script Approach

If the inline `env` approach doesn't work, use a wrapper script.

Create `.claude/pw-mcp.sh`:

```bash
#!/bin/bash
export PLAYWRIGHT_BROWSERS_PATH="/absolute/path/to/your/project/.claude/pw-cache"
export PLAYWRIGHT_SERVER_REGISTRY="/absolute/path/to/your/project/.claude/pw-server-registry"
exec npx @playwright/mcp@latest "$@"
```

```bash
chmod +x .claude/pw-mcp.sh
```

Then in `.mcp.json`:

```json
{
  "mcpServers": {
    "playwright": {
      "command": "/absolute/path/to/your/project/.claude/pw-mcp.sh"
    }
  }
}
```

The inline `env` approach is simpler and avoids maintaining a separate shell script. Use the wrapper script only if `env` doesn't work.
