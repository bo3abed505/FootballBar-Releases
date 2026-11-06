# FootballBar — Release Engineering & Deployment Guide

## Product Overview & Version Metadata

- **Application Name**: FootballBar (Executable binary: `FootballTray.exe`)
- **Current Public Release**: `v1.3.1`
- **Target OS**: Windows 11 (64-bit) / Windows 10 build 17763+ (64-bit)
- **Architecture**: `win-x64`
- **Application Framework**: WinUI 3 / Windows App SDK (.NET 10.0)
- **Packaging Models**:
  - One-Click Interactive Installer: `FootballTray-Setup-v1.3.1-win-x64.exe`
  - Unpackaged Portable Application: `FootballTray-v1.3.1-win-x64.zip`
  - Microsoft Store Desktop Bridge: `FootballTray-v1.3.1-win-x64.msix`

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
│ Embedded SQLite Database (better-sqlite3)│
└────────────────────────────────────────┘
```

> [!IMPORTANT]
> The Windows Desktop client is completely decoupled from backend credentials and third-party API keys. It contains **zero** database connection strings, **zero** provider API keys, and **zero** development paths.

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

To optionally generate a static update discovery manifest (`release-manifest.json`), provide the target HTTPS download URL:
```powershell
pwsh -ExecutionPolicy Bypass -File ./scripts/publish-release.ps1 -UpdateDownloadUrl "https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip"
```

> [!NOTE]
> `https://example.invalid/...` is an illustrative placeholder only and is **not** the configured FootballTray production host.
>
> **Release Engineering & Operational Boundaries**:
> 1. `publish-release.ps1` creates release artifacts strictly **locally** under `artifacts/releases/`.
> 2. The script performs **no** external network uploads.
> 3. Public hosting, CDN deployment, or GitHub Releases asset publication remains a separate release-infrastructure step.
> 4. Automatic client update checking and acquisition are implemented; `UpdateDiscoveryService` consumes `release-manifest.json` for remote update discovery.
> 5. `publish-release.ps1` establishes reproducible release artifacts, manifest generation, and offline validation.

The script will:
1. Clean previous publish folders in `artifacts/releases/v1.0.0/`.
2. Restore all NuGet packages for `win-x64`.
3. Publish a fully self-contained `win-x64` Release build carrying all Windows App SDK runtime components.
4. Generate `VERSION.txt` and `RELEASE_NOTES.txt`.
5. Validate published output integrity and runtime dependencies.
6. Compress the output into `FootballTray-v1.0.0-win-x64.zip`.
7. Compute SHA-256 hashes and save `SHA256SUMS.txt`.
8. Generate `release-manifest.json` (optional, when `-UpdateDownloadUrl` is supplied).

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
- Absence of test/development files (`*.Tests.dll`, `*.cs`, `*.ts`, `.env*`).
- Release Manifest Integrity: If `release-manifest.json` is present, verifies schema version (`"1.0"`), version match, architecture (`"win-x64"`), HTTPS download URL with matching archive filename, SHA-256 hash match against actual ZIP, byte size match against actual ZIP, and valid explicit-offset publication timestamp.

### Validation Environments Note
- **Isolated Publish-Directory Validation**: The release ZIP is extracted to a clean temporary location outside the repository tree, verifying that the application boots and initializes Windows App SDK runtime without referencing workspace files.
- **Clean-Machine / VM Validation**: Formal validation on a dedicated clean Windows 11 machine/VM without development SDKs remains a staging verification step prior to final user distribution.

---

## Release Artifact Structure

```
artifacts/
  releases/
    v1.3.1/
      FootballTray-Setup-v1.3.1-win-x64.exe # One-click dark setup wizard installer
      FootballTray-v1.3.1-win-x64.msix      # Microsoft Store Desktop Bridge package
      FootballTray-v1.3.1-win-x64.zip       # Portable distribution archive
      release-manifest.json                 # Automatic update discovery manifest
      ota-telemetry-manifest.json           # Dynamic route telemetry manifest
      RELEASE_NOTES.txt                     # User-facing release notes
      SHA256SUMS.txt                        # SHA-256 Checksums for all assets
      VERSION.txt                           # Build and environment metadata
      win-x64/                              # Published self-contained application folder
        FootballTray.exe                    # Main application executable
        FootballTray.dll
        FootballTray.Core.dll
        Microsoft.WinUI.dll
        Microsoft.WindowsAppRuntime.Bootstrap.dll
        ... (runtime dependencies)
```

---

## Release Update Manifest (Optional)

