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
  import { ask, dialog } from "$lib/confirm.svelte";
  import { toast } from "$lib/toasts.svelte";
  import { runGameAction } from "$lib/game-notify";

  let items = $state<LibraryItem[]>([]);
  let tasks = $state<DownloadTask[]>([]);
  let loading = $state(true);
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
  const notes = $derived(item?.game.notes?.trim() || "");
  const played = $derived(item ? formatPlayDuration(item.local?.play_time_seconds ?? 0) : "");
  const lastPlayed = $derived(item ? formatLastPlayed(item.local?.last_played ?? null) : "");
  const pct = $derived(taskPct(task));

  async function load() {
    try {
      items = await getLibrary();
      tasks = await getDownloadTasks();
    } catch (e) {
      toast("error", t("Library.Title"), format(t("Library.LoadError"), String(e)));
    } finally {
      loading = false;
    }
  }

  async function run(action: "install" | "update" | "pause" | "resume" | "cancel" | "uninstall", fn: () => Promise<unknown>) {
    if (!item) return;
    await runGameAction(action, item.game.name, fn, item.game.version);
    await load();
  }

  async function launch() {
    if (!item) return;
    const result = await launchGame(item.game.id);
    if (!result.success) {
      toast("error", t("Library.LaunchErrorTitle"), result.error || t("Library.UnknownError"));
    }
  }

  async function verify() {
    if (!item) return;
    verifying = true;
    try {
      await verifyInstall(item.game.id);
      toast("success", t("Library.VerifyTitle"), t("Library.VerifyOk"));
      await load();
    } catch (e) {
      toast("error", t("Library.VerifyTitle"), format(t("Library.VerifyError"), String(e)));
      await load();
    } finally {
      verifying = false;
    }
  }

  async function openFolder() {
    if (!item) return;
    try {
      await openInstallFolder(item.game.id);
    } catch (e) {
      toast("error", t("Library.OpenFolder"), format(t("Library.OpenFolderError"), String(e)));
    }
  }

  async function uninstall() {
    if (!item) return;
    const ok = await ask(
      t("Library.UninstallTitle"),
      format(t("Library.UninstallConfirm"), item.game.name),
      t("Library.Uninstall"),
    );
    if (ok) await run("uninstall", () => uninstallGame(item.game.id));
  }

  function back() {
    session.gameId = null;
  }

  function onKey(e: KeyboardEvent) {
    if (e.key !== "Escape") return;
    if (dialog.open) return;
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
  <button class="fixed inset-0 z-50 flex items-center justify-center bg-black/85 p-8" onclick={() => (lightbox = null)}>
    <img src={lightbox} alt="" class="max-h-full max-w-full rounded-lg object-contain shadow-2xl" />
  </button>
{/if}

<div class="flex min-h-full min-w-0 flex-col overflow-x-hidden">
  {#if loading && !item}
    <p class="px-8 py-6 text-muted">…</p>
  {:else if !item}
    <div class="px-8 py-10">
      <h1 class="text-xl font-bold">{t("Library.MissingGame")}</h1>
      <button class="mt-3 text-sm font-semibold text-accent" onclick={back}>{t("Library.Back")}</button>
    </div>
  {:else if flags}
    <div class="relative isolate overflow-hidden bg-sidebar shadow-[var(--shadow-hero)]">
      {#if hero}
        <img src={hero} alt="" class="absolute inset-0 h-full w-full object-cover" />
      {/if}
      <div class="absolute inset-0 bg-gradient-to-t from-window via-window/55 to-black/20"></div>
      <button
        class="absolute left-5 top-5 z-20 flex h-9 w-9 items-center justify-center rounded-full bg-black/45 text-white backdrop-blur-sm hover:bg-black/65"
        onclick={back}
        aria-label={t("Library.Back")}
      >
        <svg class="h-5 w-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">
          <path d="M15 18l-6-6 6-6" />
        </svg>
      </button>
      <div class="relative z-10 flex min-h-[20rem] items-end gap-5 px-8 pb-8 pt-24 lg:min-h-[24rem]">
        {#if cover}
          <img
            src={cover}
            alt={item.game.name}
            class="hidden h-52 w-[8.67rem] max-w-none shrink-0 rounded-lg object-cover object-top shadow-2xl ring-1 ring-white/10 sm:block"
          />
        {/if}
        <div class="min-w-0 flex-1">
          <h1 class="text-4xl font-bold tracking-tight text-white drop-shadow-lg">{item.game.name}</h1>
          <div class="mt-3 flex flex-wrap items-center gap-2 text-sm text-white/80">
            <span
              class="rounded-full px-2.5 py-0.5 text-[11px] font-semibold
                {flags.status === 'Installed' ? 'bg-ok/25 text-ok' : ''}
                {flags.busy ? 'bg-accent/25 text-white' : ''}
                {flags.status === 'Failed' ? 'bg-danger/25 text-danger' : ''}
                {flags.status === 'NotInstalled' || flags.status === 'Paused' ? 'bg-black/40 text-white/80' : ''}"
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
          {#if flags.busy || flags.status === "Paused"}
            <div class="mt-4 max-w-md">
              <p class="text-sm font-semibold text-white">
                {task
                  ? `${Math.round(pct)}% · ${formatBytes(task.downloaded_bytes)} / ${formatBytes(task.total_bytes)}`
                  : t(`Library.Status.${flags.status}`)}
              </p>
              <div class="mt-2 h-1.5 overflow-hidden rounded-full bg-white/20">
                <div class="h-full rounded-full bg-accent" style:width="{pct}%"></div>
              </div>
            </div>
          {/if}
          <div class="mt-5 flex flex-wrap gap-2">
            {#if flags.canLaunch}
              <button class="btn-primary btn px-6 py-2.5 text-sm" onclick={launch}>{t("Library.Launch")}</button>
            {/if}
            {#if flags.canInstall}
              <button class="btn-primary btn px-6 py-2.5 text-sm" onclick={() => run("install", () => installGame(item.game.id))}>{t("Library.Install")}</button>
            {/if}
            {#if flags.updateAvailable}
              <button class="btn-primary btn px-6 py-2.5 text-sm" onclick={() => run("update", () => updateGame(item.game.id))}>{t("Library.Update")}</button>
            {/if}
            {#if flags.canPause}
              <button class="btn-ghost btn border-white/20 bg-black/30 text-white" onclick={() => run("pause", () => pauseInstall(item.game.id))}>{t("Library.PauseDownload")}</button>
            {/if}
            {#if flags.canResume}
              <button class="btn-primary btn px-6 py-2.5 text-sm" onclick={() => run("resume", () => resumeInstall(item.game.id))}>{t("Library.ResumeDownload")}</button>
            {/if}
            {#if flags.canCancel}
              <button class="btn-ghost btn border-white/20 bg-black/30 text-white" onclick={() => run("cancel", () => cancelInstall(item.game.id))}>{t("Library.CancelDownload")}</button>
            {/if}
          </div>
        </div>
      </div>
    </div>

    <div class="grid min-w-0 gap-8 px-8 py-8 lg:grid-cols-[minmax(0,1fr)_280px]">
      <div class="min-w-0">
        {#if tags.length}
          <div class="mb-5 flex flex-wrap gap-1.5">
            {#each tags as tagName (tagName)}
              <span class="chip">{tagName}</span>
            {/each}
          </div>
        {/if}

        <h2 class="text-xs font-semibold tracking-[0.14em] text-muted uppercase">{t("Library.Details")}</h2>
        <p class="mt-2 break-words whitespace-pre-wrap text-sm leading-relaxed text-muted">{description}</p>

        {#if notes}
          <h2 class="mt-8 text-xs font-semibold tracking-[0.14em] text-muted uppercase">{t("Library.Notes")}</h2>
          <p class="mt-2 break-words whitespace-pre-wrap text-sm leading-relaxed text-muted">{notes}</p>
        {/if}

        {#if shots.length}
          <h2 class="mt-8 text-xs font-semibold tracking-[0.14em] text-muted uppercase">{t("Library.Gallery")}</h2>
          <div class="mt-3 flex min-w-0 gap-2 overflow-x-auto pb-2">
            {#each shots as src (src)}
              <button class="h-28 w-48 shrink-0 overflow-hidden rounded-lg bg-sidebar ring-1 ring-border" onclick={() => (lightbox = src)}>
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
        <div class="flex flex-col gap-2">
          {#if flags.canVerify}
            <button class="btn-ghost btn" disabled={verifying} onclick={verify}>
              {verifying ? t("Library.Verifying") : t("Library.Verify")}
            </button>
          {/if}
          {#if flags.canOpenFolder}
            <button class="btn-ghost btn" onclick={openFolder}>{t("Library.OpenFolder")}</button>
          {/if}
          {#if flags.canUninstall}
            <button class="btn-danger btn" onclick={uninstall}>{t("Library.Uninstall")}</button>
          {/if}
        </div>

        <dl class="panel space-y-3 p-4 text-sm">
          <div>
            <dt class="text-[11px] tracking-wide text-muted uppercase">{t("Library.DetailsVersion")}</dt>
            <dd class="mt-0.5">{item.local?.installed_version || item.game.version || "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] tracking-wide text-muted uppercase">{t("Library.DetailsSize")}</dt>
            <dd class="mt-0.5">{item.game.sizeBytes > 0 ? formatBytes(item.game.sizeBytes) : "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] tracking-wide text-muted uppercase">{t("Library.DetailsPlayed")}</dt>
            <dd class="mt-0.5">{played || "—"}</dd>
          </div>
          <div>
            <dt class="text-[11px] tracking-wide text-muted uppercase">{t("Library.LastPlayed")}</dt>
            <dd class="mt-0.5">{lastPlayed || t("Library.LastPlayedNever")}</dd>
          </div>
          {#if item.local?.installed_path}
            <div>
              <dt class="text-[11px] tracking-wide text-muted uppercase">{t("Library.InstalledPath")}</dt>
              <dd class="mt-0.5 break-all text-xs text-muted">{item.local.installed_path}</dd>
            </div>
          {/if}
        </dl>
      </aside>
    </div>
  {/if}
</div>
