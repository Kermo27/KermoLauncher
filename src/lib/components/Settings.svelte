<script lang="ts">
  import {
    dataDirectory,
    listProtonVersions,
    saveSettings,
    testShare,
    validateInstallFolder,
  } from "$lib/api";
  import { format } from "$lib/format";
  import { t } from "$lib/i18n";
  import { applySession, session } from "$lib/session.svelte";
  import type { AppSettings, ProtonInstall } from "$lib/types";
  import { onMount } from "svelte";

  const showWine = !/windows nt/i.test(navigator.userAgent);

  let draft = $state($state.snapshot(session.settings!));
  let shareUrl = $state(session.settings?.Nextcloud?.ShareUrl ?? "");
  let status = $state("");
  let ok = $state(false);
  let saved = $state("");
  let folderMsg = $state("");
  let protons = $state<ProtonInstall[]>([]);
  let dataDir = $state("");

  const nextSettings = $derived(buildSettings(draft, shareUrl));
  const dirty = $derived(JSON.stringify(nextSettings) !== JSON.stringify(session.settings));

  function buildSettings(s: AppSettings, url: string): AppSettings {
    const parallel = Math.min(8, Math.max(1, Number(s.MaxParallelDownloads) || 1));
    return {
      ...s,
      MaxParallelDownloads: parallel,
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
    saved = "";
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
    saved = "";
    await checkFolder();
    if (folderMsg) return;
    const next = buildSettings(draft, shareUrl);
    await saveSettings(next);
    applySession(next);
    draft = $state.snapshot(next);
    shareUrl = next.Nextcloud?.ShareUrl ?? "";
    saved = t("Settings.SavedMessage");
  }

  onMount(async () => {
    if (showWine) {
      protons = await listProtonVersions();
      dataDir = await dataDirectory();
    }
  });
</script>

<div class="h-full overflow-auto p-8">
  <h1 class="text-2xl font-bold">{t("Settings.Title")}</h1>
  {#if dirty}
    <p class="mt-2 text-sm text-accent">{t("Settings.Unsaved")}</p>
  {/if}

  <section class="mt-6 max-w-xl rounded-xl border border-border bg-card p-5">
    <h2 class="font-semibold">{t("Settings.Nextcloud.Title")}</h2>
    <label class="mt-3 block text-sm text-muted">{t("Settings.Nextcloud.ShareUrl")}
      <input class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2 text-text" bind:value={shareUrl} />
    </label>
    <button class="mt-3 rounded-md border border-border px-3 py-2 text-sm" onclick={test}>{t("Settings.Nextcloud.TestConnection")}</button>
    {#if status}<p class="mt-2 text-sm {ok ? 'text-ok' : 'text-danger'}">{status}</p>{/if}
  </section>

  <section class="mt-4 max-w-xl rounded-xl border border-border bg-card p-5">
    <h2 class="font-semibold">{t("Settings.Folders.Title")}</h2>
    <p class="mt-1 text-sm text-muted">{t("Settings.Folders.Hint")}</p>
    <label class="mt-3 block text-sm text-muted">{t("Settings.Folders.Install")}
      <input class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2 text-text" bind:value={draft.InstallFolder} oninput={checkFolder} />
    </label>
    {#if folderMsg}<p class="mt-2 text-sm text-danger">{folderMsg}</p>{/if}
  </section>

  <section class="mt-4 max-w-xl rounded-xl border border-border bg-card p-5">
    <h2 class="font-semibold">{t("Settings.General.Title")}</h2>
    <label class="mt-3 flex items-center gap-2 text-sm text-muted">
      <input type="checkbox" bind:checked={draft.AutoUpdate} />
      {t("Settings.General.AutoUpdate")}
    </label>
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.Theme")}
      <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.Theme}>
        <option value="Light">{t("Settings.Theme.Light")}</option>
        <option value="Dark">{t("Settings.Theme.Dark")}</option>
        <option value="System">{t("Settings.Theme.System")}</option>
      </select>
    </label>
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.Language")}
      <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.Language}>
        <option value="System">{t("Settings.Language.System")}</option>
        <option value="en">{t("Settings.Language.English")}</option>
        <option value="pl">{t("Settings.Language.Polish")}</option>
      </select>
    </label>
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.MaxParallel")}
      <input type="number" min="1" max="8" class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.MaxParallelDownloads} />
    </label>
    <p class="mt-1 text-[11px] text-muted">{t("Settings.General.MaxParallelHint")}</p>
  </section>

  {#if showWine}
    <section class="mt-4 max-w-xl rounded-xl border border-border bg-card p-5">
      <h2 class="font-semibold">{t("Settings.Wine.Title")}</h2>
      <p class="mt-2 text-sm text-muted">{t("Settings.Wine.Hint")}</p>
      <label class="mt-4 flex items-center gap-2 text-sm text-muted">
        <input type="checkbox" bind:checked={draft.LaunchWindowsGamesWithWine} />
        {t("Settings.Wine.Enable")}
      </label>
      <label class="mt-3 block text-sm text-muted">{t("Settings.Wine.Backend")}
        <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.LinuxCompatBackend}>
          <option value="Proton">{t("Settings.Wine.Backend.Proton")}</option>
          <option value="Wine">{t("Settings.Wine.Backend.Wine")}</option>
        </select>
      </label>
      {#if draft.LinuxCompatBackend === "Proton"}
        <label class="mt-3 block text-sm text-muted">{t("Settings.Wine.Proton.Version")}
          <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.ProtonVersion}>
            <option value="">{t("Settings.Wine.Proton.Auto")}</option>
            {#if draft.ProtonVersion && !protons.some((p) => p.name === draft.ProtonVersion)}
              <option value={draft.ProtonVersion}>{draft.ProtonVersion}</option>
            {/if}
            {#each protons as p (p.name)}
              <option value={p.name}>{p.name}</option>
            {/each}
          </select>
        </label>
        <p class="mt-2 text-[11px] text-muted">{t("Settings.Wine.Proton.Hint")}</p>
      {:else}
        <label class="mt-3 block text-sm text-muted">{t("Settings.Wine.Command")}
          <input class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2 text-text" placeholder={t("Settings.Wine.CommandPlaceholder")} bind:value={draft.WineCommand} />
        </label>
        <label class="mt-3 block text-sm text-muted">{t("Settings.Wine.Prefix")}
          <input class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2 text-text" placeholder={dataDir ? `${dataDir}/wineprefix` : t("Settings.Wine.PrefixPlaceholder")} bind:value={draft.WinePrefix} />
        </label>
      {/if}
    </section>
  {/if}

  <button class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-white" onclick={save}>{t("Settings.Save")}</button>
  {#if saved && !dirty}<p class="mt-2 text-sm text-ok">{saved}</p>{/if}
</div>
