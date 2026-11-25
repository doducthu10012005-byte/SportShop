import { ApiError, request } from "../api.js";
import { API_BASE_URL } from "../config.js";
import { renderState, showToast } from "../ui.js";
import { restoreSession, watchSession } from "../auth.js";

const guard = document.querySelector("[data-admin-guard]");
const signIn = guard.querySelector("[data-guard-login]");
const adminSections = document.querySelectorAll("[data-admin-only]");
watchSession(({ phase, user, message }) => {
  const allowed = user?.role === "Admin" && !user.mustChangePassword;
  guard.hidden = allowed;
  for (const section of adminSections) section.hidden = !allowed;
  if (user?.mustChangePassword) {
    window.location.replace("../account/#password");
    return;
  }
  signIn.hidden = phase !== "anonymous";
  guard.querySelector("[data-guard-message]").textContent = user ? "Bạn không có quyền truy cập trang quản trị."
    : phase === "anonymous" ? message || "Đăng nhập bằng tài khoản Admin để tiếp tục." : "Đang kiểm tra phiên đăng nhập…";
});
void restoreSession();

const endpoint = document.querySelector("#api-endpoint");
const feedback = document.querySelector("#connection-state");
const check = document.querySelector("#check-connection");
const copy = document.querySelector("#copy-endpoint");
endpoint.value = API_BASE_URL;

document.querySelector("#connection-form").addEventListener("submit", async event => {
  event.preventDefault();
  if (check.disabled) return;
  check.disabled = true;
  renderState(feedback, "loading", "Đang kiểm tra kết nối", "Đang chờ phản hồi từ API.");
  try {
    await request("/payment-methods", { auth: false, signal: AbortSignal.timeout(8000) });
    renderState(feedback, "success", "Kết nối thành công", "API đã trả về phản hồi hợp lệ.");
    showToast("Đã kết nối với API.");
  } catch (error) {
    const message = error instanceof ApiError ? error.message : "API không phản hồi trong thời gian chờ.";
    renderState(feedback, "error", "Không thể kết nối", message);
  } finally {
    check.disabled = false;
  }
});

copy.addEventListener("click", async () => {
  try {
    await navigator.clipboard.writeText(endpoint.value);
    showToast("Đã sao chép địa chỉ API.");
  } catch {
    endpoint.focus();
    endpoint.select();
    showToast("Đã chọn địa chỉ. Dùng Ctrl+C hoặc ⌘C để sao chép.");
  }
});
