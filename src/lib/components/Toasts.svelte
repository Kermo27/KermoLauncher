<script lang="ts">
  import { dismiss, toasts } from "$lib/toasts.svelte";
  import { fly } from "svelte/transition";

  const accent = {
    info: "border-accent",
    success: "border-ok",
    warning: "border-danger",
    error: "border-danger",
  };

  const icon = {
    info: "ℹ",
    success: "✓",
    warning: "!",
    error: "✕",
  };
</script>

<div class="pointer-events-none fixed right-5 top-5 z-50 flex w-[360px] max-w-[calc(100vw-2.5rem)] flex-col gap-2.5">
  {#each toasts as item (item.id)}
    <div
      class="pointer-events-auto rounded-xl border border-border {accent[item.kind]} border-l-4 bg-card/95 px-3.5 py-2.5 shadow-[var(--shadow-hero)] backdrop-blur-sm"
      transition:fly={{ y: -12, duration: 180 }}
    >
      <div class="flex gap-2.5">
        <span class="mt-0.5 text-sm">{icon[item.kind]}</span>
        <div class="min-w-0 flex-1">
          <p class="text-xs font-bold">{item.title}</p>
          {#if item.message}
            <p class="mt-0.5 text-[11px] text-muted">{item.message}</p>
          {/if}
        </div>
        <button class="text-muted hover:text-text" onclick={() => dismiss(item.id)}>✕</button>
      </div>
    </div>
  {/each}
</div>
