<script lang="ts">
  import { getDownloadTasks, getLibrary, refreshCatalog } from "$lib/api";
  import { tagsOf } from "$lib/cover";
  import { format } from "$lib/format";
  import { t } from "$lib/i18n";
  import type { DownloadTask, LibraryItem } from "$lib/types";
  import { onMount } from "svelte";
  import GameCard from "./GameCard.svelte";

  const ALL = "__all__";

  let items = $state<LibraryItem[]>([]);
  let tasks = $state<DownloadTask[]>([]);
  let search = $state("");
  let tag = $state(ALL);
  let sort = $state<"name" | "play" | "size">("name");
  let loading = $state(true);
  let refreshing = $state(false);
  let error = $state("");

  const tags = $derived(
    [ALL, ...[...new Set(items.flatMap((i) => tagsOf(i)))].sort((a, b) => a.localeCompare(b))],
  );

  const filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    let list = items.filter((i) => {
      if (tag !== ALL && !tagsOf(i).includes(tag)) return false;
      if (!q) return true;
      const blob = `${i.game.name} ${description(i)} ${tagsOf(i).join(" ")}`.toLowerCase();
      return blob.includes(q);
    });
    list = [...list].sort((a, b) => {
      if (sort === "play") return (b.local?.play_time_seconds ?? 0) - (a.local?.play_time_seconds ?? 0);
      if (sort === "size") return b.game.sizeBytes - a.game.sizeBytes;
      return a.game.name.localeCompare(b.game.name);
    });
    return list;
  });

  function description(i: LibraryItem) {
    return i.game.description || i.extraDescription || "";
  }

  function taskFor(id: string) {
    return tasks.find((x) => x.game_id === id) ?? null;
  }

  async function load() {
    try {
      items = await getLibrary();
      tasks = await getDownloadTasks();
      error = "";
    } catch (e) {
      error = format(t("Library.LoadError"), String(e));
    } finally {
      loading = false;
    }
  }

  async function refresh() {
    refreshing = true;
    try {
      await refreshCatalog();
      await load();
    } catch (e) {
      error = format(t("Library.RefreshError"), String(e));
    } finally {
      refreshing = false;
    }
  }

  onMount(() => {
    void load();
    const timer = setInterval(load, 1000);
    return () => clearInterval(timer);
  });
</script>

<div class="flex h-full flex-col p-5">
  <header class="mb-4 flex flex-wrap items-end gap-3">
    <div class="mr-auto">
      <h1 class="text-2xl font-bold">{t("Library.Title")}</h1>
      <p class="text-[11px] text-muted">{format(t("Library.CountGames"), filtered.length)}</p>
    </div>
    <input
      class="w-72 rounded-md border border-border bg-window px-3 py-2 text-sm"
      placeholder={t("Library.SearchPlaceholder")}
      bind:value={search}
    />
    <select class="rounded-md border border-border bg-window px-3 py-2 text-sm" bind:value={sort}>
      <option value="name">{t("Library.SortName")}</option>
      <option value="play">{t("Library.SortPlayTime")}</option>
      <option value="size">{t("Library.SortSize")}</option>
    </select>
    <button class="rounded-md border border-border px-3 py-2 text-sm" disabled={refreshing} onclick={refresh}>
      {t("Library.Refresh")}
    </button>
  </header>

  <div class="mb-4 flex flex-wrap gap-1.5">
    {#each tags as name (name)}
      <button
        class="rounded-full px-2.5 py-1 text-[11px] {tag === name ? 'bg-accent text-white' : 'bg-sidebar text-muted'}"
        onclick={() => (tag = name)}
      >
        {name === ALL ? t("Library.FilterAll") : name}
      </button>
    {/each}
  </div>

  {#if error}
    <p class="mb-3 rounded-lg bg-danger/90 px-3 py-2 text-xs text-white">{error}</p>
  {/if}

  {#if loading}
    <p class="text-muted">…</p>
  {:else if items.length === 0}
    <div class="rounded-xl border border-border bg-card p-10 text-center">
      <h2 class="text-base font-semibold">{t("Library.EmptyTitle")}</h2>
      <p class="mt-2 text-sm text-muted">{t("Library.EmptyHint")}</p>
    </div>
  {:else if filtered.length === 0}
    <div class="rounded-xl border border-border bg-card p-10 text-center">
      <h2 class="text-base font-semibold">{t("Library.NoResultsTitle")}</h2>
      <p class="mt-2 text-sm text-muted">{t("Library.NoResultsMessage")}</p>
      <button class="mt-3 text-sm text-accent" onclick={() => { search = ""; tag = ALL; }}>{t("Library.ClearFilters")}</button>
    </div>
  {:else}
    <div class="flex flex-wrap gap-2 overflow-auto pb-8">
      {#each filtered as item (item.game.id)}
        <GameCard {item} task={taskFor(item.game.id)} onChanged={load} />
      {/each}
    </div>
  {/if}
</div>
