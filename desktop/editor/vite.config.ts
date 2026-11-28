import { defineConfig } from 'vite';

/** Produce a fully local editor bundle without a development server or remote assets. */
export default defineConfig({
  base: './',
  build: { target: 'es2022', sourcemap: false, emptyOutDir: true },
});
