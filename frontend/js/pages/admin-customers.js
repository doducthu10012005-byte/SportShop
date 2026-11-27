import { element, field } from "../admin-catalog-form.js";
import { paymentJson, paymentMoney } from "../admin-payments.js";
import { startAdminSession } from "../admin-session.js";
import { button, operationDialog, pagination, readPanel, table, tableRow } from "../admin-stock-ui.js";
import { ApiError } from "../api.js";
import { authorizedRequest } from "../auth.js";
import { positiveInt } from "../catalog.js";
import { orderTime } from "../orders.js";

const params = new URLSearchParams(location.search);
const detail = params.has("id");
const userId = positiveInt(params.get("id"));
const path = "/admin/customers";
const workspace = document.querySelector("[data-customers-workspace]");
const notice = element("p", null, { "data-feedback": "", role: "status", hidden: "" });
const number = new Intl.NumberFormat("vi-VN");
const genders = new Map([["Male", "Nam"], ["Female", "Nữ"], ["Other", "Khác"]]);
if (detail) document.querySelector("[data-page-title]").textContent = "Chi tiết khách hàng";

function feedback(message, warning = false) {
  notice.textContent = message;
  notice.hidden = !message;
  notice.toggleAttribute("data-warning", warning);
}
const dialog = operationDialog(feedback);
let session;

function date(value) {
  return value ? element("time", orderTime(value), { datetime: value }) : "—";
}

function birthday(value) {
  const parts = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? "");
  return parts ? element("time", parts[3] + "/" + parts[2] + "/" + parts[1], { datetime: value }) : "—";
}

function accountStatus(active) {
  const badge = element("span", active ? "Đang hoạt động" : "Đã khóa", { class: "management-status", "data-customer-status": "" });
  badge.toggleAttribute("data-active", active);
  return badge;
}

function invalidResponse() {
  return new ApiError("Phản hồi dữ liệu khách hàng không hợp lệ.", { code: "INVALID_RESPONSE" });
}

function validCustomer(customer) {
  return Number.isInteger(customer?.userId) && customer.userId > 0 && typeof customer.fullName === "string" &&
    typeof customer.email === "string" && typeof customer.isActive === "boolean";
}

function list(signal) {
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Danh sách khách hàng" });
  panel.append(element("h2", "Danh sách khách hàng"));
  const filter = element("form", null, { class: "management-filters customers-filters", "data-customers-filter": "" });
  field(filter, "keyword", "Tìm khách hàng", { type: "search", placeholder: "Tên, email hoặc số điện thoại" });
  field(filter, "isActive", "Trạng thái tài khoản", {
    options: [["true", "Đang hoạt động"], ["false", "Đã khóa"]], placeholder: "Tất cả"
  });
  field(filter, "sort", "Sắp xếp", {
    options: [["-createdAt", "Đăng ký mới nhất"], ["createdAt", "Đăng ký cũ nhất"],
      ["fullName", "Tên A–Z"], ["-fullName", "Tên Z–A"], ["email", "Email A–Z"], ["-email", "Email Z–A"],
      ["-isActive", "Đang hoạt động trước"], ["isActive", "Đã khóa trước"], ["userId", "Mã tăng dần"], ["-userId", "Mã giảm dần"]],
    value: "-createdAt"
  });
  field(filter, "pageSize", "Số dòng mỗi trang", { options: [[20, "20"], [10, "10"], [50, "50"]], value: 20 });
  filter.append(button("Áp dụng", { type: "submit" }));
  panel.append(filter);

  const grid = table("Danh sách khách hàng", ["Mã khách hàng", "Họ tên", "Email", "Số điện thoại", "Ngày đăng ký", "Trạng thái"]);
  const output = element("div", null, { "data-customers-list": "" });
  let query = { page: 1, pageSize: 20, sort: "-createdAt" };
  const pages = pagination(page => { query = { ...query, page }; void load(); });
  output.append(grid.region, pages.root);
  const load = readPanel(panel, output, signal,
    async requestSignal => (await authorizedRequest(path, { query, signal: requestSignal })).data,
    ({ items, pagination: meta }) => {
      if (!Array.isArray(items) || !items.every(validCustomer) || !Number.isInteger(meta?.page) ||
        !Number.isInteger(meta.totalPages) || !Number.isInteger(meta.totalItems)) throw invalidResponse();
      if (meta.page > 1 && meta.page > meta.totalPages) {
        query = { ...query, page: Math.max(1, meta.totalPages) };
        void load();
        return false;
      }
      grid.body.replaceChildren(...items.map(customer => {
        const name = element("a", customer.fullName, { href: "?id=" + customer.userId, class: "admin-order-link" });
        const row = tableRow(grid.body, customer.userId, name, customer.email, customer.phoneNumber,
          date(customer.createdAt), accountStatus(customer.isActive));
        row.dataset.customerId = customer.userId;
        return row;
      }));
      pages.update(meta);
      return items.length > 0;
    }, "Không tìm thấy khách hàng");

  filter.addEventListener("submit", event => {
    event.preventDefault();
    const values = Object.fromEntries(new FormData(filter));
    values.keyword = values.keyword.trim();
    query = { ...Object.fromEntries(Object.entries(values).filter(([, value]) => value !== "")), page: 1 };
    void load();
  });
  workspace.append(panel);
  void load();
}

