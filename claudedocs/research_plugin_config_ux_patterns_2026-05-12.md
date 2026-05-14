# Research: Jellyfin Plugin Configuration UX Patterns

**Date**: 2026-05-12
**Confidence**: High (direct source code analysis of 7 plugins + framework analysis)
**Purpose**: Inform JF-134 (feature flags/preferences) and JF-135 (content sharing permissions) design decisions

---

## Executive Summary

Jellyfin plugin configuration pages span a wide complexity spectrum — from single-field forms (Fanart) to full single-page applications with reactive state management (Intro Skipper). The Alexa Skill plugin currently sits at medium complexity (~487 lines, inline script, flat sections). For the upcoming JF-134 and JF-135 features, the **LDAP Auth** and **Webhook** plugins provide the most relevant patterns: accordion sections for feature grouping, inline test buttons, conditional section visibility, and dynamic list management.

---

## 1. Framework: What Jellyfin Provides

### Registration
Plugins expose config pages via `GetPages()` returning `PluginPageInfo[]`. Key fields:
- `Name` / `DisplayName` — internal ID and dashboard label
- `EmbeddedResourcePath` — path to embedded HTML resource
- `EnableInMainMenu` — appear in main nav
- `MenuSection` / `MenuIcon` — optional menu customization

### Available Global Objects
| Object | Key Methods |
|--------|-------------|
| `ApiClient` | `getPluginConfiguration(id)`, `updatePluginConfiguration(id, config)`, `getUsers()`, `getVirtualFolders()`, `ajax(opts)` |
| `Dashboard` | `showLoadingMsg()`, `hideLoadingMsg()`, `alert(msg)`, `processPluginConfigurationUpdateResult(result)` |
| `LibraryMenu` | `setTabs(name, index, getTabsFn)` |

### Emby Web Components (custom elements)
| Component | Usage |
|-----------|-------|
| `emby-input` | Text, password, number inputs with `label` attribute |
| `emby-checkbox` | Styled checkboxes |
| `emby-select` | Styled dropdowns |
| `emby-button` | Styled buttons (`raised`, `button-submit`, `button-alt`, `block`) |
| `emby-collapse` | Collapsible sections with `title` attribute |
| `emby-linkbutton` | Styled link buttons |

### CSS Classes
`page type-interior pluginConfigurationPage`, `content-primary`, `inputContainer`, `selectContainer`, `checkboxContainer`, `checkboxContainer-withDescription`, `fieldDescription`, `verticalSection`, `sectionTitle`, `paperList`, `checkboxList`, `detailTable`.

### Load/Save Pattern (universal across all plugins)
```
viewshow → Dashboard.showLoadingMsg()
        → ApiClient.getPluginConfiguration(pluginId)
        → populate form fields
        → Dashboard.hideLoadingMsg()

save    → ApiClient.getPluginConfiguration(pluginId)  // re-fetch to avoid races
        → read form fields into config object
        → ApiClient.updatePluginConfiguration(pluginId, config)
        → Dashboard.processPluginConfigurationUpdateResult(result)
```

### Two JS Integration Models
1. **`data-controller="__plugin/xxx"`** — ES module loaded by Jellyfin's module system. `export default function(view, params)` receives the page element. Uses `viewshow`/`viewhide` lifecycle.
2. **Inline `<script>`** — Simpler. Binds to `pageshow` on the page div. Used by LDAP Auth and our Alexa Skill plugin.

---

## 2. Plugin-by-Plugin Analysis

### 2.1 Open Subtitles — Simple Form (LOW complexity)
**Pattern**: Flat form, no sections.

- Controls: 2 text inputs (username/password)
- Validation: On-save via custom API endpoint (`ValidateLoginInfo`)
- Dynamic: Conditional warning banner when credentials are invalid
- Lines: ~50 HTML, ~70 JS

**Relevant to us**: The conditional warning banner pattern (show/hide based on config state).

### 2.2 Trakt — Per-User Config + Dynamic Lists (MEDIUM-HIGH)
**Pattern**: Sectioned form with per-user configuration and OAuth flow.

- Controls: User dropdown, checkboxes with descriptions, authorize buttons, dynamic folder exclusion checklist
- Validation: None beyond standard form submission
- Dynamic:
  - User list populated via `ApiClient.getUsers()`
  - Library folders loaded via `ApiClient.getVirtualFolders(userId)`
  - Conditional visibility based on authorization status
- Advanced UX:
  - **OAuth device flow**: Click "Authorize Device" → shows user code → polls for authorization
  - **Per-user configuration**: Entire form loads/saves per-user settings within `config.TraktUsers[]`
  - **Dynamic folder checklist**: Generated from server library data

**Relevant to us**: The per-user config pattern maps directly to our JF-135.3 (per-user content access). The dynamic folder list is how we'd load Jellyfin collections for JF-135.2.

### 2.3 LDAP Auth — Accordion Sections + Inline Testing (HIGH complexity)
**Pattern**: Accordion-based sections with inline test actions. ~900 lines inline.

