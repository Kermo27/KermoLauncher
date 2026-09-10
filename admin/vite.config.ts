import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";

const dir = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  root: dir,
  plugins: [tailwindcss(), svelte()],
  clearScreen: false,
  server: {
    port: 1422,
    strictPort: true,
    host: "127.0.0.1",
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
});
