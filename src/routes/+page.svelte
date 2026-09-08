<script lang="ts">
  import Onboarding from "$lib/components/Onboarding.svelte";
  import Shell from "$lib/components/Shell.svelte";
  import { session } from "$lib/session.svelte";
</script>

{#if session.settings && !session.settings.OnboardingCompleted}
  <Onboarding />
{:else}
  <Shell />
{/if}
