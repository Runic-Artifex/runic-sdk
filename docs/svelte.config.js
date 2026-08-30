import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

const siteOrigin =
  process.env.RUNIC_SITE_ORIGIN ?? 'https://docs.runic-artifex.eu';
const buildOutput = process.env.RUNIC_DOCS_BUILD_OUTPUT ?? 'build';

if (new URL(siteOrigin).origin !== siteOrigin) {
  throw new Error('RUNIC_SITE_ORIGIN must be an absolute URL origin');
}

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter({
      pages: buildOutput,
      assets: buildOutput,
      precompress: true,
      strict: true,
    }),
    files: { assets: 'public' },
    prerender: {
      origin: siteOrigin,
    },
  },
};

export default config;
