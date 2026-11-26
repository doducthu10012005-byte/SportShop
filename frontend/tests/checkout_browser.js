const checks = [];
const errors = [];
const control = async action => (await fetch("/__fixture" + (action ? "?action=" + action : ""))).json();
const node = (frame, selector) => frame.contentDocument.querySelector(selector);
const form = frame => node(frame, "[data-checkout-form]");
const submit = frame => form(frame).querySelector("[type=submit]");
const heading = frame => node(frame, "[data-checkout-state] [data-state-title]").textContent;
const receipt = frame => node(frame, "[data-checkout-result]");
const methodIds = frame => [...form(frame).elements.paymentMethodId.options].map(option => option.value);

function assert(condition, label) {
  if (!condition) throw new Error(label);
  checks.push(label);
}

async function until(condition, label) {
  for (let i = 0; i < 160; i++) {
    if (await condition()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timeout " + label);
}

async function open(path = "/checkout/", width = 1060) {
  const iframe = document.createElement("iframe");
  iframe.style.cssText = `display:block;border:0;width:${width}px;height:800px`;
  iframe.src = path;
  const loaded = new Promise(resolve => iframe.addEventListener("load", resolve, { once: true }));
  document.body.append(iframe);
  await loaded;
  iframe.contentWindow.addEventListener("error", event => { if (event.error) errors.push(event.error.name); });
  iframe.contentWindow.addEventListener("unhandledrejection", () => errors.push("unhandled rejection"));
  return iframe;
}

async function account(username) {
  const iframe = await open("/account/#login");
  await until(() => !node(iframe, "[data-account-view=login]").hidden, "login form");
  const login = node(iframe, "[data-account-form=login]");
  login.elements.email.value = username + "@example.test";
  login.elements.password.value = "Test-Password123";
  login.requestSubmit();
  await until(() => !node(iframe, "[data-account-view=profile]").hidden, "account profile");
  return iframe;
}

function valid(frame, method = 3) {
  Object.assign(form(frame).elements.recipientName, { value: " Người nhận thử " });
  form(frame).elements.recipientPhone.value = "0912345678";
  form(frame).elements.shippingAddress.value = " 12 Đường thử, Hà Nội ";
  form(frame).elements.note.value = " Gọi trước khi giao ";
  form(frame).elements.paymentMethodId.value = String(method);
}

async function ready() {
  const frame = await open();
  await until(() => !form(frame).hidden && !submit(frame).disabled, "checkout ready");
  return frame;
}

async function refused(action, expected) {
  await control("checkout-reset");
  const iframe = await ready();
  valid(iframe);
  const before = await control(action);
  form(iframe).requestSubmit();
  await until(() => heading(iframe) === "Đặt hàng chưa thành công", action);
  const after = await control();
  assert(receipt(iframe).hidden && node(iframe, "[data-checkout-state]").textContent.includes(expected) &&
    after.orders.length === before.orders.length && after.orderRequests.length === before.orderRequests.length + 1,
    action + " handled without duplicate order");
  iframe.remove();
}

try {
  let frame = await open();
  assert(!node(frame, "[data-checkout-login]").hidden && form(frame).hidden &&
    (await control()).orderRequests.length === 0, "guest sees Customer sign-in");
  frame.remove();
  const admin = await account("admin");
  frame = await open();
  await until(() => heading(frame).includes("Chỉ khách hàng"), "Admin block");
  assert(form(frame).hidden && (await control()).orderRequests.length === 0, "Admin cannot use Checkout");
  frame.remove();
  node(admin, "[data-account-view=profile] [data-logout]").click();
  await until(() => !node(admin, "[data-account-view=login]").hidden, "Admin logs out");
  admin.remove();
  const customer = await account("customer");
  customer.remove();

  frame = await open();
  await until(() => heading(frame) === "Giỏ hàng trống", "empty Cart");
  assert(form(frame).hidden, "empty Cart has no order form");
  frame.remove();
  await control("checkout-reset");
  await control("stock-zero");
  frame = await open();
  await until(() => !node(frame, "[data-checkout-stock]").hidden, "unavailable items");
  form(frame).requestSubmit();
  assert(submit(frame).disabled && (await control()).orderRequests.length === 0 &&
    (await control()).cart.items.length === 1, "unavailable items block Checkout without being deleted");
  frame.remove();
  const cartPage = await open("/cart/");
  await until(() => !node(cartPage, "[data-checkout-blocked]").hidden, "cart block");
  assert(node(cartPage, "[data-checkout-link]").hidden, "Cart hides Checkout entry for an unavailable line");
  cartPage.remove();

  await control("checkout-reset");
  await control("cod-only");
  frame = await ready();
  assert(methodIds(frame).join(",") === ",3", "inactive BANK_TRANSFER is absent");
  frame.remove();
  await control("no-methods");
  frame = await open();
  await until(() => heading(frame) === "Chưa có phương thức thanh toán", "no payment methods");
  assert(submit(frame).disabled, "checkout blocked without available methods");
  frame.remove();

  await control("checkout-reset");
  await control("method-error");
  await control("address-error");
  frame = await open();
  await until(() => heading(frame) === "Chưa tải được thông tin đặt hàng" &&
    node(frame, "[data-address-status]").textContent.includes("nhập địa chỉ"), "lookup failures");
  assert(submit(frame).disabled, "payment lookup error stops Checkout and address lookup allows manual entry");
  const retryPayment = node(frame, "[data-checkout-state] [data-retry]");
  await until(() => !retryPayment.hidden && !retryPayment.disabled, "payment retry enabled after cart load");
  retryPayment.click();
  await until(() => !submit(frame).disabled, "payment retry");
  assert(methodIds(frame).join(",") === ",3,7", "read-only retry restores payment choices");
  node(frame, "[data-more-addresses]").click();
  await until(() => form(frame).elements.savedAddress.options.length === 2, "retry addresses");
  frame.remove();

  const linked = await open("/cart/");
  await until(() => !node(linked, "[data-checkout-link]").hidden, "Checkout Cart entry");
  assert(node(linked, "[data-checkout-link]").href.endsWith("/checkout/"), "available Cart links to Checkout");
  linked.remove();

  frame = await ready();
  await until(() => form(frame).elements.savedAddress.options.length === 2, "saved addresses");
  assert(form(frame).elements.recipientName.value === "Người dùng thử" &&
    form(frame).elements.recipientPhone.value === "0912345678", "verified profile pre-fills recipient fields");
  assert(node(frame, "[data-preview-subtotal]").textContent.includes("840.000") &&
    form(frame).textContent.includes("Chốt khi đặt hàng") && !form(frame).textContent.includes("30.000"),
    "Cart subtotal is shown and fee is not guessed");
  const beforeValidation = await control();
  valid(frame);
  form(frame).elements.recipientName.value = " ";
  form(frame).elements.recipientPhone.value = "123";
  form(frame).elements.shippingAddress.value = "";
  form(frame).requestSubmit();
  assert((await control()).orderRequests.length === beforeValidation.orderRequests.length &&
    ["recipientName", "recipientPhone", "shippingAddress"].every(name => form(frame).elements[name].hasAttribute("aria-invalid")),
    "invalid receiving fields are marked with no POST");
  node(frame, "[data-more-addresses]").click();
  await until(() => form(frame).elements.savedAddress.options.length === 3, "next saved address page");
  form(frame).elements.savedAddress.value = "17";
  form(frame).elements.savedAddress.dispatchEvent(new Event("change", { bubbles: true }));
  form(frame).elements.paymentMethodId.value = "3";
  form(frame).elements.note.value = " Gọi trước ";
  const beforeCod = await control("price-on-submit");
  form(frame).requestSubmit();
  form(frame).requestSubmit();
  let snapshot = await control();
  assert(submit(frame).disabled && snapshot.cart.items.length === 1 && receipt(frame).hidden, "pending POST disables double submit and keeps Cart");
  await until(() => !receipt(frame).hidden, "COD receipt");
  snapshot = await control();
  const body = snapshot.orderRequests.at(-1);
  assert(snapshot.orderRequests.length === beforeCod.orderRequests.length + 1 &&
    Object.keys(body).sort().join(",") === "note,paymentMethodId,recipientName,recipientPhone,shippingAddress" &&
    body.paymentMethodId === 3 && body.recipientName === "Người nhận QA 2" && body.recipientPhone === "+84912345678" &&
    body.shippingAddress === "2 Đường thử, Hàng Bạc, Hoàn Kiếm, Hà Nội" && body.note === "Gọi trước",
    "one POST uses address snapshot and only contract fields");
  assert(node(frame, "[data-order-code]").textContent === "SP20261125-0040" &&
    node(frame, "[data-order-status]").textContent === "Chờ xác nhận" &&
    node(frame, "[data-order-subtotal]").textContent.includes("960.000") &&
    node(frame, "[data-order-shipping]").textContent.includes("27.000") &&
    node(frame, "[data-order-total]").textContent.includes("987.000") &&
    node(frame, "[data-order-payment]").textContent.includes("Chờ thanh toán") &&
    node(frame, "[data-bank-instructions]").hidden && snapshot.cart.items.length === 0,
    "COD receipt shows final Backend amounts after price change");
  frame.remove();

  await control("checkout-reset");
  frame = await ready();
  valid(frame, 7);
  const beforeBank = await control("expire");
  form(frame).requestSubmit();
  await until(() => !receipt(frame).hidden, "bank receipt after refresh");
  snapshot = await control();
  assert(snapshot.orderRequests.length === beforeBank.orderRequests.length + 2 &&
    snapshot.calls["auth/refresh"] === beforeBank.calls["auth/refresh"] + 1 &&
    snapshot.orders.length === beforeBank.orders.length + 1, "POST retries only after rejected 401 and successful refresh");
  assert(!node(frame, "[data-bank-instructions]").hidden &&
    node(frame, "[data-bank-text]").textContent.includes("0000000000 <b data-injected>QA</b>") &&
    !node(frame, "[data-injected]") && !node(frame, "[data-bank-deadline]").hidden &&
    node(frame, "[data-bank-deadline] time").dateTime === snapshot.orders.at(-1).payment.expiresAt &&
    node(frame, "[data-bank-remaining]").textContent.includes("phút") &&
    node(frame, "[data-order-payment]").textContent.includes("Chờ thanh toán") &&
    node(frame, "[data-order-total]").textContent.includes("867.000"), "BANK_TRANSFER displays only Backend details and pending status");
  assert(!JSON.stringify(localStorage).includes("0000000000") && sessionStorage.length === 0, "payment info stays out of browser storage");
  frame.remove();

  await control("checkout-reset");
  frame = await ready();
  valid(frame, 7);
  let before = await control("disable-on-submit");
  form(frame).requestSubmit();
  await until(() => !node(frame, "[data-field-error=paymentMethodId]").hidden && methodIds(frame).join(",") === ",3", "disabled method error");
  snapshot = await control();
  assert(snapshot.orders.length === before.orders.length && snapshot.cart.items.length === 1 && receipt(frame).hidden,
    "disabled method error is attached to field and reloads payment choices");
  frame.remove();

  await refused("forbidden", "Yêu cầu đặt hàng bị từ chối.");
  await refused("missing-order", "Không tìm thấy dữ liệu");
  await refused("concurrency", "Giỏ hàng vừa thay đổi");
  await refused("empty-on-submit", "Giỏ hàng đã trống");

  await control("checkout-reset");
  frame = await ready();
  valid(frame);
  before = await control("stock-on-submit");
  form(frame).requestSubmit();
  await until(() => !node(frame, "[data-checkout-stock]").hidden && heading(frame) === "Đặt hàng chưa thành công", "stock change on submit");
  assert(node(frame, "[data-checkout-state]").textContent.includes("Giày QA chỉ còn 1") &&
    node(frame, "[data-checkout-state]").textContent.includes("Một biến thể khác vừa hết hàng") &&
    submit(frame).disabled && (await control()).orders.length === before.orders.length &&
    (await control()).cart.items.length === 1, "stock conflict is displayed and updated Cart remains intact");
  frame.remove();

  for (const [action, method, created] of [["missing-bank", 7, 0], ["response-dropped", 3, 1],
    ["malformed-result", 3, 1], ["wrong-success-status", 3, 1]]) {
    await control("checkout-reset");
    frame = await ready();
    valid(frame, method);
    before = await control(action);
    form(frame).requestSubmit();
    await until(() => heading(frame) === "Chưa xác định được kết quả đặt hàng", action);
    form(frame).requestSubmit();
    snapshot = await control();
    assert(receipt(frame).hidden && form(frame).hidden && snapshot.orders.length === before.orders.length + created &&
      snapshot.orderRequests.length === before.orderRequests.length + 1 && snapshot.cart.items.length === 1 - created,
      action + " shows unknown outcome without automatic POST replay");
    frame.remove();
  }

  await control("checkout-reset");
  for (const width of [360, 390, 768, 1440]) {
    frame = await open("/checkout/", width);
    await until(() => !form(frame).hidden && !submit(frame).disabled, "responsive Checkout");
    assert(frame.contentWindow.innerWidth === width && frame.contentDocument.documentElement.scrollWidth <= width,
      "Checkout fits " + width + " px");
    frame.remove();
  }
  frame = await ready();
  valid(frame);
  before = await control("revoke-session");
  form(frame).requestSubmit();
  await until(() => !node(frame, "[data-checkout-login]").hidden, "unrecoverable 401");
  snapshot = await control();
  assert(form(frame).hidden && receipt(frame).hidden && snapshot.orderRequests.length === before.orderRequests.length + 1 &&
    snapshot.orders.length === before.orders.length, "failed refresh prompts sign-in without a second POST");
  assert(errors.length === 0, "Checkout modules produce no JavaScript errors");
  frame.remove();
  await fetch("/__auth_qa", { method: "POST", body: `PASS ${checks.length} Checkout browser checks` });
} catch (error) {
  await fetch("/__auth_qa", { method: "POST", body: `FAIL ${error.message} / ${checks.at(-1) ?? "startup"}` });
}
