<script lang="ts">
  import { onMount } from "svelte";
  import { invoke } from "@tauri-apps/api/core";

  let name = $state("KermoLauncher");
  let version = $state("…");

  onMount(async () => {
    const info = await invoke<{ name: string; version: string }>("app_info");
    name = info.name;
    version = info.version;
  });
</script>

<main class="flex min-h-screen flex-col items-center justify-center bg-window text-text">
  <p class="text-sm uppercase tracking-[0.2em] text-muted">rewrite / tauri</p>
  <h1 class="mt-3 text-4xl font-semibold">{name}</h1>
  <p class="mt-2 text-muted">v{version} · Rust + Tauri + SvelteKit</p>
</main>
