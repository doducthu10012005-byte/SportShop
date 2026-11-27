import { API_BASE_URL } from "/js/config.js";
import { paymentJson, paymentMoney } from "/js/admin-payments.js";

const page = "/admin/orders/?view=payments";
const detail = "[data-admin-order-detail]", list = "[data-admin-orders-list]";
const panel = "[data-admin-order-payment]", transfer = "[data-confirm-transfer]", refund = "[data-confirm-refund]";
const error = "[data-operation-error]", save = "[data-operation-save]", close = "[data-operation-close]";
const checks = [];
const control = async (action = "") => (await fetch("/__fixture?action=" + action)).json();
const find = (f, s) => f.contentDocument.querySelector(s);
const text = (f, s) => find(f, s)?.textContent.trim() ?? "";
const shown = (f, s) => find(f, s)?.hidden === false;
const click = (f, s) => find(f, s).click();
const form = f => find(f, "[data-operation-form]");
const payments = state => state.adminOrderRequests.filter(r => r.method === "PUT" && /\/(payment|refund)$/.test(r.path));
const link = id => `[data-order-id="${id}"] a`;
function assert(ok, label) { if (!ok) throw Error(label); checks.push(label); }
async function until(test, label) {
  for (let i = 0; i < 200; i++) {
    if (await test()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw Error("Timeout " + label);
}
async function open(path) {
  const f = document.createElement("iframe");
  f.width = 1440; f.height = 840; f.src = path;
  document.body.append(f);
  await until(() => f.contentDocument?.readyState === "complete" && f.contentWindow.location.pathname !== "blank", path);
  return f;
}
async function order(id, view = page) {
  const f = await open(view + (view.includes("?") ? "&" : "?") + "id=" + id);
  await until(() => shown(f, detail), "order " + id);
  return f;
}
function login(f, account) {
  const login = find(f, "[data-account-form=login]");
  login.elements.email.value = account + "@example.test";
  login.elements.password.value = "Test-Password123";
  login.requestSubmit();
}
async function logout(f, selector) {
  click(f, selector);
  await until(() => shown(f, "[data-account-view=login]"), "logout");
}
function bank(f, code, amount) {
  click(f, transfer);
  form(f).elements.transactionCode.value = code;
  form(f).elements.receivedAmount.value = amount;
}
async function write(id, route, payload, competingToken) {
  return fetch(API_BASE_URL + "/admin/orders/" + id + route, {
    method: "PUT", headers: {Authorization:"Bearer "+competingToken, "Content-Type":"application/json"}, body:JSON.stringify(payload)
  });
}
async function failure(f, mode, message, code = "TX-FAIL", amount = "248000.50") {
  bank(f, code, amount);
  await control("admin-order-mode-" + mode);
  click(f, save);
  await until(() => text(f, error).includes(message) && shown(f, detail), mode);
  click(f, close);
  await control("admin-order-mode-normal");
}

try {
  const f = await open(page);
  await until(() => shown(f, "[data-account-view=login]"), "guest");
  assert(f.contentWindow.location.search.includes("view=payments") && (await control()).adminOrderRequests.length === 0,
    "guest redirects before reading payment data");
  login(f, "customer");
  await until(() => shown(f, "[data-account-view=profile]"), "Customer login");
  const closed = await open(page + "&id=2");
  await until(() => text(closed, "[data-guard-message]").includes("không có quyền"), "Customer denied");
  assert((await control()).adminOrderRequests.length === 0 && !shown(closed, detail), "Customer cannot inspect Admin payments");
  closed.remove();
  await logout(f, "[data-account-view=profile] [data-logout]");
  login(f, "forced");
  await until(() => shown(f, "[data-account-view=password]"), "forced password");
  assert((await control()).adminOrderRequests.length === 0, "MustChangePassword blocks reads");
  await logout(f, "[data-account-view=password] [data-logout]");
  login(f, "admin");
  await until(() => shown(f, list), "payment list");
  assert(text(f, list).includes("Tổng đơn") && text(f, list).includes("Chuyển khoản") &&
    !text(f, list).includes("Đã nhận") && !text(f, list).includes("Khách thử"), "list uses supplied fields, and links by order ID");
  click(f, list + " [data-next]");
  await until(() => text(f, list).includes("2/2") && !!find(f, link(1)), "pagination");
  assert(f.contentDocument.querySelectorAll(list + " tbody tr").length === 3, "pagination uses Admin Orders endpoint");
  click(f, "[data-refund-queue]");
  await until(() => shown(f, list) && !!find(f, link(4)) && !!find(f, link(11)), "refund queue");
  assert((await control()).adminOrderRequests.at(-1).query.paymentStatus[0] === "RefundPending" &&
    !find(f, link(7)), "refund queue filters by RefundPending using the order list");

  const cod = await order(1, "/admin/orders/");
  const paymentsLink = find(cod, `a[href$="id=1&view=payments"]`);
  assert(!!paymentsLink && text(cod, panel).includes("COD được ghi nhận") && !find(cod, transfer), "COD has order link and no independent payment form");
  for (const [target, next] of [["CONFIRMED","Đã xác nhận"], ["SHIPPING","Đang giao"], ["DELIVERED","Đã giao"]]) {
    click(cod, `[data-transition="${target}"]`);
    click(cod, save);
    await until(() => text(cod, "[data-current-status]") === next && shown(cod, detail), target);
  }
  assert(text(cod, panel).includes("Đã thanh toán") && payments(await control()).length === 0, "COD Paid is shown only after Backend delivers");
  cod.remove();
  const oldMethods = await (await fetch(API_BASE_URL + "/payment-methods")).json();
  const active = await order(2);
  assert(oldMethods.data.every(method => method.methodCode !== "BANK_TRANSFER") && !!find(active, transfer),
    "Bank transfer omitted from Checkout methods can still be processed on an existing eligible order");
  assert(text(active, panel).includes("248.000,5") && text(active, panel).includes("Hạn thanh toán") &&
    !find(active, "[data-admin-order-recipient]"), "payment detail uses source amounts and omits recipient details");
  const exact = JSON.parse('{"amount":9999999999999999.99}', paymentJson);
  assert(paymentMoney(exact.amount).includes("9.999.999.999.999.999,99"), "decimal response retains all digits");
  bank(active, "  ", "248000,50");
  click(active, save);
  assert(text(active, error).includes("1 đến 100"), "blank transaction code rejected");
  form(active).elements.transactionCode.value = "ABC";
  form(active).elements.receivedAmount.value = "248000,501";
  click(active, save);
  assert(text(active, error).includes("2 chữ số") && payments(await control()).length === 0, "excess precision blocked without rounding or write");
  click(active, close);
  await failure(active, "validation", "Số tiền nhận không hợp lệ");
  await failure(active, "normal", "không khớp", "QA-AMOUNT", "247999,99");
  await failure(active, "normal", "đã được sử dụng", " QA-bank-3 ");

  bank(active, "  QA-GOOD  ", "248000,50");
  await control("admin-order-hold");
  const before = payments(await control()).length;
  click(active, save); click(active, save);
  await until(async () => payments(await control()).length === before + 1, "one transfer");
  assert(find(active, save).disabled, "double-click sends only one transfer write");
  await control("admin-order-release");
  await until(() => shown(active, detail) && text(active, panel).includes("QA-GOOD"), "bank paid");
  const result = await control();
  assert(result.paymentCommits === 1 && JSON.stringify(payments(result).at(-1).body) ===
    '{"paymentStatus":"Paid","transactionCode":"QA-GOOD","receivedAmount":248000.5}' &&
    !find(active, transfer) && text(active, panel).includes("Ghi nhận thu tiền"), "Paid response is reconciled via GET and clears transfer action");

  for (const [id, label] of [[3,"Đã thanh toán"],[5,"Quá hạn"],[6,"thất bại"],[7,"Đã hoàn tiền"],[8,"quá hạn"],[9,"Chờ thanh toán"]]) {
    const readonly = await order(id);
    assert(!find(readonly, transfer) && !find(readonly, refund) && text(readonly, panel).includes(label), "ineligible state stays read-only for order " + id);
    readonly.remove();
  }
  const late = await order(12);
  await failure(late, "overdue", "đã hết hạn");
  assert(!find(late, transfer) && text(late, panel).includes("quá hạn"), "concurrent expiry 409 refreshes eligibility");
  late.remove();

  const r = await order(4);
  click(r, refund); click(r, save);
  assert(text(r, error).includes("Cần xác nhận") && (await control()).refundCommits === 0, "refund requires explicit confirmation of external full refund");
  form(r).elements.refundCompleted.checked = true;
  await control("admin-order-hold");
  const previousRefunds = payments(await control()).filter(item => item.path.endsWith("/refund")).length;
  click(r, save); click(r, save);
  await until(async () => payments(await control()).filter(item => item.path.endsWith("/refund")).length === previousRefunds + 1, "refund in flight");
  const { competingToken } = await control("admin-order-competition");
  const competing = await write(4, "/refund", {refundCompleted:true}, competingToken);
  await control("admin-order-release");
  await until(() => shown(r, detail) && text(r, panel).includes("Đã hoàn tiền"), "refunded");
  const refunds = await control();
  assert(competing.status === 200 && refunds.refundCommits === 1 && !find(r, refund) &&
    payments(refunds).filter(item => item.path.endsWith("/refund")).length === 2 &&
    payments(refunds).at(-1).body.refundCompleted === true, "two Admins and a double-click still commit exactly one refund");

  const lost = await order(10);
  await failure(lost, "missing", "Không tìm thấy");
  await failure(lost, "server-write", "Chưa xác định");
  const original = lost.contentWindow.fetch.bind(lost.contentWindow);
  let wire;
  lost.contentWindow.fetch = (url, options) => {
    if (options?.method === "PUT") wire = options.body;
    return original(url, options);
  };
  await failure(lost, "validation", "Số tiền nhận không hợp lệ", "QA-LARGE", "9999999999999999,99");
  assert(wire.includes('"receivedAmount":9999999999999999.99'), "large decimal is sent as an exact JSON number");
  bank(lost, "QA-LOST", "248000.5");
  await control("admin-order-mode-lost");
  const preLoss = payments(await control()).length;
  click(lost, save);
  await until(() => text(lost, error).includes("đối chiếu giao dịch thủ công") &&
    shown(lost, detail) && text(lost, panel).includes("QA-LOST"), "lost response");
  const observed = await control();
  assert(payments(observed).length === preLoss + 1 && observed.adminOrderRequests.at(-1).method === "GET" && find(lost, save).disabled,
    "lost response after commit only reconciles with GET and gives manual check guidance");
  click(lost, close);
  await control("admin-order-mode-forbidden");
  click(lost, "[data-reload-order]");
  await until(() => text(lost, "[data-state-title]").includes("Không có quyền"), "403");
  assert(!shown(lost, detail), "403 hides previous payment details");
  await control("admin-order-mode-normal");
  for (const frame of [active, r, lost]) frame.remove();
  const refreshed = (await control("expire")).calls["auth/refresh"];
  find(f, "[data-admin-orders-filter]").requestSubmit();
  await until(() => shown(f, list), "401 refresh");
  assert((await control()).calls["auth/refresh"] === refreshed+1, "refresh on read recovers 401");
  const sample = await order(11);
  for (const px of [360, 390, 768, 1440]) {
    f.width = sample.width = String(px);
    await new Promise(resolve => requestAnimationFrame(resolve));
    assert([f, sample].every(frame => frame.contentDocument.documentElement.scrollWidth <= px + 1), "payments fit " + px);
  }
  const old = sample.contentDocument;
  await logout(sample, "[data-admin-logout]");
  assert(!old.querySelector("[data-orders-workspace]").children.length && localStorage.length === 0,
    "logout clears payment data and storage has no tokens");
  await fetch("/__auth_qa", {method:"POST", body:`PASS ${checks.length}/${checks.length} Admin Payments Chromium checks`});
} catch (failure) {
  await control("admin-order-release");
  await fetch("/__auth_qa", {method:"POST", body:`FAIL after ${checks.length} checks: ${failure.message}`});
}
