import { invoke } from "@tauri-apps/api/core";

const MAX = 8000;

function stringify(value: unknown): string {
  if (value instanceof Error) return value.stack ?? value.message;
  if (typeof value === "string") return value;
  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

export function logMessage(level: "error" | "warn" | "info", message: string, extra?: unknown) {
  const text = extra === undefined ? message : `${message}: ${stringify(extra)}`;
  const clipped = text.length > MAX ? `${text.slice(0, MAX)}…` : text;
  void invoke("log_frontend", { level, message: clipped }).catch(() => {});
}

export function logError(message: string, extra?: unknown) {
  logMessage("error", message, extra);
}

let bridged = false;

export function installLogBridge() {
  if (bridged || typeof window === "undefined") return;
  bridged = true;
  window.addEventListener("error", (event) => {
    logError("window.error", event.error ?? event.message);
  });
  window.addEventListener("unhandledrejection", (event) => {
    logError("unhandledrejection", event.reason);
  });
}
