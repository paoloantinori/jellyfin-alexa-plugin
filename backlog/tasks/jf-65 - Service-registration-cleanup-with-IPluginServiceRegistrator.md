---
id: JF-65
title: Service registration cleanup with IPluginServiceRegistrator
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 17:13'
labels:
  - enhancement
  - architecture
  - infrastructure
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Clean up service registration using the IPluginServiceRegistrator pattern. Inspired by the Gelato plugin's modern DI approach.

Currently the plugin may register services in the plugin constructor or other non-standard locations. Modern Jellyfin plugins use IPluginServiceRegistrator for clean DI registration.

Implementation:
1. Implement IPluginServiceRegistrator interface
2. Move all service registrations (HttpClient factories, handlers, services) to the Register method
3. Register named HttpClient instances for progressive responses and Jellyfin API calls
4. Use proper lifetimes (Singleton, Scoped, Transient) for each service
5. Remove any manual service resolution (anti-pattern)
6. This sets the foundation for B4 (interceptor-based persistence) and other DI-dependent features
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Registered RequestPipeline, interceptors (LoggingRequestInterceptor, SessionAttributesInterceptor, LoggingResponseInterceptor), and IHttpClientFactory in Registrator.cs via IPluginServiceRegistrator. Controller now receives RequestPipeline via DI injection. Plugin.HttpClient backed by IHttpClientFactory.CreateClient() with static fallback for test scenarios. SkillStartup wires the factory to Plugin.Instance on startup. RequestPipeline constructor changed to accept IEnumerable<> for DI compatibility. Updated SkillStartupTests with IHttpClientFactory mock.
<!-- SECTION:FINAL_SUMMARY:END -->
