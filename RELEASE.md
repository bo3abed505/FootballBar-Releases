# FootballTray — Release Engineering & Deployment Guide

## Product Overview & Version Metadata

- **Application Name**: FootballTray
- **Current Version**: `1.0.0`
- **Target OS**: Windows 11 (64-bit) / Windows 10 build 17763+ (64-bit)
- **Architecture**: `win-x64`
- **Application Framework**: WinUI 3 / Windows App SDK 2.4.0 (.NET 10.0)
- **Packaging Model**: Unpackaged, Self-Contained Desktop Application (`WindowsPackageType=None`)

---

## Deployment Architecture & Topology

```
┌────────────────────────────────────────┐
│   FootballTray Desktop Client (WinUI 3)│  <-- Self-contained x64 (No .NET or WinAppSDK install required)
│   - System Taskbar Live Match Ticker   │
│   - Windows App SDK Match Notifications│
│   - Local Offline Cache & Preferences  │
└───────────────────┬────────────────────┘
                    │ HTTP (JSON / ETag)
                    ▼
┌────────────────────────────────────────┐
│   FootballTray Backend (Fastify / TS)  │  <-- Node.js Service (Local or Remote)
│   - /api/tray, /api/match/:id          │
│   - State Machine & Policy Engine      │
│   - Intelligent Multi-Tier Scheduler   │
└───────────────────┬────────────────────┘
                    │ SQL
                    ▼
┌────────────────────────────────────────┐
│   PostgreSQL Persistence Database      │
└────────────────────────────────────────┘
```

> [!IMPORTANT]
> The Windows Desktop client is completely decoupled from backend credentials and third-party API keys. It contains **zero** database connection strings, **zero** API-Football keys, and **zero** development paths.

---

## Building a Release Artifact

### Prerequisites
- .NET 10.0 SDK with Windows Desktop workload
- Windows 11 / Windows 10 (x64)
- PowerShell 7 (pwsh) or Windows PowerShell 5.1

### Step 1: Run the Publish Script
Run the automated release publishing script from the repository root:

```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/publish-release.ps1
```

Or specify an explicit version if needed:
```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/publish-release.ps1 -Version "1.0.0"
```

The script will:
1. Clean previous publish folders in `artifacts/releases/v1.0.0/`.
2. Restore all NuGet packages for `win-x64`.
3. Publish a fully self-contained `win-x64` Release build carrying all Windows App SDK runtime components.
4. Generate `VERSION.txt` and `RELEASE_NOTES.txt`.
5. Compress the output into `FootballTray-v1.0.0-win-x64.zip`.
6. Compute SHA-256 hashes and save `SHA256SUMS.txt`.

---

## Validating Release Artifacts

To verify the integrity and security of the generated release:

```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/validate-release.ps1
```

The validation script verifies:
- `FootballTray.exe` exists and is a 64-bit (x64 AMD64) executable.
- All required Windows App SDK and WinUI 3 runtime binaries are present.
- `VERSION.txt` matches the configured SemVer version.
- SHA-256 checksums match the generated zip archive and binary.
- Security Boundary Scan: Verifies that no API keys, database URLs, `.env` files, or test DLLs leaked into the release folder.

### Validation Environments Note
- **Isolated Publish-Directory Validation**: The release ZIP is extracted to a clean temporary location outside the repository tree, verifying that the application boots and initializes Windows App SDK runtime without referencing workspace files.
- **Clean-Machine / VM Validation**: Formal validation on a dedicated clean Windows 11 machine/VM without development SDKs remains a staging verification step prior to final user distribution.

---

## Release Artifact Structure

```
artifacts/
  releases/
    v1.0.0/
      FootballTray-v1.0.0-win-x64.zip     # Distribution archive
      SHA256SUMS.txt                      # SHA-256 Checksums
      VERSION.txt                         # Version and build metadata
      RELEASE_NOTES.txt                   # User-facing release notes
      win-x64/                            # Self-contained application folder
        FootballTray.exe                  # Main application executable
        FootballTray.dll
        FootballTray.Core.dll
        Microsoft.WinUI.dll
        Microsoft.WindowsAppRuntime.Bootstrap.dll
        ... (runtime dependencies)
```

---

## Backend Production Deployment

