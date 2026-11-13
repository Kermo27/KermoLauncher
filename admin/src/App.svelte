<script lang="ts">
  import { listen } from "@tauri-apps/api/event";
  import { open } from "@tauri-apps/plugin-dialog";
  import { loadState, saveState, scanFolder, writeLibrary } from "./api";
  import { t } from "./i18n";
  import type { AdminGame, ScanProgress } from "./types";
  import { onMount } from "svelte";

  let scanPath = $state("");
  let games = $state<AdminGame[]>([]);
  let selectedId = $state<string | null>(null);
  let status = $state("");
  let error = $state("");
  let busy = $state(false);
  let scanProgress = $state<ScanProgress | null>(null);
  let loaded = $state(false);

  const scanPct = $derived(
    scanProgress && scanProgress.total > 0
      ? Math.min(100, (100 * scanProgress.completed) / scanProgress.total)
      : 0,
  );

  const idx = $derived(games.findIndex((g) => g.id === selectedId));

  onMount(() => {
    void boot();
    const unlisten = listen<ScanProgress>("admin-scan", (ev) => {
      scanProgress = ev.payload;
    });
    return () => {
      void unlisten.then((fn) => fn());
    };
  });

  $effect(() => {
    if (!loaded) return;
    const state = {
      scanFolder: scanPath,
      destFolder: "",
      games,
      selectedId,
    };
    const folder = scanPath.trim();
    const snapshot = games;
    const timer = setTimeout(() => {
      void saveState(state).catch(() => {});
      if (busy || !folder || snapshot.length === 0) return;
      void writeLibrary(folder, snapshot).catch((e) => {
        error = t("ErrWrite", String(e));
      });
    }, 700);
    return () => clearTimeout(timer);
  });

  async function boot() {
    try {
      const state = await loadState();
      scanPath = state.scanFolder;
      games = state.games ?? [];
      selectedId = state.selectedId ?? games[0]?.id ?? null;
    } catch {
      /* first launch */
    } finally {
      loaded = true;
    }
  }

  async function browse() {
    const dir = await open({ directory: true, multiple: false });
    if (typeof dir !== "string" || !dir) return;
    scanPath = dir;
  }

  async function scan() {
    if (!scanPath.trim() || busy) return;
    busy = true;
    error = "";
    status = "";
    scanProgress = null;
    try {
      games = await scanFolder(scanPath.trim(), games);
      if (!games.some((g) => g.id === selectedId)) {
        selectedId = games[0]?.id ?? null;
      }
      status = t("ScanDone", games.length);
    } catch (e) {
      error = t("ErrScan", String(e));
    } finally {
      busy = false;
      scanProgress = null;
    }
  }

  function csv(values: string[]) {
    return values.join(", ");
  }

  function setCsv(game: AdminGame, field: "tags" | "dependencies", value: string) {
    game[field] = value
      .split(",")
      .map((s) => s.trim())
      .filter(Boolean);
  }

</script>

<div class="flex min-h-screen flex-col">
  <header class="border-b border-border bg-sidebar px-6 py-4">
    <h1 class="text-xl font-bold">{t("Title")}</h1>
    <p class="mt-1 text-sm text-muted">{t("Subtitle")}</p>
    <label class="mt-4 block max-w-3xl text-xs text-muted">
      {t("ScanFolder")}
      <span class="mt-1 flex gap-2">
        <input class="field" bind:value={scanPath} />
        <button class="rounded-md bg-card px-3 text-sm" onclick={browse}>{t("Browse")}</button>
        <button class="rounded-md bg-accent px-4 text-sm font-medium text-white" disabled={busy} onclick={scan}>
          {t("Scan")}
        </button>
      </span>
    </label>
    {#if busy}
      <div class="mt-3 max-w-3xl">
        <p class="text-sm text-muted">
          {#if scanProgress}
            {t(
              "ScanProgress",
              scanProgress.gameName,
              scanProgress.gameIndex,
              scanProgress.gameCount,
              scanProgress.relativePath || "…",
              scanProgress.completed,
              scanProgress.total,
            )}
          {:else}
            {t("Busy")}
          {/if}
        </p>
        <div class="mt-1.5 h-1.5 overflow-hidden rounded-full bg-border">
          <div class="h-full rounded-full bg-accent transition-[width] duration-150 ease-linear" style:width="{scanPct}%"></div>
        </div>
      </div>
    {/if}
    {#if status}
      <p class="mt-2 text-sm text-ok">{status}</p>
    {/if}
    {#if error}
      <p class="mt-2 text-sm text-danger">{error}</p>
    {/if}
  </header>

  <div class="grid min-h-0 flex-1 lg:grid-cols-[280px_1fr]">
    <aside class="border-r border-border bg-sidebar p-3">
      <p class="mb-2 px-1 text-xs font-semibold text-muted">{t("Games", games.length)}</p>
      <ul class="space-y-1">
        {#each games as game (game.id)}
          <li>
            <button
              class="w-full rounded-md px-3 py-2 text-left text-sm {selectedId === game.id
                ? 'bg-card font-medium'
                : 'text-muted'}"
              onclick={() => (selectedId = game.id)}
            >
              <span class="block truncate">{game.name}</span>
              <span class="text-[11px] text-muted">{game.version}</span>
            </button>
          </li>
        {/each}
      </ul>
    </aside>

    <section class="min-w-0 p-5">
      {#if idx >= 0}
        <div class="max-w-2xl space-y-3">
          <label class="block text-xs text-muted">
            {t("Name")}
            <input class="field mt-1" bind:value={games[idx].name} />
          </label>
          <label class="block text-xs text-muted">
            {t("Version")}
            <input class="field mt-1" bind:value={games[idx].version} />
          </label>
          <label class="block text-xs text-muted">
            {t("Tags")}
            <input
              class="field mt-1"
              value={csv(games[idx].tags)}
              oninput={(e) => setCsv(games[idx], "tags", e.currentTarget.value)}
            />
          </label>
          <label class="block text-xs text-muted">
            {t("Notes")}
            <textarea class="field mt-1 h-24" bind:value={games[idx].notes}></textarea>
            <span class="mt-1 block">{t("NotesHint")}</span>
          </label>
          <label class="block text-xs text-muted">
            {t("Description")}
            <textarea class="field mt-1 h-20" bind:value={games[idx].description}></textarea>
            <span class="mt-1 block">{t("DescriptionHint")}</span>
          </label>
          <label class="block text-xs text-muted">
            {t("Launch")}
            <input
              class="field mt-1"
              value={games[idx].launchConfig?.executablePath ?? ""}
              oninput={(e) => {
                const path = e.currentTarget.value;
                games[idx].launchConfig = {
                  executablePath: path,
                  workingDirectory: games[idx].launchConfig?.workingDirectory ?? null,
                  launchArgs: games[idx].launchConfig?.launchArgs ?? null,
                };
              }}
            />
          </label>
          <p class="text-xs text-muted">{t("SteamHint")}</p>
        </div>
      {:else}
        <p class="text-sm text-muted">{t("Scan")}</p>
      {/if}
    </section>
  </div>
</div>
