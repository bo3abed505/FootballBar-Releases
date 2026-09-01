# Database Architecture & Environments

## Purpose
This document defines the relational database schema, database isolation policies, migration workflows, and environment separation for PostgreSQL powering FootballTray.

---

## 1. Database Environments

FootballTray strictly isolates development and automated test database instances:

| Environment | Database Name | Configuration Key | Purpose |
| :--- | :--- | :--- | :--- |
| **Development** | `footballtray` | `DATABASE_URL` | Live local server development, manual testing, CLI sync runs. |
| **Test / CI** | `footballtray_test` | `DATABASE_URL_TEST` | Automated integration tests (Vitest), sync tests, lifecycle assertions. |

> [!CAUTION]
> **Strict Isolation Rule**: Automated test suites (`npm test`, Vitest) are programmatically prohibited from running against the development database (`footballtray`). If `DATABASE_URL_TEST` is not configured or points to `footballtray`, the test suite fails immediately before any queries run.

---

## 2. Setup & Migration Workflows

### Setup Test Database
To initialize the test database, apply migrations, and seed baseline test fixtures:
```bash
cd backend
npm run db:test:setup
```

### Apply Schema Migrations
Migrations are managed deterministically via Drizzle ORM:
```bash
# Generate SQL migration from schema.ts
npm run db:generate

# Apply migrations to development database
npm run db:migrate
```

---

## 3. Schema & Tables

The database comprises 15 relational tables:
1. `competitions` — Leagues, cups, and tournaments.
2. `competition_seasons` — Yearly seasons per competition.
3. `teams` — Football clubs and national teams.
4. `fixtures` — Matches with live scores, states, minutes, and referee data.
5. `events` — Chronological match timeline events (Goals, Cards, Subs) with unique `event_uid`.
6. `lineups` — Team formations and coaches per match.
7. `lineup_players` — Squad players and tactical grid positions.
8. `fixture_statistics` — Shots, possession, passes, fouls, and saves.
9. `standings` — League table rankings, points, and form.
10. `provider_coverage` — Provider data availability audit.
11. `acquisition_jobs` — Scheduled background polling tasks.
12. `api_request_ledger` — Ingestion call audit log.
13. `provider_daily_budget` — Daily API-Football quota tracker.
14. `installations` — Registered client installations.
15. `installation_settings` — Client preferences (favorite teams, spoiler filters).
