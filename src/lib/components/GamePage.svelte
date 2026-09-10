<script lang="ts">
  import {
    cancelInstall,
    getDownloadTasks,
    getLibrary,
    installGame,
    launchGame,
    openInstallFolder,
    pauseInstall,
    resumeInstall,
    uninstallGame,
    updateGame,
    verifyInstall,
  } from "$lib/api";
  import { coverSrc, descriptionOf, heroSrc, screenshotSrcs, tagsOf } from "$lib/cover";
  import { format, formatBytes, formatLastPlayed, formatPlayDuration } from "$lib/format";
  import { gameFlags, taskPct } from "$lib/game";
  import { t } from "$lib/i18n";
  import { session } from "$lib/session.svelte";
  import type { DownloadTask, LibraryItem } from "$lib/types";
  import { onMount } from "svelte";

  let items = $state<LibraryItem[]>([]);
  let tasks = $state<DownloadTask[]>([]);
  let loading = $state(true);
  let err = $state("");
  let note = $state("");
  let verifying = $state(false);
  let lightbox = $state<string | null>(null);
  let failedShots = $state<string[]>([]);

  const item = $derived(items.find((i) => i.game.id === session.gameId) ?? null);
  const task = $derived(item ? (tasks.find((x) => x.game_id === item.game.id) ?? null) : null);
  const flags = $derived(item ? gameFlags(item) : null);
  const shareUrl = $derived(session.settings?.Nextcloud?.ShareUrl ?? "");
  const rootFolder = $derived(session.settings?.Nextcloud?.RootFolder ?? "");
  const shots = $derived(
    item
      ? screenshotSrcs(item, shareUrl, rootFolder).filter((s) => !failedShots.includes(s))
      : [],
  );
  const hero = $derived(item ? heroSrc(item, shareUrl, rootFolder) : null);
  const cover = $derived(item ? coverSrc(item) : null);
  const tags = $derived(item ? tagsOf(item) : []);
  const description = $derived(item ? descriptionOf(item) || t("Library.NoDescription") : "");
  const played = $derived(item ? formatPlayDuration(item.local?.play_time_seconds ?? 0) : "");
  const lastPlayed = $derived(item ? formatLastPlayed(item.local?.last_played ?? null) : "");
  const pct = $derived(taskPct(task));

  async function load() {
    try {
      items = await getLibrary();
      tasks = await getDownloadTasks();
    } catch (e) {
      err = format(t("Library.LoadError"), String(e));
    } finally {
      loading = false;
    }
  }

  async function run(fn: () => Promise<unknown>) {
    err = "";
    note = "";
    try {
      await fn();
      await load();
    } catch (e) {
      err = String(e);
    }
  }

  async function launch() {
    if (!item) return;
    err = "";
    const result = await launchGame(item.game.id);
    if (!result.success) err = result.error || t("Library.LaunchErrorTitle");
  }

  async function verify() {
    if (!item) return;
    err = "";
    note = "";
    verifying = true;
    try {
      await verifyInstall(item.game.id);
      note = t("Library.VerifyOk");
      await load();
    } catch (e) {
      err = format(t("Library.VerifyError"), String(e));
      await load();
    } finally {
      verifying = false;
    }
  }

  async function openFolder() {
    if (!item) return;
    err = "";
    try {
      await openInstallFolder(item.game.id);
    } catch (e) {
      err = format(t("Library.OpenFolderError"), String(e));
    }
  }

  function back() {
    session.gameId = null;
  }

  function onKey(e: KeyboardEvent) {
    if (e.key !== "Escape") return;
    if (lightbox) {
      lightbox = null;
      return;
    }
    back();
  }

  onMount(() => {
    void load();
    const timer = setInterval(load, 1000);
    return () => clearInterval(timer);
  });
</script>

<svelte:window onkeydown={onKey} />

