const checks = [];
const errors = [];
const control = async action => (await fetch("/__fixture" + (action ? "?action=" + action : ""))).json();
const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const shown = (frame, selector) => find(frame, selector)?.hidden === false;
const loggedIn = frame => shown(frame, "[data-account-view=profile]");
const revenueState = frame => find(frame, "[data-revenue-state]");
const form = frame => find(frame, "[data-revenue-filter]");
const finance = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND", maximumFractionDigits: 2 });
const calls = snapshot => snapshot.dashboardRequests.filter(request => request.path.endsWith("/revenue"));

function assert(ok, label) {
  if (!ok) throw new Error(label);
  checks.push(label);
}

async function until(predicate, label) {
  for (let i = 0; i < 155; i++) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timeout " + label);
}

async function open(path, width = 1060) {
  const frame = document.createElement("iframe");
  frame.width = String(width);
  frame.height = "860";
  frame.style.border = "0";
  frame.src = path;
  document.body.append(frame);
  await until(() => frame.contentDocument?.readyState === "complete" &&
    frame.contentWindow.location.pathname !== "blank", "document ready " + path);
  frame.contentWindow.addEventListener("error", event => { if (event.error) errors.push(event.error.name); });
  frame.contentWindow.addEventListener("unhandledrejection", () => errors.push("unhandled rejection"));
  return frame;
}

function enter(frame, account) {
  const f = find(frame, "[data-account-form=login]");
  f.elements.email.value = account + "@example.test";
  f.elements.password.value = "Test-Password123";
  f.requestSubmit();
}

async function login(account) {
  const frame = await open("/account/");
  await until(() => shown(frame, "[data-account-view=login]"), "login form");
  enter(frame, account);
  await until(() => loggedIn(frame), "verified account " + account);
  return frame;
}

async function logout(frame) {
  find(frame, "[data-account-view=profile] [data-logout]").click();
  await until(() => shown(frame, "[data-account-view=login]"), "logout");
  frame.remove();
}

function range(frame, from, to, groupBy = "day") {
  const fields = form(frame).elements;
  fields.from.value = from;
  fields.to.value = to;
  fields.groupBy.value = groupBy;
  form(frame).requestSubmit();
}

