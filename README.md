# FootballTray

**FootballTray** is a standalone Windows 11 football taskbar application designed to deliver real-time scores, match schedules, and live football telemetry directly on your desktop taskbar.

---

## Key Highlights

- **Native WinUI 3 Desktop Client**: Built with modern Windows App SDK and WinUI 3 controls for a fluent, high-performance UI experience matching Windows 11 aesthetics.
- **Native Taskbar Integration (No TaskbarWidgets)**: Taskbar integration is implemented using a native HWND/Win32 window positioning and embedding approach (inspired by the proven methodology in **Awqat-Salaat**). **TaskbarWidgets is NOT a dependency.**
- **Dedicated Backend Service**: A TypeScript/Node.js backend coordinates polling schedules, data normalization, embedded SQLite persistence (`better-sqlite3` WAL mode), and caching so the desktop client remains lightweight and decoupled.
- **Authoritative Primary Data Provider**: **FotMob** serves as the primary provider for unmetered live matches, schedules, lineups, timeline events, and broadcast commentary, featuring automated circuit-breaker fallback to **API-Football**.
- **Clean Architecture**: Strong boundary separation between presentation, provider-agnostic domain logic (`FootballTray.Core`), and backend ingestion.

---

## Repository Structure

```text
FootballTray/
├── docs/                     # Technical specifications and architectural design documents
│   ├── ARCHITECTURE.md       # High-level system architecture and component separation
│   ├── DATABASE.md           # SQLite schema, WAL mode, indexing, and migration strategy
│   ├── API_CONTRACT.md       # REST and streaming API definitions
│   ├── TASKBAR_INTEGRATION.md# Native Win32/HWND taskbar docking specification
│   ├── REQUEST_BUDGET.md     # API request budgeting and adaptive polling strategy
│   ├── PROVIDERS.md          # Provider adapters and normalization specs
│   └── ROADMAP.md            # Milestones and project phases
│
├── src/                      # Client source code
│   └── client/
│       ├── FootballTray.Client/ # WinUI 3 Windows desktop application
│       └── FootballTray.Core/   # Provider-agnostic football domain models & logic
│
├── backend/                  # TypeScript backend service for data ingestion & normalization
│   ├── src/                  # Backend application source
│   ├── tests/                # Backend unit and integration tests
│   ├── package.json          # Node project manifest
│   ├── tsconfig.json         # TypeScript configuration
│   └── .env.example          # Environment variables template
│
├── shared/                   # Shared type definitions and protocol contracts
├── tests/                    # Solution-wide integration test suites
├── .gitignore                # Git ignore rules for .NET, WinUI, Node, and IDE artifacts
├── README.md                 # Project overview and developer documentation
└── FootballTray.sln           # Visual Studio / .NET Solution
```

---

## Prerequisites & Tech Stack

- **Client**:
  - C# / .NET 10 (or .NET 8+)
  - Windows App SDK / WinUI 3
  - Windows 11 (Build 22000+ recommended)
- **Backend**:
  - Node.js (LTS) & TypeScript
  - Embedded SQLite (`better-sqlite3` WAL mode)
- **Data Provider**:
  - FotMob (Authoritative Primary) + API-Football (Automated Circuit-Breaker Fallback)

---

## Getting Started

### Building the .NET Client Solution

Open PowerShell or terminal in the repository root:

```pwsh
# Build the complete solution
dotnet build FootballTray.sln

# Run the WinUI 3 client application
dotnet run --project src/client/FootballTray.Client/FootballTray.Client.csproj
```

---

## Design Principles & Constraints

- **No Third-Party Taskbar Frameworks**: Taskbar embedding is handled purely through native Win32 APIs without intermediate dependencies like TaskbarWidgets.
- **Provider Independence**: Client business logic and domain entities in `FootballTray.Core` never directly reference third-party provider schemas.
- **Strict Budget Adherence**: Ingestion pipelines strictly respect API rate quotas through intelligent caching and match-state-based polling.
