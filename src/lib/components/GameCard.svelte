<script lang="ts">
  import {
    cancelInstall,
    installGame,
    launchGame,
    pauseInstall,
    resumeInstall,
    uninstallGame,
    updateGame,
  } from "$lib/api";
  import { coverSrc, descriptionOf, tagsOf } from "$lib/cover";
  import { format, formatBytes, formatPlayDuration } from "$lib/format";
  import { t } from "$lib/i18n";
  import type { DownloadTask, LibraryItem } from "$lib/types";

  let { item, task, onChanged } = $props<{
    item: LibraryItem;
    task: DownloadTask | null;
    onChanged: () => void;
  }>();

  const status = $derived(item.local?.status ?? "NotInstalled");
  const busy = $derived(status === "Downloading" || status === "Installing");
  const canInstall = $derived(status === "NotInstalled" || status === "Failed");
  const canLaunch = $derived(status === "Installed");
  const canUninstall = $derived(
    status === "Installed" || status === "Failed" || status === "Paused",
  );
  const canPause = $derived(status === "Downloading");
  const canResume = $derived(status === "Paused");
  const canCancel = $derived(status === "Downloading" || status === "Paused");
  const updateAvailable = $derived(
    status === "Installed" &&
      !!item.local?.installed_version &&
      item.local.installed_version !== item.game.version,
  );
  const cover = $derived(coverSrc(item));
  const tags = $derived(tagsOf(item));
  const description = $derived(descriptionOf(item) || t("Library.NoDescription"));
  const played = $derived(formatPlayDuration(item.local?.play_time_seconds ?? 0));
  const pct = $derived(
    task && task.total_bytes > 0
      ? Math.min(100, (100 * task.downloaded_bytes) / task.total_bytes)
      : 0,
  );
  let err = $state("");

  async function run(fn: () => Promise<unknown>) {
    err = "";
    try {
      await fn();
      onChanged();
    } catch (e) {
      err = String(e);
    }
  }

  async function launch() {
    err = "";
    const result = await launchGame(item.game.id);
    if (!result.success) err = result.error || t("Library.LaunchErrorTitle");
  }
</script>

<article class="flex h-[372px] w-[268px] flex-col rounded-xl border border-border bg-card p-3.5">
  <div class="relative h-[120px] overflow-hidden rounded-lg bg-sidebar">
    {#if cover}
      <img src={cover} alt="" class="h-full w-full object-cover" />
    {:else}
      <div class="flex h-full items-center justify-center text-3xl text-muted">▶</div>
    {/if}
    <span
      class="absolute right-2 top-2 max-w-[150px] truncate rounded-full px-2.5 py-0.5 text-[11px] font-semibold
        {status === 'Installed' ? 'bg-ok/20 text-ok' : ''}
        {busy ? 'bg-accent/20 text-accent' : ''}
        {status === 'Failed' ? 'bg-danger/20 text-danger' : ''}
        {status === 'NotInstalled' || status === 'Paused' ? 'bg-window text-muted' : ''}"
    >
      {t(`Library.Status.${status}`)}
    </span>
    {#if updateAvailable}
      <span class="absolute left-2 top-2 rounded-full bg-accent px-2.5 py-0.5 text-[10px] font-bold text-white">
        {t("Library.UpdateAvailable")}
      </span>
    {/if}
    {#if item.game.sizeBytes > 0}
      <span class="absolute bottom-2 left-2 rounded-md bg-card px-2 py-0.5 text-[11px] font-semibold text-muted">
        {formatBytes(item.game.sizeBytes)}
      </span>
    {/if}
  </div>

  <h2 class="mt-2.5 line-clamp-2 text-base font-bold">{item.game.name}</h2>
  <div class="mt-1 flex justify-between text-[11px] text-muted">
    <span>{item.game.version}</span>
    {#if played}
      <span>{format(t("Library.Played"), played)}</span>
    {/if}
  </div>
  <p class="mt-1 line-clamp-3 min-h-[46px] text-[11px] text-muted">{description}</p>

  <div class="mt-1 h-6">
    {#if busy || status === "Paused"}
      <p class="text-[11px] font-semibold">
        {task ? `${Math.round(pct)}% · ${formatBytes(task.downloaded_bytes)} / ${formatBytes(task.total_bytes)}` : t(`Library.Status.${status}`)}
      </p>
      <div class="mt-1 h-1 overflow-hidden rounded bg-border">
        <div class="h-full bg-accent" style="width: {pct}%"></div>
      </div>
    {:else}
      <div class="flex gap-1 overflow-hidden">
        {#each tags.slice(0, 3) as tag}
          <span class="rounded-full bg-sidebar px-2 py-0.5 text-[10px] text-muted">{tag}</span>
        {/each}
      </div>
    {/if}
  </div>

  {#if err}
    <p class="mt-1 truncate text-[11px] text-danger">{err}</p>
  {/if}

  <div class="mt-auto grid grid-cols-2 gap-1 pt-2">
    {#if canInstall}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run(() => installGame(item.game.id))}>{t("Library.Install")}</button>
    {/if}
    {#if canPause}
      <button class="rounded-lg border border-border px-2 py-2 text-xs" onclick={() => run(() => pauseInstall(item.game.id))}>{t("Library.PauseDownload")}</button>
    {/if}
    {#if canResume}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run(() => resumeInstall(item.game.id))}>{t("Library.ResumeDownload")}</button>
    {/if}
    {#if updateAvailable}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run(() => updateGame(item.game.id))}>{t("Library.Update")}</button>
    {/if}
    {#if canLaunch}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={launch}>{t("Library.Launch")}</button>
    {/if}
    {#if canCancel}
      <button class="rounded-lg border border-border px-2 py-2 text-xs" onclick={() => run(() => cancelInstall(item.game.id))}>{t("Library.CancelDownload")}</button>
    {/if}
    {#if canUninstall}
      <button
        class="col-span-2 rounded-lg border border-border px-2 py-1.5 text-xs"
        onclick={() => {
          if (confirm(format(t("Library.UninstallConfirm"), item.game.name))) {
            run(() => uninstallGame(item.game.id));
          }
        }}
      >{t("Library.Uninstall")}</button>
    {/if}
  </div>
</article>
