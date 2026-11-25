import { restoreSession, watchSession } from "./auth.js";

const accountLink = document.querySelector("[data-account-link]");
watchSession(({ phase, user }) => {
  if (user?.mustChangePassword) {
    window.location.replace(new URL("#password", accountLink.href));
    return;
  }
  accountLink.textContent = phase === "anonymous" ? "Đăng nhập" : "Tài khoản";
});
void restoreSession();
