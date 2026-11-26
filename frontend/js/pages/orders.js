import "../store-session.js";
import { ApiError } from "../api.js";
import { authorizedRequest, sessionMessage, watchSession } from "../auth.js";
import { element, money, positiveInt, productImage, setCatalogState } from "../catalog.js";
import { orderDetailUrl, orderTime, paymentStatus } from "../orders.js";

const params = new URLSearchParams(location.search);
const detailView = params.has("id");
const orderId = positiveInt(params.get("id"));
const title = document.querySelector("[data-orders-title]");
const filter = document.querySelector("[data-orders-filter]");
const panel = document.querySelector("[data-orders-state]");
const notice = document.querySelector("[data-order-notice]");
const list = document.querySelector("[data-orders-list]");
const pager = list.querySelector("nav");
const detail = document.querySelector("[data-order-detail]");
const cancel = document.querySelector("[data-open-cancel]");
const refresh = document.querySelector("[data-refresh-order]");
const dialog = document.querySelector("[data-cancel-dialog]");
const cancelForm = dialog.querySelector("form");
const reason = cancelForm.elements.cancelReason;
const errorText = dialog.querySelector("#cancel-error");
const confirm = cancelForm.querySelector("[type=submit]");
const keep = cancelForm.querySelector("[data-keep-order]");
const retry = panel.querySelector("[data-retry]");
const login = panel.querySelector("[data-orders-login]");
const statusCode = [...filter.elements.statusCode.options].some(option => option.value === params.get("statusCode"))
  ? params.get("statusCode") : undefined;
const query = { statusCode: statusCode || undefined, page: positiveInt(params.get("page")) ?? 1,
  pageSize: Math.min(positiveInt(params.get("pageSize")) ?? 12, 50), sort: params.get("sort") === "createdAt" ? "createdAt" : "-createdAt" };
const orderPath = "/orders/" + orderId;
let owner;
let order;
let reading = false;
let cancelling = false;
let uncertain = false;

filter.elements.statusCode.value = query.statusCode ?? "";
filter.elements.sort.value = query.sort;
document.querySelector("[data-order-back]").hidden = !detailView;
if (detailView) title.textContent = "Chi tiết đơn hàng";

function listUrl(values) {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value != null && value !== "") search.set(key, value);
  return "./?" + search;
}

filter.addEventListener("submit", event => {
  event.preventDefault();
  location.assign(listUrl({ ...query, page: 1, statusCode: filter.elements.statusCode.value, sort: filter.elements.sort.value }));
});

function dated(value) {
  const time = element("time", "", orderTime(value));
  time.dateTime = value;
  return time;
}

function updatePageLink(selector, page) {
  const link = pager.querySelector(selector);
  if (page) {
    link.href = listUrl({ ...query, page });
    link.removeAttribute("aria-disabled");
  } else {
    link.removeAttribute("href");
    link.setAttribute("aria-disabled", "true");
  }
}

function showList({ items, pagination }) {
  list.querySelector("ul").replaceChildren(...items.map(item => {
    const row = element("li", "order-panel");
    const link = element("a");
    link.href = orderDetailUrl(item.orderId);
    const summary = element("div");
    const date = element("p");
    date.append(dated(item.createdAt), " · ", `${item.totalItems} sản phẩm`);
    summary.append(element("strong", "", item.orderCode), date, element("p", "", item.status.statusName));
    const cost = element("div");
    cost.append(element("strong", "", money(item.totalAmount)),
      element("p", "", [item.paymentMethodName, paymentStatus(item.paymentStatus)].filter(Boolean).join(" · ")));
    link.append(summary, cost);
    row.append(link);
    return row;
  }));
  const { page, totalPages, totalItems } = pagination;
  pager.hidden = !totalPages || (totalPages === 1 && page === 1);
  pager.querySelector("[data-page-label]").textContent = page <= totalPages ? `Trang ${page} / ${totalPages}` : `Trang ${page}`;
  updatePageLink("[data-prev]", page > 1 && totalPages ? Math.min(page - 1, totalPages) : null);
  updatePageLink("[data-next]", page < totalPages ? page + 1 : null);
  list.hidden = !items.length && pager.hidden;
  if (!items.length) setCatalogState(panel, "empty", totalItems ? "Trang này chưa có đơn hàng" : "Không có đơn hàng",
    query.statusCode ? "Thử chọn trạng thái khác." : totalItems ? "Dùng phân trang để quay lại kết quả." : "Đơn mới sẽ xuất hiện ở đây.");
  else setCatalogState(panel);
}

function facts(selector, values) {
  detail.querySelector(selector).replaceChildren(...values.filter(([, value]) => value != null && value !== "").map(([name, value]) => {
    const row = element("div");
    const description = element("dd");
    description.append(value);
    row.append(element("dt", "", name), description);
    return row;
  }));
}

