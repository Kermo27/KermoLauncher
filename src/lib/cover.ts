import { convertFileSrc } from "@tauri-apps/api/core";
import type { LibraryItem } from "./types";

export function coverSrc(item: LibraryItem) {
  if (item.coverPath) return convertFileSrc(item.coverPath);
  const shot = item.game.screenshotUrls?.[0];
  return shot || null;
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
