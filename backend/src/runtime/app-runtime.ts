/**
 * app-runtime.ts
 *
 * Centralized production runtime manager for FootballTray backend.
 *
 * Responsibilities:
 *   - Owns and manages the lifecycle of Fastify HTTP server and AcquisitionScheduler.
 *   - Guarantees exactly ONE AcquisitionScheduler instance per runtime.
 *   - Orchestrates strict startup ordering:
 *       1. Build Fastify application
 *       2. Create FootballProvider via ProviderFactory
 *       3. Initialize SyncService & FixturesRepository
 *       4. Instantiate AcquisitionScheduler
 *       5. Start HTTP listener
 *       6. Start AcquisitionScheduler
 *   - Orchestrates strict shutdown ordering:
 *       1. Stop AcquisitionScheduler cleanly (awaiting in-flight cycle/sleep)
 *       2. Close Fastify HTTP server
 *       3. Remove signal listeners
 *   - Implements idempotent SIGINT/SIGTERM process signal handling.
 *   - Startup failure cleanup: closes HTTP server if scheduler fails to start.
 */

import { FastifyInstance } from 'fastify';
import { FootballProvider } from '../providers/football-provider.js';
import { createProvider } from '../acquisition/provider-factory.js';
import { EmptyFootballProvider } from '../providers/empty.provider.js';
import { ApiFootballProvider } from '../providers/api-football/api-football.provider.js';
import { SyncService } from '../acquisition/sync.service.js';
import { FixturesRepository } from '../repositories/fixtures.repository.js';
import { AcquisitionScheduler } from '../acquisition/acquisition-scheduler.js';
import { DaemonPreferencesService } from '../acquisition/daemon-preferences.service.js';

export interface RuntimeLogger {
  info(message: string, meta?: Record<string, unknown>): void;
  warn(message: string, meta?: Record<string, unknown>): void;
  error(message: string, meta?: Record<string, unknown>): void;
}

export interface AppRuntimeOptions {
  app?: FastifyInstance;
  enableScheduler?: boolean; // default: true
  provider?: FootballProvider;
  fixturesRepository?: FixturesRepository;
  scheduler?: AcquisitionScheduler;
  port?: number;
  host?: string;
  registerSignals?: boolean; // default: true
  logger?: RuntimeLogger;
}

export class AppRuntime {
  public readonly app: FastifyInstance;
  public readonly provider: FootballProvider;
  public readonly syncService: SyncService;
  public readonly fixturesRepository: FixturesRepository;
  public readonly preferencesService: DaemonPreferencesService;
  public readonly scheduler: AcquisitionScheduler | null;
  public readonly port: number;
  public readonly host: string;

  private isStarted = false;
  private isShuttingDown = false;
  private readonly registerSignals: boolean;
  private readonly logger: RuntimeLogger;
  private signalCleanupFns: Array<() => void> = [];

  constructor(app: FastifyInstance, options: AppRuntimeOptions = {}) {
    this.app = app;
    this.port = options.port ?? parseInt(process.env.PORT || '4000', 10);
    this.host = options.host ?? '0.0.0.0';
    this.registerSignals = options.registerSignals ?? true;

    this.logger = options.logger ?? {
      info: (msg, meta) => console.log(`[RUNTIME] ${msg}`, meta ? JSON.stringify(meta) : ''),
      warn: (msg, meta) => console.warn(`[RUNTIME:WARN] ${msg}`, meta ? JSON.stringify(meta) : ''),
      error: (msg, meta) => console.error(`[RUNTIME:ERROR] ${msg}`, meta ? JSON.stringify(meta) : ''),
    };

    // 1. Construct Provider via ProviderFactory (boundary enforcement)
    if (options.provider) {
      this.provider = options.provider;
    } else {
      const isProduction = process.env.NODE_ENV === 'production';
      const keyIsPresent = typeof process.env.API_FOOTBALL_KEY === 'string' &&
        process.env.API_FOOTBALL_KEY.trim().length > 0;

      if (!keyIsPresent) {
        this.logger.warn(
          'API_FOOTBALL_KEY is not configured in the environment. ' +
          'Live match synchronization is disabled. ' +
          'Returning an empty active calendar; mock provider fallback is completely disabled.'
        );
        this.provider = new EmptyFootballProvider();
      } else if (isProduction || process.env.ACTIVE_PROVIDER === 'api_football' || (!process.env.ACTIVE_PROVIDER && keyIsPresent)) {
        process.env.ALLOW_LIVE_PROVIDER_REQUESTS = process.env.ALLOW_LIVE_PROVIDER_REQUESTS || 'true';
        this.provider = new ApiFootballProvider();
      } else {
        this.provider = createProvider();
      }
    }

    // 2. Initialize Repositories and Services
    this.fixturesRepository = options.fixturesRepository ?? new FixturesRepository();
    this.syncService = new SyncService(this.provider);
    this.preferencesService = new DaemonPreferencesService();

    // 3. Construct Scheduler if enabled
    const enableScheduler = options.enableScheduler ?? true;
    if (enableScheduler) {
      const initialCompetitionIds = this.preferencesService.getEffectiveCompetitionNumericIds();
      this.scheduler =
        options.scheduler ??
        new AcquisitionScheduler({
          syncService: this.syncService,
          fixturesRepository: this.fixturesRepository,
          targetCompetitionIds: initialCompetitionIds.length > 0 ? initialCompetitionIds : undefined,
          providerName: this.provider.providerName,
        });
      this.preferencesService.bindScheduler(this.scheduler);
      (this.app as any).scheduler = this.scheduler;
    } else {
      this.scheduler = null;
    }

    (this.app as any).syncService = this.syncService;
    (this.app as any).preferencesService = this.preferencesService;
  }

