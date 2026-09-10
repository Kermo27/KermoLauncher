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
  <button class="fixed inset-0 z-[60] bg-black/60" onclick={() => answer(false)} aria-label={dialog.cancelLabel || t("Dialog.Cancel")}></button>
  <div class="pointer-events-none fixed inset-0 z-[60] flex items-center justify-center p-6">
    <div class="panel pointer-events-auto w-full max-w-md p-6 shadow-[var(--shadow-hero)]">
      <h2 class="text-lg font-bold">{dialog.title}</h2>
      <p class="mt-2 text-sm text-muted">{dialog.message}</p>
      <div class="mt-6 flex justify-end gap-2">
        <button class="btn-ghost btn" onclick={() => answer(false)}>
          {dialog.cancelLabel || t("Dialog.Cancel")}
        </button>
        <button
          class={["btn", dialog.danger ? "btn-danger bg-danger text-white" : "btn-primary"]}
          onclick={() => answer(true)}
        >
          {dialog.confirmLabel || t("Dialog.Confirm")}
        </button>
      </div>
    </div>
  </div>
{/if}
