<script lang="ts">
  import Confirm from "$lib/components/Confirm.svelte";
  import Onboarding from "$lib/components/Onboarding.svelte";
  import Shell from "$lib/components/Shell.svelte";
  import Toasts from "$lib/components/Toasts.svelte";
  import { session } from "$lib/session.svelte";
</script>

{#if session.settings && !session.settings.OnboardingCompleted}
  <Onboarding />
{:else}
  <Shell />
{/if}
<Toasts />
<Confirm />
