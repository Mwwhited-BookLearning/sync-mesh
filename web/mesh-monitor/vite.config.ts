/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  test: {
    environment: 'jsdom',
    include: ['tests/unit/**/*.spec.ts'],
  },
  server: {
    // Proxies to SyncMesh.MeshMonitor.Api's default "http" launch profile
    // port (see src/SyncMesh.MeshMonitor.Api/Properties/launchSettings.json).
    // This keeps every browser-side request same-origin (Vite's own dev
    // port), so the API needs no CORS configuration at all.
    proxy: {
      '/api': 'http://localhost:5129',
      '/hubs': { target: 'http://localhost:5129', ws: true },
    },
  },
})
