import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  build: { target: 'es2022' },
  server:
    process.env.CODEX_SANDBOX === 'seatbelt'
      ? { watch: { usePolling: true } }
      : undefined,
});
