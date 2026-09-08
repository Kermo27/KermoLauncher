<script lang="ts">
  import "../app.css";
  import { onMount } from "svelte";
  import { boot, session } from "$lib/session.svelte";

  let { children } = $props();

  onMount(() => {
    boot();
  });
</script>

{#if session.ready}
    {@render children()}
{:else}
    <main class="flex min-h-screen items-center justify-center text-muted">...</main>
{/if}