When `-UpdateDownloadUrl` is supplied to `publish-release.ps1`, a deterministic `release-manifest.json` document is written to the release root. This manifest establishes the static contract that a future client update discovery service can consume to safely detect, evaluate, and offer new releases without hardcoded backend coupling or provider traffic.

### Schema Fields (`schemaVersion: "1.0"`)

| Field | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `schemaVersion` | string | Yes | Manifest schema version (strictly `"1.0"`). |
| `version` | string | Yes | FootballTray application version using the installer-compatible version grammar (e.g. `"1.0.0"`). |
| `publishedAt` | string | Yes | ISO 8601 UTC timestamp with explicit `Z` offset (e.g. `"2026-09-10T19:30:00Z"`). |
| `architecture` | string | Yes | Target platform architecture (strictly `"win-x64"`). |
| `downloadUrl` | string | Yes | Absolute HTTPS URI pointing directly to the release ZIP archive. Must match archive filename. |
| `sha256` | string | Yes | Lowercase 64-hex SHA-256 hash of the distribution ZIP archive. |
| `sizeBytes` | integer | Yes | Exact file size in bytes of the distribution ZIP archive. |
| `releaseNotes` | string | No | User-facing release notes content (up to 10,000 characters). |

Example `release-manifest.json`:
```json
{
  "schemaVersion": "1.0",
  "version": "1.0.0",
  "publishedAt": "2026-09-10T19:30:00Z",
  "architecture": "win-x64",
  "downloadUrl": "https://example.invalid/releases/v1.0.0/FootballTray-v1.0.0-win-x64.zip",
  "sha256": "0a0216b61bd63dafdeab85a00b6319e4fa0108e958d0fe25b4bde943ff58ddd4",
  "sizeBytes": 65518293,
  "releaseNotes": "FootballTray v1.0.0\n============================================================\n..."
}
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
| **Backend reports Ingestion Quota Limits** | Telemetry ingestion rate limit reached | Check `/api/diagnostics` -> `budget.isExhausted`. | Acquisition scheduler operates with unmetered primary live ingestion and automated circuit-breaker resilience. |
| **Database disconnected or locked** | SQLite database file inaccessible or locked | Check `GET /health` -> `"database": "disconnected"`. | Ensure SQLite database file has write permissions and disk space available (`better-sqlite3` WAL mode). |

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

### 4. Automatic Updates & Installer Handoff

FootballTray supports user-approved automatic updates orchestrated from the client UI:

1. **User Approval Required**: Updates are never silently downloaded or installed without explicit user action. The user must explicitly click "Download update" and "Install update" in the settings panel.
2. **Payload Inclusion**: `scripts/install-client.ps1` is included as part of the official client publish payload (`win-x64/install-client.ps1`) and installed beside `FootballTray.exe`.
3. **Trusted Installer Handoff**: When installing an update, FootballTray copies its **current trusted local** `install-client.ps1` into an isolated handoff directory (`%LOCALAPPDATA%\FootballTray\Updates\<version>\handoff_<guid>\install-client.ps1`) and launches it using the absolute Windows PowerShell host (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`). The downloaded ZIP's scripts are never directly executed to launch the upgrade.
4. **Bootstrap Constraint (First Auto-Update-Capable Release)**:
   - Historical v1.0.0 builds created prior to this feature did not package `install-client.ps1` beside `FootballTray.exe`.
   - Automatic installer handoff strictly requires `install-client.ps1` beside `FootballTray.exe` and fails closed if it is absent (no repository fallback).
   - The first auto-update-capable build must be installed or upgraded via the standard manual installer path (`pwsh ./scripts/install-client.ps1`) to seed the trusted script into the application directory.
   - Subsequent releases automatically carry `install-client.ps1` forward through the installer's normal `$SourceDir` recursive payload copying.
   - Trust chain: First seeded auto-update-capable release -> its trusted local installer -> next verified release -> next release installs its own trusted installer -> repeat.
5. **Process Handoff & Instance Protection**: The installer is invoked with `-WaitForProcessId <callerPid>`. It performs read-only preflight checks and waits up to 15 seconds for the exact caller PID to exit gracefully. If the caller fails to exit within 15 seconds, the installer fails closed without force-killing the caller. Once the caller exits, the installer cleans any other remaining FootballTray instances before beginning destructive file operations.

---

## Current Distribution & Phase 5 Status

