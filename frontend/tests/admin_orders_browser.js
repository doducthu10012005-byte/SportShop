import { API_BASE_URL } from "/js/config.js";

const listPath = "/admin/orders/";
const checks = [], errors = [];
const control = async (action = "") => (await fetch("/__fixture?action=" + action)).json();
const requests = async () => (await control()).adminOrderRequests;
const writes = async id => (await requests()).filter(r => r.method === "PUT" && (!id || r.path === "admin/orders/" + id + "/status"));
const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const text = (frame, selector) => find(frame, selector)?.textContent.trim() ?? "";
const shown = (frame, selector) => find(frame, selector)?.hidden === false;
const click = (frame, selector) => find(frame, selector).click();
const row = id => `[data-order-id="${id}"]`;
const details = "[data-admin-order-detail]";
const list = "[data-admin-orders-list]";
const error = "[data-operation-error]";
const confirm = "[data-operation-save]";
const close = "[data-operation-close]";
const status = frame => text(frame, "[data-current-status]");
const action = code => `[data-transition="${code}"]`;
const filter = frame => find(frame, "[data-admin-orders-filter]");

function assert(ok, label) {
  if (!ok) throw Error(label);
  checks.push(label);
}

async function until(condition, label) {
  for (let i = 0; i < 200; i++) {
    if (await condition()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw Error("Timeout " + label);
}

async function open(path) {
  const frame = document.createElement("iframe");
  frame.width = 1440;
  frame.height = 850;
  frame.addEventListener("load", () => {
    frame.contentWindow.addEventListener("error", event => { if (event.error) errors.push(event.error.name); });
    frame.contentWindow.addEventListener("unhandledrejection", () => errors.push("unhandled rejection"));
  });
  frame.src = path;
  document.body.append(frame);
  await until(() => frame.contentDocument?.readyState === "complete" && frame.contentWindow.location.pathname !== "blank", path);
  return frame;
}

function login(frame, account) {
  const form = find(frame, "[data-account-form=login]");
  form.elements.email.value = account + "@example.test";
  form.elements.password.value = "Test-Password123";
  form.requestSubmit();
}

async function logout(frame, selector) {
  click(frame, selector);
  await until(() => shown(frame, "[data-account-view=login]"), "logout");
}

async function detail(id) {
  const frame = await open(listPath + "?id=" + id);
  await until(() => shown(frame, details), "order " + id);
  return frame;
}

function values(frame, fields) {
  for (const [name, value] of Object.entries(fields)) filter(frame).elements.namedItem(name).value = value;
  filter(frame).requestSubmit();
}

function setNote(frame, name, value) {
  find(frame, "[data-operation-form]").elements.namedItem(name).value = value;
}

try {
  const orders = await open(listPath);
  await until(() => shown(orders, "[data-account-view=login]"), "guest");
  assert(!(await requests()).length && orders.contentWindow.location.search.includes("view=orders"), "guest redirects before private reads");
  login(orders, "customer");
  await until(() => shown(orders, "[data-account-view=profile]"), "customer");
  const denied = await open(listPath + "?id=1");
  await until(() => text(denied, "[data-guard-message]").includes("không có quyền"), "customer denied");
  assert(!(await requests()).length && !shown(denied, details), "Customer sees no Admin data");
  denied.remove();
  await logout(orders, "[data-account-view=profile] [data-logout]");
  login(orders, "forced");
  await until(() => shown(orders, "[data-account-view=password]"), "forced password");
  assert(!(await requests()).length, "MustChangePassword blocks order access");
  await logout(orders, "[data-account-view=password] [data-logout]");
  login(orders, "admin");
  await until(() => shown(orders, list), "Admin list");
  assert(text(orders, "[data-admin-email]") === "admin@example.test" && !!find(orders, row(23)) &&
    orders.contentDocument.querySelectorAll("tbody tr").length === 20, "Admin list shows newest 20 records");
  const date = find(orders, row(23) + " time");
  assert(date.textContent.includes("24/11") && date.textContent.includes("05:00") &&
    text(orders, row(23)).includes("Khách thử 23") && text(orders, row(23)).includes("248.000"),
    "list includes customer, totals and Vietnam time across midnight");
  click(orders, list + " [data-next]");
  await until(() => !!find(orders, row(1)), "second page");
  assert(text(orders, list).includes("2/2") && orders.contentDocument.querySelectorAll("tbody tr").length === 3,
    "pagination uses Backend metadata");
  const prior = (await requests()).length;
  values(orders, {from: "2026-11-30", to: "2026-11-01"});
  assert(find(orders, "#orders-date-error").hidden === false && (await requests()).length === prior, "invalid dates blocked before request");
  values(orders, {keyword: " 0901234567 ", statusCode: "CONFIRMED", paymentStatus: "Paid",
    from: "2026-11-04", to: "2026-11-04", sort: "createdAt", pageSize: "10"});
  await until(() => text(orders, list).includes("1 kết quả") && !!find(orders, row(3)), "filters");
  const query = (await requests()).at(-1).query;
  assert(query.keyword[0] === "0901234567" && query.statusCode[0] === "CONFIRMED" && query.paymentStatus[0] === "Paid" &&
    query.from[0] === "2026-11-04" && query.to[0] === "2026-11-04" && query.sort[0] === "createdAt" &&
    query.pageSize[0] === "10" && query.page[0] === "1", "all documented filters and sorting reach Backend");
  values(orders, {keyword: "no-match"});
  await until(() => find(orders, "[data-state-title]").textContent === "Không tìm thấy đơn hàng", "empty list");
  assert(!shown(orders, list), "empty view hides previous results");

  const cod = await detail(1);
  assert(text(cod, "[data-admin-order-recipient]").includes("Khách thử 1") &&
    text(cod, "[data-admin-order-recipient]").includes("Snapshot giao hàng <b data-injected>QA</b>") &&
    text(cod, details).includes("Giày snapshot") && text(cod, details).includes("SNAP-1") &&
    text(cod, "[data-admin-order-totals]").includes("28.000") && !find(cod, "[data-injected]"),
    "detail shows recipient and item snapshots, totals and safely rendered text");
  assert(!!find(cod, action("CONFIRMED")) && !!find(cod, action("CANCELLED")) && !find(cod, action("SHIPPING")),
    "PENDING offers only confirm and cancel");
  click(cod, action("CONFIRMED"));
  setNote(cod, "note", "  Đã kiểm hàng  ");
  await control("admin-order-hold");
  click(cod, confirm);
  click(cod, confirm);
  await until(async () => (await writes(1)).length === 1, "one held write");
  assert(find(cod, confirm).disabled && status(cod) === "Chờ xác nhận", "double-click blocked with no optimistic update");
  await control("admin-order-release");
  await until(() => shown(cod, details) && status(cod) === "Đã xác nhận", "confirmed");
  let recorded = await writes(1);
  assert(recorded.length === 1 && JSON.stringify(recorded[0].body) === '{"statusCode":"CONFIRMED","note":"Đã kiểm hàng"}' &&
    text(cod, "[data-admin-order-history]").includes("Đã kiểm hàng") && (await requests()).at(-1).method === "GET",
    "confirm sends contract DTO and reloads history");
  click(cod, action("SHIPPING"));
  click(cod, confirm);
  await until(() => status(cod) === "Đang giao" && shown(cod, details), "shipping");
  click(cod, action("DELIVERED"));
  click(cod, confirm);
  await until(() => status(cod) === "Đã giao" && shown(cod, details), "delivered");
  assert(text(cod, "[data-admin-order-payment]").includes("Đã thanh toán") &&
    !find(cod, "[data-transition]") && (await writes(1)).length === 3, "COD moves through SHIPPING and DELIVERED and shows server Paid");

  const unpaid = await detail(2);
  assert(!find(unpaid, action("SHIPPING")) && !!find(unpaid, action("CANCELLED")) &&
    text(unpaid, "[data-order-transitions]").includes("cần được thanh toán"), "unpaid transfer is blocked from shipping");
  click(unpaid, action("CANCELLED"));
  setNote(unpaid, "cancelReason", "  ab  ");
  click(unpaid, confirm);
  assert(text(unpaid, error).includes("5 đến 255") && !(await writes(2)).length, "short cancellation reason is blocked");
  setNote(unpaid, "cancelReason", "  Hết hàng thực tế  ");
  await control("admin-order-mode-validation");
  click(unpaid, confirm);
  await until(() => text(unpaid, error).includes("Dữ liệu không hợp lệ") && shown(unpaid, details), "validation and reload");
  assert(find(unpaid, confirm).disabled && status(unpaid) === "Đã xác nhận", "Backend 400 forces a fresh order review");
  click(unpaid, close);
  await control("admin-order-mode-normal");
  click(unpaid, action("CANCELLED"));
  setNote(unpaid, "cancelReason", "  Hết hàng thực tế  ");
  click(unpaid, confirm);
  await until(() => status(unpaid) === "Đã hủy" && shown(unpaid, details), "cancelled");
  assert(JSON.stringify((await writes(2)).at(-1).body) === '{"statusCode":"CANCELLED","cancelReason":"Hết hàng thực tế"}' &&
    text(unpaid, "[data-admin-order-history]").includes("Hết hàng thực tế") &&
    text(unpaid, "[data-admin-order-payment]").includes("thất bại"), "cancellation writes reason and renders server history and payment");

  const paid = await detail(3);
  assert(!!find(paid, action("SHIPPING")) && text(paid, "[data-admin-order-payment]").includes("QA-BANK-3"),
    "paid transfer can ship and Admin sees its transaction code");
  click(paid, action("CANCELLED"));
  setNote(paid, "cancelReason", "Đã liên hệ khách");
  await control("admin-order-hold");
  click(paid, confirm);
  await until(async () => (await writes(3)).length === 1, "first admin's write is waiting");
  const { competingToken } = await control("admin-order-competition");
  const competing = await fetch(API_BASE_URL + "/admin/orders/3/status", { method: "PUT", headers: {
    Authorization: "Bearer " + competingToken, "Content-Type": "application/json"
  }, body: '{"statusCode":"SHIPPING"}' });
  await control("admin-order-release");
  await until(() => status(paid) === "Đang giao" && text(paid, error).includes("Không thể chuyển"), "second admin wins");
  const concurrent = await writes(3);
  assert(competing.status === 200 && concurrent.map(r => r.actor).join() === "2,3" &&
    find(paid, confirm).disabled && !find(paid, action("CANCELLED")),
    "two Admin actors update one order, lose with 409 and reconcile without replay");
  click(paid, close);
  click(paid, action("DELIVERED"));
  await control("admin-order-mode-conflict");
  click(paid, confirm);
  await until(() => text(paid, error).includes("vừa thay đổi") && shown(paid, details), "concurrency conflict");
  assert(find(paid, confirm).disabled && status(paid) === "Đang giao", "CONCURRENCY_CONFLICT is visible and detail reloads");
  click(paid, close);

  const uncertain = await detail(4);
  click(uncertain, action("CONFIRMED"));
  await control("admin-order-mode-lost");
  click(uncertain, confirm);
  await until(() => text(uncertain, "[data-feedback]").includes("Chưa xác định") &&
    shown(uncertain, details) && status(uncertain) === "Đã xác nhận", "reconcile lost response");
  assert((await writes(4)).length === 1 && find(uncertain, confirm).disabled &&
    text(uncertain, "[data-admin-order-history]").includes("Đã xác nhận"), "lost write response reloads detail and prevents replay");
  click(uncertain, close);
  click(uncertain, action("SHIPPING"));
  await control("admin-order-mode-server-write");
  click(uncertain, confirm);
  await until(() => text(uncertain, error).includes("Chưa xác định") && shown(uncertain, details), "server write error");
  assert((await writes(4)).length === 2 && !text(uncertain, error).includes("Chi tiết nội bộ"),
    "500 on write reports uncertainty and hides server details");
  click(uncertain, close);
  await control("admin-order-mode-forbidden");
  click(cod, "[data-reload-order]");
  await until(() => text(cod, "[data-state-title]").includes("Không có quyền"), "403");
  assert(!shown(cod, details), "403 hides previously loaded order details");
  await control("admin-order-mode-server-read");
  filter(orders).requestSubmit();
  await until(() => text(orders, "[data-state-title]").includes("Chưa tải"), "500");
  assert(!shown(orders, list) && !text(orders, "[data-state-message]").includes("Chi tiết nội bộ"), "500 on read hides internals");
  await control("admin-order-mode-normal");
  const missing = await open(listPath + "?id=999");
  await until(() => text(missing, "[data-state-title]").includes("Không tìm thấy"), "404");
  assert(!shown(missing, details), "404 shows no order details");
  missing.remove();
  for (const frame of [cod, unpaid, paid]) frame.remove();
  const refreshes = (await control("expire")).calls["auth/refresh"];
  values(orders, {keyword: "", statusCode: "", paymentStatus: "", from: "", to: ""});
  await until(() => shown(orders, list), "refresh once");
  assert((await control()).calls["auth/refresh"] === refreshes + 1, "401 refreshes session and retries the read");

  for (const width of [360, 390, 768, 1440]) {
    orders.width = uncertain.width = String(width);
    await new Promise(resolve => requestAnimationFrame(resolve));
    const widths = [orders, uncertain].map(frame => frame.contentDocument.documentElement.scrollWidth);
    assert(widths.every(actual => actual <= width + 1), `list and detail fit ${width} px (actual ${widths.join(", ")})`);
  }
  const old = uncertain.contentDocument;
  await logout(orders, "[data-admin-logout]");
  await until(() => shown(uncertain, "[data-account-view=login]"), "cross-tab logout");
  assert(!old.querySelector("[data-orders-workspace]").children.length && !old.querySelector("[data-admin-email]").textContent,
    "logout removes private orders and identity from the old document");
  uncertain.remove();
  login(orders, "admin-other");
  await until(() => shown(orders, list) && text(orders, "[data-admin-email]") === "admin-other@example.test", "other Admin");
  assert(text(orders, "[data-admin-email]") === "admin-other@example.test" && !old.querySelector("[data-orders-workspace]").children.length,
    "switching accounts uses latest identity and old page stays empty");
  await control("revoke-session");
  filter(orders).requestSubmit();
  await until(() => shown(orders, "[data-account-view=login]"), "session expiry");
  assert(text(orders, "[data-session-notice]").includes("Phiên quản trị đã kết thúc") && localStorage.length === 0,
    "unrecoverable 401 clears session and redirects");
  assert(sessionStorage.length === 0 && errors.length === 0, errors.join(", ") || "no sensitive storage or unhandled JavaScript error");
  await fetch("/__auth_qa", {method: "POST", body: `PASS ${checks.length}/${checks.length} Admin Orders Chromium checks`});
} catch (failure) {
  await control("admin-order-release");
  await fetch("/__auth_qa", {method: "POST", body: `FAIL after ${checks.length} checks: ${failure.message}`});
}
