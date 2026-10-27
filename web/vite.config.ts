import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

// Pages build into the host's wwwroot (served by Aion.Host with script-src 'self': no inline
// scripts or styles, nothing from a CDN). wardrobe-viewer.js stays a hand-written module there
// (shared with tools/wardrobe.html) and is loaded at runtime, so the output dir is not emptied.
// `npm run dev` proxies the API and wardrobe files to a host on 127.0.0.1:8780.
const host = 'http://127.0.0.1:8780';
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: resolve(__dirname, '../server/Aion.Host/wwwroot'),
    emptyOutDir: false,
    assetsDir: 'assets',
    rollupOptions: { input: { index: resolve(__dirname, 'index.html'), admin: resolve(__dirname, 'admin.html') } },
  },
  server: { proxy: { '/api': host, '/wardrobe': host, '/wardrobe-viewer.js': host } },
});
