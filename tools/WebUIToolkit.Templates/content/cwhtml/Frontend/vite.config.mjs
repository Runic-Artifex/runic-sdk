import { defineConfig } from "vite";

export default defineConfig({
  appType: "custom",
  publicDir: false,
  build: {
    outDir: "dist",
    emptyOutDir: true,
    target: "es2022",
    rollupOptions: {
      input: "src/main.js",
      output: {
        entryFileNames: "cwhtml.js",
        chunkFileNames: "assets/[name]-[hash].js",
        assetFileNames: (asset) =>
          asset.names.some((name) => name.endsWith(".css"))
            ? "cwhtml.css"
            : "assets/[name]-[hash][extname]",
      },
    },
  },
});