try {
  let dashboard = await open("/admin/");
  await until(() => shown(dashboard, "[data-account-view=login]"), "guest redirect");
  assert(dashboard.contentWindow.location.pathname === "/account/" &&
    (await control()).dashboardRequests.length === 0, "anonymous user redirected before dashboard reads");
  enter(dashboard, "customer");
  await until(() => loggedIn(dashboard), "customer signs in");
  assert(dashboard.contentWindow.location.pathname === "/account/", "customer login cannot return to admin dashboard");
  const blocked = await open("/admin/");
  await until(() => find(blocked, "[data-guard-message]")?.textContent.includes("không có quyền"), "customer denied");
  assert(find(blocked, "[data-admin-name]").textContent === "" && (await control()).dashboardRequests.length === 0,
    "customer cannot see or request dashboard data");
  blocked.remove();
  await logout(dashboard);

  dashboard = await open("/admin/");
  await until(() => shown(dashboard, "[data-account-view=login]"), "admin login");
  enter(dashboard, "forced");
  await until(() => shown(dashboard, "[data-account-view=password]"), "forced password");
  assert(dashboard.contentWindow.location.pathname === "/account/" && (await control()).dashboardRequests.length === 0,
    "forced password change blocks dashboard reads");
  find(dashboard, "[data-account-view=password] [data-logout]").click();
  await until(() => shown(dashboard, "[data-account-view=login]"), "forced logout");
  enter(dashboard, "admin");
  await until(() => shown(dashboard, "[data-revenue-output]") && shown(dashboard, "[data-recent-output]"), "admin dashboard");
  assert(dashboard.contentWindow.location.pathname === "/admin/" &&
    find(dashboard, "[data-admin-email]").textContent === "admin@example.test", "Admin is returned to Dashboard with verified identity");

  let snapshot = await control();
  const report = calls(snapshot).at(-1).query;
  const recent = snapshot.dashboardRequests.find(request => request.path === "admin/orders").query;
  const expectedLocal = Object.fromEntries(new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Ho_Chi_Minh",
    year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(new Date()).map(part => [part.type, part.value]));
  assert(report.to[0] === `${expectedLocal.year}-${expectedLocal.month}-${expectedLocal.day}` && report.groupBy[0] === "day" &&
    Date.parse(report.to[0]) - Date.parse(report.from[0]) === 6 * 86400000, "default includes seven Vietnam calendar days");
  assert(recent.page[0] === "1" && recent.pageSize[0] === "5" && recent.sort[0] === "-createdAt", "uses Admin order query for five most recent");
  assert(find(dashboard, '[data-metric="subtotal"]').textContent === finance.format(450000.75) &&
    find(dashboard, '[data-metric="shippingFee"]').textContent === finance.format(60000) &&
    find(dashboard, '[data-metric="totalAmount"]').textContent === finance.format(510000.75) &&
    find(dashboard, '[data-metric="totalOrders"]').textContent === "3" &&
    find(dashboard, '[data-metric="averageSubtotal"]').textContent === finance.format(150000.25), "renders all five summary fields separately");
  assert(find(dashboard, "[data-revenue-bars]").children.length === 2 &&
    find(dashboard, "[data-revenue-rows]").children.length === 2 &&
    find(dashboard, "[data-order-statuses]").textContent.includes("Đã giao4"), "renders series and independent status counts");
  assert(find(dashboard, "[data-recent-orders]").textContent.includes("SP-QA-<b data-injected>204</b>") &&
    !find(dashboard, "[data-injected]") && !find(dashboard, "[data-recent-orders]").textContent.includes("riêng tư"),
    "order table renders text safely and omits unused personal data");
  assert(find(dashboard, ".admin-nav-planned").textContent.match(/Chưa triển khai/g).length === 2 &&
    !find(dashboard, ".admin-nav-planned a") &&
    find(dashboard, '#admin-nav a[href="./catalog/"]'), "catalog link is available and planned screens stay unlinked");

  let before = calls(await control()).length;
  range(dashboard, "", "2026-11-30");
  assert(!find(dashboard, "#date-error").hidden && form(dashboard).elements.from.hasAttribute("aria-invalid"),
    "missing date is reported");
  range(dashboard, "2026-11-30", "2026-11-01");
  assert(find(dashboard, "#date-error").textContent.includes("ngày bắt đầu"), "reversed date range is rejected");
  range(dashboard, "2024-01-01", "2025-01-01");
  assert(find(dashboard, "#date-error").textContent.includes("366") && calls(await control()).length === before,
    "more than 366 inclusive days makes no read");
  range(dashboard, "2024-01-01", "2024-12-31", "month");
  await until(() => shown(dashboard, "[data-revenue-output]") &&
    find(dashboard, "[data-report-period]").textContent.includes("2024-12-31"), "366 day range");
  snapshot = await control();
  assert(calls(snapshot).length === before + 1 && calls(snapshot).at(-1).query.groupBy[0] === "month" &&
    find(dashboard, "[data-revenue-bars]").textContent.includes("12/2024") && find(dashboard, "#date-error").hidden,
    "366 inclusive days allowed with monthly buckets");

  await control("dashboard-invalid");
  range(dashboard, "2026-11-01", "2026-11-30");
  await until(() => find(dashboard, "#date-error").textContent === "Kiểm tra ngày kết thúc.", "server validation");
  assert(!shown(dashboard, "[data-revenue-output]") && form(dashboard).elements.to.hasAttribute("aria-invalid"),
    "server 400 highlights date field");
  await control("dashboard-no-status");
  find(dashboard, "[data-revenue-retry]").click();
  await until(() => shown(dashboard, "[data-status-empty]"), "optional status breakdown");
  assert(find(dashboard, "[data-status-empty]").textContent.includes("API chưa cung cấp"), "missing optional DTO does not fabricate counts");

  await control("dashboard-empty");
  range(dashboard, "2026-11-03", "2026-11-05");
  find(dashboard, "[data-orders-reload]").click();
  await until(() => shown(dashboard, "[data-series-empty]") &&
    find(dashboard, "[data-orders-state]")?.dataset.state === "empty", "empty widgets");
  assert(find(dashboard, '[data-metric="totalOrders"]').textContent === "0" &&
    find(dashboard, "[data-revenue-bars]").children.length === 0 && !shown(dashboard, "[data-recent-output]"), "empty widgets clear previous rows");

  await control("dashboard-forbidden");
  range(dashboard, "2026-11-08", "2026-11-09");
  await until(() => revenueState(dashboard).textContent.includes("Không có quyền xem dữ liệu"), "403");
  assert(!shown(dashboard, "[data-revenue-output]") && !find(dashboard, "[data-admin-only]").hidden, "403 hides inaccessible report without ending session");
  await control("dashboard-server-error");
  find(dashboard, "[data-revenue-retry]").click();
  find(dashboard, "[data-orders-reload]").click();
  await until(() => revenueState(dashboard).dataset.state === "error" &&
    find(dashboard, "[data-orders-state]").dataset.state === "error", "500 widgets");
  assert(!revenueState(dashboard).textContent.includes("Nội bộ QA") && !shown(dashboard, "[data-recent-output]"), "500 uses safe error and no stale output");

  await control("dashboard-normal");
  snapshot = await control("expire");
  find(dashboard, "[data-revenue-retry]").click();
  await until(() => shown(dashboard, "[data-revenue-output]"), "refresh recovered");
  let after = await control();
  assert(calls(after).length === calls(snapshot).length + 2 &&
    after.calls["auth/refresh"] === snapshot.calls["auth/refresh"] + 1, "401 triggers exactly one refresh and retries report once");
  assert(localStorage.length === 1 && localStorage.key(0) === "sportshop.session" && sessionStorage.length === 0,
    "dashboard data and access token stay out of browser storage");

  for (const width of [360, 390, 768, 1440]) {
    const frame = await open("/admin/", width);
    await until(() => shown(frame, "[data-revenue-output]") && shown(frame, "[data-recent-output]"), "responsive dashboard");
    assert(frame.contentWindow.innerWidth === width && frame.contentDocument.documentElement.scrollWidth <= width,
      "dashboard fits " + width + " px");
    if (width < 700) {
      const menu = find(frame, "[data-admin-menu]");
      assert(frame.contentWindow.getComputedStyle(find(frame, "#admin-nav")).display === "none", "sidebar starts collapsed at " + width);
      menu.click();
      assert(menu.getAttribute("aria-expanded") === "true" && frame.contentWindow.getComputedStyle(find(frame, "#admin-nav")).display !== "none",
        "sidebar opens at " + width);
      const scroll = find(frame, "[data-recent-output]");
      assert(scroll.scrollWidth > scroll.clientWidth && frame.contentWindow.getComputedStyle(scroll).overflowX === "auto",
        "order table scrolls within its panel at " + width);
    }
    frame.remove();
  }

  await control("dashboard-pause");
  before = calls(await control()).length;
  range(dashboard, "2026-11-01", "2026-11-10");
  await until(async () => calls(await control()).length === before + 1, "report in flight");
  await control("dashboard-normal");
  range(dashboard, "2026-11-11", "2026-11-15");
  await until(() => shown(dashboard, "[data-revenue-output]") &&
    find(dashboard, "[data-report-period]").textContent.includes("2026-11-15"), "new report wins");
  await control("dashboard-release");
  assert(!find(dashboard, "[data-report-period]").textContent.includes("2026-11-10"), "older response cannot replace a newer query");

  const privateDocument = dashboard.contentDocument;
  await control("dashboard-pause");
  before = calls(await control()).length;
  range(dashboard, "2026-11-19", "2026-11-20");
  await until(async () => calls(await control()).length === before + 1, "report in flight during logout");
  const loggingOut = find(dashboard, "[data-admin-logout]");
  loggingOut.click();
  assert(privateDocument.querySelector("[data-revenue-rows]").children.length === 0 &&
    privateDocument.querySelector("[data-recent-orders]").children.length === 0 &&
    privateDocument.querySelector("[data-admin-email]").textContent === "", "logout removes private content from old document before navigation");
  await until(() => shown(dashboard, "[data-account-view=login]"), "redirect after logout");
  await control("dashboard-normal");
  await control("dashboard-release");
  assert(privateDocument.querySelector("[data-revenue-rows]").children.length === 0, "late response cannot write into logged out dashboard");
  enter(dashboard, "admin-other");
  await until(() => find(dashboard, "[data-admin-email]")?.textContent === "admin-other@example.test" &&
    shown(dashboard, "[data-revenue-output]"), "switch admin");
  assert(!find(dashboard, "[data-admin-email]").textContent.includes("admin@example.test"), "new login uses current account only");
  await control("revoke-session");
  range(dashboard, "2026-11-20", "2026-11-25");
  await until(() => shown(dashboard, "[data-account-view=login]"), "expired redirect");
  assert(find(dashboard, "[data-session-notice]").textContent.includes("Phiên quản trị đã kết thúc") &&
    localStorage.length === 0, "unrecoverable 401 sends user to login and clears marker");
  assert(errors.length === 0, "no JavaScript module errors");
  dashboard.remove();
  await fetch("/__auth_qa", { method: "POST", body: `PASS ${checks.length} Dashboard browser checks` });
} catch (error) {
  await fetch("/__auth_qa", { method: "POST", body: `FAIL ${error.message} / ${checks.at(-1) ?? "startup"}` });
}