- Controls: Text, number, password, checkboxes, dropdowns, test buttons
- Validation: HTML5 `required` + `form.reportValidity()` + server-side test endpoints
- Dynamic:
  - Media folder list from `ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'))`
  - Conditional section visibility: folder access only shown when "Enable User Creation" checked
- Advanced UX:
  - **Three inline test buttons**: "Save and Test LDAP Server Settings", "Save and Test Filter Settings", "Save and Query User" — each does partial save + test
  - **Inline test results**: Dedicated result divs
  - **Conditional sections**: Show/hide based on checkbox state
  - **Select all/none**: Bulk toggle for folder checkboxes

**Relevant to us**: This is the closest pattern to what we need. The accordion sections (`emby-collapse`) map to our Feature Flags / Playback Prefs / Search Prefs / Content Access sections. The conditional visibility pattern is how we'd show collection-level settings only when the collection access mode is not "AllowAll". The inline test buttons pattern could apply to "test content access" verification.

### 2.4 Webhook — Dynamic Template-Based Multi-Destination (VERY HIGH)
**Pattern**: Template-driven dynamic form generation. ~600 HTML, ~500+ JS.

- Controls: All types including color picker, textareas
- Dynamic:
  - **HTML `<template>` elements**: Reusable templates for each destination type (Discord, Slack, SMTP, etc.)
  - **Runtime template cloning**: `template.content.cloneNode(true)` for new destinations
  - User list fetched for filter checkboxes
- Advanced UX:
  - **Per-destination config**: Each destination in `emby-collapse` with remove button
  - **Composed templates**: Base template + destination-specific template merged
  - **Dynamic key-value additions**: Generic destination allows arbitrary header/field additions

**Relevant to us**: The `<template>` + clone pattern is how we'd render dynamic per-user content access rules (JF-135.3) — a base template for the user row with optional content access overrides cloned in.

### 2.5 Playback Reporting — Multi-Page Tabs (MEDIUM)
**Pattern**: Seven separate HTML pages with shared tab navigation.

- Uses `LibraryMenu.setTabs()` for multi-page navigation
- Immediate save on dropdown change (no save button needed)
- File browser via `Dashboard.DirectoryBrowser`

**Relevant to us**: The multi-page pattern is overkill for us — our features fit in one page with accordion sections.

### 2.6 Intro Skipper — Modern SPA with Vite/TypeScript (HIGHEST complexity)
**Pattern**: Full SPA with Vite build pipeline, TypeScript, reactive store, validation framework.

- Build: Vite → IIFE format (`introskipper.js` + `introskipper.css`)
- Mount: `MutationObserver` detects dashboard injection, mounts app
- Components: ~20 reusable TypeScript component factories
- Store: Pub/sub `configStore` with `get/set/subscribe/emit`, dirty tracking
- Validation: Declarative rules, real-time inline validation, cross-field validation
- Lifecycle: `render()` / `destroy()` per tab with cleanup

**Relevant to us**: This is the gold standard but overkill for our current scope. The reactive store pattern and declarative validation are aspirational. The component factory pattern (`checkboxField()`, `selectField()`) could inspire reusable helpers if we refactor later.

---

## 3. Pattern Comparison Matrix

| Plugin | Layout | Dynamic Lists | Conditional Visibility | Inline Testing | Per-Item Config | Validation | JS Pattern | Lines |
|--------|--------|--------------|----------------------|----------------|----------------|------------|------------|-------|
| Fanart | Single field | No | No | No | No | None | ES module | ~35 |
| OpenSubtitles | Flat form | No | Warning banner | No | No | On-save API | ES module | ~120 |
| Trakt | Sections | Users, folders | Auth status | OAuth flow | Per-user | None | ES module | ~400 |
| LDAP Auth | Accordions | Folders | Checkbox-gated | 3 test buttons | No | HTML5+server | Inline | ~900 |
| Webhook | Dynamic templates | Users, notif types | Template composition | No | Per-destination | HTML5 required | ES module | ~1100 |
| Playback Reporting | Multi-page tabs | Users | No | No | No | Immediate save | ES module | ~700 |
| Intro Skipper | SPA tabs | Everything | Store-driven | Scan operations | Per-episode | Real-time reactive | TS/Vite | ~2000+ |
| **Alexa Skill (current)** | **Flat sections** | **Users table** | **No** | **Test connection** | **Per-user skill** | **None** | **Inline** | **~487** |

---

## 4. Recommendations for JF-134 and JF-135

### Architecture Decision: Stay with Inline Script + Emby Collapse

The LDAP Auth pattern is the sweet spot for our needs:
- We already use inline `<script>` — no reason to switch to ES modules or Vite for this scope
- `emby-collapse` provides clean accordion sections for Feature Flags / Playback / Search / Content Access
- Conditional visibility via JS `display:none/block` is proven and simple
- Dynamic list loading via `ApiClient.getUsers()` / `ApiClient.getVirtualFolders()` is standard

### Recommended Config Page Layout

