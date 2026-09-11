<script lang="ts">
  import { changelogDialog, dismissChangelog } from "$lib/changelog.svelte";
  import { changelogLang, t } from "$lib/i18n";
  import { format } from "$lib/format";

  function onKey(event: KeyboardEvent) {
    if (!changelogDialog.open) return;
    if (event.key === "Escape" || event.key === "Enter") {
      event.preventDefault();
      void dismissChangelog();
    }
  }
</script>

<svelte:window onkeydown={onKey} />

{#if changelogDialog.open}
  <button
    class="fixed inset-0 z-[60] bg-black/60"
    onclick={() => void dismissChangelog()}
    aria-label={t("Changelog.Close")}
  ></button>
  <div class="pointer-events-none fixed inset-0 z-[60] flex items-center justify-center p-6">
    <div class="panel pointer-events-auto flex max-h-[min(36rem,85vh)] w-full max-w-lg flex-col p-6 shadow-[var(--shadow-hero)]">
      <h2 class="text-lg font-bold">{t("Changelog.Title")}</h2>
      <p class="mt-1 text-sm text-muted">{format(t("Changelog.Subtitle"), changelogDialog.fromVersion)}</p>
      <div class="mt-4 min-h-0 flex-1 space-y-5 overflow-y-auto pr-1">
        {#each changelogDialog.entries as release (release.version)}
          <section>
            <h3 class="text-sm font-semibold tracking-wide text-accent">v{release.version}</h3>
            <ul class="mt-2 list-disc space-y-1.5 pl-5 text-sm text-text">
              {#each release.items[changelogLang()] as item (item)}
                <li>{item}</li>
              {/each}
            </ul>
          </section>
        {/each}
      </div>
      <div class="mt-6 flex justify-end">
        <button class="btn-primary btn" onclick={() => void dismissChangelog()}>
          {t("Changelog.Ok")}
        </button>
      </div>
    </div>
  </div>
{/if}
