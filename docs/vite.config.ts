import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server:
    process.env.CODEX_SANDBOX === 'seatbelt'
      ? { watch: { usePolling: true } }
      : undefined,
});
