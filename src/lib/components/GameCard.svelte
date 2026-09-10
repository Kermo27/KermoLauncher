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
  import { ask } from "$lib/confirm.svelte";
  import { coverSrc, descriptionOf, tagsOf } from "$lib/cover";
  import { format, formatBytes, formatPlayDuration } from "$lib/format";
  import { gameFlags, taskPct } from "$lib/game";
  import { runGameAction } from "$lib/game-notify";
  import { t } from "$lib/i18n";
  import { session } from "$lib/session.svelte";
  import { toast } from "$lib/toasts.svelte";
  import type { DownloadTask, LibraryItem } from "$lib/types";

  let { item, task, onChanged } = $props<{
    item: LibraryItem;
    task: DownloadTask | null;
    onChanged: () => void;
  }>();

  const flags = $derived(gameFlags(item));
  const cover = $derived(coverSrc(item));
  const tags = $derived(tagsOf(item));
  const description = $derived(descriptionOf(item) || t("Library.NoDescription"));
  const played = $derived(formatPlayDuration(item.local?.play_time_seconds ?? 0));
  const pct = $derived(taskPct(task));

  async function run(action: "install" | "update" | "pause" | "resume" | "cancel" | "uninstall", fn: () => Promise<unknown>) {
    await runGameAction(action, item.game.name, fn, item.game.version);
    onChanged();
  }

  async function launch() {
    const result = await launchGame(item.game.id);
    if (!result.success) {
      toast("error", t("Library.LaunchErrorTitle"), result.error || t("Library.UnknownError"));
    }
  }

  async function uninstall() {
    const ok = await ask(
      t("Library.UninstallTitle"),
      format(t("Library.UninstallConfirm"), item.game.name),
      t("Library.Uninstall"),
    );
    if (ok) await run("uninstall", () => uninstallGame(item.game.id));
  }
</script>

<article class="flex h-[372px] w-[268px] flex-col rounded-xl border border-border bg-card p-3.5 hover:border-accent">
  <button class="flex min-h-0 flex-1 cursor-pointer flex-col text-left" onclick={() => (session.gameId = item.game.id)}>
    <div class="relative h-[120px] overflow-hidden rounded-lg bg-sidebar">
      {#if cover}
        <img src={cover} alt="" class="h-full w-full object-cover" />
      {:else}
        <div class="flex h-full items-center justify-center text-3xl text-muted">▶</div>
      {/if}
      <span
        class="absolute right-2 top-2 max-w-[150px] truncate rounded-full px-2.5 py-0.5 text-[11px] font-semibold
          {flags.status === 'Installed' ? 'bg-ok/20 text-ok' : ''}
          {flags.busy ? 'bg-accent/20 text-accent' : ''}
          {flags.status === 'Failed' ? 'bg-danger/20 text-danger' : ''}
          {flags.status === 'NotInstalled' || flags.status === 'Paused' ? 'bg-window text-muted' : ''}"
      >
        {t(`Library.Status.${flags.status}`)}
      </span>
      {#if flags.updateAvailable}
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
      {#if flags.busy || flags.status === "Paused"}
        <p class="text-[11px] font-semibold">
          {task ? `${Math.round(pct)}% · ${formatBytes(task.downloaded_bytes)} / ${formatBytes(task.total_bytes)}` : t(`Library.Status.${flags.status}`)}
        </p>
        <div class="mt-1 h-1 overflow-hidden rounded bg-border">
          <div class="h-full bg-accent" style="width: {pct}%"></div>
        </div>
      {:else}
        <div class="flex gap-1 overflow-hidden">
          {#each tags.slice(0, 3) as tag (tag)}
            <span class="rounded-full bg-sidebar px-2 py-0.5 text-[10px] text-muted">{tag}</span>
          {/each}
        </div>
      {/if}
    </div>
  </button>
  <div class="mt-auto grid grid-cols-2 gap-1 pt-2">
    {#if flags.canInstall}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run("install", () => installGame(item.game.id))}>{t("Library.Install")}</button>
    {/if}
    {#if flags.canPause}
      <button class="rounded-lg border border-border px-2 py-2 text-xs" onclick={() => run("pause", () => pauseInstall(item.game.id))}>{t("Library.PauseDownload")}</button>
    {/if}
    {#if flags.canResume}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run("resume", () => resumeInstall(item.game.id))}>{t("Library.ResumeDownload")}</button>
    {/if}
    {#if flags.updateAvailable}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={() => run("update", () => updateGame(item.game.id))}>{t("Library.Update")}</button>
    {/if}
    {#if flags.canLaunch}
      <button class="rounded-lg bg-accent px-2 py-2 text-xs font-medium text-white" onclick={launch}>{t("Library.Launch")}</button>
    {/if}
    {#if flags.canCancel}
      <button class="rounded-lg border border-border px-2 py-2 text-xs" onclick={() => run("cancel", () => cancelInstall(item.game.id))}>{t("Library.CancelDownload")}</button>
    {/if}
    {#if flags.canUninstall}
      <button class="col-span-2 rounded-lg border border-border px-2 py-1.5 text-xs" onclick={uninstall}>{t("Library.Uninstall")}</button>
    {/if}
  </div>
</article>
