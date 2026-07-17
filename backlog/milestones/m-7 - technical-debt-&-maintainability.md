---
id: m-7
title: "Technical Debt & Maintainability"
---

## Description

Reduce the two strategic drags identified in the 2026-07-12 architecture review: the 2268-line BaseHandler God class and the 16 hand-maintained locale interaction models (only it-IT is generated). Plus quick wins: CI gate on main, HttpClient reuse, ConfigureAwait analyzer, DI cleanup, dead code.
