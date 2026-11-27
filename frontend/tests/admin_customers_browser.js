const page = "/admin/customers/";
const list = "[data-customers-list]", profile = "[data-customer-detail]", filters = "[data-customers-filter]";
const action = "[data-change-customer-status]", save = "[data-operation-save]", close = "[data-operation-close]";
const error = "[data-operation-error]", badge = "[data-customer-status]";
const checks = [], errors = [];
const control = async (action = "") => (await fetch("/__fixture?action=" + action)).json();
const requests = async () => (await control()).customerRequests;
const writes = async () => (await requests()).filter(request => request.method === "PUT");
const find = (f, s) => f.contentDocument.querySelector(s);
const text = (f, s) => find(f, s)?.textContent.trim() ?? "";
const shown = (f, s) => find(f, s)?.hidden === false;
const click = (f, s) => find(f, s).click();
const row = id => `[data-customer-id="${id}"]`;

function assert(ok, label) {
  if (!ok) throw Error(label);
  checks.push(label);
}

async function until(test, label) {
  for (let i = 0; i < 220; i++) {
    if (await test()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw Error("Timeout " + label);
}

async function open(path) {
  const f = document.createElement("iframe");
  f.width = 1440; f.height = 850; f.src = path;
  f.addEventListener("load", () => {
    f.contentWindow.addEventListener("error", event => { if (event.error) errors.push(event.error.name); });
    f.contentWindow.addEventListener("unhandledrejection", () => errors.push("unhandled rejection"));
  });
  document.body.append(f);
  await until(() => f.contentDocument?.readyState === "complete" && f.contentWindow.location.pathname !== "blank", path);
  return f;
}

function login(f, account) {
  const form = find(f, "[data-account-form=login]");
  form.elements.email.value = account + "@example.test";
  form.elements.password.value = "Test-Password123";
  form.requestSubmit();
}

async function logout(f, selector) {
  click(f, selector);
  await until(() => shown(f, "[data-account-view=login]"), "logout");
}

function search(f, values) {
  const form = find(f, filters);
  for (const [name, value] of Object.entries(values)) form.elements[name].value = value;
  form.requestSubmit();
}

async function update(f, mode, message) {
  click(f, action);
  await control("customers-mode-" + mode);
  click(f, save);
  await until(() => text(f, error).includes(message) && shown(f, profile), mode);
  click(f, close);
  await control("customers-mode-normal");
}

try {
  const listFrame = await open(page);
  await until(() => shown(listFrame, "[data-account-view=login]"), "guest redirected");
  assert(!(await requests()).length && listFrame.contentWindow.location.search.includes("view=customers"), "guest redirects before customer reads");
  login(listFrame, "customer");
  await until(() => shown(listFrame, "[data-account-view=profile]"), "customer logged in");
  const blocked = await open(page + "?id=1");
  await until(() => text(blocked, "[data-guard-message]").includes("không có quyền"), "Customer denied");
  assert(!(await requests()).length && !shown(blocked, profile), "Customer cannot read Admin customer profile");
  blocked.remove();
  await logout(listFrame, "[data-account-view=profile] [data-logout]");
  login(listFrame, "forced");
  await until(() => shown(listFrame, "[data-account-view=password]"), "password required");
  assert(!(await requests()).length, "MustChangePassword blocks Admin reads");
  await logout(listFrame, "[data-account-view=password] [data-logout]");
  login(listFrame, "admin");
  await until(() => shown(listFrame, list), "customer list");
  assert(text(listFrame, "[data-admin-email]") === "admin@example.test" &&
    listFrame.contentDocument.querySelectorAll("tbody tr").length === 20 && !!find(listFrame, row(23)), "newest 20 customer records displayed");
  assert(text(listFrame, row(22)).includes("<b data-injected>QA</b>") && !find(listFrame, "[data-injected]") &&
    text(listFrame, row(22)).includes("—"), "API data is escaped and optional phone remains empty");
  assert(text(listFrame, row(23) + " time").includes("24/11") && text(listFrame, row(23) + " time").includes("05:00"),
    "registration date uses Vietnam timezone");
  click(listFrame, list + " [data-next]");
  await until(() => !!find(listFrame, row(1)) && shown(listFrame, list), "page two");
  assert(text(listFrame, list).includes("2/2") && listFrame.contentDocument.querySelectorAll("tbody tr").length === 3, "server pagination metadata drives controls");
  search(listFrame, {keyword:"  customer-02@ ", isActive:"false", sort:"email", pageSize:"10"});
  await until(() => !!find(listFrame, row(2)) && text(listFrame, list).includes("1 kết quả"), "filter and number");
  const query = (await requests()).at(-1).query;
  assert(query.keyword[0] === "customer-02@" && query.isActive[0] === "false" && query.sort[0] === "email" &&
    query.page[0] === "1" && query.pageSize[0] === "10" && text(listFrame, row(2)).includes("Đã khóa"),
    "trimmed email keyword, locked filter, and sorting parameters reach API");
  search(listFrame, {keyword:"0901234501", isActive:"true"});
  await until(() => !!find(listFrame, row(1)) && shown(listFrame, list), "search phone");
  assert(text(listFrame, row(1)).includes("0901234501"), "search and filter by phone and active status");
  search(listFrame, {keyword:"Khách QA 01"});
  await until(() => shown(listFrame, list), "search name");
  assert(!!find(listFrame, row(1)) && (await requests()).at(-1).query.keyword[0] === "Khách QA 01", "search by full name");
  search(listFrame, {keyword:"not found"});
  await until(() => text(listFrame, "[data-state-title]") === "Không tìm thấy khách hàng", "empty results");
  assert(!shown(listFrame, list), "empty search hides the table");
  await control("customers-mode-malformed");
  search(listFrame, {keyword:""});
  await until(() => text(listFrame, "[data-state-message]").includes("không hợp lệ"), "malformed list");
  assert(!shown(listFrame, list), "malformed payload produces a controlled error");

  await control("customers-mode-normal");
  const invalid = await open(page + "?id=bogus");
  await until(() => !!find(invalid, "[data-customer-invalid]"), "invalid id");
  assert(!(await requests()).some(r => r.path.endsWith("/bogus")), "invalid ID blocked before network request");
  invalid.remove();
  const missing = await open(page + "?id=99");
  await until(() => text(missing, "[data-state-title]").includes("Không tìm thấy"), "customer 404");
  assert(!shown(missing, profile), "404 does not display profile or status actions");
  missing.remove();
  const f = await open(page + "?id=1");
  await until(() => shown(f, profile), "detail");
  const facts = text(f, "[data-customer-facts]");
  assert(facts.includes("03/11/1992") && facts.includes("Nữ") && facts.includes("Lần đăng nhập gần nhất") &&
    facts.includes("250.000,75") && facts.includes("Tổng số đơn") && !text(f, profile).includes("fixture-ignored"),
    "detail uses documented fields and excludes unexpected security data");
  assert(!find(f, profile + " a[href*='/orders/']") && !facts.includes("Địa chỉ"), "profile does not fabricate order links or addresses");
  click(f, action);
  assert(text(f, "[data-operation-fields]").includes("thu hồi") && !(await writes()).length, "lock explains effect before confirmation");
  await control("customers-hold");
  click(f, save); click(f, save);
  await until(async () => (await writes()).length === 1, "locked write held");
  assert(find(f, save).disabled && text(f, badge) === "Đang hoạt động", "double-click sends one request and waits for API");
  await control("customers-release");
  await until(() => shown(f, profile) && text(f, badge) === "Đã khóa", "locked");
  let changes = await writes();
  assert(changes[0].path === "admin/customers/1/status" && JSON.stringify(changes[0].body) === '{"isActive":false}' &&
    (await requests()).at(-1).method === "GET", "lock uses exact DTO then reads authoritative profile");
  await update(f, "invalid", "Trạng thái không hợp lệ");
  assert(text(f, badge) === "Đã khóa", "400 keeps the last persisted status");
  click(f, action); click(f, save);
  await until(() => shown(f, profile) && text(f, badge) === "Đang hoạt động", "unlocked");
  assert(JSON.stringify((await writes()).at(-1).body) === '{"isActive":true}', "unlock writes only isActive true");
  await update(f, "conflict", "vừa thay đổi");
  assert(text(f, badge) === "Đã khóa", "409 refreshes the record updated by the competing request");
  click(f, action);
  await control("customers-mode-lost");
  const beforeLost = (await writes()).length;
  click(f, save);
  await until(() => text(f, error).includes("Chưa xác định") && shown(f, profile) && text(f, badge) === "Đang hoạt động", "unknown result");
  assert((await writes()).length === beforeLost + 1 && find(f, save).disabled && (await requests()).at(-1).method === "GET",
    "lost response uses GET and disables replay");
  click(f, close);
  await control("customers-mode-forbidden");
  click(f, "[data-customer-reload]");
  await until(() => text(f, "[data-state-title]").includes("Không có quyền"), "forbidden profile");
  assert(!shown(f, profile), "403 hides private profile");
  await control("customers-mode-server");
  search(listFrame, {});
  await until(() => text(listFrame, "[data-state-title]").includes("Chưa tải"), "internal error");
  assert(!shown(listFrame, list) && !text(listFrame, "[data-state-message]").includes("Nội bộ QA"), "500 displays safe error");
  await control("customers-mode-normal");
  f.remove();
  const refreshes = (await control("expire")).calls["auth/refresh"];
  search(listFrame, {});
  await until(() => shown(listFrame, list), "401 refreshed");
  assert((await control()).calls["auth/refresh"] === refreshes + 1, "401 refresh restores customer read");
  const sample = await open(page + "?id=1");
  await until(() => shown(sample, profile), "profile responsive");
  for (const width of [360, 390, 768, 1440]) {
    listFrame.width = sample.width = String(width);
    await new Promise(resolve => requestAnimationFrame(resolve));
    assert([listFrame, sample].every(frame => frame.contentDocument.documentElement.scrollWidth <= width + 1),
      "list and profile fit " + width + " px");
  }
  const old = sample.contentDocument;
  await logout(sample, "[data-admin-logout]");
  await until(() => shown(listFrame, "[data-account-view=login]"), "other tab logs out");
  assert(!old.querySelector("[data-customers-workspace]").children.length && !old.querySelector("[data-admin-email]").textContent &&
    localStorage.length === 0, "logout clears customer data and browser marker");
  listFrame.remove();
  login(sample, "admin-other");
  await until(() => shown(sample, profile) && text(sample, "[data-admin-email]") === "admin-other@example.test", "new Admin restored deep link");
  assert(sample.contentWindow.location.search === "?id=1" && !old.querySelector("[data-customers-workspace]").children.length &&
    localStorage.length === 1 && localStorage.key(0) === "sportshop.session", "new Admin returns to requested profile without storing PII");
  await control("revoke-session");
  click(sample, "[data-customer-reload]");
  await until(() => shown(sample, "[data-account-view=login]"), "unrecoverable 401");
  assert(text(sample, "[data-session-notice]").includes("Phiên quản trị đã kết thúc") &&
    localStorage.length === 0 && errors.length === 0, errors.join(", ") || "expired session leaves no data or JS errors");
  await fetch("/__auth_qa", {method:"POST", body:`PASS ${checks.length}/${checks.length} Admin Customers Chromium checks`});
} catch (failure) {
  await control("customers-release");
  await fetch("/__auth_qa", {method:"POST", body:`FAIL after ${checks.length} checks: ${failure.message}`});
}
