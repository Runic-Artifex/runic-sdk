import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

const siteOrigin =
  process.env.RUNIC_SITE_ORIGIN ?? 'https://docs.runic-artifex.eu';

if (new URL(siteOrigin).origin !== siteOrigin) {
  throw new Error('RUNIC_SITE_ORIGIN must be an absolute URL origin');
}

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter({
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