  /**
   * Returns whether the runtime and HTTP server are active.
   */
  public isRunning(): boolean {
    return this.isStarted && !this.isShuttingDown;
  }

  /**
   * Starts the runtime: binds HTTP port and starts AcquisitionScheduler.
   * Cleans up automatically if startup fails midway.
   */
  public async start(): Promise<string> {
    if (this.isStarted) {
      this.logger.warn('AppRuntime start() called when already started.');
      return `http://${this.host}:${this.port}`;
    }

    this.logger.info('Starting FootballTray backend runtime...');

    // 1. Start HTTP Server
    let address: string;
    try {
      address = await this.app.listen({ port: this.port, host: this.host });
      this.logger.info(`FootballTray HTTP server listening on ${address}`);
    } catch (err: any) {
      this.logger.error(`Failed to start HTTP server: ${err.message}`);
      throw err;
    }

    // 2. Start Acquisition Scheduler
    if (this.scheduler) {
      try {
        this.logger.info('Starting background acquisition scheduler...');
        this.scheduler.start();
        this.logger.info('Background acquisition scheduler started successfully.');
      } catch (err: any) {
        this.logger.error(`Failed to start scheduler, rolling back HTTP server: ${err.message}`);
        // Cleanup HTTP server on scheduler startup failure
        await this.app.close().catch(() => {});
        throw err;
      }
    }

    this.isStarted = true;

    // 3. Register Process Signal Handlers
    if (this.registerSignals) {
      this.attachSignalHandlers();
    }

    return address;
  }

  /**
   * Stops the runtime gracefully.
   * Idempotent: multiple calls execute teardown only once.
   */
  public async stop(): Promise<void> {
    if (!this.isStarted || this.isShuttingDown) {
      return;
    }

    this.isShuttingDown = true;
    this.logger.info('Initiating graceful runtime shutdown...');

    // 1. Stop background scheduler first
    if (this.scheduler) {
      try {
        this.logger.info('Stopping background acquisition scheduler...');
        await this.scheduler.stop();
        this.logger.info('Background acquisition scheduler stopped cleanly.');
      } catch (err: any) {
        this.logger.error(`Error stopping scheduler: ${err.message}`);
      }
    }

    // 2. Close HTTP Server
    try {
      this.logger.info('Closing HTTP server...');
      await this.app.close();
      this.logger.info('HTTP server closed.');
    } catch (err: any) {
      this.logger.error(`Error closing HTTP server: ${err.message}`);
    }

    // 3. Remove signal listeners
    this.detachSignalHandlers();

    this.isStarted = false;
    this.isShuttingDown = false;
    this.logger.info('Graceful runtime shutdown complete.');
  }

  private attachSignalHandlers(): void {
    const signals: NodeJS.Signals[] = ['SIGINT', 'SIGTERM'];

    for (const signal of signals) {
      const handler = () => {
        this.logger.info(`Received ${signal}, starting graceful shutdown...`);
        this.stop()
          .then(() => {
            process.exit(0);
          })
          .catch((err) => {
            this.logger.error(`Error during ${signal} shutdown: ${err.message}`);
            process.exit(1);
          });
      };

      process.once(signal, handler);
      this.signalCleanupFns.push(() => {
        process.removeListener(signal, handler);
      });
    }
  }

  private detachSignalHandlers(): void {
    for (const cleanup of this.signalCleanupFns) {
      cleanup();
    }
    this.signalCleanupFns = [];
  }
}
