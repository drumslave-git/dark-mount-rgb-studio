import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The tray app: it serves the built page and answers /api (tray/HttpServer.cs).
const TRAY = 'http://localhost:4173';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    // Fixed file names: the tray app embeds exactly index.html, assets/app.js and assets/app.css
    // (tray/DarkMountStudio.csproj) and serves them uncached, so no hashes are needed.
    rolldownOptions: {
      output: { entryFileNames: 'assets/app.js', assetFileNames: 'assets/app[extname]' },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    // "npm run dev" serves the page with hot reload and passes the API on to the running tray
    // app, which only answers requests that look like they come from its own page.
    proxy: {
      '/api': {
        target: TRAY,
        changeOrigin: true,
        configure: (proxy) => proxy.on('proxyReq', (req) => req.setHeader('origin', TRAY)),
      },
    },
  },
});
