# Architecture Overview

## Purpose
This document details the high-level system architecture of **FootballTray**, including the separation between the native WinUI 3 desktop client, the TypeScript/Node.js backend ingestion service, the PostgreSQL database, and external football data providers.

## Core Principles
1. **Separation of Concerns**: The desktop client handles presentation and native Windows 11 shell integration; the backend handles scheduled polling, provider normalization, rate limiting, and persistence.
2. **Provider Agnostic**: Domain models are abstracted from third-party provider payloads.
3. **Resilience & Offline Handling**: The client gracefully handles network hiccups and displays cached state when offline.
4. **Lightweight Taskbar Footprint**: Minimal resource usage on the Windows client while docked to the taskbar.
