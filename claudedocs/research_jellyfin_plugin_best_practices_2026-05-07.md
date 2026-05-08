# Research: Jellyfin Plugin Best Practices & Improvement Opportunities

**Date**: 2026-05-07
**Scope**: Exhaustive survey of Jellyfin plugin ecosystem (official template + 6 community plugins)
**Goal**: Identify patterns and improvements applicable to the Alexa Skill plugin

---

## Executive Summary

The Alexa Skill plugin is **already one of the most architecturally mature Jellyfin plugins** in the ecosystem. It implements patterns (circuit breaker, search cache, request pipeline with interceptors, correlation IDs, retry with budget-aware backoff, per-intent metrics, diagnostics API, health checks) that **no other community plugin implements**. However, the research identified 8 concrete improvement areas where the plugin could further strengthen its robustness.

---

## Sources Analyzed

| Plugin | Stars | Key Patterns Observed |
|--------|-------|----------------------|
| **Official Template** (`jellyfin-plugin-template`) | 412 | Minimal: `BasePlugin<T>`, `IPluginServiceRegistrator`, `IHasWebPages`, `IHostedService` |
| **LDAP Auth** | ~300 | Dual interface (auth + password reset), `IScheduledTask`, config test API endpoints |
| **Trakt** | ~250 | ServerMediator pattern, `IExternalUrlProvider`, event helpers, bi-directional sync tasks |
| **OpenSubtitles** | ~200 | Named `HttpClient` with rate-limiting `DelegatingHandler`, lazy login state, `ConfigurationChanged` event |
| **Playback Reporting** | ~180 | `BaseSqliteRepository`, schema migration, in-memory playback tracking, Chart.js dashboard |
| **Fanart** | ~150 | Minimal: standard config pattern, no notable extras |
| **Anime** | ~200 | Custom `AsyncLock`, `RateLimiter` with token-bucket, but uses old service-locator anti-pattern |

---

## Current State Assessment

### What the Alexa Skill plugin already does well (unique in the ecosystem)

| Feature | Alexa Plugin | Other Plugins |
|---------|-------------|---------------|
| **Circuit breaker** | Full 3-state (Closed/Open/HalfOpen) with configurable thresholds | None |
| **Request pipeline with interceptors** | Custom middleware chain (logging, metrics, circuit breaker, session attrs) | None — handlers called directly |
| **Correlation IDs** | 8-char per-request ID propagated through pipeline | None |
| **Structured logging scopes** | `BeginScope` with correlation ID, intent, locale | None |
| **Retry with budget-aware backoff** | Exponential + jitter + timeout budget check | Simple rate limiting at best (Anime, Trakt) |
| **Search result cache** | Per-user LRU with TTL, cache-miss fallback on failure | None |
| **Per-intent metrics** | Count, timing (avg/min/max), error rates per intent | None |
| **Health check API** | 3-tier (Healthy/Degraded/Unhealthy) + Jellyfin connectivity ping | None |
| **Diagnostics API** | Full metrics dump, user status, validation errors | Playback Reporting has a Chart.js dashboard but no API |
| **`IPluginServiceRegistrator`** | Full DI registration with auto-discovered handlers | Only newer plugins (LDAP, Trakt, OpenSubtitles) |
| **Named `HttpClient`** | `AddHttpClient("AlexaSkill")` via DI | Only OpenSubtitles does this |
| **`IHostedService`** | `SkillStartup` for background skill provisioning | Only Trakt and Playback Reporting |

### Areas where other plugins demonstrate patterns we don't use

---

## Improvement Recommendations

### Priority 1 — High Impact, Moderate Effort

#### 1. `ConfigurationChanged` Event Subscription
**Source**: OpenSubtitles plugin
**Current gap**: Configuration changes (e.g. server address update) require skill restart to propagate to active services like `JellyfinConnectivityChecker`, `CircuitBreaker`, and `SearchResultCache`.
**Recommendation**: Subscribe to `ConfigurationChanged` on the plugin base class to propagate updates to active services without requiring a Jellyfin restart.
```csharp
// In Plugin.cs constructor or SkillStartup:
ConfigurationChanged += (_, _) => {
    _connectivityChecker?.InvalidateCache();
    _circuitBreaker?.Reset();
    _searchCache?.Clear();
};
```
**Impact**: Operational — avoids needing restarts when changing server address or SSL settings.

