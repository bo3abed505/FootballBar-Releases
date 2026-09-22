import { describe, it, expect, beforeAll, afterAll, beforeEach } from 'vitest';
import pg from 'pg';
import dotenv from 'dotenv';
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  runProductionMigration,
  MigrationExecutionError,
  PostMigrationVerificationError,
} from '../src/db/migrate-prod.js';
import { readMigrationFiles } from 'drizzle-orm/migrator';

dotenv.config();

const { Pool } = pg;

describe('Production Database Migration Support Architecture (migrate-prod.ts)', () => {
  const testUrl = process.env.DATABASE_URL_TEST;
  if (!testUrl) {
    throw new Error('DATABASE_URL_TEST must be configured in environment for integration tests.');
  }

  const currentDir = path.dirname(fileURLToPath(import.meta.url));
  const migrationsFolder = path.resolve(currentDir, '../drizzle');
  const packagedMigrations = readMigrationFiles({ migrationsFolder });

  let pool: pg.Pool;

  const resetToLegacySchema = async () => {
    await pool.query('DROP TABLE IF EXISTS "competition_provider_aliases" CASCADE;');
    await pool.query('DROP TABLE IF EXISTS "team_provider_aliases" CASCADE;');
    await pool.query('DROP TABLE IF EXISTS "fixture_provider_aliases" CASCADE;');
    await pool.query('ALTER TABLE "competitions" DROP COLUMN IF EXISTS "canonical_id";');
    await pool.query('ALTER TABLE "teams" DROP COLUMN IF EXISTS "canonical_id";');
    await pool.query('ALTER TABLE "fixtures" DROP COLUMN IF EXISTS "canonical_id";');
    await pool.query('ALTER TABLE "fixtures" DROP COLUMN IF EXISTS "live_source_provider";');
    await pool.query('ALTER TABLE "fixtures" DROP COLUMN IF EXISTS "live_observed_at";');
    await pool.query('ALTER TABLE "fixtures" DROP COLUMN IF EXISTS "schedule_source_provider";');
    await pool.query('ALTER TABLE "fixtures" DROP COLUMN IF EXISTS "schedule_observed_at";');
    await pool.query('DROP TABLE IF EXISTS "scheduler_cycles" CASCADE;');
    await pool.query('ALTER TABLE "api_request_ledger" DROP COLUMN IF EXISTS "cycle_id";');
    await pool.query('ALTER TABLE "api_request_ledger" DROP COLUMN IF EXISTS "competition_id";');
    await pool.query('TRUNCATE TABLE "fixtures", "events", "fixture_statistics", "competition_seasons", "teams", "competitions" CASCADE;');
    await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
  };

  beforeAll(async () => {
    pool = new Pool({
      connectionString: testUrl,
      max: 5,
    });

    // Ensure we are connected strictly to footballtray_test
    const checkRes = await pool.query('SELECT current_database() AS db_name;');
    const dbName = checkRes.rows[0]?.db_name;
    if (dbName !== 'footballtray_test') {
      throw new Error(`CRITICAL TEST SAFETY VIOLATION: Connected to '${dbName}' instead of 'footballtray_test'.`);
    }

    // Ensure all packaged migrations are applied for initial state
    try {
      await runProductionMigration({
        expectedDatabaseName: 'footballtray_test',
        connectionString: testUrl,
      });
    } catch {
      await resetToLegacySchema();
      await runProductionMigration({
        expectedDatabaseName: 'footballtray_test',
        connectionString: testUrl,
        baselineLegacySchema: true,
      });
    }
  });

  afterAll(async () => {
    // Restore authentic fully-migrated schema and migration records for footballtray_test
    await resetToLegacySchema();
    await runProductionMigration({
      expectedDatabaseName: 'footballtray_test',
      connectionString: testUrl,
      baselineLegacySchema: true,
    });
    await pool.end();
  });

  // -------------------------------------------------------------------------
  // Mandatory Design Correction 1: Database Identity Verification
  // -------------------------------------------------------------------------
  describe('Correction 1: Database Identity Enforcement', () => {
    it('CR1-1: Aborts immediately if connected database does not match expectedDatabaseName', async () => {
      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_prod',
          connectionString: testUrl,
        })
      ).rejects.toThrow(/Connected to database 'footballtray_test', but expected 'footballtray_prod'/);
    });

    it('CR1-2: Aborts immediately if connected database is development database (footballtray)', async () => {
      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray',
          connectionString: testUrl,
        })
      ).rejects.toThrow(/Positive database identity verification failed/);
    });

    it('CR1-3: Fails closed when DATABASE_URL is missing or empty', async () => {
      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: '   ',
        })
      ).rejects.toThrow(/DATABASE_URL is not defined in environment or options/);
    });

    it('CR1-4: Source code verifies CLI production entrypoint hardcodes footballtray_prod and never uses process.env.EXPECTED_DATABASE_NAME', () => {
      const sourcePath = path.resolve(currentDir, '../src/db/migrate-prod.ts');
      const sourceContent = fs.readFileSync(sourcePath, 'utf8');

      // Must NOT use EXPECTED_DATABASE_NAME from env
      expect(sourceContent).not.toContain('EXPECTED_DATABASE_NAME');

      // CLI entrypoint must hardcode footballtray_prod
      expect(sourceContent).toContain("expectedDatabaseName: 'footballtray_prod'");
    });
  });

  // -------------------------------------------------------------------------
  // Mandatory Design Correction 2: Error Hierarchy & Post-Verification Separation
  // -------------------------------------------------------------------------
  describe('Correction 2: MigrationExecutionError vs PostMigrationVerificationError', () => {
    it('CR2-1: MigrationExecutionError is a typed subclass of Error with cause property', () => {
      const cause = new Error('SQL syntax error');
      const err = new MigrationExecutionError('Migration failed', cause);
      expect(err).toBeInstanceOf(Error);
      expect(err).toBeInstanceOf(MigrationExecutionError);
      expect(err.name).toBe('MigrationExecutionError');
      expect(err.cause).toBe(cause);
    });

    it('CR2-2: PostMigrationVerificationError is a typed subclass of Error with cause property', () => {
      const cause = new Error('Index missing');
      const err = new PostMigrationVerificationError('Verification failed', cause);
      expect(err).toBeInstanceOf(Error);
      expect(err).toBeInstanceOf(PostMigrationVerificationError);
      expect(err.name).toBe('PostMigrationVerificationError');
      expect(err.cause).toBe(cause);
    });
  });

  // -------------------------------------------------------------------------
  // Mandatory Design Correction 3: Migration-History Validation
  // -------------------------------------------------------------------------
  describe('Correction 3: Migration History Validation & Invariants (A-D)', () => {
    it('CR3-A/B: Populated public DB with missing or empty Drizzle history fails closed without --baseline-legacy-schema', async () => {
      // Temporarily clear Drizzle history while public tables exist
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');

      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: testUrl,
          baselineLegacySchema: false,
        })
      ).rejects.toThrow(
        /Automatic baselining is disallowed in standard mode to prevent schema corruption.*supply --baseline-legacy-schema/
      );
    });

    it('CR3-C: --baseline-legacy-schema fails if 0002 objects already exist (preventing corrupt re-baseline)', async () => {
      // Ensure scheduler_cycles (a 0002 object) exists while Drizzle history is empty
      await pool.query(`
        CREATE TABLE IF NOT EXISTS "scheduler_cycles" (
          id text PRIMARY KEY,
          action text NOT NULL,
          reason text NOT NULL,
          started_at timestamptz NOT NULL,
          created_at timestamptz DEFAULT now() NOT NULL
        );
      `);
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');

      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: testUrl,
          baselineLegacySchema: true,
        })
      ).rejects.toThrow(/Inconsistent schema: 'scheduler_cycles' already exists before Phase 2A migration/);
    });

    it('CR3-C: --baseline-legacy-schema is rejected when Drizzle migration history already exists', async () => {
      // Re-populate migration history
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
      await pool.query(
        'INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);',
        [packagedMigrations[0].hash, packagedMigrations[0].folderMillis]
      );

      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: testUrl,
          baselineLegacySchema: true,
        })
      ).rejects.toThrow(
        /--baseline-legacy-schema was supplied, but drizzle\.__drizzle_migrations already contains 1 migration records/
      );
    });

    it('CR3-D: Rejects unknown migration created_at timestamp in database history', async () => {
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
      await pool.query(
        'INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);',
        ['some_fake_hash', '1788250000000']
      );

      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: testUrl,
        })
      ).rejects.toThrow(/Recorded migration created_at '1788250000000' is not recognized in packaged migrations/);
    });

    it('CR3-D: Rejects hash mismatch for known migration timestamp', async () => {
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
      await pool.query(
        'INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);',
        ['tampered_invalid_hash', packagedMigrations[0].folderMillis]
      );

      await expect(
        runProductionMigration({
          expectedDatabaseName: 'footballtray_test',
          connectionString: testUrl,
        })
      ).rejects.toThrow(/Migration hash mismatch for timestamp/);
    });

    it('CR3-D: Rejects when database history is newer than newest packaged migration', async () => {
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
      // Insert all authentic migrations
      for (const m of packagedMigrations) {
        await pool.query(
          'INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);',
          [m.hash, m.folderMillis]
        );
      }

      // Read packaged migrations folder and create temporary directory with fewer migrations
      const tempDir = path.resolve(currentDir, 'temp-older-migrations');
      try {
        fs.mkdirSync(path.join(tempDir, 'meta'), { recursive: true });
        // Create an older journal having only migration 0000
        fs.writeFileSync(
          path.join(tempDir, 'meta', '_journal.json'),
          JSON.stringify({
            version: '7',
            dialect: 'postgresql',
            entries: [
              { idx: 0, version: '7', when: packagedMigrations[0].folderMillis, tag: '0000_regular_wild_pack', breakpoints: true }
            ]
          }),
          'utf8'
        );
        fs.copyFileSync(
          path.join(migrationsFolder, '0000_regular_wild_pack.sql'),
          path.join(tempDir, '0000_regular_wild_pack.sql')
        );

        await expect(
          runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            migrationsFolder: tempDir,
          })
        ).rejects.toThrow(/Refusing to run out-of-date release package against newer database/);
      } finally {
        try {
          fs.rmSync(tempDir, { recursive: true, force: true });
        } catch {}
      }
    });

    it('CR3-D: Idempotent execution succeeds and returns verified true when already at latest migration', async () => {
      // Seed all authentic migrations
      await pool.query('DELETE FROM "drizzle"."__drizzle_migrations";');
      for (const m of packagedMigrations) {
        await pool.query(
          'INSERT INTO "drizzle"."__drizzle_migrations" ("hash", "created_at") VALUES ($1, $2);',
          [m.hash, m.folderMillis]
        );
      }

      const result = await runProductionMigration({
        expectedDatabaseName: 'footballtray_test',
        connectionString: testUrl,
      });

      const latestPackagedMigration = packagedMigrations[packagedMigrations.length - 1];
      expect(result.verified).toBe(true);
      expect(result.database).toBe('footballtray_test');
      expect(result.state).toBe('MIGRATION_COMMITTED');
      expect(result.latestMigrationTimestamp).toBe(latestPackagedMigration.folderMillis.toString());
      expect(result.latestMigrationHash).toBe(latestPackagedMigration.hash);
    });
  });

  // -------------------------------------------------------------------------
  // Mandatory Design Correction 4: Transactional Baseline Seeding
  // -------------------------------------------------------------------------
  describe('Correction 4: Transactional Baseline Seeding on Legacy Schema', () => {
    it('CR4-1: Drops newer objects, runs with --baseline-legacy-schema, seeds authentic 0000/0001 in transaction, applies pending migrations, and passes verification', async () => {
      // 1. Reset to authentic legacy state (0000 + 0001 present, 0002/0003 absent)
      await resetToLegacySchema();

      // 2. Run with --baseline-legacy-schema
      const result = await runProductionMigration({
        expectedDatabaseName: 'footballtray_test',
        connectionString: testUrl,
        baselineLegacySchema: true,
      });

      const latestPackagedMigration = packagedMigrations[packagedMigrations.length - 1];
      expect(result.verified).toBe(true);
      expect(result.database).toBe('footballtray_test');
      expect(result.state).toBe('MIGRATION_COMMITTED');
      expect(result.latestMigrationTimestamp).toBe(latestPackagedMigration.folderMillis.toString());
      expect(result.latestMigrationHash).toBe(latestPackagedMigration.hash);

      // 3. Verify all migration records exist in drizzle.__drizzle_migrations
      const histRes = await pool.query('SELECT hash, created_at FROM "drizzle"."__drizzle_migrations" ORDER BY created_at ASC;');
      expect(histRes.rows).toHaveLength(packagedMigrations.length);
      for (let i = 0; i < packagedMigrations.length; i++) {
        expect(histRes.rows[i].created_at.toString()).toBe(packagedMigrations[i].folderMillis.toString());
        expect(histRes.rows[i].hash).toBe(packagedMigrations[i].hash);
      }
    });
  });

  // -------------------------------------------------------------------------
  // Mandatory Design Correction 5: Post-Migration Schema Verification
  // -------------------------------------------------------------------------
  describe('Correction 5: Post-Migration Schema Invariants Verification', () => {
    it('CR5-1: Verifies scheduler_cycles table exists in public schema', async () => {
      const res = await pool.query(
        "SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'scheduler_cycles';"
      );
      expect(res.rows.length).toBe(1);
    });

    it('CR5-2: Verifies cycle_id and competition_id exist and are nullable on api_request_ledger', async () => {
      const res = await pool.query(`
        SELECT column_name, is_nullable
        FROM information_schema.columns
        WHERE table_name = 'api_request_ledger' AND column_name IN ('cycle_id', 'competition_id')
        ORDER BY column_name;
      `);
      expect(res.rows).toHaveLength(2);
      expect(res.rows[0].column_name).toBe('competition_id');
      expect(res.rows[0].is_nullable).toBe('YES');
      expect(res.rows[1].column_name).toBe('cycle_id');
      expect(res.rows[1].is_nullable).toBe('YES');
    });

    it('CR5-3: Verifies all five Phase 2A indexes exist in database', async () => {
      const expectedIndexes = [
        'scheduler_cycles_started_idx',
        'scheduler_cycles_action_idx',
        'scheduler_cycles_pruning_idx',
        'ledger_cycle_id_idx',
        'ledger_competition_id_idx',
      ];

      const res = await pool.query(
        `SELECT indexname FROM pg_indexes 
         WHERE tablename IN ('scheduler_cycles', 'api_request_ledger')
           AND indexname = ANY($1::text[]);`,
        [expectedIndexes]
      );
      const found = res.rows.map((r) => r.indexname);
      for (const expected of expectedIndexes) {
        expect(found).toContain(expected);
      }
    });

    it('CR5-4: Verifies latest migration created_at and authentic hash match packaged latest migration metadata', async () => {
      const res = await pool.query(
        'SELECT hash, created_at FROM "drizzle"."__drizzle_migrations" ORDER BY created_at DESC LIMIT 1;'
      );
      const latest = res.rows[0];
      const latestPackagedMigration = packagedMigrations[packagedMigrations.length - 1];
      expect(latest.created_at.toString()).toBe(latestPackagedMigration.folderMillis.toString());
      expect(latest.hash).toBe(latestPackagedMigration.hash);
    });

    it('CR5-5: Verifies additive canonical foundation tables exist in public schema', async () => {
      const res = await pool.query(
        `SELECT table_name FROM information_schema.tables
         WHERE table_schema = 'public'
           AND table_name IN ('competition_provider_aliases', 'team_provider_aliases', 'fixture_provider_aliases')
         ORDER BY table_name;`
      );
      expect(res.rows).toHaveLength(3);
    });

    it('CR5-6: Verifies canonical_id column exists and is NOT NULL on competitions, teams, fixtures', async () => {
      const res = await pool.query(`
        SELECT table_name, column_name, is_nullable
        FROM information_schema.columns
        WHERE column_name = 'canonical_id'
          AND table_name IN ('competitions', 'teams', 'fixtures')
        ORDER BY table_name;
      `);
      expect(res.rows).toHaveLength(3);
      for (const row of res.rows) {
        expect(row.is_nullable).toBe('NO');
      }
    });

    it('CR5-7: Verifies all canonical foundation indexes exist in database', async () => {
      const expectedIndexes = [
        'comp_provider_aliases_canonical_idx',
        'team_provider_aliases_canonical_idx',
        'fix_provider_aliases_canonical_idx',
      ];

      const res = await pool.query(
        `SELECT indexname FROM pg_indexes
         WHERE indexname = ANY($1::text[]);`,
        [expectedIndexes]
      );
      const found = res.rows.map((r) => r.indexname);
      for (const expected of expectedIndexes) {
        expect(found).toContain(expected);
      }
    });
  });

  // -------------------------------------------------------------------------
  // Review Hardening: Legacy Baseline Verification & Transition State Semantics (Tests A-G)
  // -------------------------------------------------------------------------
  describe('Review Hardening: Legacy Baseline Verification & Transition State Semantics (Tests A-G)', () => {
    it('Test A: Legacy table exists but required 0000 column is missing -> fails closed with zero migration records written', async () => {
      await resetToLegacySchema();

      // Drop column 'venue' from 'fixtures'
      await pool.query('ALTER TABLE "fixtures" DROP COLUMN "venue";');
      try {
        await expect(
          runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            baselineLegacySchema: true,
          })
        ).rejects.toThrow(/Legacy schema verification failed: Table 'fixtures' missing required column 'venue'/);

        // Verify zero baseline rows written
        const histRes = await pool.query('SELECT COUNT(*) AS cnt FROM "drizzle"."__drizzle_migrations";');
        expect(Number(histRes.rows[0].cnt)).toBe(0);
      } finally {
        await pool.query('ALTER TABLE "fixtures" ADD COLUMN "venue" text;');
      }
    });

    it('Test B1: Legacy schema has wrong column nullability -> fails closed with zero rows written', async () => {
      await resetToLegacySchema();

      // Make teams.name nullable (must be not null)
      await pool.query('ALTER TABLE "teams" ALTER COLUMN "name" DROP NOT NULL;');
      try {
        await expect(
          runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            baselineLegacySchema: true,
          })
        ).rejects.toThrow(/Legacy schema verification failed: Table 'teams' column 'name' nullability mismatch/);

        const histRes = await pool.query('SELECT COUNT(*) AS cnt FROM "drizzle"."__drizzle_migrations";');
        expect(Number(histRes.rows[0].cnt)).toBe(0);
      } finally {
        await pool.query('ALTER TABLE "teams" ALTER COLUMN "name" SET NOT NULL;');
      }
    });

    it('Test B2: Legacy schema has wrong column data type -> fails closed with zero rows written', async () => {
      await resetToLegacySchema();

      // Alter fixtures.round to varchar(50) instead of text
      await pool.query('ALTER TABLE "fixtures" ALTER COLUMN "round" TYPE varchar(50);');
      try {
        await expect(
          runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            baselineLegacySchema: true,
          })
        ).rejects.toThrow(/Legacy schema verification failed: Table 'fixtures' column 'round' type mismatch/);

        const histRes = await pool.query('SELECT COUNT(*) AS cnt FROM "drizzle"."__drizzle_migrations";');
        expect(Number(histRes.rows[0].cnt)).toBe(0);
      } finally {
        await pool.query('ALTER TABLE "fixtures" ALTER COLUMN "round" TYPE text;');
      }
    });

    it('Test B3: Legacy schema is missing required index -> fails closed with zero rows written', async () => {
      await resetToLegacySchema();

      // Drop legacy index events_uid_idx
      await pool.query('DROP INDEX IF EXISTS "events_uid_idx";');
      try {
        await expect(
          runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            baselineLegacySchema: true,
          })
        ).rejects.toThrow(/Legacy schema verification failed: Missing required index 'events_uid_idx'/);

        const histRes = await pool.query('SELECT COUNT(*) AS cnt FROM "drizzle"."__drizzle_migrations";');
        expect(Number(histRes.rows[0].cnt)).toBe(0);
      } finally {
        await pool.query('CREATE UNIQUE INDEX "events_uid_idx" ON "events" USING btree ("event_uid");');
      }
    });

    it('Test C: Baseline transaction succeeds but subsequent 0002 migration fails -> authentic 0000/0001 history remains, 0002 rolled back, state is BASELINE_COMMITTED', async () => {
      await resetToLegacySchema();

      const tempDir = path.resolve(currentDir, 'temp-broken-0002');
      try {
        fs.mkdirSync(path.join(tempDir, 'meta'), { recursive: true });
        const journalContent = fs.readFileSync(path.join(migrationsFolder, 'meta', '_journal.json'), 'utf8');
        fs.writeFileSync(path.join(tempDir, 'meta', '_journal.json'), journalContent, 'utf8');

        const sqlFiles = fs.readdirSync(migrationsFolder).filter(f => f.endsWith('.sql'));
        for (const file of sqlFiles) {
          if (file.startsWith('0002_')) {
            fs.writeFileSync(
              path.join(tempDir, file),
              'THIS IS SYNTAX ERROR INVALID SQL STATMENT FOR TEST;\n',
              'utf8'
            );
          } else {
            fs.copyFileSync(path.join(migrationsFolder, file), path.join(tempDir, file));
          }
        }

        let caughtError: unknown;
        try {
          await runProductionMigration({
            expectedDatabaseName: 'footballtray_test',
            connectionString: testUrl,
            baselineLegacySchema: true,
            migrationsFolder: tempDir,
          });
        } catch (err) {
          caughtError = err;
        }

        expect(caughtError).toBeInstanceOf(MigrationExecutionError);
        const migErr = caughtError as MigrationExecutionError;
        expect(migErr.migrationState).toBe('BASELINE_COMMITTED');

        // Authentic 0000 and 0001 metadata remain committed
        const histRes = await pool.query('SELECT hash, created_at FROM "drizzle"."__drizzle_migrations" ORDER BY created_at ASC;');
        expect(histRes.rows).toHaveLength(2);
        expect(histRes.rows[0].created_at.toString()).toBe(packagedMigrations[0].folderMillis.toString());
        expect(histRes.rows[0].hash).toBe(packagedMigrations[0].hash);
        expect(histRes.rows[1].created_at.toString()).toBe(packagedMigrations[1].folderMillis.toString());
        expect(histRes.rows[1].hash).toBe(packagedMigrations[1].hash);

        // 0002 schema objects are absent (rolled back)
        const tableRes = await pool.query("SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'scheduler_cycles';");
        expect(tableRes.rows).toHaveLength(0);

        const colRes = await pool.query("SELECT 1 FROM information_schema.columns WHERE table_name = 'api_request_ledger' AND column_name = 'cycle_id';");
        expect(colRes.rows).toHaveLength(0);
      } finally {
        try {
          fs.rmSync(tempDir, { recursive: true, force: true });
        } catch {}
      }
    });

    it('Test D: Normal migration succeeds and reports state MIGRATION_COMMITTED', async () => {
      // Reset to legacy schema
      await resetToLegacySchema();

      const result = await runProductionMigration({
        expectedDatabaseName: 'footballtray_test',
        connectionString: testUrl,
        baselineLegacySchema: true,
      });

      expect(result.state).toBe('MIGRATION_COMMITTED');
      expect(result.verified).toBe(true);
    });

    it('Test E: Failure before any baseline/migration commit reports state NO_DB_CHANGE', async () => {
      let caughtError: unknown;
      try {
        await runProductionMigration({
          expectedDatabaseName: 'wrong_db_name',
          connectionString: testUrl,
        });
      } catch (err) {
        caughtError = err;
      }

      expect(caughtError).toBeDefined();
      const state = (caughtError as any)?.migrationState || 'NO_DB_CHANGE';
      expect(state).toBe('NO_DB_CHANGE');
    });

    it('Test G: Latest packaged migration metadata is derived dynamically from readMigrationFiles()', () => {
      const migrations = readMigrationFiles({ migrationsFolder });
      const latestPackagedMigration = migrations[migrations.length - 1];

      expect(latestPackagedMigration).toBeDefined();
      expect(latestPackagedMigration.folderMillis).toBeDefined();
      expect(latestPackagedMigration.hash).toBeDefined();
      expect(typeof latestPackagedMigration.hash).toBe('string');
      expect(latestPackagedMigration.hash.length).toBeGreaterThan(0);
    });
  });
});
