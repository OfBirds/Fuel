/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import fs from 'node:fs';
import path from 'node:path';

const APP_VERSION = process.env.APP_VERSION || 'dev';

// The service worker lives in /public (served verbatim), so Vite's `define` can't reach it.
// Stamp the build version into its cache name — in dev via middleware, in the production build
// by patching the emitted dist/sw.js — so each release uses a fresh cache name and the SW's
// activate handler evicts the previous cache instead of piling up stale hashed assets.
function stampServiceWorker(): Plugin {
  const stamp = (src: string) => src.replaceAll('__APP_VERSION__', APP_VERSION);
  return {
    name: 'stamp-service-worker',
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        if (req.url?.split('?')[0] !== '/sw.js') return next();
        res.setHeader('Content-Type', 'application/javascript');
        res.end(stamp(fs.readFileSync(path.resolve(process.cwd(), 'public/sw.js'), 'utf8')));
      });
    },
    writeBundle(options) {
      const out = path.resolve(options.dir ?? 'dist', 'sw.js');
      if (fs.existsSync(out)) fs.writeFileSync(out, stamp(fs.readFileSync(out, 'utf8')));
    },
  };
}

export default defineConfig({
  plugins: [react(), stampServiceWorker()],
  // Baked in at build time from the APP_VERSION env var (set by CI / Dockerfile).
  define: {
    __APP_VERSION__: JSON.stringify(APP_VERSION),
  },
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: 'http://localhost:5200',
        changeOrigin: true
      }
    }
  },
  test: {
    environment: 'jsdom',
    setupFiles: 'src/test/setup.ts',
  }
});