#### 2. `IScheduledTask` for Periodic Operations
**Source**: LDAP Auth (profile image sync), Trakt (library sync)
**Current gap**: Token refresh and skill health monitoring run only at startup. Expired tokens aren't proactively refreshed. There's no periodic cleanup of stale session tokens or expired cache entries.
**Recommendation**: Implement `IScheduledTask` for:
- **Token refresh task**: Periodically refresh LWA tokens before they expire
- **Cache cleanup task**: Purge expired entries from `SearchResultCache`
- **Health report task**: Optional periodic self-check with logging

These tasks would appear in the Jellyfin dashboard under Scheduled Tasks, giving admins visibility.
**Impact**: Reliability — prevents token expiry gaps, reduces memory growth.

#### 3. Polly or Resilience Framework Integration
**Source**: Retry logic is hand-rolled; OpenSubtitles uses a custom rate-limiting `DelegatingHandler`
**Current gap**: `RetryHelper` is well-implemented but hand-rolled. Polly (now `Microsoft.Extensions.Resilience`) provides battle-tested retry, circuit breaker, timeout, and hedging policies that compose cleanly.
**Recommendation**: Evaluate replacing `RetryHelper` + `CircuitBreaker` with `Microsoft.Extensions.Resilience` pipelines. Benefits:
- Composable policies (retry + circuit breaker + timeout in one pipeline)
- `HedgingHandler` could send a second probe if the first is slow (useful within Alexa's 8s budget)
- Metrics integration with `System.Diagnostics.Metrics`
- Custom delegating handler approach like OpenSubtitles for rate limiting
**Impact**: Reliability + maintainability. Reduces custom resilience code. **However**, this is a judgment call — the current implementation works well and may not justify the migration cost.

### Priority 2 — Medium Impact, Low Effort

#### 4. `IExternalUrlProvider` for Alexa Skill Links
**Source**: Trakt plugin adds external links to item detail pages
**Current gap**: Users have no way to see their Alexa skill status from the Jellyfin web UI.
**Recommendation**: Implement `IExternalUrlProvider` to add a link to the Alexa skill diagnostics page from user profiles or item detail pages.
**Impact**: UX — admins can discover the diagnostics page from the standard Jellyfin UI.

#### 5. Event-Driven Architecture via `IEventConsumer`
**Source**: Jellyfin core provides `IEventConsumer<T>` for reacting to server events
**Current gap**: The plugin doesn't react to Jellyfin events (user creation, library changes, server shutdown) to clean up state or invalidate caches.
**Recommendation**: Implement `IEventConsumer<UserUpdatedEventArgs>` to sync user changes, and `IEventConsumer<ServerRestartingEventArgs>` for graceful shutdown of active Alexa sessions.
**Impact**: Consistency — keeps plugin state in sync with Jellyfin state changes.

#### 6. Configuration Page Diagnostic Visualization
**Source**: Playback Reporting plugin uses Chart.js for activity visualizations
**Current gap**: The config page is functional but lacks visual diagnostics. The `/diagnostics/metrics` API endpoint exists but isn't visualized.
**Recommendation**: Add a diagnostics tab to the config page that renders per-intent metrics, cache hit rates, and circuit breaker status using Chart.js (already available in the Jellyfin web client).
**Impact**: Operations — admins can spot issues visually instead of querying the API.

### Priority 3 — Lower Impact, Architectural Polish

#### 7. `ILogger<T>` Direct Injection vs `ILoggerFactory`
**Source**: LDAP Auth and OpenSubtitles inject `ILogger<T>` directly (newer pattern)
**Current gap**: BaseHandler accepts `ILoggerFactory` and creates `ILogger<BaseHandler>` — all handlers log under the `BaseHandler` category, losing the concrete handler type in logs.
**Recommendation**: Consider injecting `ILogger<T>` per handler type so logs show the specific handler (e.g., `PlayFavoritesIntentHandler` instead of `BaseHandler`). This could be done by having each handler create its own typed logger:
```csharp
// In each handler constructor:
Logger = loggerFactory.CreateLogger<PlayFavoritesIntentHandler>();
```
Or by making `Logger` virtual and overriding it in derived classes.
**Impact**: Observability — easier to filter logs by specific intent handler.

#### 8. Thread-Safety Improvements for `Plugin.Instance` Access
**Source**: Static `Plugin.Instance` is a common pattern across all plugins, but some access it unsafely
**Current gap**: Multiple places access `Plugin.Instance!` without null checks (e.g., `BaseHandler.cs:108`, `SkillStartup.cs:62-65`). While the instance should always be set by the time handlers run, defensive null checks would prevent NREs during edge cases (shutdown, hot-reload).
**Recommendation**: Add a `Debug.Assert(Instance != null)` or a guard method:
```csharp
internal static Plugin RequireInstance => Instance ?? throw new InvalidOperationException("Plugin not initialized");
```
**Impact**: Robustness — prevents NREs during edge-case lifecycle transitions.

---

## Comparison Matrix

| Pattern | Alexa Plugin | LDAP | Trakt | OpenSub | PlayReport | Fanart | Anime |
|---------|:-----------:|:----:|:-----:|:-------:|:----------:|:------:|:-----:|
| `IPluginServiceRegistrator` | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ |
| `IHostedService` | ✅ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ |
| `IScheduledTask` | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Circuit Breaker | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Retry + Backoff | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Request Pipeline | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Correlation IDs | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Search Cache | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Health Check API | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Per-intent Metrics | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Named HttpClient | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ |
| Config Change Events | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ |
| `IExternalUrlProvider` | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| `IEventConsumer` | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Dashboard Visualization | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ |
| `ILogger<T>` per handler | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ |

---

## Key Findings

### What makes this plugin unique in the ecosystem

The Alexa Skill plugin is the **only Jellyfin plugin** that implements:
1. A custom request pipeline with interceptor pattern
2. Circuit breaker pattern
3. Budget-aware retry with exponential backoff + jitter
4. Per-intent request metrics with timing
5. Correlation ID tracing
6. Search result caching with fallback
7. Structured health check API

These patterns are typically found in production microservices, not Jellyfin plugins. The plugin is architecturally more sophisticated than any other community plugin surveyed.

### What the ecosystem teaches us

The most actionable patterns from other plugins are:
1. **`IScheduledTask`** (from LDAP/Trakt) — for periodic token refresh and cache cleanup
2. **`ConfigurationChanged` event** (from OpenSubtitles) — for hot config propagation
3. **`IExternalUrlProvider`** (from Trakt) — for discoverability in Jellyfin UI
4. **Named HttpClient with DelegatingHandler** (from OpenSubtitles) — for rate limiting at the HTTP layer
5. **Chart.js dashboard** (from Playback Reporting) — for visual diagnostics

---

## Recommended Action Priority

| # | Improvement | Effort | Impact | Priority |
|---|------------|--------|--------|----------|
| 1 | `ConfigurationChanged` subscription | Low | Medium | P1 |
| 2 | `IScheduledTask` for token refresh + cache cleanup | Medium | High | P1 |
| 3 | Evaluate Microsoft.Extensions.Resilience | Medium | Medium | P1 (evaluate) |
| 4 | `IExternalUrlProvider` for diagnostics link | Low | Low-Medium | P2 |
| 5 | `IEventConsumer` for user/library sync | Medium | Medium | P2 |
| 6 | Chart.js diagnostics in config page | Medium | Medium | P2 |
| 7 | `ILogger<T>` per handler type | Low | Low | P3 |
| 8 | Defensive `Plugin.Instance` access | Low | Low | P3 |
