import { ApiError, request } from "../api.js";
import { API_BASE_URL } from "../config.js";
import { renderState, showToast } from "../ui.js";

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