function updateCancel() {
  cancel.hidden = order?.status.statusCode !== "PENDING" || uncertain;
  refresh.disabled = cancel.disabled = reading || cancelling;
  confirm.disabled = keep.disabled = cancelling;
}

function showDetail(data) {
  const { status, payment, items, statusHistory } = data ?? {};
  if (data?.orderId !== orderId || !status?.statusCode || !Array.isArray(items) || !Array.isArray(statusHistory))
    throw new ApiError("Phản hồi chi tiết đơn hàng không hợp lệ.", { code: "INVALID_RESPONSE" });
  title.textContent = data.orderCode;
  detail.querySelector("[data-order-overview]").replaceChildren(status.statusName, " · ", dated(data.createdAt));
  facts("[data-order-recipient]", [["Người nhận", data.recipientName], ["Điện thoại", data.recipientPhone],
    ["Địa chỉ giao hàng", data.shippingAddress], ["Ghi chú", data.note], ["Lý do hủy", data.cancelReason]]);
  detail.querySelector("[data-order-lines]").replaceChildren(...items.map(item => {
    const row = element("li");
    const copy = element("div");
    copy.append(element("strong", "", item.productName),
      element("p", "catalog-meta", [item.sku, item.sizeName, item.colorName].filter(Boolean).join(" · ")),
      element("p", "", `${item.quantity} × ${money(item.unitPrice)}`), element("p", "product-price", money(item.lineTotal)));
    row.append(productImage(item.imageUrl), copy);
    return row;
  }));
  facts("[data-order-amounts]", [["Tiền hàng", money(data.subtotal)], ["Phí vận chuyển", money(data.shippingFee)],
    ["Tổng thanh toán", money(data.totalAmount)]]);
  facts("[data-order-payment]", payment ? [["Phương thức", payment.methodName ?? payment.methodCode],
    ["Trạng thái", paymentStatus(payment.paymentStatus)], ["Số tiền phải trả", money(payment.amount)],
    ["Đã nhận", payment.receivedAmount == null ? null : money(payment.receivedAmount)],
    ["Hạn thanh toán (giờ Việt Nam)", payment.expiresAt ? dated(payment.expiresAt) : null],
    ["Ghi nhận thanh toán", payment.paidAt ? dated(payment.paidAt) : null],
    ["Ghi nhận hoàn tiền", payment.refundedAt ? dated(payment.refundedAt) : null]] : [["Thông tin", "Chưa có dữ liệu thanh toán"]]);
  detail.querySelector("[data-overdue]").hidden = payment?.isOverdue !== true;
  const bank = detail.querySelector("[data-bank-instructions]");
  bank.textContent = payment?.methodCode === "BANK_TRANSFER" && typeof payment.instructions === "string" ? payment.instructions.trim() : "";
  bank.hidden = !bank.textContent;
  detail.querySelector("[data-order-history]").replaceChildren(...statusHistory.map(entry => {
    const step = element("li");
    step.append(element("strong", "", entry.statusName), dated(entry.changedAt));
    if (entry.note) step.append(element("p", "order-text", entry.note));
    return step;
  }));
  order = data;
  detail.hidden = false;
  setCatalogState(panel);
  if (uncertain && status.statusCode !== "PENDING") {
    uncertain = false;
    showNotice(status.statusCode === "CANCELLED" ? "Đơn hàng hiện có trạng thái đã hủy theo dữ liệu mới nhất."
      : "Đơn hàng đã chuyển trạng thái và không còn cho phép hủy.");
  }
}

function showNotice(message) {
  notice.textContent = message;
  notice.hidden = !message;
}

async function load(current) {
  if (reading || current !== owner) return;
  if (detailView && !orderId) {
    setCatalogState(panel, "empty", "Không tìm thấy đơn hàng");
    return;
  }
  reading = true;
  retry.disabled = true;
  updateCancel();
  list.hidden = detail.hidden = true;
  setCatalogState(panel, "loading", "Đang tải đơn hàng");
  try {
    const { data } = await authorizedRequest(detailView ? orderPath : "/orders", {
      query: detailView ? undefined : query, signal: AbortSignal.any([current.signal, AbortSignal.timeout(12000)])
    });
    if (current !== owner) return;
    if (detailView) showDetail(data);
    else showList(data);
  } catch (error) {
    if (current !== owner) return;
    order = null;
    if (error.status === 404) setCatalogState(panel, "empty", "Không tìm thấy đơn hàng");
    else if (error.status === 403) setCatalogState(panel, "error", "Không có quyền xem đơn hàng", sessionMessage(error));
    else setCatalogState(panel, "error", "Chưa tải được đơn hàng", sessionMessage(error), true);
  } finally {
    if (current === owner) {
      retry.disabled = reading = false;
      updateCancel();
    }
  }
}

