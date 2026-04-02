import { defineConfig } from 'vite';

export default defineConfig({
  root: 'client',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: false,   // preserve wwwroot/css/ and other static files
  },
  server: {
    proxy: {
      '/viz': { target: 'http://localhost:5000', ws: true },
      '/api': { target: 'http://localhost:5000' },
    },
  },
});