Because build tools like TypeScript (`tsc`) and migration tooling (`drizzle-kit`) are declared in `devDependencies` to keep the production runtime minimal and secure, backend deployment is separated into build, database schema provisioning, and runtime stages.

### 1. Build Environment (CI or Build Machine)
```bash
cd backend
npm ci
npm run build
```
This produces the compiled JavaScript artifacts in `backend/dist/`.

### 2. Database Schema Provisioning (Administrative Pre-Start Step)
Database schema deployment is an explicit pre-start administrative procedure executed from a build, CI, or administrative host where development tooling (`drizzle-kit`) is available:
```bash
# Executed by administrator / CI pipeline against target PostgreSQL:
npx drizzle-kit push
```
> [!NOTE]
> FootballTray does not automatically modify or push database schemas during normal application startup (`npm start`). Production database provisioning must be completed prior to starting runtime services.

### 3. Production Runtime (Production Host / Container)
Deploy `dist/`, `package.json`, `package-lock.json`, and `.env` (configured from `.env.production.example`) to the production server, then install production-only dependencies:
```bash
npm ci --omit=dev
```

Start the production service:
```bash
npm start
```
`npm start` runs `node dist/server.js` and requires only production runtime dependencies.

---

## Client Configuration & First-Run

- **Default Backend URL**: `http://127.0.0.1:4000`
- **Custom Backend URL**: Set environment variable `FOOTBALLTRAY_BACKEND_URL` (e.g. `http://192.168.1.50:4000`) before starting `FootballTray.exe`.
- **Local Cache Storage**: `%LOCALAPPDATA%\FootballTray\cache.json`
- **Preferences Storage**: `%LOCALAPPDATA%\FootballTray\preferences.json`
- **Local Logs Directory**: `%LOCALAPPDATA%\FootballTray\logs\footballtray.log` (5 MB bounded rotation, max 3 retained `.log` archives)

---

## Diagnostics, Observability & Production Troubleshooting

FootballTray includes local, bounded, non-intrusive diagnostics designed to diagnose client and backend health without exposing secrets or sending telemetry to third parties.

### 1. Client Diagnostics & Log Access
- **Diagnostics UI**: The main settings window features a dedicated **System Diagnostics** panel displaying:
  - Client version & build architecture
  - Overall health state (`Healthy`, `Degraded`, `Recovering`, `Failed`)
  - Server synchronization status & last successful response timestamp
  - Taskbar HWND host attachment status
  - Notification registration state
- **Copy Diagnostics to Clipboard**: Clicking the "Copy Diagnostics" button exports a structured, sanitized diagnostics report ready for issue tracking.
- **Log Files**: Local log files are located at `%LOCALAPPDATA%\FootballTray\logs\footballtray.log`.
  - Bounded to 5 MB per file with automatic rotation.
  - Retains a maximum of 3 historical archives (`footballtray.1.log`, `footballtray.2.log`, `footballtray.3.log`).
  - Automatic redaction of any sensitive credentials or keys.

### 2. Backend Health & Observability Endpoints
- **Architecture**: Diagnostics API provides lightweight operational telemetry using existing PostgreSQL database connection pool and application state. No secondary database, SQLite store, external metrics service, or telemetry backend is used.
- **`/health`**: Lightweight health check returning:
  ```json
  {
    "status": "ok",
    "database": "connected",
    "latencyMs": 4,
    "timestamp": "2026-09-02T12:00:00.000Z",
    "version": "1.0.0",
    "provider": "mock",
    "scheduler": { "running": true, "lastCycleOutcome": "success" },
    "uptimeSeconds": 3600
  }
  ```
- **`/api/diagnostics`**: Comprehensive operational telemetry returning memory usage, scheduler cycle history, and provider quota consumption without exposing database credentials or API keys.

### 3. Production Troubleshooting Runbook

