import { authorizedRequest, sessionMessage } from "../auth.js";
import { startAdminSession } from "../admin-session.js";
import { money } from "../catalog.js";
import { adminOrderDetailUrl, orderTime, paymentStatus } from "../orders.js";
import { renderState } from "../ui.js";

const form = document.querySelector("[data-revenue-filter]");
const dateError = form.querySelector("#date-error");
const revenueOutput = document.querySelector("[data-revenue-output]");
const ordersOutput = document.querySelector("[data-recent-output]");
const bars = revenueOutput.querySelector("[data-revenue-bars]");
const revenueRows = revenueOutput.querySelector("[data-revenue-rows]");
const statusRows = revenueOutput.querySelector("[data-order-statuses]");
const orderRows = ordersOutput.querySelector("[data-recent-orders]");
const refreshOrders = document.querySelector("[data-orders-reload]");
const number = new Intl.NumberFormat("vi-VN");
const vietnam = new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Ho_Chi_Minh", year: "numeric", month: "2-digit", day: "2-digit" });
const statusLabels = new Map([
  ["PENDING", "Chờ xác nhận"], ["CONFIRMED", "Đã xác nhận"], ["SHIPPING", "Đang giao"],
  ["DELIVERED", "Đã giao"], ["CANCELLED", "Đã hủy"]
]);
let revenueQuery;

function node(tag, text, className) {
  const item = document.createElement(tag);
  if (text != null) item.textContent = text;
  if (className) item.className = className;
  return item;
}

function resetDates() {
  // Subtract calendar days from the local date, independent of the browser timezone.
  const parts = Object.fromEntries(vietnam.formatToParts(new Date()).map(part => [part.type, part.value]));
  const today = `${parts.year}-${parts.month}-${parts.day}`;
  const start = new Date(today + "T00:00:00Z");
  start.setUTCDate(start.getUTCDate() - 6);
  form.elements.from.value = start.toISOString().slice(0, 10);
  form.elements.to.value = today;
  form.elements.groupBy.value = "day";
  revenueQuery = readQuery();
  showDateError("");
}

function readQuery() {
  return Object.fromEntries(new FormData(form));
}

function showDateError(message, field) {
  dateError.hidden = !message;
  dateError.textContent = message;
  for (const input of form.elements) input.removeAttribute("aria-invalid");
  if (field) {
    field.setAttribute("aria-invalid", "true");
    field.focus();
  }
}

function validRange(query) {
  function timestamp(value) {
    return /^\d{4}-\d{2}-\d{2}$/.test(value) && !value.startsWith("0000") ? Date.parse(value + "T00:00:00Z") : NaN;
  }
  const from = timestamp(query.from);
  const to = timestamp(query.to);
  if (!Number.isFinite(from)) showDateError("Chọn ngày bắt đầu hợp lệ.", form.elements.from);
  else if (!Number.isFinite(to)) showDateError("Chọn ngày kết thúc hợp lệ.", form.elements.to);
  else if (from > to) showDateError("Ngày kết thúc phải từ ngày bắt đầu trở đi.", form.elements.to);
  else if ((to - from) / 86400000 >= 366) showDateError("Khoảng thời gian tối đa 366 ngày.", form.elements.to);
  else return true;
  return false;
}

function periodLabel(period, grouping) {
  const [year, month, day] = period.split("-");
  return grouping === "month" ? `${month}/${year}` : `${day}/${month}`;
}

function tableRow(heading, ...cells) {
  const row = node("tr");
  const title = node("th");
  title.append(heading);
  title.scope = "row";
  row.append(title, ...cells.map(value => {
    const cell = node("td");
    cell.append(value);
    return cell;
  }));
  return row;
}

