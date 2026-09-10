export type ToastKind = "info" | "success" | "warning" | "error";

export type Toast = {
  id: number;
  kind: ToastKind;
  title: string;
  message: string;
}

let nextId = 1;
const timers = new Map<number, ReturnType<typeof setTimeout>>();

export const toasts = $state<Toast[]>([]);

export function toast(kind: ToastKind, title: string, message = "") {
  const id = nextId++;
  toasts.push({ id, kind, title, message });
  while (toasts.length > 4) {
    const dropped = toasts[0];
    if (dropped) dismiss(dropped.id);
  }
  const ms = kind === "error" || kind === "warning" ? 10000 : 6000;
  timers.set(
    id,
    setTimeout(() => dismiss(id), ms),
  );
}

export function dismiss(id: number) {
  const timer = timers.get(id);
  if (timer) clearTimeout(timer);
  timers.delete(id);
  const i = toasts.findIndex((item) => item.id === id);
  if (i >= 0) toasts.splice(i, 1);
}
