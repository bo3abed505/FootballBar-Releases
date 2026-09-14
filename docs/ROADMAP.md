# FootballTray Roadmap

## Purpose
This document tracks milestones, planned feature releases, and technical enhancements across both the client and backend components of FootballTray.

## Phases
1. **Phase 1: Project Scaffolding & Architecture**: Production repository layout, clean WinUI 3 shell, and provider-agnostic domain layer.
2. **Phase 2: Taskbar Injection Proof-of-Concept**: Native HWND integration into Windows 11 taskbar without external helper libraries.
3. **Phase 3: Backend Ingestion & Database**: PostgreSQL schema setup, API-Football ingestion adapter, and rate-budgeted polling pipeline.
4. **Phase 4: Client UI & Live Match Experience**: Dynamic match views, league filtering, notifications, and lightweight tray telemetry.
5. **Phase 5: Performance Tuning & Distribution**: MSIX packaging, automatic updates, and minimal memory footprint optimization.
   - *Status: Practically complete.* Automatic updates are fully implemented and validated; runtime memory behavior was audited with no evidence of runaway growth; current source release footprint was reduced by removing unused transitive AI/ML dependencies (Strategy A2). MSIX architectural feasibility was evaluated and implementation is intentionally deferred until a later maintenance/stabilization stage. The unpackaged per-user installer and GitHub Releases updater remain the authoritative distribution model.
