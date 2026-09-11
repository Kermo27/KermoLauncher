<script lang="ts">
  import {
    dataDirectory,
    listProtonVersions,
    openLogFolder,
    saveSettings,
    testShare,
    validateInstallFolder,
  } from "$lib/api";
  import { format } from "$lib/format";
  import { t } from "$lib/i18n";
  import { applySession, session } from "$lib/session.svelte";
  import type { AppSettings, ProtonInstall } from "$lib/types";
  import { onMount } from "svelte";
  import { toast } from "$lib/toasts.svelte";
  import { checkForAppUpdate } from "$lib/updater";
  import Select from "./Select.svelte";

  const showWine = !/windows nt/i.test(navigator.userAgent);

  let draft = $state($state.snapshot(session.settings!));
  let shareUrl = $state(session.settings?.Nextcloud?.ShareUrl ?? "");
  let status = $state("");
  let ok = $state(false);
  let folderMsg = $state("");
  let protons = $state<ProtonInstall[]>([]);
  let dataDir = $state("");
  let checkingUpdate = $state(false);

  const nextSettings = $derived(buildSettings(draft, shareUrl));
  const dirty = $derived(JSON.stringify(nextSettings) !== JSON.stringify(session.settings));
  const protonOptions = $derived.by(() => {
    const list = [
      { value: "", label: t("Settings.Wine.Proton.Auto") },
      ...protons.map((p) => ({ value: p.name, label: p.name })),
    ];
    if (draft.ProtonVersion && !protons.some((p) => p.name === draft.ProtonVersion)) {
      list.splice(1, 0, { value: draft.ProtonVersion, label: draft.ProtonVersion });
    }
    return list;
  });

  function buildSettings(s: AppSettings, url: string): AppSettings {
    const parallel = Math.min(8, Math.max(1, Number(s.MaxParallelDownloads) || 1));
    return {
      ...s,
      MaxParallelDownloads: parallel,
      LastSeenVersion: session.settings?.LastSeenVersion ?? s.LastSeenVersion ?? "",
      Nextcloud: url.trim()
        ? {
            ShareUrl: url.trim(),
            ShareToken: s.Nextcloud?.ShareToken ?? "",
            RootFolder: s.Nextcloud?.RootFolder ?? "",
          }
        : null,
    };
  }

  async function test() {
    try {
      const probe = await testShare(shareUrl);
      ok = true;
      status =
        probe.gameCount > 0
          ? format(t("Settings.ConnectionOkFound"), probe.gameCount, probe.rootFolder.length > 0 ? probe.rootFolder : "/")
          : t("Settings.ConnectionOkEmpty");
      draft = {
        ...draft,
        Nextcloud: {
          ShareUrl: shareUrl.trim(),
          ShareToken: draft.Nextcloud?.ShareToken ?? "",
          RootFolder: probe.rootFolder,
        },
      };
    } catch (e) {
      ok = false;
      status = format(t("Settings.ConnectionErrorMessage"), String(e));
    }
  }

  async function checkFolder() {
    const r = await validateInstallFolder(draft.InstallFolder);
    folderMsg = r.ok ? "" : r.error === "empty" ? t("Onboarding.FolderEmpty") : format(t("Onboarding.FolderInvalid"), r.error ?? "");
  }

  async function save() {
    await checkFolder();
    if (folderMsg) return;
    const next = buildSettings(draft, shareUrl);
    await saveSettings(next);
    applySession(next);
    draft = $state.snapshot(next);
    shareUrl = next.Nextcloud?.ShareUrl ?? "";
    toast("success", t("Settings.SavedTitle"), t("Settings.SavedMessage"));
  }

  async function checkUpdates() {
    checkingUpdate = true;
    try {
      await checkForAppUpdate();
    } finally {
      checkingUpdate = false;
    }
  }

  async function openLogs() {
    try {
      await openLogFolder();
    } catch (e) {
      toast("error", t("Settings.Logs.OpenError"), String(e));
    }
  }

  onMount(async () => {
    dataDir = await dataDirectory();
    if (showWine) {
      protons = await listProtonVersions();
    }
  });
