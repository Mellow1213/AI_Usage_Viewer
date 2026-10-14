import { defineConfig } from 'vite';
export default defineConfig({
  base: './',
  build: {
    outDir: 'dist', emptyOutDir: true,
    rollupOptions: { onwarn(warning, warn) {
      // React Server Component directives have no meaning in this local client-only app.
      if (warning.code === 'MODULE_LEVEL_DIRECTIVE' && warning.message.includes('use client')) return;
      warn(warning);
    } }
  }
});
