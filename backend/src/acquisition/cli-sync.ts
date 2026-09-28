import { createProvider, ProviderConfigError } from './provider-factory.js';
import { getTargetCompetitionIds, CompetitionConfigError } from './competition-config.js';
import { MockFootballProvider } from '../providers/mock.provider.js';
import { FixtureFilter } from '../providers/football-provider.js';
import { SyncService } from './sync.service.js';
import { pool } from '../db/index.js';

/**
 * CLI synchronization entry point.
 *
 * Modes (controlled by CLI args and environment variables):
 *
 *   npm run dev:sync              ACTIVE_PROVIDER defaults to "mock"
 *   npm run dev:sync:goal         mock + simulate goal
 *   npm run dev:sync:reset        mock + clear persistent state
 *   npm run sync:live             ACTIVE_PROVIDER=api_football (requires ALLOW_LIVE_PROVIDER_REQUESTS=true + key)
 */
async function runCliSync() {
  const args = process.argv.slice(2);
  const isReset = args.includes('--reset') || args.includes('reset');
  const isSimulateGoal = args.includes('--simulate-goal') || args.includes('goal') || args.includes('--goal');

  // -----------------------------------------------------------------------
  // Parse competition allowlist (safe to do before provider construction)
  // -----------------------------------------------------------------------
  let targetCompIds: number[] = [];
  try {
    targetCompIds = getTargetCompetitionIds();
  } catch (err) {
    if (err instanceof CompetitionConfigError) {
      console.error(`[CONFIG ERROR] ${err.message}`);
      process.exit(1);
    }
    throw err;
  }

  // -----------------------------------------------------------------------
  // Construct provider — fails closed for api_football without key/flag
  // -----------------------------------------------------------------------
  let provider;
  try {
    provider = createProvider();
  } catch (err) {
    if (err instanceof ProviderConfigError) {
      console.error(`[CONFIG ERROR] ${err.message}`);
      process.exit(1);
    }
    throw err;
  }

  // -----------------------------------------------------------------------
  // Fail-closed enforcement for API-Football:
  // Disallow unfiltered global queries in live mode to protect daily quota.
  // -----------------------------------------------------------------------
  if (provider.providerName === 'api_football' && targetCompIds.length === 0) {
    console.error(
      '[CONFIG ERROR] TARGET_COMPETITION_IDS must be configured with at least one competition ID when running with ACTIVE_PROVIDER=api_football. ' +
      'Unfiltered global fixture queries are rejected to prevent rapid quota depletion.'
    );
    process.exit(1);
  }

  if (targetCompIds.length > 0) {
    console.log(`[CONFIG] Competition allowlist: ${targetCompIds.join(', ')}`);
  }

  console.log(`[SYNC] Using provider: ${provider.providerName}`);

  // -----------------------------------------------------------------------
  // Mock-only simulation helpers (strongly typed via instanceof)
  // -----------------------------------------------------------------------
  if (provider instanceof MockFootballProvider) {
    if (isReset) {
      console.log('[CLI_SYNC] Resetting mock development state back to baseline...');
      provider.clearPersistentState();
      provider.reset();
    } else if (isSimulateGoal) {
      console.log('[CLI_SYNC] Simulating match event: Barcelona goal at 89th minute (2-1)...');
      provider.simulateGoal('fix_elclasico_2026', true, 89, {
        name: 'L. Martinez (Dev Player)',
        id: 'ply_bar_10',
      });
    }
  } else if (isReset || isSimulateGoal) {
    console.warn(`[CLI_SYNC] --reset / --simulate-goal are only supported for the mock provider. Ignoring.`);
  }

  const syncService = new SyncService(provider);

  console.log('====================================================');
  console.log('FOOTBALLTRAY DATA ACQUISITION & SYNCHRONIZATION');
  console.log('====================================================');

  const dateArg = args.find((a) => /^\d{4}-\d{2}-\d{2}$/.test(a));
  const targetDate = dateArg ?? new Date().toISOString().slice(0, 10);
  console.log(`[SYNC] Target date: ${targetDate}`);

  const filter: FixtureFilter = {
    date: targetDate,
    competitionIds: targetCompIds.length > 0 ? targetCompIds : undefined,
  };

  try {
    const summary = await syncService.sync(filter);

    console.log(`provider: ${summary.provider}`);
    console.log(`fixtures discovered: ${summary.fixturesDiscovered}`);
    console.log(`fixtures inserted: ${summary.fixturesInserted}`);
    console.log(`fixtures updated: ${summary.fixturesUpdated}`);
    console.log(`events inserted: ${summary.eventsInserted}`);
    console.log(`events skipped as duplicates: ${summary.eventsSkippedDuplicates}`);
    console.log(`changes detected: ${summary.hasChanges ? 'YES' : 'NO'}`);
    console.log('====================================================');
  } finally {
    await pool.end();
  }
}

runCliSync().catch((err) => {
  console.error('[SYNC] Fatal error during synchronization:', err);
  process.exit(1);
});


