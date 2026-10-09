import Fastify from 'fastify';
import cors from '@fastify/cors';
import { apiRoutes } from './api/routes.js';
import * as dotenv from 'dotenv';
import { AppRuntime } from './runtime/app-runtime.js';

dotenv.config();

export async function buildServer(opts = {}) {
  const app = Fastify({
    logger: process.env.NODE_ENV === 'test' ? false : (process.env.NODE_ENV === 'production' ? { level: 'warn' } : true),
    ...opts,
  });

  // Enable CORS
  await app.register(cors, {
    origin: true,
  });

  // Register API Routes
  await app.register(apiRoutes);

  return app;
}

export { AppRuntime };

// Top-level crash and unhandled error handling
process.on('uncaughtException', (err: Error) => {
  console.error(`[FATAL:UNCAUGHT_EXCEPTION] ${err.name}: ${err.message}`, err.stack);
});

process.on('unhandledRejection', (reason: unknown) => {
  const msg = reason instanceof Error ? reason.message : String(reason);
  console.error(`[FATAL:UNHANDLED_REJECTION] ${msg}`);
});

// Start server and background acquisition runtime if run directly
if (process.argv[1]?.endsWith('server.ts') || process.argv[1]?.endsWith('server.js')) {
  buildServer()
    .then((app) => {
      const runtime = new AppRuntime(app);
      return runtime.start();
    })
    .catch((err) => {
      console.error('[SERVER] Failed to start application runtime:', err);
      process.exit(1);
    });
}

