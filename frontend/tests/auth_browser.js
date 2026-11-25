const result = document.querySelector("#qa");
const checks = [];
const logged = [];
const password = "Test-Password123";

function assert(condition, name) {
  if (!condition) throw new Error(name);
  checks.push(name);
}

async function until(check, name) {
  for (let i = 0; i < 180; i++) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timed out: " + name);
}

const control = async action => (await fetch("/__fixture" + (action ? "?action=" + action : ""))).json();
const count = (state, path) => state.calls[path] ?? 0;

async function open(path, width = 768) {
  const frame = document.createElement("iframe");
  frame.style.cssText = "display:block;border:0;width:" + width + "px;height:900px";
  frame.src = path;
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  document.body.append(frame);
  await loaded;
  for (const level of ["log", "info", "warn", "error", "debug"])
    frame.contentWindow.console[level] = (...values) => logged.push(values.map(String).join(" "));
  return frame;
}

const view = (frame, name) => frame.contentDocument.querySelector('[data-account-view="' + name + '"]');
const shown = (frame, name) => view(frame, name)?.hidden === false;
const form = (frame, name) => view(frame, name).querySelector("form");
function fill(frame, name, values) {
  const target = form(frame, name);
  for (const [key, value] of Object.entries(values)) target.elements.namedItem(key).value = value;
  target.requestSubmit();
}
function login(frame, account, secret = password) {
  fill(frame, "login", { email: account + "@example.test", password: secret });
}
const alert = (frame, name) => form(frame, name).querySelector("[data-form-error]").textContent;
const sessionModule = frame => frame.contentWindow.eval('import("/js/auth.js")');

