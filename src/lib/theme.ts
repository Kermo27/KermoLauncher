export function applyTheme(theme: string) {
  const root = document.documentElement;
  let mode = theme.toLowerCase();
  if (mode === "system") {
    mode = window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  }
  root.dataset.theme = mode === "light" ? "light" : "dark";
}
