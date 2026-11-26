const checks = [];
const errors = [];
const control = async action => (await fetch("/__fixture" + (action ? "?action=" + action : ""))).json();
const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const rows = frame => [...frame.contentDocument.querySelectorAll(".order-list li")];
const title = frame => find(frame, "[data-state-title]").textContent;
const detail = frame => find(frame, "[data-order-detail]");
const timeline = frame => find(frame, "[data-order-history]");
const cancel = frame => find(frame, "[data-open-cancel]");
const dialog = frame => find(frame, "[data-cancel-dialog]");
const notice = frame => find(frame, "[data-order-notice]").textContent;
const posts = snapshot => snapshot.historyCalls.filter(call => call.method === "POST");
const countDetail = (snapshot, id) => snapshot.historyCalls.filter(call => call.method === "GET" && call.path === "orders/" + id).length;

function assert(condition, label) {
  if (!condition) throw new Error(label);
  checks.push(label);
}

async function until(check, label) {
  for (let i = 0; i < 170; i++) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timeout " + label);
}

async function open(path, width = 950) {
  const frame = document.createElement("iframe");
  frame.style.cssText = `display:block;border:0;width:${width}px;height:850px`;
  frame.src = path;
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  document.body.append(frame);
  await loaded;
  frame.contentWindow.addEventListener("error", error => { if (error.error) errors.push(error.error.name); });
  frame.contentWindow.addEventListener("unhandledrejection", () => errors.push("unhandled rejection"));
  return frame;
}

async function login(user) {
  const frame = await open("/account/#login");
  await until(() => !find(frame, "[data-account-view=login]").hidden, "login ready");
  const form = find(frame, "[data-account-form=login]");
  form.elements.email.value = user + "@example.test";
  form.elements.password.value = "Test-Password123";
  form.requestSubmit();
  await until(() => !find(frame, "[data-account-view=profile]").hidden, "profile " + user);
  return frame;
}

const showOrder = async id => {
  const frame = await open("/orders/?id=" + id);
  await until(() => !detail(frame).hidden, "order " + id);
  return frame;
};

function confirm(frame, reason) {
  const form = dialog(frame).querySelector("form");
  form.elements.cancelReason.value = reason;
  form.requestSubmit();
}

