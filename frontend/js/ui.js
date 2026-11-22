const toast = document.querySelector("[data-toast]");
let toastTimer;

export function showToast(message) {
  if (!toast) return;
  toast.textContent = message;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toast.textContent = ""; }, 4500);
}

export function renderState(region, type, title, message) {
  region.dataset.state = type;
  region.setAttribute("aria-busy", String(type === "loading"));
  region.querySelector("[data-state-title]").textContent = title;
  region.querySelector("[data-state-message]").textContent = message;
}

for (const button of document.querySelectorAll("[data-nav-toggle]")) {
  const menu = document.getElementById(button.getAttribute("aria-controls"));
  if (!menu) continue;
  const close = () => {
    menu.dataset.open = "false";
    button.setAttribute("aria-expanded", "false");
  };
  button.addEventListener("click", () => {
    const open = button.getAttribute("aria-expanded") !== "true";
    menu.dataset.open = String(open);
    button.setAttribute("aria-expanded", String(open));
  });
  menu.addEventListener("click", event => {
    if (event.target.closest("a")) close();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && button.getAttribute("aria-expanded") === "true") {
      close();
      button.focus();
    }
  });
}

for (const trigger of document.querySelectorAll("[data-dialog]")) {
  const dialog = document.getElementById(trigger.dataset.dialog);
  if (dialog instanceof HTMLDialogElement) trigger.addEventListener("click", () => dialog.showModal());
}
