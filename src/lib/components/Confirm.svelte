<script lang="ts">
  import { answer, dialog } from "$lib/confirm.svelte";
  import { t } from "$lib/i18n";

  function onKey(e: KeyboardEvent) {
    if (!dialog.open) return;
    if (e.key === "Escape") answer(false);
    if (e.key === "Enter") answer(true);
  }
</script>

<svelte:window onkeydown={onKey} />

{#if dialog.open}
  <button class="fixed inset-0 z-[60] bg-black/50" onclick={() => answer(false)} aria-label={t("Dialog.Cancel")}></button>
  <div class="pointer-events-none fixed inset-0 z-[60] flex items-center justify-center p-6">
    <div class="pointer-events-auto w-full max-w-md rounded-xl border border-border bg-card p-5">
      <h2 class="text-base font-bold">{dialog.title}</h2>
      <p class="mt-2 text-sm text-muted">{dialog.message}</p>
      <div class="mt-5 flex justify-end gap-2">
        <button class="rounded-md border border-border px-3 py-2 text-sm" onclick={() => answer(false)}>
          {t("Dialog.Cancel")}
        </button>
        <button class="rounded-md bg-danger px-3 py-2 text-sm font-medium text-white" onclick={() => answer(true)}>
          {dialog.confirmLabel || t("Dialog.Confirm")}
        </button>
      </div>
    </div>
  </div>
{/if}
