import { logout, restoreSession, watchSession } from "./auth.js";

export function startAdminSession({ account, view, productId, clear, load }) {
  const guard = document.querySelector("[data-admin-guard]");
  const sections = document.querySelectorAll("[data-admin-only]");
  const name = document.querySelector("[data-admin-name]");
  const email = document.querySelector("[data-admin-email]");
  const nav = document.querySelector("#admin-nav");
  const menu = document.querySelector("[data-admin-menu]");
  const login = new URL("?return=admin#login", account);
  if (view) login.searchParams.set("view", view);
  if (productId) login.searchParams.set("productId", productId);
  guard.querySelector("[data-guard-login]").href = login;
  let adminId = null;
  let version = 0;
  let signingOut = false;

  function closeMenu() {
    nav.removeAttribute("data-open");
    menu.setAttribute("aria-expanded", "false");
  }
  menu.addEventListener("click", () => menu.setAttribute("aria-expanded", String(nav.toggleAttribute("data-open"))));
  nav.addEventListener("keydown", event => {
    if (event.key !== "Escape" || !nav.hasAttribute("data-open")) return;
    closeMenu();
    menu.focus();
  });
  document.querySelector("[data-admin-logout]").addEventListener("click", () => {
    signingOut = true;
    void logout();
  });

  watchSession(({ phase, user, message }) => {
    const navigation = ++version;
    const allowed = phase === "authenticated" && user?.role === "Admin" && !user.mustChangePassword;
    const nextId = allowed ? user.userId : null;
    const changed = adminId !== nextId;
    adminId = nextId;
    if (changed || !allowed) {
      clear();
      name.textContent = email.textContent = "";
      closeMenu();
    }
    guard.hidden = allowed;
    for (const section of sections) section.hidden = !allowed;
    if (user?.mustChangePassword) {
      location.replace(new URL("#password", account));
      return;
    }
    if (allowed) {
      name.textContent = user.fullName || "Quản trị viên";
      email.textContent = user.email || "";
      if (changed) load();
      return;
    }
    guard.querySelector("[data-guard-login]").hidden = phase !== "anonymous";
    guard.querySelector("[data-guard-message]").textContent = user ? "Bạn không có quyền truy cập trang quản trị."
      : phase === "signing-out" ? "Đang đăng xuất…" : message || "Đang kiểm tra phiên đăng nhập…";
    if (phase === "anonymous") {
      // A different account in another tab can replace this session immediately.
      queueMicrotask(() => {
        if (navigation !== version) return;
        if (message && !signingOut) login.searchParams.set("expired", "1");
        location.replace(login);
      });
    }
  });
  void restoreSession();
}
