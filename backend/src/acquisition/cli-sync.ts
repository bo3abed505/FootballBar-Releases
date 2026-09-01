import { MockFootballProvider } from '../providers/mock.provider.js';
import { SyncService } from './sync.service.js';
import { pool } from '../db/index.js';

async function runCliSync() {
  const provider = new MockFootballProvider();
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