</script>

<div class="relative h-full overflow-x-hidden overflow-y-auto">
  <div class="mx-auto max-w-3xl px-8 py-7 pb-28">
    <header class="mb-8">
      <h1 class="text-3xl font-bold tracking-tight">{t("Settings.Title")}</h1>
      <p class="mt-1 text-sm text-muted">{t("Settings.Subtitle")}</p>
      {#if session.version}
        <p class="mt-2 text-xs text-muted">{format(t("Settings.Version"), session.version)}</p>
      {/if}
    </header>

    <div class="space-y-4">
      <section class="panel">
        <div class="border-b border-border px-5 py-4">
          <h2 class="text-sm font-semibold">{t("Settings.Nextcloud.Title")}</h2>
          <p class="mt-1 text-xs leading-relaxed text-muted">{t("Settings.Nextcloud.Hint")}</p>
        </div>
        <div class="space-y-3 p-5">
          <label class="block text-xs font-medium tracking-wide text-muted">
            {t("Settings.Nextcloud.ShareUrl")}
            <input
              class="field mt-1.5"
              placeholder={t("Settings.Nextcloud.ShareUrlPlaceholder")}
              bind:value={shareUrl}
            />
          </label>
          <div class="flex flex-wrap items-center gap-3">
            <button class="btn-ghost btn" onclick={test}>{t("Settings.Nextcloud.TestConnection")}</button>
            {#if status}
              <p class="text-sm {ok ? 'text-ok' : 'text-danger'}">{status}</p>
            {/if}
          </div>
        </div>
      </section>

      <section class="panel">
        <div class="border-b border-border px-5 py-4">
          <h2 class="text-sm font-semibold">{t("Settings.Folders.Title")}</h2>
          <p class="mt-1 text-xs leading-relaxed text-muted">{t("Settings.Folders.Hint")}</p>
        </div>
        <div class="p-5">
          <label class="block text-xs font-medium tracking-wide text-muted">
            {t("Settings.Folders.Install")}
            <input class="field mt-1.5" bind:value={draft.InstallFolder} oninput={checkFolder} />
          </label>
          {#if folderMsg}
            <p class="mt-2 text-sm text-danger">{folderMsg}</p>
          {/if}
        </div>
      </section>

      <section class="panel">
        <div class="border-b border-border px-5 py-4">
          <h2 class="text-sm font-semibold">{t("Settings.Appearance.Title")}</h2>
          <p class="mt-1 text-xs leading-relaxed text-muted">{t("Settings.Appearance.Hint")}</p>
        </div>
        <div class="grid gap-4 p-5 sm:grid-cols-2">
          <div>
            <p class="text-xs font-medium tracking-wide text-muted">{t("Settings.General.Theme")}</p>
            <Select
              class="mt-1.5"
              bind:value={draft.Theme}
              options={[
                { value: "Light", label: t("Settings.Theme.Light") },
                { value: "Dark", label: t("Settings.Theme.Dark") },
                { value: "System", label: t("Settings.Theme.System") },
              ]}
            />
          </div>
          <div>
            <p class="text-xs font-medium tracking-wide text-muted">{t("Settings.General.Language")}</p>
            <Select
              class="mt-1.5"
              bind:value={draft.Language}
              options={[
                { value: "System", label: t("Settings.Language.System") },
                { value: "en", label: t("Settings.Language.English") },
                { value: "pl", label: t("Settings.Language.Polish") },
              ]}
            />
          </div>
        </div>
      </section>

      <section class="panel">
        <div class="border-b border-border px-5 py-4">
          <h2 class="text-sm font-semibold">{t("Settings.General.Title")}</h2>
        </div>
        <div class="space-y-3 p-5">
          <label class="flex items-center justify-between gap-6 rounded-lg bg-window px-4 py-3">
            <span class="min-w-0">
              <span class="block text-sm font-medium">{t("Settings.General.AutoUpdate")}</span>
            </span>
            <input class="switch" type="checkbox" bind:checked={draft.AutoUpdate} />
          </label>
          <div class="flex flex-wrap items-end gap-3">
            <label class="min-w-[12rem] flex-1 text-xs font-medium tracking-wide text-muted">
              {t("Settings.General.MaxParallel")}
              <input type="number" min="1" max="8" class="field mt-1.5" bind:value={draft.MaxParallelDownloads} />
            </label>
            <p class="mb-2 text-xs text-muted">{t("Settings.General.MaxParallelHint")}</p>
          </div>
          <button class="btn-ghost btn" disabled={checkingUpdate} onclick={checkUpdates}>
            {t("Settings.General.CheckUpdates")}
          </button>
        </div>
      </section>

      <section class="panel">
        <div class="border-b border-border px-5 py-4">
          <h2 class="text-sm font-semibold">{t("Settings.Logs.Title")}</h2>
          <p class="mt-1 text-xs leading-relaxed text-muted">{t("Settings.Logs.Hint")}</p>
        </div>
        <div class="space-y-3 p-5">
          {#if dataDir}
            <p class="break-all font-mono text-xs text-muted">{dataDir}/logs</p>
          {/if}
          <button class="btn-ghost btn" onclick={openLogs}>{t("Settings.Logs.Open")}</button>
        </div>
      </section>

      {#if showWine}
        <section class="panel">
          <div class="border-b border-border px-5 py-4">
            <h2 class="text-sm font-semibold">{t("Settings.Wine.Title")}</h2>
            <p class="mt-1 text-xs leading-relaxed text-muted">{t("Settings.Wine.Hint")}</p>
          </div>
          <div class="space-y-3 p-5">
            <label class="flex items-center justify-between gap-6 rounded-lg bg-window px-4 py-3">
              <span class="min-w-0">
                <span class="block text-sm font-medium">{t("Settings.Wine.Enable")}</span>
              </span>
              <input class="switch" type="checkbox" bind:checked={draft.LaunchWindowsGamesWithWine} />
            </label>
            <div>
              <p class="text-xs font-medium tracking-wide text-muted">{t("Settings.Wine.Backend")}</p>
              <Select
                class="mt-1.5"
                bind:value={draft.LinuxCompatBackend}
                options={[
                  { value: "Proton", label: t("Settings.Wine.Backend.Proton") },
                  { value: "Wine", label: t("Settings.Wine.Backend.Wine") },
                ]}
              />
            </div>
            {#if draft.LinuxCompatBackend === "Proton"}
              <div>
                <p class="text-xs font-medium tracking-wide text-muted">{t("Settings.Wine.Proton.Version")}</p>
                <Select class="mt-1.5" bind:value={draft.ProtonVersion} options={protonOptions} />
              </div>
              <p class="text-xs leading-relaxed text-muted">{t("Settings.Wine.Proton.Hint")}</p>
            {:else}
              <label class="block text-xs font-medium tracking-wide text-muted">
                {t("Settings.Wine.Command")}
                <input class="field mt-1.5" placeholder={t("Settings.Wine.CommandPlaceholder")} bind:value={draft.WineCommand} />
              </label>
              <label class="block text-xs font-medium tracking-wide text-muted">
                {t("Settings.Wine.Prefix")}
                <input class="field mt-1.5" placeholder={dataDir ? `${dataDir}/wineprefix` : t("Settings.Wine.PrefixPlaceholder")} bind:value={draft.WinePrefix} />
              </label>
            {/if}
          </div>
        </section>
      {/if}
    </div>
  </div>

  <div class="sticky bottom-0 border-t border-border bg-window/90 px-8 py-3 backdrop-blur-sm">
    <div class="mx-auto flex max-w-3xl items-center gap-4">
      {#if dirty}
        <p class="min-w-0 flex-1 text-sm text-accent">{t("Settings.Unsaved")}</p>
      {/if}
      <button class="btn-primary btn ml-auto shrink-0 px-5 py-2.5" disabled={!dirty} onclick={save}>{t("Settings.Save")}</button>
    </div>
  </div>
</div>