```
┌─────────────────────────────────────────────────────┐
│ Alexa Skill                                    [Help]│
├─────────────────────────────────────────────────────┤
│ ▼ General Configuration                              │
│   Server Address, SSL, Fuzzy Match, LWA creds       │
│   [Test Connection]                                  │
├─────────────────────────────────────────────────────┤
│ ▼ Feature Flags                                      │  ← NEW (JF-134.1)
│   [Toggle] Radio Mode                                │
│   [Toggle] Podcasts                                  │
│   [Toggle] Live TV                                   │
│   [Toggle] Sleep Timer                               │
│   [Toggle] Queue Management                          │
│   [Toggle] Browse Library                            │
│   [Toggle] Recommendations                           │
│   [Toggle] APL Visuals                               │
│   [Toggle] Video Playback                            │
├─────────────────────────────────────────────────────┤
│ ▼ Playback Preferences                               │  ← NEW (JF-134.2)
│   Default Shuffle: [Toggle]                          │
│   Gapless Playback: [Toggle]                         │
│   Pre-fetch Count: [1] (0-3)                        │
│   Default Repeat: [None ▾]                           │
│   Max Queue Size: [50] (10-200)                     │
├─────────────────────────────────────────────────────┤
│ ▼ Search Preferences                                 │  ← NEW (JF-134.3)
│   Max Results: [5] (1-10)                           │
│   Fuzzy Threshold: [0.70] (0.3-1.0)                │
│   Auto-play Single: [Toggle]                         │
│   Show Type in Disambiguation: [Toggle]              │
├─────────────────────────────────────────────────────┤
│ ▼ Content Access                                     │  ← NEW (JF-135)
│   Media Type Access:                                 │
│     [✓] Music  [✓] Videos  [✓] Podcasts             │
│     [✓] Live TV  [✓] Books                          │
│   Collection Mode: [Allow All ▾]                     │
│   Collections: (dynamic list from Jellyfin API)      │
│     [✓] Music  [✓] Kids Movies  [ ] Private         │
├─────────────────────────────────────────────────────┤
│ ▼ User Skill Configuration                           │
│   (existing table + per-user content access overrides)│
│   [+ Add New User Skill]                             │
├─────────────────────────────────────────────────────┤
│ [Save]                                               │
└─────────────────────────────────────────────────────┘
```

### Key Patterns to Adopt

1. **`emby-collapse` for section grouping** (from LDAP Auth)
   - Each config section is a collapsible accordion
   - Reduces visual noise while keeping everything on one page
   - Code: `<div is="emby-collapse" title="Feature Flags">...</div>`

2. **`checkboxContainer-withDescription`** for feature flags (from LDAP Auth, Trakt)
   - Each toggle gets a description line explaining what it controls
   - Code: `<div class="checkboxContainer checkboxContainer-withDescription">`

3. **Conditional section visibility** (from LDAP Auth)
   - Collection access list shown/hidden based on CollectionAccessMode dropdown
   - Per-user content overrides shown/hidden via expandable row
   - Code: `document.getElementById('section').style.display = checkbox.checked ? 'block' : 'none'`

4. **Dynamic collection list** (from Trakt's folder pattern)
   - Fetch collections via `ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'))`
   - Render as checkboxes for allow/deny selection
   - Re-fetch on page show to stay current

5. **Inline test button** (from LDAP Auth)
   - "Test Connection" button already exists
   - Could add "Verify Content Access" button that tests if configured permissions work correctly

6. **`<template>` cloning for per-user overrides** (from Webhook)
   - Define a template for user content access overrides
   - Clone when expanding a user's content access section
   - Avoids duplicating HTML in a loop

### Implementation Approach

**Step 1**: Add all new properties to `PluginConfiguration` (C# side)
**Step 2**: Add new `emby-collapse` sections to `config.html`
**Step 3**: Wire up load/save in the inline `<script>` (follow existing pattern)
**Step 4**: Add conditional visibility JS for collection access section
**Step 5**: Add dynamic collection loading via `ApiClient.getVirtualFolders()`
**Step 6**: Add per-user content access overrides to existing user table

### Estimated Complexity
- ~300-400 additional lines to config.html (feature flags + playback + search + content access sections)
- ~50-100 lines to PluginConfiguration.cs (new properties)
- No need for separate JS files, new build pipeline, or framework migration

---

## Sources

- `jellyfin-plugin-opensubtitles` — `Web/opensubtitles.html`
- `jellyfin-plugin-trakt` — `Trakt/Web/trakt.html`
- `jellyfin-plugin-ldapauth` — `LDAP-Auth/Config/configPage.html`
- `jellyfin-plugin-webhook` — `Jellyfin.Plugin.Webhook/Configuration/Web/config.html`
- `jellyfin-plugin-playbackreporting` — `Pages/*.html` (7 pages)
- `intro-skipper` — `web/src/` (TypeScript SPA)
- `jellyfin-plugin-fanart` — `Web/fanart.html`
- Jellyfin web framework — `useConfigurationPages`, `ApiClient`, `Dashboard` globals
- Emby web components — `emby-input`, `emby-collapse`, `emby-checkbox`, etc.
