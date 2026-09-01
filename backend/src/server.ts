import Fastify from 'fastify';
import cors from '@fastify/cors';
import etag from '@fastify/etag';
import { apiRoutes } from './api/routes.js';
import * as dotenv from 'dotenv';

dotenv.config();

export async function buildServer(opts = {}) {
  const app = Fastify({
    logger: process.env.NODE_ENV === 'test' ? false : {
      transport: {
        target: 'pino-pretty',
        options: { colorize: true },
      },
    },
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

// Start server if run directly
if (process.argv[1]?.endsWith('server.ts') || process.argv[1]?.endsWith('server.js')) {
  const port = parseInt(process.env.PORT || '4000', 10);
  const host = '0.0.0.0';

  buildServer()
    .then((app) => app.listen({ port, host }))
    .then((address) => {
      console.log(`[SERVER] FootballTray backend listening on ${address}`);
    })
    .catch((err) => {
      console.error('[SERVER] Failed to start server:', err);
      process.exit(1);
    });
}
