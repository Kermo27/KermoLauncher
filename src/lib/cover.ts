import { convertFileSrc } from "@tauri-apps/api/core";
import { shareFileUrl } from "./share";
import type { LibraryItem } from "./types";

export function coverSrc(item: LibraryItem) {
  if (item.coverPath) return convertFileSrc(item.coverPath);
  const shot = item.game.screenshotUrls?.[0];
  return shot || null;
}

export function heroSrc(item: LibraryItem, shareUrl = "", rootFolder = "") {
  if (item.heroPath) return convertFileSrc(item.heroPath);
  const shots = screenshotSrcs(item, shareUrl, rootFolder);
  if (shots[0]) return shots[0];
  return coverSrc(item);
}

export function screenshotSrcs(item: LibraryItem, shareUrl = "", rootFolder = "") {
  const catalog = (item.game.screenshotUrls ?? [])
    .map((u) => shareFileUrl(shareUrl, rootFolder, u))
    .filter(Boolean);
  if (catalog.length) return catalog;
  return (item.extraScreenshotPaths ?? []).map((p) => convertFileSrc(p));
}

export function tagsOf(item: LibraryItem) {
  const extra = item.extraTags ?? [];
  const fromGame = item.game.tags ?? [];
  return [...fromGame, ...extra.filter((t) => !fromGame.includes(t))];
}

export function descriptionOf(item: LibraryItem) {
  const d = item.game.description?.trim();
  if (d) return d;
  return item.extraDescription?.trim() || "";
}
