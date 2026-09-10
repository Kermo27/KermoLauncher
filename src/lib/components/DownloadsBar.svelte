<script lang="ts">
  import { cancelInstall, pauseInstall, resumeInstall } from "$lib/api";
  import { format, formatBytes } from "$lib/format";
  import { gameFlags, taskPct } from "$lib/game";
  import { runGameAction } from "$lib/game-notify";
  import { t } from "$lib/i18n";
  import type { DownloadTask, LibraryItem } from "$lib/types";

  type Props = {
    items: LibraryItem[];
    tasks: DownloadTask[];
    onChanged: () => void;
  };

  let { items, tasks, onChanged }: Props = $props();

  const rows = $derived.by(() => {
    const byId = new Map(items.map((entry) => [entry.game.id, entry] as const));
    const seen = new Set<string>();
    const out: { item: LibraryItem | null; task: DownloadTask | null }[] = [];
    for (const task of tasks) {
      if (task.status !== "Queued" && task.status !== "Downloading" && task.status !== "Paused") {
        continue;
      }
      seen.add(task.game_id);
      out.push({ item: byId.get(task.game_id) ?? null, task });
    }
    for (const entry of items) {
      if (seen.has(entry.game.id)) continue;
      const status = entry.local?.status;
      if (status === "Downloading" || status === "Installing" || status === "Paused") {
        out.push({
          item: entry,
          task: tasks.find((download) => download.game_id === entry.game.id) ?? null,
        });
      }
    }
    return out;
  });

  async function run(
    action: "pause" | "resume" | "cancel",
    item: LibraryItem,
    fn: () => Promise<unknown>,
  ) {
    await runGameAction(action, item.game.name, fn);
    onChanged();
  }
</script>

{#if rows.length}
  <aside class="border-t border-border bg-sidebar px-4 py-2.5">
    <div class="mb-1.5 flex items-center justify-between">
      <p class="text-[11px] font-semibold tracking-wide text-muted uppercase">{t("Downloads.Title")}</p>
      <p class="text-[11px] text-muted">{format(t("Downloads.Count"), rows.length)}</p>
    </div>
    <div class="flex max-h-36 flex-col gap-2 overflow-auto">
      {#each rows as row (row.item?.game.id ?? row.task?.id)}
        {@const current = row.item}
        {@const flags = current ? gameFlags(current) : null}
        {@const pct = taskPct(row.task)}
        <div class="flex items-center gap-3">
          <div class="min-w-0 flex-1">
            <p class="truncate text-xs font-semibold">{current?.game.name ?? row.task?.game_id}</p>
            <p class="mt-0.5 text-[11px] text-muted">
              {#if row.task && row.task.total_bytes > 0}
                {Math.round(pct)}% · {formatBytes(row.task.downloaded_bytes)} / {formatBytes(row.task.total_bytes)}
              {:else if current}
                {t(`Library.Status.${current.local?.status ?? "Downloading"}`)}
              {/if}
            </p>
            <div class="mt-1 h-1 overflow-hidden rounded-full bg-border">
              <div class="h-full rounded-full bg-accent" style:width="{pct}%"></div>
            </div>
          </div>
          {#if current && flags}
            <div class="flex shrink-0 gap-1">
              {#if flags.canPause}
                <button
                  class="btn-ghost btn px-2 py-1 text-[11px]"
                  onclick={() => run("pause", current, () => pauseInstall(current.game.id))}
                >{t("Library.PauseDownload")}</button>
              {/if}
              {#if flags.canResume}
                <button
                  class="btn-primary btn px-2 py-1 text-[11px]"
                  onclick={() => run("resume", current, () => resumeInstall(current.game.id))}
                >{t("Library.ResumeDownload")}</button>
              {/if}
              {#if flags.canCancel}
                <button
                  class="btn-ghost btn px-2 py-1 text-[11px]"
                  onclick={() => run("cancel", current, () => cancelInstall(current.game.id))}
                >{t("Library.CancelDownload")}</button>
              {/if}
            </div>
          {/if}
        </div>
      {/each}
    </div>
  </aside>
{/if}
