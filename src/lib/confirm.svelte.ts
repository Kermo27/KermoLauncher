export const dialog = $state({
  open: false,
  title: "",
  message: "",
  confirmLabel: "",
  resolve: null as ((ok: boolean) => void) | null,
})

export function ask(title: string, message: string, confirmLabel = "") {
  return new Promise<boolean>((resolve) => {
    dialog.open = true;
    dialog.title = title;
    dialog.message = message;
    dialog.confirmLabel = confirmLabel;
    dialog.resolve = resolve;
  });
}

export function answer(ok: boolean) {
  dialog.resolve?.(ok);
  dialog.open = false;
  dialog.resolve = null;
}