| Issue / Symptom | Probable Cause | Diagnostic Verification | Recovery Action |
| :--- | :--- | :--- | :--- |
| **Client displays "Stale Data" badge** | Backend unreachable or poll timeout | Check `footballtray.log` for `[Sync][PollFailed]` errors. Verify `GET /health` on backend. | Ensure backend service is running; client will automatically resume fresh sync on next poll. |
| **Taskbar ticker not visible** | Explorer restart or HWND re-docking | Check Diagnostics UI: Taskbar Host will show `Unattached` or `Recovering`. | Taskbar watcher automatically detects `TaskbarCreated` message and re-attaches within 5 seconds. |
| **Notifications not appearing** | Notifications disabled in Windows Settings or preferences | Check Diagnostics UI for Notification Engine status (`Registered` vs `Disabled`). Check `preferences.json`. | Enable notifications in Windows App Settings or toggle "Enable Notifications" in FootballTray UI. |
| **Backend reports Provider Quota Exhaustion** | Daily API-Football request budget limit reached | Check `/api/diagnostics` -> `budget.isExhausted`. | Acquisition scheduler automatically enters fallback idle mode until daily budget resets at 00:00 UTC. |
| **Database disconnected** | PostgreSQL service stopped or invalid credentials in `.env` | Check `GET /health` -> `"database": "disconnected"`. | Restart PostgreSQL service and check `DATABASE_URL` in backend `.env`. |

---

## Client Installation, Upgrade & Uninstallation (Per-User)

FootballTray provides an unpackaged, per-user client installer script requiring **zero Administrator privileges**.

### 1. Fresh Installation
To install the client from the latest built release payload into `%LOCALAPPDATA%\Programs\FootballTray`:

```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/install-client.ps1
```

Optional flags:
* `-StartWithWindows`: Adds a shortcut to the user's Startup folder (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\FootballTray.lnk`).
* `-Launch`: Launches `FootballTray.exe` immediately after installation.
* `-SourceDir <path>`: Specifies an explicit release payload directory.
* `-WhatIf`: Previews the installation actions without modifying files or registry.

The installer:
1. Validates the x64 self-contained payload.
2. Stages files in `%LOCALAPPDATA%\Programs\FootballTray.__staging`.
3. Atomically activates the payload into `%LOCALAPPDATA%\Programs\FootballTray`.
4. Creates a Start Menu shortcut (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\FootballTray.lnk`).
5. Registers the application in Windows **Settings > Apps > Installed apps** (`HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballTray`).

### 2. Upgrading
To upgrade an existing installation:

```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/install-client.ps1
```

* **Running Process Handling**: Automatically signals running `FootballTray.exe` processes to exit gracefully, waiting up to 5 seconds before terminating to release PE/DLL file locks.
* **Transactional Staging & Rollback**: Stages files in `%LOCALAPPDATA%\Programs\FootballTray.__staging` and preserves the previous installation in `%LOCALAPPDATA%\Programs\FootballTray.__backup`. Backup is retained until binary activation, Start Menu shortcut, Startup shortcut, and HKCU registry registration have all succeeded and passed verification. Any post-activation failure triggers an immediate automatic restoration of the previous installation.
* **Deterministic Downgrade Protection**: Evaluates SemVer (including prerelease components). Blocks accidental older version installations unless `-AllowDowngrade` is explicitly passed.
* **Fail-Closed Version Validation**: Fails closed if release metadata is missing or invalid; never invents arbitrary default versions.
* **Data Preservation**: User data in `%LOCALAPPDATA%\FootballTray` (`preferences.json`, `tray.json`, `Cache/`, `logs/`) is **completely isolated and 100% preserved**.

### 3. Uninstallation
To remove the application:

```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/uninstall-client.ps1
```

Or invoke the uninstaller registered in Windows Settings > Apps.

The uninstaller:
1. Closes any active `FootballTray.exe` process.
2. Removes the binaries from `%LOCALAPPDATA%\Programs\FootballTray`.
3. Removes Start Menu and Startup shortcuts.
4. Cleans the `HKCU` Uninstall registry key.
5. **Preserves User Data**: Keeps `%LOCALAPPDATA%\FootballTray` intact so user settings and match history are preserved for future installs.

---

## Future Installer Strategy

For future automated distribution:
1. **Packaging**: The current unpackaged self-contained folder structure provides a drop-in payload for standard Windows installer creators (such as Inno Setup or WiX Toolset).
2. **Code Signing**: The output `FootballTray.exe` and installer can be signed using `signtool.exe` with an Authenticode certificate.
3. **Shortcuts**: The installer will register a Start Menu shortcut and Startup folder entry without altering application architecture.