function renderRevenue(data, query) {
  const { summary, series, ordersByStatus } = data;
  const metrics = [...revenueOutput.querySelectorAll("[data-metric]")];
  if (!summary || !metrics.every(item => Number.isFinite(summary[item.dataset.metric])) || !Array.isArray(series))
    throw new Error("Invalid revenue response");
  for (const item of metrics) {
    const key = item.dataset.metric;
    item.textContent = key === "totalOrders" ? number.format(summary[key]) : money(summary[key]);
  }
  revenueOutput.querySelector("[data-report-period]").textContent = `${data.from} đến ${data.to}`;
  revenueOutput.querySelector("[data-chart-frequency]").textContent = query.groupBy === "month"
    ? "Tiền hàng theo tháng" : "Tiền hàng theo ngày";
  const maximum = Math.max(0, ...series.map(bucket => bucket.subtotal));
  bars.replaceChildren(...series.map(bucket => {
    const item = node("li");
    const column = node("span", null, "bar-column");
    column.setAttribute("aria-hidden", "true");
    column.title = `${bucket.period} · ${money(bucket.subtotal)}`;
    const fill = node("span", null, "bar-fill");
    const height = maximum > 0 && Number.isFinite(bucket.subtotal) ? 100 * Math.max(0, bucket.subtotal) / maximum : 0;
    fill.style.setProperty("--bar", height + "%");
    column.append(fill);
    item.append(column, node("span", periodLabel(bucket.period, query.groupBy), "bar-label"));
    item.setAttribute("aria-label", column.title);
    return item;
  }));
  revenueRows.replaceChildren(...series.map(bucket => tableRow(bucket.period, money(bucket.subtotal), money(bucket.shippingFee),
    money(bucket.totalAmount), number.format(bucket.orderCount))));
  revenueOutput.querySelector("[data-chart-scroll]").hidden = !series.length;
  revenueOutput.querySelector("[data-series-details]").hidden = !series.length;
  revenueOutput.querySelector("[data-series-empty]").hidden = Boolean(series.length);

  const statuses = Array.isArray(ordersByStatus) ? ordersByStatus : [];
  statusRows.replaceChildren(...statuses.map(status => {
    const entry = node("li");
    entry.append(node("span", statusLabels.get(status.statusCode) ?? status.statusCode),
      node("strong", number.format(status.count)));
    return entry;
  }));
  const statusEmpty = revenueOutput.querySelector("[data-status-empty]");
  statusEmpty.hidden = Boolean(statuses.length);
  statusEmpty.textContent = Array.isArray(ordersByStatus) ? "Không có đơn trong khoảng ngày đã chọn." : "API chưa cung cấp số đơn theo trạng thái.";
}

function renderOrders(data) {
  orderRows.replaceChildren(...data.items.map(order => {
    const payment = node("div", order.paymentMethodCode === "COD" ? "COD" : order.paymentMethodCode === "BANK_TRANSFER"
      ? "Chuyển khoản" : order.paymentMethodCode);
    payment.append(node("small", paymentStatus(order.paymentStatus), "payment-status"));
    const url = adminOrderDetailUrl(order.orderId);
    const code = url ? node("a", order.orderCode, "admin-order-link") : order.orderCode;
    if (url) code.href = url;
    return tableRow(code, orderTime(order.createdAt), order.status.statusName, payment, money(order.totalAmount));
  }));
  return data.items.length > 0;
}

function resource(path, state, output, display, setBusy, onError) {
  let active;
  const clear = () => {
    active?.abort();
    active = null;
    output.hidden = state.hidden = true;
    setBusy(false);
  };
  async function load(query) {
    clear();
    const request = active = new AbortController();
    setBusy(true);
    state.hidden = false;
    renderState(state, "loading", "Đang tải dữ liệu", "");
    try {
      const { data } = await authorizedRequest(path, {
        query, signal: AbortSignal.any([request.signal, AbortSignal.timeout(12000)])
      });
      if (request !== active) return;
      if (display(data, query) === false) renderState(state, "empty", "Chưa có đơn hàng", "");
      else {
        output.hidden = false;
        state.hidden = true;
      }
    } catch (error) {
      if (request !== active) return;
      renderState(state, "error", error.status === 403 ? "Không có quyền xem dữ liệu" : "Chưa tải được dữ liệu", sessionMessage(error));
      onError?.(error);
    } finally {
      if (request === active) setBusy(false);
    }
  }
  return { clear, load };
}

const revenueState = document.querySelector("[data-revenue-state]");
const retryRevenue = revenueState.querySelector("[data-revenue-retry]");
const revenue = resource("/admin/statistics/revenue", revenueState, revenueOutput, renderRevenue,
  busy => { retryRevenue.hidden = busy; }, error => {
    const field = error.fields?.find(item => ["from", "to", "groupBy"].includes(item.field));
    if (error.code === "VALIDATION_ERROR" && field) showDateError(field.message, form.elements[field.field]);
  });
const recent = resource("/admin/orders", document.querySelector("[data-orders-state]"), ordersOutput, renderOrders,
  busy => { refreshOrders.disabled = busy; });
const loadRecent = () => recent.load({ page: 1, pageSize: 5, sort: "-createdAt" });

function clearDashboard() {
  revenue.clear();
  recent.clear();
  revenueOutput.querySelector("[data-report-period]").textContent = "";
  revenueOutput.querySelector("[data-series-details]").open = false;
  showDateError("");
  for (const item of revenueOutput.querySelectorAll("[data-metric]")) item.textContent = "";
  for (const list of [bars, revenueRows, statusRows, orderRows]) list.replaceChildren();
}

form.addEventListener("submit", event => {
  event.preventDefault();
  showDateError("");
  const query = readQuery();
  if (!validRange(query)) return;
  revenueQuery = query;
  void revenue.load(query);
});
retryRevenue.addEventListener("click", () => {
  showDateError("");
  void revenue.load(revenueQuery);
});
refreshOrders.addEventListener("click", () => { void loadRecent(); });
startAdminSession({
  account: new URL("../account/", location.href),
  clear: clearDashboard,
  load() {
    resetDates();
    void revenue.load(revenueQuery);
    void loadRecent();
  }
});