try {
  let frame = await open("/orders/");
  assert(!find(frame, "[data-orders-login]").hidden && (await control()).historyCalls.length === 0,
    "guest sees sign-in without Orders API calls");
  frame.remove();
  const admin = await login("admin");
  frame = await open("/orders/?id=1");
  await until(() => title(frame).includes("dành cho khách hàng"), "Admin access gate");
  assert(detail(frame).hidden && find(admin, "[data-customer-orders]").hidden &&
    (await control()).historyCalls.length === 0, "Admin has no Customer Orders access");
  frame.remove();
  find(admin, "[data-account-view=profile] [data-logout]").click();
  await until(() => !find(admin, "[data-account-view=login]").hidden, "Admin logout");
  admin.remove();
  const customer = await login("customer");
  const ordersLink = find(customer, "[data-customer-orders]");
  assert(!ordersLink.hidden && ordersLink.href.endsWith("/orders/"), "Customer account links to Orders");
  customer.remove();

  frame = await open("/orders/?pageSize=2");
  await until(() => rows(frame).length === 2, "list");
  assert(rows(frame)[0].textContent.includes("SP20261114-0004") &&
    rows(frame)[0].textContent.includes("930.000") && rows(frame)[0].textContent.includes("Đang giao") &&
    find(frame, "[data-page-label]").textContent === "Trang 1 / 2", "list renders latest order, total, status and pagination");
  const expectedTime = new Intl.DateTimeFormat("vi-VN", { dateStyle: "short", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh" })
    .format(new Date("2026-11-14T20:30:00Z"));
  assert(rows(frame)[0].querySelector("time").textContent === expectedTime, "created date uses Vietnam timezone");
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  find(frame, "[data-next]").click();
  await loaded;
  await until(() => rows(frame).length === 2, "next page");
  assert(rows(frame)[0].textContent.includes("SP20261114-0002") &&
    (await control()).historyCalls.at(-1).query.page[0] === "2", "next requests the selected page");
  const filter = find(frame, "[data-orders-filter]");
  filter.elements.statusCode.value = "PENDING";
  filter.elements.sort.value = "createdAt";
  filter.requestSubmit();
  await until(() => rows(frame)[0]?.textContent.includes("SP20261114-0001"), "filtered oldest first");
  const query = (await control()).historyCalls.at(-1).query;
  assert(query.statusCode[0] === "PENDING" && query.sort[0] === "createdAt" &&
    query.page[0] === "1" && query.pageSize[0] === "2", "filter and sort preserve page size and reset page");
  frame.remove();

  frame = await open("/orders/?statusCode=DELIVERED");
  await until(() => title(frame) === "Không có đơn hàng", "empty");
  assert(!rows(frame).length, "empty filtered list");
  frame.remove();
  await control("orders-offline");
  frame = await open("/orders/");
  await until(() => title(frame) === "Chưa tải được đơn hàng", "list network error");
  find(frame, "[data-orders-state] [data-retry]").click();
  await until(() => rows(frame).length === 4, "read retry");
  assert(rows(frame).length === 4, "read retry recovers from network failure");
  frame.remove();
  await control("orders-denied");
  frame = await open("/orders/");
  await until(() => title(frame) === "Không có quyền xem đơn hàng", "403 list");
  assert(!rows(frame).length && find(frame, "[data-orders-state] [data-retry]").hidden, "403 has no order data");
  frame.remove();

  frame = await showOrder(1);
  assert(detail(frame).textContent.includes("Sản phẩm snapshot <b data-injected>QA</b>") &&
    detail(frame).textContent.includes("12 Địa chỉ snapshot <i data-injected>QA</i>") &&
    detail(frame).textContent.includes("450.000") && detail(frame).textContent.includes("930.000") &&
    !find(frame, "[data-injected]"), "detail uses snapshot text and amounts without HTML injection");
  assert(!detail(frame).textContent.includes("ADMIN-ONLY-QA") && find(frame, "[data-bank-instructions]").hidden &&
    detail(frame).textContent.includes("Đã thanh toán") && timeline(frame).children.length === 1,
    "detail shows server payment status and history without Admin code or guessed instructions");
  cancel(frame).click();
  assert(dialog(frame).open, "pending order opens confirmation");
  let before = await control();
  confirm(frame, " x ");
  assert(!find(frame, "#cancel-error").hidden && posts(await control()).length === posts(before).length, "trimmed invalid reason blocked locally");
  await control("cancel-invalid");
  confirm(frame, "Lý do kiểm thử");
  await until(() => find(frame, "#cancel-error").textContent === "Kiểm tra lại lý do hủy.", "server validation");
  assert(dialog(frame).open, "server validation remains in confirmation dialog");
  before = await control();
  confirm(frame, "  Đặt nhầm sản phẩm  ");
  confirm(frame, "  Đặt nhầm sản phẩm  ");
  await until(() => timeline(frame).children.length === 2 && cancel(frame).hidden, "cancel and GET");
  let after = await control();
  assert(posts(after).length === posts(before).length + 1 &&
    JSON.stringify(posts(after).at(-1).body) === JSON.stringify({ cancelReason: "Đặt nhầm sản phẩm" }) &&
    countDetail(after, 1) === countDetail(before, 1) + 1 &&
    detail(frame).textContent.includes("Chờ hoàn tiền") && notice(frame).includes("đang chờ hoàn tiền"),
    "cancel sends one POST then reads authoritative status, timeline and refund data");
  frame.remove();

  frame = await showOrder(2);
  assert(!find(frame, "[data-bank-instructions]").hidden && detail(frame).textContent.includes("Hướng dẫn QA <span") &&
    !find(frame, "[data-injected]"), "optional bank instructions render as text");
  before = await control("cancel-concurrency");
  cancel(frame).click();
  confirm(frame, "Tôi đặt nhầm");
  await until(() => !cancel(frame).disabled && notice(frame).includes("chưa hoàn tất"), "409 concurrency");
  assert(!cancel(frame).hidden && posts(await control()).length === posts(before).length + 1,
    "concurrency refresh allows explicit confirmation while still pending");
  await control("cancel-conflict");
  cancel(frame).click();
  confirm(frame, "Tôi đặt nhầm");
  await until(() => !detail(frame).hidden && cancel(frame).hidden && detail(frame).textContent.includes("Đã xác nhận"), "409 reread");
  assert(timeline(frame).children.length === 2 && notice(frame).includes("chưa hoàn tất"), "409 refreshes state and hides cancel");
  frame.remove();

  frame = await showOrder(3);
  before = await control("cancel-uncommitted");
  cancel(frame).click();
  confirm(frame, "Mất kết nối");
  await until(() => notice(frame).includes("Chưa xác định") && cancel(frame).hidden &&
    !find(frame, "[data-refresh-order]").disabled, "uncommitted lost response");
  find(frame, "[data-refresh-order]").click();
  await until(() => !detail(frame).hidden && !find(frame, "[data-refresh-order]").disabled, "manual read");
  assert(posts(await control()).length === posts(before).length + 1 && cancel(frame).hidden, "uncertain cancel only reloads and does not offer duplicate submit");
  frame.remove();
  frame = await showOrder(3);
  before = await control("cancel-lost");
  cancel(frame).click();
  confirm(frame, "Mất phản hồi sau khi hủy");
  await until(() => notice(frame).includes("đã hủy theo dữ liệu mới nhất"), "committed lost response");
  assert(posts(await control()).length === posts(before).length + 1 && cancel(frame).hidden, "lost committed response reconciles by GET");
  frame.remove();

  frame = await open("/orders/?id=999");
  await until(() => title(frame) === "Không tìm thấy đơn hàng", "foreign order");
  assert(detail(frame).hidden && cancel(frame).hidden, "foreign order 404 reveals no order content");
  frame.remove();
  frame = await showOrder(4);
  assert(cancel(frame).hidden && timeline(frame).children.length === 2, "shipping order only shows timeline");
  assert(localStorage.length === 1 && localStorage.key(0) === "sportshop.session" && sessionStorage.length === 0,
    "authenticated order data stays out of browser storage");
  frame.remove();

  await control("checkout-reset");
  const checkout = await open("/checkout/");
  await until(() => !find(checkout, "[data-checkout-form] button[type=submit]").disabled, "Checkout ready");
  const orderForm = find(checkout, "[data-checkout-form]");
  orderForm.elements.shippingAddress.value = "12 Đường thử";
  orderForm.elements.paymentMethodId.value = "3";
  orderForm.requestSubmit();
  await until(() => !find(checkout, "[data-checkout-result]").hidden, "checkout result");
  const checkoutLink = find(checkout, "[data-order-detail-link]");
  assert(!checkoutLink.hidden && new URL(checkoutLink.href).searchParams.get("id") === "40" &&
    (await control()).cart.items.length === 0, "Checkout links to created order and retains normal Cart consumption");
  checkout.remove();
  frame = await showOrder(40);
  assert(find(frame, "[data-orders-title]").textContent === "SP20261125-0040", "Checkout link opens created order");
  frame.remove();

  for (const width of [360, 390, 768, 1440]) {
    const list = await open("/orders/", width);
    const order = await open("/orders/?id=4", width);
    await until(() => rows(list).length === 4 && !detail(order).hidden, "responsive " + width);
    assert([list, order].every(view => view.contentWindow.innerWidth === width &&
      view.contentDocument.documentElement.scrollWidth <= width), "Orders fits " + width + " px");
    list.remove();
    order.remove();
  }
  frame = await showOrder(4);
  before = await control("expire");
  find(frame, "[data-refresh-order]").click();
  await until(async () => !detail(frame).hidden && countDetail(await control(), 4) === countDetail(before, 4) + 2, "refresh after 401");
  assert((await control()).calls["auth/refresh"] === before.calls["auth/refresh"] + 1, "expired access token refreshes before displaying order");
  await control("revoke-session");
  find(frame, "[data-refresh-order]").click();
  await until(() => !find(frame, "[data-orders-login]").hidden, "terminal 401");
  assert(detail(frame).hidden && !detail(frame).textContent.includes("Địa chỉ snapshot") && localStorage.length === 0 &&
    sessionStorage.length === 0, "expired session removes private order details and storage");
  frame.remove();
  assert(errors.length === 0, "no JavaScript module errors");
  await fetch("/__auth_qa", { method: "POST", body: `PASS ${checks.length} Orders browser checks` });
} catch (error) {
  await fetch("/__auth_qa", { method: "POST", body: `FAIL ${error.message} / ${checks.at(-1) ?? "startup"}` });
}