try {
  const store = await open("/");
  assert(store.contentDocument.querySelector("[data-account-link]").textContent === "Đăng nhập", "store loads auth modules");
  store.remove();
  for (const width of [360, 390, 768, 1440]) {
    const frame = await open("/account/#register", width);
    assert(shown(frame, "register") && frame.contentWindow.innerWidth === width &&
      frame.contentDocument.documentElement.scrollWidth <= width, "register layout at " + width);
    const target = form(frame, "register").elements.namedItem("email");
    assert(target.labels.length === 1 && target.autocomplete === "email", "label at " + width);
    frame.remove();
  }

  let account = await open("/account/#register");
  account.contentDocument.querySelector(".skip-link").click();
  assert(account.contentDocument.activeElement.id === "main" && shown(account, "register"), "skip link preserves register view");
  fill(account, "register", { fullName: "A", email: "bad", password: "short", confirmPassword: "different" });
  assert(alert(account, "register").includes("kiểm tra") && count(await control(), "auth/register") === 0, "local validation stops submit");
  const registration = { fullName: "Khách thử", email: "invalid@example.test", password, confirmPassword: password };
  fill(account, "register", registration);
  await until(() => form(account, "register").elements.namedItem("email").getAttribute("aria-invalid") === "true" &&
    form(account, "register").querySelector('[data-field-error="email"]').textContent.includes("không hợp lệ"), "register server error");
  assert(alert(account, "register") === "Dữ liệu không hợp lệ.", "register validation response");
  fill(account, "register", { ...registration, email: "customer@example.test" });
  await until(() => shown(account, "login"), "register switches to login");
  assert(!localStorage.getItem("sportshop.session") && count(await control(), "auth/login") === 0, "register does not log in");

  login(account, "customer", "bad");
  await until(() => alert(account, "login").includes("không đúng"), "invalid login");
  assert(count(await control(), "auth/refresh") === 0, "invalid credentials do not refresh");
  for (const name of ["locked", "inactive"]) {
    login(account, name);
    await until(() => alert(account, "login").includes("đã bị khóa"), "locked login");
    assert(!localStorage.getItem("sportshop.session"), "reject account " + name);
  }

  const offlineFetch = account.contentWindow.fetch;
  account.contentWindow.fetch = () => Promise.reject(new TypeError("offline"));
  login(account, "customer");
  await until(() => alert(account, "login").includes("Không thể kết nối"), "network message");
  assert(!form(account, "login").querySelector("button").disabled, "network error re-enables login");
  account.contentWindow.fetch = offlineFetch;

  login(account, "customer");
  await until(() => shown(account, "profile"), "customer profile");
  assert(account.contentDocument.querySelector("[data-profile-role]").textContent === "Khách hàng" &&
    account.contentDocument.querySelector("[data-admin-link]").hidden, "customer login and no admin link");
  const cookieScope = await open("/api/v1/auth/__inspect");
  assert(typeof localStorage.getItem("sportshop.session") === "string" &&
    !cookieScope.contentDocument.cookie.includes("refreshToken"), "only marker exposed to JavaScript");
  cookieScope.remove();
  let before = await control();
  await new Promise(resolve => {
    account.addEventListener("load", resolve, { once: true });
    account.contentWindow.location.reload();
  });
  await until(() => shown(account, "profile"), "resume after real page reload");
  let after = await control();
  assert(count(after, "auth/refresh") === count(before, "auth/refresh") + 1 &&
    count(after, "users/me") === count(before, "users/me") + 1, "reload refreshes then reads profile");

  let admin = await open("/admin/");
  await until(() => admin.contentDocument.querySelector("[data-guard-message]").textContent.includes("không có quyền"), "deny customer admin access");
  assert([...admin.contentDocument.querySelectorAll("[data-admin-only]")].every(item => item.hidden), "customer never sees admin layout");
  admin.remove();

  let module = await sessionModule(account);
  before = await control("deny");
  const forbidden = await module.authorizedRequest("/users/me").catch(error => error.code);
  assert(forbidden === "FORBIDDEN" && shown(account, "profile") &&
    count(await control("normal"), "auth/refresh") === count(before, "auth/refresh"), "ordinary 403 keeps the session without refresh");

  before = await control("expire");
  const batch = await Promise.all(Array.from({length: 5}, () => module.authorizedRequest("/users/me")));
  after = await control();
  assert(batch.every(reply => reply.success) && count(after, "auth/refresh") === count(before, "auth/refresh") + 1 &&
    count(after, "users/me") === count(before, "users/me") + 10, "five simultaneous 401 use exactly one refresh and one retry each");

  // Tabs share only the marker. Each needs its own access token, and cookie rotations must be serialized.
  before = await control("pause");
  const tabOne = open("/account/");
  const tabTwo = open("/account/");
  await until(async () => count(await control(), "auth/refresh") === count(before, "auth/refresh") + 1, "cross-tab queued");
  await control("resume");
  const tabs = await Promise.all([tabOne, tabTwo]);
  await until(() => tabs.every(tab => shown(tab, "profile")), "both tabs recovered");
  assert((await control()).maxConcurrent === 1, "Web Locks serialize refresh across same-origin tabs");
  for (const tab of tabs) tab.remove();

  await control("expire");
  before = await control("pause");
  const pending = module.authorizedRequest("/users/me").catch(error => error.name);
  await until(async () => count(await control(), "auth/refresh") === count(before, "auth/refresh") + 1, "refresh reaches server");
  const logout = module.logout();
  await control("resume");
  await Promise.all([pending, logout]);
  assert(shown(account, "login") && !(await control()).hasCookie && !localStorage.getItem("sportshop.session"),
    "logout waits for cookie rotation and clears the session");

  login(account, "customer");
  await until(() => shown(account, "profile"), "customer login for bounded retry");
  before = await control("revoke");
  const stillRevoked = await module.authorizedRequest("/users/me").catch(error => error.code);
  after = await control("normal");
  assert(stillRevoked === "TOKEN_REVOKED" && shown(account, "login") &&
    count(after, "auth/refresh") === count(before, "auth/refresh") + 1 &&
    count(after, "users/me") === count(before, "users/me") + 2, "a second 401 clears the session without another retry");
  const probe = await open("/account/");
  assert(shown(probe, "login"), "a cleared marker prevents automatic refresh");
  probe.remove();

  login(account, "admin");
  await until(() => shown(account, "profile"), "admin login");
  assert(!account.contentDocument.querySelector("[data-admin-link]").hidden, "admin has dashboard link");
  admin = await open("/admin/", 390);
  await until(() => admin.contentDocument.querySelector("[data-admin-guard]").hidden, "render verified admin dashboard");
  assert([...admin.contentDocument.querySelectorAll("[data-admin-only]")].every(item => !item.hidden), "admin guard permits admin");
  admin.remove();
  await control("force");
  await module.authorizedRequest("/users/me").catch(() => {});
  await until(() => shown(account, "password"), "403 forced password change");
  assert(account.contentDocument.querySelector("[data-store-link]").hidden, "403 hides unrelated navigation");
  await control("normal");
  await module.logout();

  login(account, "forced");
  await until(() => shown(account, "password"), "login flag forces password change");
  account.contentWindow.location.hash = "profile";
  await until(() => account.contentWindow.location.hash === "#password", "guard protects account hash");
  assert(!account.contentDocument.querySelector("[data-forced-note]").hidden &&
    account.contentDocument.querySelector("[data-profile-back]").hidden, "password screen blocks bypass");
  admin = await open("/admin/");
  await until(() => admin.contentWindow.location.pathname === "/account/" && shown(admin, "password"), "forced admin redirect");
  admin.remove();
  const forcedStore = await open("/");
  await until(() => forcedStore.contentWindow.location.pathname === "/account/" && shown(forcedStore, "password"), "forced store redirect");
  assert(!forcedStore.contentDocument.querySelector("[data-forced-note]").hidden, "store forwards password-restricted sessions");
  forcedStore.remove();
  fill(account, "password", { currentPassword: "wrong", newPassword: "New-Test123", confirmNewPassword: "New-Test123" });
  await until(() => alert(account, "password").includes("không đúng"), "invalid password change");
  assert(form(account, "password").elements.namedItem("currentPassword").getAttribute("aria-invalid") === "true",
    "password error mapped to currentPassword");
  before = await control();
  fill(account, "password", { currentPassword: password, newPassword: "New-Test123", confirmNewPassword: "New-Test123" });
  await until(() => shown(account, "login"), "successful password change");
  after = await control();
  assert(!localStorage.getItem("sportshop.session") && count(after, "auth/refresh") === count(before, "auth/refresh") &&
    account.contentDocument.querySelector("[data-session-notice]").textContent.includes("Đã đổi mật khẩu"),
    "password change signs out without automatic refresh");
  assert(!JSON.stringify(localStorage).includes("fake-") && !JSON.stringify(sessionStorage).includes("fake-") &&
    logged.every(line => !line.includes("fake-") && !line.includes(password)), "no token or password in storage or console");
  result.textContent = "PASS " + checks.length + " Auth browser checks";
} catch (error) {
  result.textContent = "FAIL " + error.message + " / " + (checks.at(-1) ?? "startup");
}
await fetch("/__auth_qa", { method: "POST", body: result.textContent });