function cancelError(message) {
  errorText.textContent = message;
  errorText.hidden = !message;
  if (message) {
    reason.setAttribute("aria-invalid", "true");
    reason.focus();
  } else reason.removeAttribute("aria-invalid");
}

window.addEventListener("pageshow", event => {
  if (event.persisted && owner && !cancelling) void load(owner);
});
cancel.addEventListener("click", () => {
  if (cancel.disabled || cancel.hidden) return;
  cancelForm.reset();
  cancelError("");
  dialog.showModal();
});
keep.addEventListener("click", () => dialog.close());
dialog.addEventListener("cancel", event => { if (cancelling) event.preventDefault(); });
cancelForm.addEventListener("submit", async event => {
  event.preventDefault();
  if (cancelling || reading || uncertain || !owner || order?.status.statusCode !== "PENDING") return;
  const cancelReason = reason.value.trim();
  if (cancelReason.length < 5 || cancelReason.length > 255) {
    cancelError("Lý do hủy phải từ 5 đến 255 ký tự.");
    return;
  }
  cancelError("");
  showNotice("");
  const current = owner;
  cancelling = true;
  updateCancel();
  try {
    const reply = await authorizedRequest(orderPath + "/cancel", {
      method: "POST", expectedStatus: 200, body: { cancelReason },
      signal: AbortSignal.any([current.signal, AbortSignal.timeout(12000)])
    });
    if (owner !== current) return;
    if (reply.data?.orderId !== orderId || reply.data.status?.statusCode !== "CANCELLED")
      throw new ApiError("Phản hồi hủy đơn không hợp lệ.", { code: "INVALID_RESPONSE" });
    dialog.close();
    showNotice(reply.message || "Backend đã xác nhận hủy đơn.");
    await load(current);
  } catch (error) {
    if (owner !== current) return;
    if (error.status === 400 && error.code === "VALIDATION_ERROR") {
      cancelError(error.fields?.find(field => field.field === "cancelReason")?.message || sessionMessage(error));
      return;
    }
    dialog.close();
    uncertain = !error.status || error.status >= 500 || error.code === "INVALID_RESPONSE";
    if (uncertain) showNotice("Chưa xác định được kết quả hủy đơn. Hãy kiểm tra trạng thái trước khi gửi yêu cầu mới.");
    else showNotice(error.status === 409 ? "Đơn hàng vừa thay đổi. Đang đối chiếu trạng thái mới nhất." : sessionMessage(error));
    await load(current);
    if (owner === current && error.status === 409) {
      showNotice("Yêu cầu hủy chưa hoàn tất. Kiểm tra trạng thái mới nhất của đơn hàng.");
    }
  } finally {
    if (owner === current) {
      cancelling = false;
      updateCancel();
      if (!notice.hidden) notice.focus();
    }
  }
});

for (const button of [retry, refresh]) button.addEventListener("click", async () => {
  if (!owner || cancelling || reading) return;
  const current = owner;
  await load(current);
  if (current === owner) (retry.hidden ? title : retry).focus();
});
watchSession(({ phase, user, message }) => {
  const uid = phase === "authenticated" && user.role === "Customer" && !user.mustChangePassword ? user.userId : null;
  if (owner && uid === owner.userId) return;
  owner?.abort();
  owner = uid == null ? null : Object.assign(new AbortController(), { userId: uid });
  order = null;
  filter.hidden = !owner || detailView;
  list.hidden = detail.hidden = true;
  title.textContent = detailView ? "Chi tiết đơn hàng" : "Đơn hàng của tôi";
  detail.querySelectorAll("dl, ul, ol, [data-order-overview], [data-bank-instructions]").forEach(node => node.replaceChildren());
  list.querySelector("ul").replaceChildren();
  cancelForm.reset();
  cancelError("");
  if (dialog.open) dialog.close();
  reading = cancelling = uncertain = false;
  retry.disabled = false;
  updateCancel();
  showNotice("");
  login.hidden = phase !== "anonymous";
  if (owner) void load(owner);
  else if (phase === "anonymous") setCatalogState(panel, "empty", "Đăng nhập để xem đơn hàng", message || "Dùng tài khoản khách hàng để tiếp tục.");
  else if (phase === "authenticated" && !user.mustChangePassword)
    setCatalogState(panel, "empty", "Lịch sử đơn hàng dành cho khách hàng");
  else setCatalogState(panel, "loading", "Đang kiểm tra phiên đăng nhập");
});
