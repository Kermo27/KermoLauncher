<script lang="ts">
  import { getDownloadTasks, getLibrary, refreshCatalog } from "$lib/api";
  import { tagsOf } from "$lib/cover";
  import { format } from "$lib/format";
  import { gameFlags } from "$lib/game";
  import { t } from "$lib/i18n";
  import type { DownloadTask, LibraryItem } from "$lib/types";
  import { onMount } from "svelte";
  import GameCard from "./GameCard.svelte";
  import Select from "./Select.svelte";
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

  const continuePlaying = $derived(
    [...items]
      .filter((i) => i.local?.status === "Installed" && (i.local.last_played ?? 0) > 0)
      .sort((a, b) => (b.local?.last_played ?? 0) - (a.local?.last_played ?? 0))
      .slice(0, 8),
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

<div class="flex h-full min-w-0 flex-col overflow-x-hidden px-7 py-6">
  <header class="mb-5 flex flex-wrap items-end gap-3">
    <div class="mr-auto">
      <h1 class="text-3xl font-bold tracking-tight">{t("Library.Title")}</h1>
      <p class="mt-1 text-xs text-muted">{format(t("Library.CountGames"), filtered.length)}</p>
    </div>
    <input
      class="field w-80"
      placeholder={t("Library.SearchPlaceholder")}
      bind:value={search}
    />
    <Select
      class="w-[10.75rem]"
      bind:value={sort}
      options={[
        { value: "name", label: t("Library.SortName") },
        { value: "play", label: t("Library.SortPlayTime") },
        { value: "size", label: t("Library.SortSize") },
      ]}
    />
    <button class="btn-ghost btn" disabled={refreshing} onclick={refresh}>
      {t("Library.Refresh")}
    </button>
  </header>

  <div class="mb-2 flex flex-wrap gap-1.5">
    {#each statusFilters as filter (filter.id)}
      <button
        class={["chip", statusFilter === filter.id && "chip-active"]}
        onclick={() => (statusFilter = filter.id)}
      >
        {t(filter.key)}
      </button>
    {/each}
  </div>

  <div class="mb-6 flex flex-wrap gap-1.5">
    {#each tags as name (name)}
      <button class={["chip", tag === name && "chip-active"]} onclick={() => (tag = name)}>
        {name === ALL ? t("Library.FilterAll") : name}
      </button>
    {/each}
  </div>

  {#if loading}
    <p class="text-sm text-muted">…</p>
  {:else if items.length === 0}
    <div class="panel px-10 py-16 text-center">
      <h2 class="text-lg font-semibold">{t("Library.EmptyTitle")}</h2>
      <p class="mt-2 text-sm text-muted">{t("Library.EmptyHint")}</p>
    </div>
  {:else if filtered.length === 0}
    <div class="panel px-10 py-16 text-center">
      <h2 class="text-lg font-semibold">{t("Library.NoResultsTitle")}</h2>
      <p class="mt-2 text-sm text-muted">{t("Library.NoResultsMessage")}</p>
      <button class="mt-4 text-sm font-semibold text-accent" onclick={clearFilters}>{t("Library.ClearFilters")}</button>
    </div>
  {:else}
    {#if continuePlaying.length}
      <section class="mb-8 min-w-0">
        <h2 class="mb-3 text-xs font-semibold tracking-[0.14em] text-muted uppercase">{t("Library.ContinuePlaying")}</h2>
        <div class="flex min-w-0 gap-3 overflow-x-auto pb-1">
          {#each continuePlaying as item (item.game.id)}
            <GameCard {item} task={taskFor(item.game.id)} onChanged={load} size="lg" />
          {/each}
        </div>
      </section>
    {/if}

    <div class="grid grid-cols-[repeat(auto-fill,minmax(9.5rem,1fr))] gap-4 pb-8">
      {#each filtered as item (item.game.id)}
        <GameCard {item} task={taskFor(item.game.id)} onChanged={load} />
      {/each}
    </div>
  {/if}
</div>
