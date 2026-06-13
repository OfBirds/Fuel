/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Baked in at build time from the APP_VERSION env var (set by CI / Dockerfile).
  define: {
    __APP_VERSION__: JSON.stringify(process.env.APP_VERSION || 'dev'),
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
