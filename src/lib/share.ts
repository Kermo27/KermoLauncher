export function shareFileUrl(shareUrl: string, rootFolder: string, relativePath: string) {
  if (!relativePath) return "";
  if (/^https?:\/\//i.test(relativePath)) return relativePath;
  const base = shareUrl.trim().replace(/\/+$/, "");
  if (!base) return "";
  const parts = [rootFolder, relativePath]
    .flatMap((p) => p.replace(/\\/g, "/").split("/"))
    .filter((p) => p && p !== ".");
  return `${base}/download?path=/${parts.map(encodeURIComponent).join("/")}`;
}
