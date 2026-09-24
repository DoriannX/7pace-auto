/// <reference types="vitest/config" />
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

const page = (name: string) => fileURLToPath(new URL(name, import.meta.url))

// Deux pages, une par fenêtre Tauri : la fenêtre principale et le widget.
export default defineConfig({
  plugins: [svelte()],
  clearScreen: false,
  // La compilation Rust verrouille ses fichiers : Vite ne doit pas surveiller src-tauri.
  // L'app installée en mode front de dev charge http://127.0.0.1:1420 (dev_front.rs).
  server: { host: '127.0.0.1', port: 1420, strictPort: true, watch: { ignored: ['**/src-tauri/**'] } },
  build: {
    target: 'es2022',
    rollupOptions: { input: { main: page('index.html'), widget: page('widget.html') } },
  },
  test: { include: ['src/**/*.test.ts'] },
})
