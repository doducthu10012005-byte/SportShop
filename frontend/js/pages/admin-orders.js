import { element, field } from "../admin-catalog-form.js";
import { startAdminSession } from "../admin-session.js";
import { button, operationDialog, pagination, readPanel, table, tableRow } from "../admin-stock-ui.js";
import { ApiError } from "../api.js";
import { authorizedRequest } from "../auth.js";
import { money, positiveInt, productImage } from "../catalog.js";
import { adminOrderDetailUrl, orderTime, paymentStatus } from "../orders.js";

const states = [
  ["PENDING", "Chờ xác nhận"], ["CONFIRMED", "Đã xác nhận"], ["SHIPPING", "Đang giao"],
  ["DELIVERED", "Đã giao"], ["CANCELLED", "Đã hủy"]
];
const paymentStates = ["Pending", "Paid", "Failed", "Expired", "RefundPending", "Refunded"];
const actions = {
  CONFIRMED: "Xác nhận đơn", SHIPPING: "Chuyển sang giao hàng", DELIVERED: "Xác nhận đã giao", CANCELLED: "Hủy đơn"
};
const flow = new Map([["PENDING", ["CONFIRMED", "CANCELLED"]], ["CONFIRMED", ["SHIPPING", "CANCELLED"]],
  ["SHIPPING", ["DELIVERED"]]]);
const params = new URLSearchParams(location.search);
const detail = params.has("id");
const orderId = positiveInt(params.get("id"));
const workspace = document.querySelector("[data-orders-workspace]");
const title = document.querySelector("[data-page-title]");
title.textContent = detail ? "Chi tiết đơn hàng" : "Đơn hàng";
const notice = element("p", null, { "data-feedback": "", role: "status", hidden: "" });
function feedback(message, warning = false) {
  notice.textContent = message;
  notice.hidden = !message;
  notice.toggleAttribute("data-warning", warning);
}
const dialog = operationDialog(feedback);
let session;

function date(value) {
  return element("time", orderTime(value), { datetime: value });
}

function detailLink(order) {
  const url = adminOrderDetailUrl(order.orderId);
  return url ? element("a", order.orderCode, { href: url, class: "admin-order-link" }) : order.orderCode;
}

function showList(signal) {
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Danh sách đơn hàng" });
  panel.append(element("h2", "Danh sách đơn hàng"));
  const filter = element("form", null, { class: "management-filters orders-admin-filters", "data-admin-orders-filter": "" });
  field(filter, "keyword", "Tìm đơn hàng", { type: "search", placeholder: "Mã đơn, tên hoặc SĐT người nhận" });
  field(filter, "statusCode", "Trạng thái đơn", { options: states, placeholder: "Tất cả" });
  field(filter, "paymentStatus", "Thanh toán", { options: paymentStates.map(code => [code, paymentStatus(code)]), placeholder: "Tất cả" });
  field(filter, "from", "Từ ngày (giờ Việt Nam)", { type: "date", "aria-describedby": "orders-date-error" });
  const to = field(filter, "to", "Đến ngày (giờ Việt Nam)", { type: "date", "aria-describedby": "orders-date-error" });
  field(filter, "sort", "Sắp xếp", { options: [["-createdAt", "Mới nhất"], ["createdAt", "Cũ nhất"]], value: "-createdAt" });
  field(filter, "pageSize", "Số dòng mỗi trang", { options: [[20, "20"], [10, "10"], [50, "50"]], value: 20 });
  const invalid = element("p", null, { id: "orders-date-error", class: "form-error orders-admin-filter-error", role: "alert", hidden: "" });
  filter.append(button("Áp dụng", { type: "submit" }), invalid);
  panel.append(filter);

  const grid = table("Danh sách đơn hàng", ["Mã đơn", "Ngày đặt (giờ Việt Nam)", "Khách hàng", "Trạng thái", "Thanh toán", "Tổng tiền"]);
  const output = element("div", null, { "data-admin-orders-list": "" });
  let query = { page: 1, pageSize: 20, sort: "-createdAt" };
  const pages = pagination(page => { query = { ...query, page }; void load(); });
  output.append(grid.region, pages.root);
  const load = readPanel(panel, output, signal,
    async requestSignal => (await authorizedRequest("/admin/orders", { query, signal: requestSignal })).data,
    ({ items, pagination: meta }) => {
      if (meta.page > 1 && meta.page > meta.totalPages) {
        query = { ...query, page: Math.max(1, meta.totalPages) };
        void load();
        return false;
      }
      grid.body.replaceChildren(...items.map(order => {
        const payment = element("span", paymentStatus(order.paymentStatus));
        const method = order.paymentMethodCode === "BANK_TRANSFER" ? "Chuyển khoản" : order.paymentMethodCode;
        if (method) payment.append(element("small", method, { class: "payment-status" }));
        const row = tableRow(grid.body, detailLink(order), date(order.createdAt), order.customer?.fullName,
          order.status.statusName, payment, money(order.totalAmount));
        row.dataset.orderId = order.orderId;
        return row;
      }));
      pages.update(meta);
      return items.length > 0;
    }, "Không tìm thấy đơn hàng");

  filter.addEventListener("submit", event => {
    event.preventDefault();
    const values = Object.fromEntries(new FormData(filter));
    if (values.from && values.to && values.from > values.to) {
      invalid.textContent = "Ngày kết thúc phải từ ngày bắt đầu trở đi.";
      invalid.hidden = false;
      to.setAttribute("aria-invalid", "true");
      to.focus();
      return;
    }
    invalid.hidden = true;
    invalid.textContent = "";
    to.removeAttribute("aria-invalid");
    values.keyword = values.keyword.trim();
    query = { ...Object.fromEntries(Object.entries(values).filter(([, value]) => value !== "")), page: 1 };
    void load();
  });
  workspace.append(panel);
  void load();
}

