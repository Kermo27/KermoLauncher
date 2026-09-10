<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { boot, session } from "$lib/session.svelte";
  import { checkForAppUpdate } from "$lib/updater";

  let { children } = $props();

  onMount(() => {
    void (async () => {
      await boot();
      if (session.settings?.AutoUpdate && session.settings.OnboardingCompleted) {
        void checkForAppUpdate({ silent: true });
      }
    })();
  });
</script>

{#if session.ready}
    {@render children()}
{:else}
    <main class="flex min-h-screen items-center justify-center text-muted">...</main>
{/if}
