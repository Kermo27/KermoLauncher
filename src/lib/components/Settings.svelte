<script lang="ts">
  import {
    saveSettings,
    testShare,
    validateInstallFolder,
  } from "$lib/api";
  import { format } from "$lib/format";
  import { t, setLanguage } from "$lib/i18n";
  import { applySession, session } from "$lib/session.svelte";
  import { applyTheme } from "$lib/theme";

  let draft = $state(session.settings!);
  let shareUrl = $state(session.settings?.Nextcloud?.ShareUrl ?? "");
  let status = $state("");
  let ok = $state(false);
  let saved = $state("");
  let folderMsg = $state("");
  let tick = $state(0);

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
    const next = {
      ...draft,
      Nextcloud: shareUrl.trim()
        ? {
            ShareUrl: shareUrl.trim(),
            ShareToken: draft.Nextcloud?.ShareToken ?? "",
            RootFolder: draft.Nextcloud?.RootFolder ?? "",
          }
        : null,
    };
    await saveSettings(next);
    applySession(next);
    saved = t("Settings.SavedMessage");
    tick += 1;
  }
</script>

{#key tick}
<div class="h-full overflow-auto p-8">
  <h1 class="text-2xl font-bold">{t("Settings.Title")}</h1>

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
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.Theme")}
      <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.Theme} onchange={() => applyTheme(draft.Theme)}>
        <option value="Light">{t("Settings.Theme.Light")}</option>
        <option value="Dark">{t("Settings.Theme.Dark")}</option>
        <option value="System">{t("Settings.Theme.System")}</option>
      </select>
    </label>
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.Language")}
      <select class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.Language} onchange={() => setLanguage(draft.Language)}>
        <option value="System">{t("Settings.Language.System")}</option>
        <option value="en">{t("Settings.Language.English")}</option>
        <option value="pl">{t("Settings.Language.Polish")}</option>
      </select>
    </label>
    <label class="mt-3 block text-sm text-muted">{t("Settings.General.MaxParallel")}
      <input type="number" min="1" max="8" class="mt-1 w-full rounded-md border border-border bg-window px-3 py-2" bind:value={draft.MaxParallelDownloads} />
    </label>
  </section>

  <button class="mt-6 rounded-md bg-accent px-4 py-2 text-sm font-medium text-white" onclick={save}>{t("Settings.Save")}</button>
  {#if saved}<p class="mt-2 text-sm text-ok">{saved}</p>{/if}
</div>
{/key}