function facts(label, values, marker) {
  const section = element("section", null, { [marker]: "" });
  const list = element("dl");
  for (const [name, value] of values) {
    if (value == null || value === "") continue;
    const pair = element("div");
    const answer = element("dd");
    answer.append(value);
    pair.append(element("dt", name), answer);
    list.append(pair);
  }
  section.append(element("h3", label), list);
  return section;
}

function itemsPanel(items) {
  const panel = element("section");
  panel.append(element("h3", "Sản phẩm lúc đặt hàng"));
  const grid = table("Sản phẩm lúc đặt hàng", ["Sản phẩm", "Đơn giá", "Số lượng", "Thành tiền"]);
  for (const item of items) {
    const product = element("div", null, { class: "orders-admin-item" });
    const title = element("span", item.productName);
    title.append(element("small", [item.sku, item.sizeName, item.colorName].filter(Boolean).join(" · ")));
    product.append(productImage(item.imageUrl), title);
    tableRow(grid.body, product, money(item.unitPrice), item.quantity, money(item.lineTotal));
  }
  panel.append(items.length ? grid.region : element("p", "Đơn chưa có dòng sản phẩm."));
  return panel;
}

function historyPanel(history) {
  const panel = element("section", null, { "data-admin-order-history": "" });
  const list = element("ol", null, { class: "orders-admin-history" });
  for (const change of history) {
    const step = element("li");
    step.append(element("strong", change.statusName), " · ", date(change.changedAt));
    if (change.note) step.append(element("p", change.note));
    list.append(step);
  }
  panel.append(element("h3", "Lịch sử trạng thái"), history.length ? list : element("p", "Chưa có lịch sử."));
  return panel;
}

