<script lang="ts">
  import {
    cancelInstall,
    installGame,
    launchGame,
    pauseInstall,
    resumeInstall,
    updateGame,
  } from "$lib/api";
  import { coverSrc } from "$lib/cover";
  import { formatBytes } from "$lib/format";
  import { gameFlags, taskPct } from "$lib/game";
  import { runGameAction } from "$lib/game-notify";
  import { t } from "$lib/i18n";
  import { session } from "$lib/session.svelte";
  import { toast } from "$lib/toasts.svelte";
  import type { DownloadTask, LibraryItem } from "$lib/types";

  let { item, task, onChanged, size = "md" } = $props<{
    item: LibraryItem;
    task: DownloadTask | null;
    onChanged: () => void;
    size?: "md" | "lg";
  }>();

  const flags = $derived(gameFlags(item));
  const cover = $derived(coverSrc(item));
  const pct = $derived(taskPct(task));
  const primary = $derived.by(() => {
    if (flags.canLaunch && !flags.updateAvailable) return "launch" as const;
    if (flags.updateAvailable) return "update" as const;
    if (flags.canResume) return "resume" as const;
    if (flags.canInstall) return "install" as const;
    if (flags.canPause) return "pause" as const;
    return null;
  });

  async function run(
    action: "install" | "update" | "pause" | "resume" | "cancel",
    fn: () => Promise<unknown>,
  ) {
    await runGameAction(action, item.game.name, fn, item.game.version);
    onChanged();
  }

  async function launch() {
    const result = await launchGame(item.game.id);
    if (!result.success) {
      toast("error", t("Library.LaunchErrorTitle"), result.error || t("Library.UnknownError"));
    }
  }

  function openDetails() {
    session.gameId = item.game.id;
  }
</script>

<article
  class={[
    "group relative overflow-hidden rounded-lg bg-card shadow-md ring-1 ring-border transition hover:ring-accent",
    size === "lg" ? "w-[10.5rem] shrink-0" : "w-full",
  ]}
>
  <button class="relative block aspect-[2/3] w-full cursor-pointer text-left" onclick={openDetails}>
    {#if cover}
      <img src={cover} alt={item.game.name} class="h-full w-full object-cover" />
    {:else}
      <div class="flex h-full items-center justify-center bg-sidebar text-3xl text-muted">▶</div>
    {/if}
    <div class="absolute inset-0 bg-gradient-to-t from-black/85 via-black/20 to-transparent"></div>
    {#if flags.updateAvailable}
      <span class="absolute left-2 top-2 rounded-full bg-accent px-2 py-0.5 text-[10px] font-bold text-white">
        {t("Library.UpdateAvailable")}
      </span>
    {/if}
    {#if flags.busy || flags.status === "Paused"}
      <div class="absolute inset-x-2 bottom-10">
        <div class="h-1 overflow-hidden rounded-full bg-white/20">
          <div class="h-full rounded-full bg-accent" style:width="{pct}%"></div>
        </div>
        <p class="mt-1 text-[10px] font-semibold text-white/90">
          {task && task.total_bytes > 0
            ? `${Math.round(pct)}% · ${formatBytes(task.downloaded_bytes)}`
            : t(`Library.Status.${flags.status}`)}
        </p>
      </div>
    {/if}
    <h2 class="absolute inset-x-2 bottom-2 line-clamp-2 text-sm font-bold text-white drop-shadow">{item.game.name}</h2>
  </button>
  {#if primary || flags.canCancel}
  <div
    class="pointer-events-none absolute inset-0 z-10 flex items-end justify-center bg-black/45 px-2 pb-10 opacity-0 transition-opacity group-focus-within:opacity-100 group-hover:opacity-100"
  >
    <div class="pointer-events-auto flex w-full flex-col gap-1">
      {#if primary === "launch"}
        <button class="btn-primary btn w-full py-1.5 text-[11px]" onclick={launch}>{t("Library.Launch")}</button>
      {:else if primary === "update"}
        <button class="btn-primary btn w-full py-1.5 text-[11px]" onclick={() => run("update", () => updateGame(item.game.id))}>{t("Library.Update")}</button>
      {:else if primary === "resume"}
        <button class="btn-primary btn w-full py-1.5 text-[11px]" onclick={() => run("resume", () => resumeInstall(item.game.id))}>{t("Library.ResumeDownload")}</button>
      {:else if primary === "install"}
        <button class="btn-primary btn w-full py-1.5 text-[11px]" onclick={() => run("install", () => installGame(item.game.id))}>{t("Library.Install")}</button>
      {:else if primary === "pause"}
        <button class="btn-ghost btn w-full border-white/20 bg-black/40 py-1.5 text-[11px] text-white" onclick={() => run("pause", () => pauseInstall(item.game.id))}>{t("Library.PauseDownload")}</button>
      {/if}
      {#if flags.canCancel && primary !== "pause"}
        <button class="btn-ghost btn w-full border-white/20 bg-black/40 py-1 text-[11px] text-white" onclick={() => run("cancel", () => cancelInstall(item.game.id))}>{t("Library.CancelDownload")}</button>
      {/if}
    </div>
  </div>
  {/if}
</article>
