const REPO = "Kermo27/KermoLauncher";
const FALLBACK = `https://github.com/${REPO}/releases/latest`;

const I18N = {
  pl: {
    tagline: "Osobista biblioteka gier",
    hero: "Launcher, nie sklep.",
    lede: "Katalog z publicznego share’a Nextcloud, instalacja z weryfikacją SHA-256, Proton na Linuksie. Aktualizacje z GitHub Releases.",
    download: "Pobierz",
    winHint: "Jeden plik .exe, x64.",
    linuxHint: "Archiwum z instalatorem (.desktop + ~/.local/bin).",
    linuxBin: "Tylko binarka",
    linuxNote: "Po ręcznym pobraniu binarki: chmod +x KermoLauncher-*-linux-x64. Instalator z tar.gz robi to sam.",
    f1: "Biblioteka z Nextcloud (metadata.json)",
    f2: "Delta update’y i pauza pobierania",
    f3: "Windowsowe gry na Linuksie przez Proton",
    f4: "Self-update z sumą SHA-256",
    allReleases: "Wszystkie wydania",
    langBtn: "EN",
  },
  en: {
    tagline: "A personal game library",
    hero: "A launcher, not a store.",
    lede: "Catalog from a public Nextcloud share, SHA-256 verified installs, Proton on Linux. Updates from GitHub Releases.",
    download: "Download",
    winHint: "A single .exe, x64.",
    linuxHint: "Tarball with installer (.desktop + ~/.local/bin).",
    linuxBin: "Binary only",
    linuxNote: "After downloading the bare binary: chmod +x KermoLauncher-*-linux-x64. The tar.gz installer does that for you.",
    f1: "Library from Nextcloud (metadata.json)",
    f2: "Delta updates and pause/resume",
    f3: "Windows games on Linux via Proton",
    f4: "Self-update with SHA-256",
    allReleases: "All releases",
    langBtn: "PL",
  },
};

function applyLang(lang) {
  const dict = I18N[lang] ?? I18N.pl;
  document.documentElement.lang = lang;
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    const key = el.getAttribute("data-i18n");
    if (dict[key]) el.textContent = dict[key];
  });
  const toggle = document.getElementById("lang-toggle");
  if (toggle) toggle.textContent = dict.langBtn;
  localStorage.setItem("lang", lang);
}

function pickAsset(assets, test) {
  return assets.find((a) => test(a.name))?.browser_download_url ?? FALLBACK;
}

async function loadRelease() {
  const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`);
  if (!res.ok) throw new Error(res.statusText);
  const data = await res.json();
  const assets = data.assets ?? [];
  const tag = (data.tag_name || "").replace(/^v/i, "") || "latest";

  const version = document.getElementById("version");
  if (version) version.textContent = tag;

  const win = document.getElementById("dl-win");
  const linux = document.getElementById("dl-linux");
  const bin = document.getElementById("dl-bin");
  if (win) win.href = pickAsset(assets, (n) => n.endsWith("-win-x64.exe"));
  if (linux) linux.href = pickAsset(assets, (n) => n.endsWith("-linux-x64.tar.gz"));
  if (bin) {
    bin.href = pickAsset(
      assets,
      (n) => n.includes("-linux-x64") && !n.endsWith(".tar.gz") && !n.endsWith(".zip")
    );
  }
}

const startLang = localStorage.getItem("lang") === "en" ? "en" : "pl";
applyLang(startLang);
document.getElementById("lang-toggle")?.addEventListener("click", () => {
  applyLang(document.documentElement.lang === "pl" ? "en" : "pl");
});

loadRelease().catch(() => {
  const version = document.getElementById("version");
  if (version) version.textContent = "GitHub";
});
