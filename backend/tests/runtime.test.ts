import { describe, it, expect, vi, beforeEach } from 'vitest';
import { AppRuntime } from '../src/runtime/app-runtime.js';
import { buildServer } from '../src/server.js';
import { MockFootballProvider } from '../src/providers/mock.provider.js';
import { AcquisitionScheduler } from '../src/acquisition/acquisition-scheduler.js';

describe('Milestone 8 Work Package 2 (Phase 4) - AppRuntime Lifecycle Integration', () => {
  const silentLogger = {
    info: () => {},
    warn: () => {},
    error: () => {},
  };

  // ============================================================
  // 1. Ownership & Construction
  // ============================================================
  describe('Runtime Ownership & Single Scheduler Guarantee', () => {
    it('creates exactly one AcquisitionScheduler when enableScheduler is true', async () => {
      const app = await buildServer();
      const runtime = new AppRuntime(app, {
        provider: new MockFootballProvider(),
        enableScheduler: true,
        registerSignals: false,
        logger: silentLogger,
      });

      expect(runtime.scheduler).not.toBeNull();
      expect(runtime.scheduler).toBeInstanceOf(AcquisitionScheduler);
    });

    it('does NOT instantiate an AcquisitionScheduler when enableScheduler is false', async () => {
      const app = await buildServer();
      const runtime = new AppRuntime(app, {
        provider: new MockFootballProvider(),
        enableScheduler: false,
        registerSignals: false,
        logger: silentLogger,
      });

      expect(runtime.scheduler).toBeNull();
    });

    it('buildServer() alone does NOT instantiate or start an AcquisitionScheduler', async () => {
      const app = await buildServer();
      // Fastify instance alone has no scheduler attached
      expect((app as any).scheduler).toBeUndefined();
      await app.close();
    });
  });

  // ============================================================
  // 2. Startup Lifecycle & Ordering
  // ============================================================
  describe('Startup Lifecycle & Failure Cleanup', () => {
    it('starts HTTP listener first, then starts AcquisitionScheduler', async () => {
      const executionOrder: string[] = [];

      const mockApp: any = {
        listen: vi.fn().mockImplementation(async () => {
          executionOrder.push('http_listen');
          return 'http://127.0.0.1:4000';
        }),
        close: vi.fn().mockImplementation(async () => {
          executionOrder.push('http_close');
        }),
      };

      const mockScheduler: any = {
        start: vi.fn().mockImplementation(() => {
          executionOrder.push('scheduler_start');
        }),
        stop: vi.fn().mockImplementation(async () => {
          executionOrder.push('scheduler_stop');
        }),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      expect(runtime.isRunning()).toBe(false);
      await runtime.start();

      expect(runtime.isRunning()).toBe(true);
      expect(executionOrder).toEqual(['http_listen', 'scheduler_start']);
      expect(mockScheduler.start).toHaveBeenCalledTimes(1);

      await runtime.stop();
    });

    it('rolls back and closes HTTP server if scheduler fails to start', async () => {
      const mockApp: any = {
        listen: vi.fn().mockResolvedValue('http://127.0.0.1:4000'),
        close: vi.fn().mockResolvedValue(undefined),
      };

      const mockScheduler: any = {
        start: vi.fn().mockImplementation(() => {
          throw new Error('Scheduler configuration failure');
        }),
        stop: vi.fn().mockResolvedValue(undefined),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      await expect(runtime.start()).rejects.toThrow('Scheduler configuration failure');

      // Assert HTTP server was closed cleanly following scheduler startup error
      expect(mockApp.close).toHaveBeenCalledTimes(1);
      expect(runtime.isRunning()).toBe(false);
    });

    it('start() is idempotent when called repeatedly on an active runtime', async () => {
      const mockApp: any = {
        listen: vi.fn().mockResolvedValue('http://127.0.0.1:4000'),
        close: vi.fn().mockResolvedValue(undefined),
      };

      const mockScheduler: any = {
        start: vi.fn(),
        stop: vi.fn().mockResolvedValue(undefined),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      await runtime.start();
      await runtime.start(); // Duplicate start call

      expect(mockApp.listen).toHaveBeenCalledTimes(1);
      expect(mockScheduler.start).toHaveBeenCalledTimes(1);

      await runtime.stop();
    });
  });

  // ============================================================
  // 3. Graceful Shutdown & Teardown Ordering
  // ============================================================
  describe('Graceful Shutdown & Ordering', () => {
    it('stops AcquisitionScheduler before closing HTTP server', async () => {
      const teardownOrder: string[] = [];

      const mockApp: any = {
        listen: vi.fn().mockResolvedValue('http://127.0.0.1:4000'),
        close: vi.fn().mockImplementation(async () => {
          teardownOrder.push('http_close');
        }),
      };

      const mockScheduler: any = {
        start: vi.fn(),
        stop: vi.fn().mockImplementation(async () => {
          teardownOrder.push('scheduler_stop');
        }),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      await runtime.start();
      await runtime.stop();

      expect(teardownOrder).toEqual(['scheduler_stop', 'http_close']);
      expect(mockScheduler.stop).toHaveBeenCalledTimes(1);
      expect(mockApp.close).toHaveBeenCalledTimes(1);
      expect(runtime.isRunning()).toBe(false);
    });

    it('stop() is idempotent and handles multiple concurrent or sequential calls cleanly', async () => {
      const mockApp: any = {
        listen: vi.fn().mockResolvedValue('http://127.0.0.1:4000'),
        close: vi.fn().mockResolvedValue(undefined),
      };

      const mockScheduler: any = {
        start: vi.fn(),
        stop: vi.fn().mockResolvedValue(undefined),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      await runtime.start();

      // Trigger multiple stop calls simultaneously
      await Promise.all([runtime.stop(), runtime.stop(), runtime.stop()]);

      expect(mockScheduler.stop).toHaveBeenCalledTimes(1);
      expect(mockApp.close).toHaveBeenCalledTimes(1);
    });

    it('stop() is a safe no-op if runtime was never started', async () => {
      const mockApp: any = {
        listen: vi.fn(),
        close: vi.fn(),
      };

      const mockScheduler: any = {
        start: vi.fn(),
        stop: vi.fn(),
      };

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: false,
        logger: silentLogger,
      });

      await runtime.stop();

      expect(mockScheduler.stop).not.toHaveBeenCalled();
      expect(mockApp.close).not.toHaveBeenCalled();
    });
  });

  // ============================================================
  // 4. Process Signal Registration & Cleanup
  // ============================================================
  describe('Process Signal Handling', () => {
    it('registers SIGINT and SIGTERM listeners on start and cleans them up on stop', async () => {
      const mockApp: any = {
        listen: vi.fn().mockResolvedValue('http://127.0.0.1:4000'),
        close: vi.fn().mockResolvedValue(undefined),
      };

      const mockScheduler: any = {
        start: vi.fn(),
        stop: vi.fn().mockResolvedValue(undefined),
      };

      const sigintListenersBefore = process.listenerCount('SIGINT');
      const sigtermListenersBefore = process.listenerCount('SIGTERM');

      const runtime = new AppRuntime(mockApp, {
        provider: new MockFootballProvider(),
        scheduler: mockScheduler,
        registerSignals: true,
        logger: silentLogger,
      });

      await runtime.start();

      expect(process.listenerCount('SIGINT')).toBe(sigintListenersBefore + 1);
      expect(process.listenerCount('SIGTERM')).toBe(sigtermListenersBefore + 1);

      await runtime.stop();

      expect(process.listenerCount('SIGINT')).toBe(sigintListenersBefore);
      expect(process.listenerCount('SIGTERM')).toBe(sigtermListenersBefore);
    });
  });
});