function profileFacts(customer) {
  const facts = element("dl", null, { class: "customer-facts", "data-customer-facts": "" });
  const data = [
    ["Mã khách hàng", customer.userId], ["Email", customer.email], ["Số điện thoại", customer.phoneNumber],
    ["Ngày sinh", birthday(customer.dateOfBirth)], ["Giới tính", genders.get(customer.gender)],
    ["Ngày đăng ký (giờ Việt Nam)", date(customer.createdAt)], ["Lần đăng nhập gần nhất", date(customer.lastLoginAt)],
    ["Tổng số đơn", number.format(customer.totalOrders)], ["Chi tiêu từ đơn đã giao", paymentMoney(customer.totalSpent)]
  ];
  for (const [label, value] of data) {
    const pair = element("div");
    const answer = element("dd");
    answer.append(value ?? "—");
    pair.append(element("dt", label), answer);
    facts.append(pair);
  }
  return facts;
}

function profile(signal) {
  const panel = element("section", null, { class: "admin-panel catalog-management", "aria-label": "Chi tiết khách hàng" });
  panel.append(element("a", "← Danh sách khách hàng", { href: "./", class: "admin-order-link" }));
  workspace.append(panel);
  if (!userId) {
    panel.append(element("p", "Mã khách hàng không hợp lệ.", { role: "alert", "data-customer-invalid": "" }));
    return;
  }
  const customerPath = path + "/" + userId;
  const output = element("div", null, { class: "catalog-management", "data-customer-detail": "" });
  const load = readPanel(panel, output, signal,
    async requestSignal => (await authorizedRequest(customerPath, { signal: requestSignal, jsonReviver: paymentJson })).data,
    customer => {
      if (!validCustomer(customer) || customer.userId !== userId || !Number.isInteger(customer.totalOrders)) throw invalidResponse();
      const heading = element("div", null, { class: "admin-panel-heading" });
      const identity = element("div", null, { class: "customer-heading" });
      identity.append(element("h2", customer.fullName), accountStatus(customer.isActive));
      const actions = element("div", null, { class: "management-actions" });
      const reload = button("Tải lại", { "data-customer-reload": "" });
      reload.addEventListener("click", () => void load());
      const change = button(customer.isActive ? "Khóa tài khoản" : "Mở khóa tài khoản", { "data-change-customer-status": "" });
      change.addEventListener("click", () => confirmStatus(customer));
      actions.append(reload, change);
      heading.append(identity, actions);
      output.replaceChildren(heading, profileFacts(customer));
    }, "Không tìm thấy khách hàng");

  function confirmStatus(customer) {
    const active = !customer.isActive;
    const action = active ? "Mở khóa tài khoản" : "Khóa tài khoản";
    void dialog.open(signal, {
      title: action, label: "Xác nhận " + (active ? "mở khóa" : "khóa"), path: customerPath + "/status",
      method: "PUT", success: active ? "Đã mở khóa tài khoản." : "Đã khóa tài khoản.",
      reconcileRejection: true, reload: load,
      unknownMessage: "Chưa xác định được kết quả. Hãy kiểm tra trạng thái tài khoản sau khi tải lại trước khi thao tác tiếp.",
      render(fields) {
        const who = element("p", customer.fullName + " · #" + customer.userId, { class: "management-wide" });
        const note = element("p", active ? "Khách hàng sẽ có thể đăng nhập lại."
          : "Khách hàng sẽ không thể đăng nhập mới. Phiên làm mới đăng nhập sẽ bị thu hồi.", { class: "management-wide" });
        fields.append(who, note);
      },
      payload: () => ({ body: { isActive: active } })
    });
  }
  void load();
}

startAdminSession({
  account: new URL("../../account/", location.href),
  view: "customers",
  customerId: userId,
  clear() {
    session?.abort();
    dialog.dismiss();
    feedback("");
    workspace.replaceChildren();
  },
  load() {
    session = new AbortController();
    workspace.append(notice);
    if (detail) profile(session.signal);
    else list(session.signal);
  }
});
