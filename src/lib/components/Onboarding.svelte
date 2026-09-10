<script lang="ts">
  import {
    defaultInstallFolder,
    refreshCatalog,
    saveSettings,
    testShare,
    validateInstallFolder,
  } from "$lib/api";
  import { format, formatBytes } from "$lib/format";
  import { t, setLanguage } from "$lib/i18n";
  import { applySession, session } from "$lib/session.svelte";
  import { applyTheme } from "$lib/theme";
  import { onMount } from "svelte";
  import Select from "./Select.svelte";

  let step = $state(0);
  let theme = $state("Dark");
  let language = $state("System");
  let installFolder = $state("");
  let folderOk = $state(false);
  let folderStatus = $state("");
  let shareUrl = $state("");
  let connectionOk = $state(false);
  let connectionStatus = $state("");
  let busy = $state(false);
  let error = $state("");
  let tick = $state(0);

  const canFolder = $derived(folderOk && installFolder.trim().length > 0);
  const canFinish = $derived(connectionOk && shareUrl.trim().length > 0);

  onMount(async () => {
    const existing = session.settings;
    theme = existing?.Theme || "Dark";
    language = existing?.Language || "System";
    applyTheme(theme);
    setLanguage(language);
    tick += 1;
    installFolder = existing?.InstallFolder?.trim()
      ? existing.InstallFolder
      : await defaultInstallFolder();
    await checkFolder();
  });

  function previewTheme() {
    applyTheme(theme);
    setLanguage(language);
    tick += 1;
  }

  async function checkFolder() {
    const result = await validateInstallFolder(installFolder);
    folderOk = result.ok;
    if (!result.ok) {
      folderStatus =
        result.error === "empty"
          ? t("Onboarding.FolderEmpty")
          : format(t("Onboarding.FolderInvalid"), result.error ?? "");
      return;
    }
    folderStatus = format(t("Onboarding.FolderOk"), formatBytes(result.freeBytes));
  }

  async function testConn() {
    error = "";
    connectionOk = false;
    connectionStatus = "";
    const url = shareUrl.trim();
    if (!url) {
      connectionStatus = t("Settings.NoConfigMessage");
      return;
    }
    busy = true;
    try {
      const probe = await testShare(url);
      connectionOk = true;
      connectionStatus =
        probe.gameCount > 0
          ? format(
              t("Settings.ConnectionOkFound"),
              probe.gameCount,
              probe.rootFolder.length > 0 ? probe.rootFolder : "/",
            )
          : t("Settings.ConnectionOkEmpty");
    } catch (e) {
      connectionOk = false;
      connectionStatus = format(t("Settings.ConnectionErrorMessage"), String(e));
    } finally {
      busy = false;
    }
  }

  async function primary() {
    error = "";
    if (step === 0) {
      previewTheme();
      step = 1;
      return;
    }
    if (step === 1) {
      await checkFolder();
      if (!canFolder) return;
      step = 2;
      return;
    }
    if (!canFinish || !session.settings) return;
    busy = true;
    try {
      const probe = await testShare(shareUrl.trim());
      const next = {
        ...session.settings,
        InstallFolder: installFolder.trim(),
        Theme: theme,
        Language: language,
        OnboardingCompleted: true,
        Nextcloud: {
          ShareUrl: shareUrl.trim(),
          ShareToken: "",
          RootFolder: probe.rootFolder,
        },
      };
      await saveSettings(next);
      applySession(next);
      try {
        await refreshCatalog();
      } catch (e) {
        error = format(t("Onboarding.SyncFailed"), String(e));
      }
    } catch (e) {
      error = format(t("Onboarding.FinishFailed"), String(e));
    } finally {
      busy = false;
    }
  }
</script>

{#key tick}
  <main class="flex min-h-screen items-center justify-center bg-[radial-gradient(ellipse_at_top,var(--color-raised),var(--color-window)_58%)] p-6">
    <section class="panel w-full max-w-xl p-7 shadow-[var(--shadow-hero)]">
      <img
        src="/favicon.png"
        alt="KermoLauncher"
        class="mx-auto h-16 w-16 rounded-2xl shadow-[var(--shadow-hero)] ring-1 ring-white/10"
      />
      <p class="mt-4 text-center text-xs font-semibold tracking-[0.16em] text-accent uppercase">KermoLauncher</p>
      <h1 class="mt-2 text-center text-3xl font-bold tracking-tight">{t("Onboarding.Title")}</h1>
      <p class="mt-1 text-center text-sm text-muted">{t("Onboarding.Subtitle")}</p>

      <div class="mt-6 flex justify-center gap-2">
        {#each [0, 1, 2] as i (i)}
          <span class="h-2 w-8 rounded-full {step === i ? 'bg-accent' : 'bg-border'}"></span>
        {/each}
      </div>

      <div class="mt-6 space-y-4">
        {#if step === 0}
          <p class="text-sm text-muted">{t("Onboarding.Welcome.Body")}</p>
          <div>
            <p class="text-xs font-medium tracking-wide text-muted">{t("Settings.General.Theme")}</p>
            <Select
              class="mt-1.5"
              bind:value={theme}
              onchange={previewTheme}
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
              bind:value={language}
              onchange={previewTheme}
              options={[
                { value: "System", label: t("Settings.Language.System") },
                { value: "en", label: t("Settings.Language.English") },
                { value: "pl", label: t("Settings.Language.Polish") },
              ]}
            />
          </div>
        {:else if step === 1}
          <p class="text-sm text-muted">{t("Onboarding.Folder.Body")}</p>
          <label class="block text-xs font-medium tracking-wide text-muted">{t("Settings.Folders.Install")}
            <input
              class="field mt-1.5"
              bind:value={installFolder}
              oninput={checkFolder}
            />
          </label>
          <p class="text-sm {folderOk ? 'text-ok' : 'text-danger'}">{folderStatus}</p>
        {:else}
          <p class="text-sm text-muted">{t("Onboarding.Source.Body")}</p>
          <label class="block text-xs font-medium tracking-wide text-muted">{t("Settings.Nextcloud.ShareUrl")}
            <input
              class="field mt-1.5"
              placeholder={t("Settings.Nextcloud.ShareUrlPlaceholder")}
              bind:value={shareUrl}
              oninput={() => {
                connectionOk = false;
                connectionStatus = "";
              }}
            />
          </label>
          <button
            class="btn-ghost btn"
            disabled={busy}
            onclick={testConn}
          >
            {t("Settings.Nextcloud.TestConnection")}
          </button>
          {#if connectionStatus}
            <p class="text-sm {connectionOk ? 'text-ok' : 'text-danger'}">{connectionStatus}</p>
          {/if}
        {/if}

        {#if error}
          <p class="text-sm text-danger">{error}</p>
        {/if}
      </div>

      <div class="mt-8 flex justify-end gap-2">
        {#if step > 0}
          <button
            class="btn px-4 py-2 text-muted"
            disabled={busy}
            onclick={() => {
              step -= 1;
              error = "";
            }}
          >
            {t("Onboarding.Back")}
          </button>
        {/if}
        <button
          class="btn-primary btn px-5 disabled:opacity-40"
          disabled={busy || (step === 1 && !canFolder) || (step === 2 && !canFinish)}
          onclick={primary}
        >
          {step < 2 ? t("Onboarding.Next") : t("Onboarding.Finish")}
        </button>
      </div>
    </section>
  </main>
{/key}
