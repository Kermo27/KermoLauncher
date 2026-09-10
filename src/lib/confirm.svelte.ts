export const dialog = $state({
  open: false,
  title: "",
  message: "",
  confirmLabel: "",
  cancelLabel: "",
  danger: true,
  resolve: null as ((ok: boolean) => void) | null,
})

export function ask(
  title: string,
  message: string,
  confirmLabel = "",
  options: { danger?: boolean; cancelLabel?: string } = {},
) {
  return new Promise<boolean>((resolve) => {
    dialog.open = true;
    dialog.title = title;
    dialog.message = message;
    dialog.confirmLabel = confirmLabel;
    dialog.cancelLabel = options.cancelLabel ?? "";
    dialog.danger = options.danger ?? true;
    dialog.resolve = resolve;
  });
}

export function answer(ok: boolean) {
  dialog.resolve?.(ok);
  dialog.open = false;
  dialog.resolve = null;
}
