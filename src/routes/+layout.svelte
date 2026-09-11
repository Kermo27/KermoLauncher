<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { boot, session } from "$lib/session.svelte";
  import { changelogDialog, maybeShowChangelog } from "$lib/changelog.svelte";
  import { checkForAppUpdate } from "$lib/updater";
  import { installLogBridge, logError } from "$lib/log";

  let { children } = $props();

  onMount(() => {
    installLogBridge();
    void (async () => {
      try {
        await boot();
        await maybeShowChangelog();
        if (
          !changelogDialog.open &&
          session.settings?.AutoUpdate &&
          session.settings.OnboardingCompleted
        ) {
          void checkForAppUpdate({ silent: true });
        }
      } catch (e) {
        logError("boot failed", e);
      }
    })();
  });
</script>

{#if session.ready}
    {@render children()}
{:else}
    <main class="flex min-h-screen items-center justify-center text-sm text-muted">…</main>
{/if}
