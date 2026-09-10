<script lang="ts" generics="T extends string">
  import type { ClassValue } from "svelte/elements";

  type Option = { value: T; label: string };

  let {
    value = $bindable(),
    options,
    class: className,
    onchange,
  }: {
    value: T;
    options: Option[];
    class?: ClassValue;
    onchange?: () => void;
  } = $props();

  let open = $state(false);
  const uid = $props.id();
  const selected = $derived(options.find((option) => option.value === value) ?? options[0]);

  function toggle() {
    open = !open;
  }

  function choose(next: T) {
    value = next;
    open = false;
    onchange?.();
  }

  function onKey(event: KeyboardEvent) {
    if (event.key === "Escape") open = false;
  }

  function clickOutside(element: HTMLElement) {
    function handle(event: MouseEvent) {
      if (!open) return;
      if (!element.contains(event.target as Node)) open = false;
    }
    window.addEventListener("click", handle);
    return () => window.removeEventListener("click", handle);
  }
</script>

<svelte:window onkeydown={onKey} />

<div class={["relative", open && "z-30", className]} {@attach clickOutside}>
  <button
    id={uid}
    type="button"
    class="field flex w-full cursor-pointer items-center justify-between gap-3 text-left leading-normal"
    aria-haspopup="listbox"
    aria-expanded={open}
    aria-controls="{uid}-list"
    onclick={toggle}
  >
    <span class="min-w-0 truncate">{selected?.label ?? ""}</span>
    <svg
      class={["h-4 w-4 shrink-0 text-muted transition", open && "rotate-180"]}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      aria-hidden="true"
    >
      <path d="M6 9l6 6 6-6" />
    </svg>
  </button>
  {#if open}
    <ul
      id="{uid}-list"
      class="absolute right-0 z-40 mt-1.5 max-h-60 min-w-full overflow-y-auto rounded-lg border border-border bg-card py-1 shadow-[var(--shadow-hero)]"
      role="listbox"
      aria-labelledby={uid}
    >
      {#each options as option (option.value)}
        <li>
          <button
            type="button"
            class={[
              "flex w-full cursor-pointer items-center justify-between gap-3 px-3 py-2 text-left text-sm hover:bg-raised",
              option.value === value ? "bg-raised text-accent" : "text-text",
            ]}
            role="option"
            aria-selected={option.value === value}
            onclick={() => choose(option.value)}
          >
            <span class="truncate">{option.label}</span>
            {#if option.value === value}
              <svg class="h-3.5 w-3.5 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" aria-hidden="true">
                <path d="M5 12l5 5 9-9" />
              </svg>
            {/if}
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>
