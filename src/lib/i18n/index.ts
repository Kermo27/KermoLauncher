import { en } from "./en";
import { pl } from "./pl";

export type Lang = "System" | "en" | "pl";

let current: Record<string, string> = en;

function resolveLang(lang: string) {
  if (lang === "pl") return pl;
  if (lang === "en") return en;
  const nav = navigator.language.toLowerCase();
  return nav.startsWith("pl") ? pl : en;
}

export function setLanguage(lang: string) {
  current = resolveLang(lang);
}

export function locale() {
  return current === pl ? "pl-PL" : "en-GB";
}

export function changelogLang(): "en" | "pl" {
  return current === pl ? "pl" : "en";
}

export function t(key: string) {
  return current[key] ?? en[key] ?? key;
}