function showDetail(signal, id) {
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Chi tiết đơn hàng" });
  panel.append(element("a", "← Danh sách đơn hàng", { href: "./", class: "admin-order-link" }));
  if (!id) {
    panel.append(element("p", "Mã định danh đơn hàng không hợp lệ.", { role: "alert" }));
    workspace.append(panel);
    return;
  }
  const output = element("div", null, { class: "orders-admin-detail", "data-admin-order-detail": "" });
  const path = "/admin/orders/" + id;
  const load = readPanel(panel, output, signal,
    async requestSignal => (await authorizedRequest(path, { signal: requestSignal })).data,
    order => {
      if (order?.orderId !== id || !order.status || !Array.isArray(order.items) || !Array.isArray(order.statusHistory)) {
        throw new ApiError("Phản hồi chi tiết đơn hàng không hợp lệ.", { code: "INVALID_RESPONSE" });
      }
      const heading = element("div", null, { class: "admin-panel-heading" });
      const summary = element("div", null, { class: "orders-admin-summary" });
      summary.append(element("h2", order.orderCode, { class: "orders-admin-title" }),
        element("span", order.status.statusName, { class: "management-status", "data-current-status": "" }), date(order.createdAt));
      const reload = button("Tải lại đơn", { "data-reload-order": "" });
      reload.addEventListener("click", () => void load());
      heading.append(summary, reload);
      const info = element("div", null, { class: "orders-admin-facts" });
      const payment = order.payment;
      info.append(facts("Khách hàng và giao hàng", [
        ["Khách hàng", order.customer?.fullName], ["Mã khách hàng", order.customer?.userId],
        ["Người nhận", order.recipientName], ["Điện thoại", order.recipientPhone], ["Địa chỉ giao hàng", order.shippingAddress],
        ["Ghi chú đặt hàng", order.note], ["Lý do hủy", order.cancelReason]
      ], "data-admin-order-recipient"), facts("Thanh toán", payment ? [
        ["Phương thức", payment.methodName ?? payment.methodCode], ["Trạng thái", paymentStatus(payment.paymentStatus)],
        ["Mã giao dịch", payment.transactionCode], ["Số tiền phải trả", money(payment.amount)],
        ["Đã nhận", payment.receivedAmount == null ? null : money(payment.receivedAmount)]
      ] : [["Thông tin", "Chưa có dữ liệu thanh toán"]], "data-admin-order-payment"),
      facts("Tổng tiền", [["Tiền hàng", money(order.subtotal)], ["Phí vận chuyển", money(order.shippingFee)],
        ["Tổng thanh toán", money(order.totalAmount)]], "data-admin-order-totals"));
      output.replaceChildren(heading, transitions(order), info, itemsPanel(order.items), historyPanel(order.statusHistory));
    }, "Không tìm thấy đơn hàng");

  function transitions(order) {
    const section = element("section", null, {
      class: "orders-admin-transitions", "aria-label": "Chuyển trạng thái", "data-order-transitions": ""
    });
    const method = order.payment?.methodCode;
    const transferUnpaid = method === "BANK_TRANSFER" && order.payment.paymentStatus !== "Paid";
    const options = flow.get(order.status.statusCode) ?? [];
    for (const target of options) {
      if (["SHIPPING", "DELIVERED"].includes(target) && (!method || transferUnpaid)) {
        section.append(element("p", transferUnpaid ? "Đơn chuyển khoản cần được thanh toán trước khi chuyển trạng thái giao hàng."
          : "Chưa có thông tin thanh toán để chuyển trạng thái giao hàng."));
        continue;
      }
      const action = button(actions[target], { "data-transition": target });
      action.addEventListener("click", () => confirm(order, target));
      section.append(action);
    }
    if (!options.length) section.append(element("p", "Đơn hàng không còn thao tác chuyển trạng thái."));
    return section;
  }

  function confirm(order, target) {
    const cancelling = target === "CANCELLED";
    const input = cancelling ? "cancelReason" : "note";
    void dialog.open(signal, {
      title: actions[target], label: cancelling ? "Xác nhận hủy" : "Xác nhận cập nhật",
      path: path + "/status", method: "PUT", success: "Đã cập nhật trạng thái đơn.",
      reload: load, reconcileRejection: true,
      render(fields) {
        fields.append(element("p", "Đơn hàng " + order.orderCode, { class: "management-wide" }));
        field(fields, input, cancelling ? "Lý do hủy (5–255 ký tự)" : "Ghi chú (tùy chọn)", {
          multiline: true, wide: true, maxlength: 255, rows: 3
        });
      },
      payload(form) {
        const value = form.elements[input].value.trim();
        if (cancelling && value.length < 5) return { error: "Nhập lý do hủy từ 5 đến 255 ký tự.", field: input };
        return { body: { statusCode: target, ...(value ? { [input]: value } : {}) } };
      }
    });
  }
  workspace.append(panel);
  void load();
}

startAdminSession({
  account: new URL("../../account/", location.href),
  view: "orders",
  orderId,
  clear() {
    session?.abort();
    dialog.dismiss();
    feedback("");
    workspace.replaceChildren();
  },
  load() {
    session = new AbortController();
    workspace.append(notice);
    if (detail) showDetail(session.signal, orderId);
    else showList(session.signal);
  }
});
