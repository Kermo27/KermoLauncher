<script lang="ts">
  import { getDownloadTasks, getLibrary, refreshCatalog } from "$lib/api";
  import { tagsOf } from "$lib/cover";
  import { format } from "$lib/format";
  import { gameFlags } from "$lib/game";
  import { t } from "$lib/i18n";
  import type { DownloadTask, LibraryItem } from "$lib/types";
  import { onMount } from "svelte";
  import GameCard from "./GameCard.svelte";
  import { toast } from "$lib/toasts.svelte";

  const ALL = "__all__";

  type StatusFilter = "all" | "installed" | "notInstalled" | "progress" | "failed" | "updates";

  const statusFilters: { id: StatusFilter; key: string }[] = [
    { id: "all", key: "Library.FilterAll" },
    { id: "installed", key: "Library.FilterInstalled" },
    { id: "notInstalled", key: "Library.FilterNotInstalled" },
    { id: "progress", key: "Library.FilterProgress" },
    { id: "failed", key: "Library.FilterFailed" },
    { id: "updates", key: "Library.FilterUpdates" },
  ];

  let items = $state<LibraryItem[]>([]);
  let tasks = $state<DownloadTask[]>([]);
  let search = $state("");
  let tag = $state(ALL);
  let statusFilter = $state<StatusFilter>("all");
  let sort = $state<"name" | "play" | "size">("name");
  let loading = $state(true);
  let refreshing = $state(false);

  const tags = $derived(
    [ALL, ...[...new Set(items.flatMap((i) => tagsOf(i)))].sort((a, b) => a.localeCompare(b))],
  );

  const filtered = $derived.by(() => {
    const q = search.trim().toLowerCase();
    let list = items.filter((i) => {
      if (!matchesStatus(i, statusFilter)) return false;
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

  function matchesStatus(item: LibraryItem, filter: StatusFilter) {
    if (filter === "all") return true;
    const flags = gameFlags(item);
    if (filter === "installed") return flags.status === "Installed";
    if (filter === "notInstalled") return flags.status === "NotInstalled";
    if (filter === "progress") return flags.busy || flags.status === "Paused";
    if (filter === "failed") return flags.status === "Failed";
    return flags.updateAvailable;
  }

  function clearFilters() {
    search = "";
    tag = ALL;
    statusFilter = "all";
  }

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
    } catch {
      /* polling */
    } finally {
      loading = false;
    }
  }

  async function refresh() {
    refreshing = true;
    try {
      const n = await refreshCatalog();
      await load();
      toast(
        "info",
        t("Library.SyncTitle"),
        n > 0 ? format(t("Library.SyncDone"), n) : t("Library.SyncEmpty"),
      );
    } catch (e) {
      toast("error", t("Library.SyncErrorTitle"), format(t("Library.RefreshError"), String(e)));
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

  <div class="mb-2 flex flex-wrap gap-1.5">
    {#each statusFilters as filter (filter.id)}
      <button
        class="rounded-full px-2.5 py-1 text-[11px] {statusFilter === filter.id ? 'bg-accent text-white' : 'bg-sidebar text-muted'}"
        onclick={() => (statusFilter = filter.id)}
      >
        {t(filter.key)}
      </button>
    {/each}
  </div>

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
      <button class="mt-3 text-sm text-accent" onclick={clearFilters}>{t("Library.ClearFilters")}</button>
    </div>
  {:else}
    <div class="flex flex-wrap gap-2 overflow-auto pb-8">
      {#each filtered as item (item.game.id)}
        <GameCard {item} task={taskFor(item.game.id)} onChanged={load} />
      {/each}
    </div>
  {/if}
</div>
