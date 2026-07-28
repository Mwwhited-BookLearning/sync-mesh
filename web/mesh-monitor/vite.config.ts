/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Proxy target for /api and /hubs — SyncMesh.MeshMonitor.Api's address.
// VITE_MESHMONITOR_API_URL is set by SyncMesh.AppHost (AddViteApp +
// WithEnvironment) when this dev server is orchestrated by Aspire, and
// carries the backend's actual dynamically-assigned endpoint — read here
// via plain `process.env` since this file runs in Node at dev-server
// startup, not `import.meta.env` (that's for browser-side application
// code). Falls back to the default launch profile port for the
// standalone `npm run dev` workflow (see UI-ARCHITECTURE.md's "Dev
// workflow" section) when nothing sets it.
const meshMonitorApiUrl = process.env.VITE_MESHMONITOR_API_URL ?? 'http://localhost:5129'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  test: {
    environment: 'jsdom',
    include: ['tests/unit/**/*.spec.ts'],
  },
  server: {
    // This keeps every browser-side request same-origin (Vite's own dev
    // port), so the API needs no CORS configuration at all.
    proxy: {
      '/api': meshMonitorApiUrl,
      '/hubs': { target: meshMonitorApiUrl, ws: true },
    },
  },
})
