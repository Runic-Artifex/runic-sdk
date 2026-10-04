import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

const siteOrigin =
  process.env.RUNIC_SITE_ORIGIN ?? 'https://docs.runic-artifex.eu';
const buildOutput = process.env.RUNIC_DOCS_BUILD_OUTPUT ?? 'build';

if (new URL(siteOrigin).origin !== siteOrigin) {
  throw new Error('RUNIC_SITE_ORIGIN must be an absolute URL origin');
}

export default defineConfig({
  plugins: [
    tailwindcss(),
    sveltekit({
      preprocess: vitePreprocess(),
      adapter: adapter({
        pages: buildOutput,
        assets: buildOutput,
        precompress: true,
        strict: true,
      }),
      files: { assets: 'public' },
      paths: { origin: siteOrigin },
    }),
  ],
  build: { target: 'es2022' },
  server:
    process.env.CODEX_SANDBOX === 'seatbelt'
      ? { watch: { usePolling: true } }
      : undefined,
});