### 1. Authoritative Distribution Model
FootballTray distributes as an **unpackaged, self-contained `win-x64` desktop application** (`WindowsPackageType=None`):
- **Hosting**: Public releases and update manifests are hosted on GitHub Releases.
- **Current Public Release**: `v1.0.3` remains the active, immutable public release.
- **Current Source State**: Source commit `535b1faf9f91eaf78ca7631d4ab931ab08d8657e` contains the validated A2 distribution optimization; public `v1.0.3` predates this commit and remains immutable.
- **Target Installation Directory**: `%LOCALAPPDATA%\Programs\FootballTray` (per-user, requiring zero UAC administrator elevation).
- **User Data Isolation**: User preferences, local caches, and diagnostic logs reside in `%LOCALAPPDATA%\FootballTray` and are guaranteed to be preserved across updates and uninstallation.
- **Transactional Upgrades**: Upgrades employ transactional staging (`.__staging`), atomic folder swap, and backup preservation (`.__backup`) with automatic rollback on any activation failure.

### 2. Automatic Updates & Production Verification
- **Architecture**: The in-app update pipeline comprises `UpdateDiscoveryService`, `UpdatePackageAcquisitionService`, and `UpdateInstallationService`. It validates SHA-256 hashes and byte sizes against `release-manifest.json` before initiating trusted installer handoff.
- **Production Validation**: The end-to-end upgrade flow was validated from `v1.0.0` to `v1.0.3` through the real client update path using automated UI driving (verifying manifest discovery, download, integrity check, graceful shutdown, PowerShell handoff, and relaunch).

### 3. Performance & Runtime Memory Audit
- **Soak Telemetry**: A continuous 60-minute soak test under active tick cycles showed working set moving from approximately 232.48 MiB to 203.30 MiB and private memory from approximately 168.69 MiB to 179.88 MiB, with cyclical drops. Handles, threads, USER objects, and GDI objects remained stable, with low CPU usage.
- **Conclusion**: No evidence of runaway memory growth was observed over the 60-minute soak.
- **Engineering Posture**: Destructive artificial workarounds—such as forced full garbage collections (`GC.Collect()`) or working-set paging hacks (`EmptyWorkingSet` / `SetProcessWorkingSetSize`)—were explicitly not adopted, preserving standard .NET 10 generational memory management.

### 4. A2 Release-Payload Distribution Optimization (Current Source)
- **Scope**: Source commit `535b1faf` eliminates an unused transitive AI/ML dependency subtree (`Microsoft.WindowsAppSDK.ML`, `Microsoft.WindowsAppSDK.AI`, `Microsoft.Windows.AI.MachineLearning`, `System.Numerics.Tensors`) from release output.
- **Savings**: Removes 13 out of 13 targeted AI/ML payload files, representing ~42,346,048 bytes (~40.38 MiB) uncompressed and ~16.74 MiB in ZIP entry reduction.
- **Classification**: Pre-A2 module inspection showed `onnxruntime.dll` and `DirectML.dll` were not loaded, while the committed A2 candidate later loaded 0 of 13 targeted modules during its exclusive runtime smoke. Strategy A2 is therefore documented as a **distribution / disk / download optimization**, not a demonstrated runtime memory reduction.
- **Release Enforcement**: Release integrity is enforced by `validate-release.ps1` and `ReleaseEngineeringTests.cs`, which reject published payloads or dependency metadata containing any forbidden AI/ML files or identities.

### 5. MSIX Packaging Feasibility & Deferral Decision
- **Evaluation**: An in-depth architectural feasibility audit evaluated Full MSIX, Sparse Packaging (packaging with external location), and the existing unpackaged model.
- **Decision**: MSIX is **not rejected**, but implementation is **intentionally deferred** until FootballTray reaches a later product-stabilization or maintenance stage where a distribution migration offers clearer product value.
- **Rationale**: The existing unpackaged installer and manifest-based GitHub updater are mature, thoroughly tested, zero-cost, and well matched to FootballTray's native Win32 taskbar architecture. Migrating distribution at this stage would add operational complexity without sufficient immediate product benefit.
- **Future Precedent**: Future MSIX investigation may refer to [Awqat Salaat](https://github.com/Khiro95/Awqat-Salaat) as a comparative open-source precedent for a full-trust packaged WinUI 3 taskbar-integrated desktop application.

---

## Future Installer Strategy

For future automated distribution:
1. **Packaging**: The current unpackaged self-contained folder structure provides a drop-in payload for standard Windows installer creators (such as Inno Setup or WiX Toolset).
2. **Code Signing**: The output `FootballTray.exe` and installer can be signed using `signtool.exe` with an Authenticode certificate.
3. **Shortcuts**: The installer will register a Start Menu shortcut and Startup folder entry without altering application architecture.

