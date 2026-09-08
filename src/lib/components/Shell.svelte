<script lang="ts">
  import { t } from "$lib/i18n";
  import { session } from "$lib/session.svelte";
  import Library from "./Library.svelte";
  import Settings from "./Settings.svelte";
</script>

<div class="flex min-h-screen">
  <nav class="flex w-52 shrink-0 flex-col gap-1 border-r border-border bg-sidebar p-3">
    <p class="mb-3 px-2 text-xs font-semibold tracking-wide text-accent">KermoLauncher</p>
    <button
      class="rounded-md px-3 py-2 text-left text-sm {session.view === 'library' ? 'bg-card font-medium' : 'text-muted'}"
      onclick={() => (session.view = "library")}
    >{t("Nav.Library")}</button>
    <button
      class="rounded-md px-3 py-2 text-left text-sm {session.view === 'settings' ? 'bg-card font-medium' : 'text-muted'}"
      onclick={() => (session.view = "settings")}
    >{t("Nav.Settings")}</button>
  </nav>
  <div class="min-w-0 flex-1 bg-window">
    {#if session.view === "settings"}
      <Settings />
    {:else}
      <Library />
    {/if}
  </div>
</div>
