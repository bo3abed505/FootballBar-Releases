import { MockFootballProvider } from '../providers/mock.provider.js';
import { SyncService } from './sync.service.js';
import { pool } from '../db/index.js';

async function runCliSync() {
  const args = process.argv.slice(2);
  const isReset = args.includes('--reset') || args.includes('reset');
  const isSimulateGoal = args.includes('--simulate-goal') || args.includes('goal') || args.includes('--goal');

  const provider = new MockFootballProvider();

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

  const syncService = new SyncService(provider);

  console.log('====================================================');
  console.log('FOOTBALLTRAY DATA ACQUISITION & SYNCHRONIZATION');
  console.log('====================================================');

  try {
    const summary = await syncService.sync();

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
