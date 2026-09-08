import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5223', changeOrigin: true },
      '/ws': { target: 'ws://localhost:5223', ws: true },
    },
  },
  build: {
    // Built straight into the API's wwwroot so the whole thing runs as one process.
    outDir: '../src/Emberloom.Api/wwwroot',
    emptyOutDir: true,
  },
})
