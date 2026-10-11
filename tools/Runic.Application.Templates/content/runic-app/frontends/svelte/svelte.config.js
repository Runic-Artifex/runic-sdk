import { vitePreprocess } from "@sveltejs/vite-plugin-svelte";

// vite-plugin-svelte and svelte-check both read this file. vitePreprocess()
// compiles the <script lang="ts"> blocks of the components.
export default {
  preprocess: vitePreprocess()
};
