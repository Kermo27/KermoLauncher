<script lang="ts">
  import { getDownloadTasks, getLibrary } from "$lib/api";
  import { t } from "$lib/i18n";
  import { session } from "$lib/session.svelte";
  import type { DownloadTask, LibraryItem } from "$lib/types";
  import { onMount } from "svelte";
  import DownloadsBar from "./DownloadsBar.svelte";
  import GamePage from "./GamePage.svelte";
  import Library from "./Library.svelte";
  import Settings from "./Settings.svelte";

  let items = $state<LibraryItem[]>([]);
  let tasks = $state<DownloadTask[]>([]);

  const activeCount = $derived(
    new Set([
      ...items
        .filter((item) => {
          const status = item.local?.status;
          return status === "Downloading" || status === "Installing" || status === "Paused";
        })
        .map((item) => item.game.id),
      ...tasks
        .filter((task) => task.status === "Queued" || task.status === "Downloading" || task.status === "Paused")
        .map((task) => task.game_id),
    ]).size,
  );

  function goLibrary() {
    session.view = "library";
    session.gameId = null;
  }

  function goSettings() {
    session.view = "settings";
    session.gameId = null;
  }

  async function load() {
    try {
      items = await getLibrary();
      tasks = await getDownloadTasks();
    } catch {
      /* polling */
    }
  }

  onMount(() => {
    void load();
    const timer = setInterval(load, 1000);
    return () => clearInterval(timer);
  });
</script>

<div class="flex h-screen overflow-hidden">
  <nav class="flex w-[4.75rem] shrink-0 flex-col items-center gap-2 border-r border-border bg-sidebar py-3">
    <img src="/favicon.png" alt="KermoLauncher" class="mb-2 h-8 w-8 rounded-md" />
    <button
      class="flex w-[3.6rem] flex-col items-center gap-1 rounded-lg px-1 py-2 text-[10px] font-semibold tracking-wide
        {session.view === 'library' ? 'bg-raised text-text' : 'text-muted hover:bg-raised/70 hover:text-text'}"
      onclick={goLibrary}
    >
      <svg class="h-5 w-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
        <rect x="3" y="3" width="8" height="8" rx="1.5" />
        <rect x="13" y="3" width="8" height="8" rx="1.5" />
        <rect x="3" y="13" width="8" height="8" rx="1.5" />
        <rect x="13" y="13" width="8" height="8" rx="1.5" />
      </svg>
      {t("Nav.Library")}
    </button>
    <button
      class="flex w-[3.6rem] flex-col items-center gap-1 rounded-lg px-1 py-2 text-[10px] font-semibold tracking-wide
        {session.view === 'settings' ? 'bg-raised text-text' : 'text-muted hover:bg-raised/70 hover:text-text'}"
      onclick={goSettings}
    >
      <svg class="h-5 w-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9c0 .7.4 1.3 1 1.5H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z" />
      </svg>
      {t("Nav.Settings")}
    </button>
    <div class="mt-auto flex flex-col items-center gap-2">
      {#if activeCount > 0}
        <span class="rounded-full bg-accent px-2 py-0.5 text-[10px] font-bold text-white">{activeCount}</span>
      {/if}
      {#if session.version}
        <span class="pb-1 text-[10px] font-medium tracking-wide text-muted tabular-nums">v{session.version}</span>
      {/if}
    </div>
  </nav>
  <div class="flex min-w-0 flex-1 flex-col bg-window">
    <div class="min-h-0 min-w-0 flex-1 overflow-x-hidden overflow-y-auto">
      {#if session.gameId}
        <GamePage />
      {:else if session.view === "settings"}
        <Settings />
      {:else}
        <Library />
      {/if}
    </div>
    <DownloadsBar {items} {tasks} onChanged={load} />
  </div>
</div>
