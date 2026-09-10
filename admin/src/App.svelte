<script lang="ts">
  import { listen } from "@tauri-apps/api/event";
  import { open } from "@tauri-apps/plugin-dialog";
  import {
    compareLibrary,
    guessDestFolder,
    loadState,
    publishLibrary,
    removeOrphan,
    saveState,
    scanFolder,
  } from "./api";
  import { t } from "./i18n";
  import type { AdminGame, CompareResult, GameSyncPlan, PublishProgress } from "./types";
  import { onMount } from "svelte";

  let scanPath = $state("");
  let destPath = $state("");
  let games = $state<AdminGame[]>([]);
  let selectedId = $state<string | null>(null);
  let compared = $state<CompareResult | null>(null);
  let status = $state("");
  let error = $state("");
  let busy = $state(false);
  let progress = $state("");
  let loaded = $state(false);

  const idx = $derived(games.findIndex((g) => g.id === selectedId));

  onMount(() => {
    void boot();
    const unlisten = listen<PublishProgress>("admin-progress", (ev) => {
      progress = `${ev.payload.relativePath} (${ev.payload.completed}/${ev.payload.total})`;
    });
    return () => {
      void unlisten.then((fn) => fn());
    };
  });

  $effect(() => {
    if (!loaded) return;
    const state = {
      scanFolder: scanPath,
      destFolder: destPath,
      games,
      selectedId,
    };
    const timer = setTimeout(() => {
      void saveState(state).catch(() => {});
    }, 700);
    return () => clearTimeout(timer);
  });

  async function boot() {
    try {
      const state = await loadState();
      scanPath = state.scanFolder;
      destPath = state.destFolder || (await guessDestFolder());
      games = state.games ?? [];
      selectedId = state.selectedId ?? games[0]?.id ?? null;
    } catch {
      destPath = await guessDestFolder();
    } finally {
      loaded = true;
    }
  }

  async function browse(kind: "scan" | "dest") {
    const dir = await open({ directory: true, multiple: false });
    if (typeof dir !== "string" || !dir) return;
    if (kind === "scan") scanPath = dir;
    else destPath = dir;
  }

  async function scan() {
    if (!scanPath.trim() || busy) return;
    busy = true;
    error = "";
    status = "";
    try {
      games = await scanFolder(scanPath.trim(), games);
      if (!games.some((g) => g.id === selectedId)) {
        selectedId = games[0]?.id ?? null;
      }
      compared = null;
      status = t("ScanDone", games.length);
    } catch (e) {
      error = t("ErrScan", String(e));
    } finally {
      busy = false;
    }
  }

  async function compare() {
    if (!destPath.trim() || games.length === 0 || busy) return;
    busy = true;
    error = "";
    try {
      compared = await compareLibrary(destPath.trim(), games);
      status = "";
    } catch (e) {
      error = t("ErrPublish", String(e));
    } finally {
      busy = false;
    }
  }

  async function publish() {
    if (!destPath.trim() || games.length === 0 || busy) return;
    busy = true;
    error = "";
    progress = "";
    try {
      const result = await publishLibrary(destPath.trim(), games);
      games = result.games;
      compared = await compareLibrary(destPath.trim(), games);
      if (result.report.copied > 0) {
        status = t("Published", result.report.copied);
      } else {
        status = t("CatalogOnly");
      }
      if (result.report.bumped.length) {
        status += " " + t("Bumped", result.report.bumped.join(", "));
      }
    } catch (e) {
      error = t("ErrPublish", String(e));
    } finally {
      busy = false;
      progress = "";
    }
  }

  async function dropOrphan(folder: string) {
    if (!confirm(t("ConfirmOrphan", folder))) return;
    busy = true;
    try {
      await removeOrphan(destPath.trim(), folder);
      compared = await compareLibrary(destPath.trim(), games);
      status = t("Removed", folder);
    } catch (e) {
      error = t("ErrPublish", String(e));
    } finally {
      busy = false;
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

  function counts(plan: GameSyncPlan) {
    const added = plan.changes.filter((c) => c.kind === "added").length;
    const changed = plan.changes.filter((c) => c.kind === "changed").length;
    const removed = plan.changes.filter((c) => c.kind === "removed").length;
    return { added, changed, removed };
  }
</script>

<div class="flex min-h-screen flex-col">
  <header class="border-b border-border bg-sidebar px-6 py-4">
    <h1 class="text-xl font-bold">{t("Title")}</h1>
    <p class="mt-1 text-sm text-muted">{t("Subtitle")}</p>
    <div class="mt-4 grid gap-3 lg:grid-cols-2">
      <label class="block text-xs text-muted">
        {t("ScanFolder")}
        <span class="mt-1 flex gap-2">
          <input class="field" bind:value={scanPath} />
          <button class="rounded-md bg-card px-3 text-sm" onclick={() => browse("scan")}>{t("Browse")}</button>
        </span>
      </label>
      <label class="block text-xs text-muted">
        {t("DestFolder")}
        <span class="mt-1 flex gap-2">
          <input class="field" bind:value={destPath} />
          <button class="rounded-md bg-card px-3 text-sm" onclick={() => browse("dest")}>{t("Browse")}</button>
        </span>
      </label>
    </div>
    <div class="mt-3 flex flex-wrap gap-2">
      <button class="rounded-md bg-card px-4 py-2 text-sm" disabled={busy} onclick={scan}>{t("Scan")}</button>
      <button class="rounded-md bg-card px-4 py-2 text-sm" disabled={busy} onclick={compare}>{t("Compare")}</button>
      <button class="rounded-md bg-accent px-4 py-2 text-sm font-medium text-white" disabled={busy} onclick={publish}
        >{t("Publish")}</button
      >
    </div>
    {#if busy}
      <p class="mt-2 text-sm text-muted">{progress || t("Busy")}</p>
    {/if}
    {#if status}
      <p class="mt-2 text-sm text-ok">{status}</p>
    {/if}
    {#if error}
      <p class="mt-2 text-sm text-danger">{error}</p>
    {/if}
  </header>

  <div class="grid min-h-0 flex-1 lg:grid-cols-[280px_1fr_320px]">
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

    <aside class="border-l border-border bg-sidebar p-4 text-sm">
      {#if compared}
        <div class="space-y-2">
          {#each compared.plans as plan (plan.gameId)}
            {@const c = counts(plan)}
            <div class="rounded-md bg-card px-3 py-2">
              <div class="flex justify-between gap-2">
                <span class="font-medium">{plan.gameName}</span>
                <span class="text-muted">
                  {plan.changes.length ? t("Summary", c.added, c.changed, c.removed) : t("InSync")}
                </span>
              </div>
              {#if plan.changes.length}
                <pre class="mt-1 max-h-32 overflow-auto text-[11px] text-muted">{plan.changes
                    .slice(0, 12)
                    .map((ch) => `${ch.kind === "added" ? "+" : ch.kind === "changed" ? "~" : "−"} ${ch.relativePath}`)
                    .join("\n")}</pre>
              {/if}
            </div>
          {/each}
        </div>
        {#if compared.orphans.length}
          <h2 class="mt-5 text-xs font-semibold text-muted">{t("Orphans")}</h2>
          <ul class="mt-2 space-y-2">
            {#each compared.orphans as folder (folder)}
              <li class="flex items-center justify-between gap-2 rounded-md bg-card px-3 py-2">
                <span class="truncate">{folder}</span>
                <button class="text-xs text-danger" onclick={() => dropOrphan(folder)}>{t("Remove")}</button>
              </li>
            {/each}
          </ul>
        {/if}
      {/if}
    </aside>
  </div>
</div>
