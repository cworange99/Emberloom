import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Which API the dev server proxies to. Defaults to the usual port; set EMBERLOOM_API to point a dev
// client at a second instance — handy for trying a change without disturbing a colony already running.
const api = process.env.EMBERLOOM_API ?? 'http://localhost:5223'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: api, changeOrigin: true },
      '/ws': { target: api.replace(/^http/, 'ws'), ws: true },
      // The 3D spikes and their art kit are served by the API from outside wwwroot. Without these
      // the inspector's place links open a 404 in dev mode.
      '/spike': { target: api, changeOrigin: true },
      '/kit': { target: api, changeOrigin: true },
    },
  },
  build: {
    // Built straight into the API's wwwroot so the whole thing runs as one process.
    outDir: '../src/Emberloom.Api/wwwroot',
    emptyOutDir: true,
  },
})
