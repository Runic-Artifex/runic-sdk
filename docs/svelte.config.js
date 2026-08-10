import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

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
      origin: 'https://runic-artifex.eu',
    },
  },
};

export default config;