{#if lightbox}
  <button
    class="fixed inset-0 z-50 flex items-center justify-center bg-black/80 p-8"
    onclick={() => (lightbox = null)}
  >
    <img src={lightbox} alt="" class="max-h-full max-w-full rounded-lg object-contain" />
  </button>
{/if}

<div class="flex h-full flex-col overflow-auto">
  <div class="flex items-center gap-3 px-6 py-3">
    <button class="rounded-md border border-border px-3 py-1.5 text-sm" onclick={back}>
      {t("Library.Back")}
    </button>
  </div>

  {#if loading && !item}
    <p class="px-6 text-muted">…</p>
  {:else if !item}
    <div class="px-6">
      <h1 class="text-xl font-bold">{t("Library.MissingGame")}</h1>
      <button class="mt-3 text-sm text-accent" onclick={back}>{t("Library.Back")}</button>
    </div>
  {:else if flags}
    <div class="relative h-72 shrink-0 bg-sidebar">
      {#if hero}
        <img src={hero} alt="" class="h-full w-full object-cover" />
      {/if}
      <div class="absolute inset-0 bg-gradient-to-t from-window via-window/40 to-transparent"></div>
      <div class="absolute bottom-0 left-0 right-0 flex items-end gap-5 px-8 pb-6">
        {#if cover}
          <img src={cover} alt="" class="h-40 w-[108px] rounded-lg object-cover shadow-lg" />
        {/if}
        <div class="min-w-0 pb-1">
          <h1 class="text-3xl font-bold">{item.game.name}</h1>
          <div class="mt-2 flex flex-wrap items-center gap-2 text-sm text-muted">
            <span
              class="rounded-full px-2.5 py-0.5 text-[11px] font-semibold
                {flags.status === 'Installed' ? 'bg-ok/20 text-ok' : ''}
                {flags.busy ? 'bg-accent/20 text-accent' : ''}
                {flags.status === 'Failed' ? 'bg-danger/20 text-danger' : ''}
                {flags.status === 'NotInstalled' || flags.status === 'Paused' ? 'bg-card text-muted' : ''}"
            >
              {t(`Library.Status.${flags.status}`)}
            </span>
            {#if flags.updateAvailable}
              <span class="rounded-full bg-accent px-2.5 py-0.5 text-[10px] font-bold text-white">
                {t("Library.UpdateAvailable")}
              </span>
            {/if}
            {#if item.game.version}
              <span>{item.game.version}</span>
            {/if}
            {#if item.game.sizeBytes > 0}
              <span>{formatBytes(item.game.sizeBytes)}</span>
            {/if}
          </div>
        </div>
      </div>
    </div>

    <div class="grid gap-8 px-8 py-6 lg:grid-cols-[1fr_280px]">
      <div>
        {#if flags.busy || flags.status === "Paused"}
          <p class="text-sm font-semibold">
            {task
              ? `${Math.round(pct)}% · ${formatBytes(task.downloaded_bytes)} / ${formatBytes(task.total_bytes)}`
              : t(`Library.Status.${flags.status}`)}
          </p>
          <div class="mt-2 h-1.5 overflow-hidden rounded bg-border">
            <div class="h-full bg-accent" style="width: {pct}%"></div>
          </div>
        {/if}

        {#if tags.length}
          <div class="mb-4 flex flex-wrap gap-1.5">
            {#each tags as tag (tag)}
              <span class="rounded-full bg-sidebar px-2.5 py-1 text-[11px] text-muted">{tag}</span>
            {/each}
          </div>
        {/if}

        <h2 class="text-sm font-semibold">{t("Library.Details")}</h2>
        <p class="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-muted">{description}</p>

        {#if shots.length}
          <h2 class="mt-8 text-sm font-semibold">{t("Library.Gallery")}</h2>
          <div class="mt-3 flex flex-wrap gap-2">
            {#each shots as src (src)}
              <button
                class="h-28 w-44 overflow-hidden rounded-lg bg-sidebar"
                onclick={() => (lightbox = src)}
              >
                <img
                  {src}
                  alt=""
                  class="h-full w-full object-cover"
                  onerror={() => (failedShots = [...failedShots, src])}
                />
              </button>
            {/each}
          </div>
        {/if}
      </div>

      <aside class="space-y-3">
        {#if err}
          <p class="rounded-lg bg-danger/90 px-3 py-2 text-xs text-white">{err}</p>
        {/if}
        {#if note}
          <p class="rounded-lg bg-ok/20 px-3 py-2 text-xs text-ok">{note}</p>
        {/if}

        <div class="flex flex-col gap-2">
          {#if flags.canInstall}
            <button class="rounded-lg bg-accent px-3 py-2.5 text-sm font-medium text-white" onclick={() => run(() => installGame(item.game.id))}>{t("Library.Install")}</button>
          {/if}
          {#if flags.canPause}
            <button class="rounded-lg border border-border px-3 py-2.5 text-sm" onclick={() => run(() => pauseInstall(item.game.id))}>{t("Library.PauseDownload")}</button>
          {/if}
          {#if flags.canResume}
            <button class="rounded-lg bg-accent px-3 py-2.5 text-sm font-medium text-white" onclick={() => run(() => resumeInstall(item.game.id))}>{t("Library.ResumeDownload")}</button>
          {/if}
          {#if flags.updateAvailable}
            <button class="rounded-lg bg-accent px-3 py-2.5 text-sm font-medium text-white" onclick={() => run(() => updateGame(item.game.id))}>{t("Library.Update")}</button>
          {/if}
          {#if flags.canLaunch}
            <button class="rounded-lg bg-accent px-3 py-2.5 text-sm font-medium text-white" onclick={launch}>{t("Library.Launch")}</button>
          {/if}
          {#if flags.canCancel}
            <button class="rounded-lg border border-border px-3 py-2.5 text-sm" onclick={() => run(() => cancelInstall(item.game.id))}>{t("Library.CancelDownload")}</button>
          {/if}
          {#if flags.canVerify}
            <button class="rounded-lg border border-border px-3 py-2.5 text-sm" disabled={verifying} onclick={verify}>
              {verifying ? t("Library.Verifying") : t("Library.Verify")}
            </button>
          {/if}
          {#if flags.canOpenFolder}
            <button class="rounded-lg border border-border px-3 py-2.5 text-sm" onclick={openFolder}>{t("Library.OpenFolder")}</button>
          {/if}
          {#if flags.canUninstall}
            <button
              class="rounded-lg border border-border px-3 py-2.5 text-sm"
              onclick={() => {
                if (confirm(format(t("Library.UninstallConfirm"), item.game.name))) {
                  run(() => uninstallGame(item.game.id));
                }
              }}
            >{t("Library.Uninstall")}</button>
          {/if}
        </div>

        <dl class="space-y-2 rounded-xl border border-border bg-card p-4 text-sm">
          <div>
            <dt class="text-[11px] text-muted">{t("Library.DetailsVersion")}</dt>
            <dd>{item.local?.installed_version || item.game.version || "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] text-muted">{t("Library.DetailsSize")}</dt>
            <dd>{item.game.sizeBytes > 0 ? formatBytes(item.game.sizeBytes) : "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] text-muted">{t("Library.DetailsPlayed")}</dt>
            <dd>{played || "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] text-muted">{t("Library.LastPlayed")}</dt>
            <dd>{lastPlayed || t("Library.LastPlayedNever")}</dd>
          </div>
          {#if item.local?.installed_path}
            <div>
              <dt class="text-[11px] text-muted">{t("Library.InstalledPath")}</dt>
              <dd class="break-all text-xs text-muted">{item.local.installed_path}</dd>
            </div>
          {/if}
        </dl>
      </aside>
    </div>
  {/if}
</div>
