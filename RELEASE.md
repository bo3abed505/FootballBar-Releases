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

---

## Future Installer Strategy

For future automated distribution:
1. **Packaging**: The current unpackaged self-contained folder structure provides a drop-in payload for standard Windows installer creators (such as Inno Setup or WiX Toolset).
2. **Code Signing**: The output `FootballTray.exe` and installer can be signed using `signtool.exe` with an Authenticode certificate.
3. **Shortcuts**: The installer will register a Start Menu shortcut and Startup folder entry without altering application architecture.
