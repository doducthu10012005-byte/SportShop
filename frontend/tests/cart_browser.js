const checks = [];
const errors = [];
const control = async action => (await fetch("/__fixture" + (action ? "?action=" + action : ""))).json();
const select = (frame, selector) => frame.contentDocument.querySelector(selector);
const lines = frame => [...frame.contentDocument.querySelectorAll("[data-item-id]")];
const details = "/products/detail.html?id=1";
const addButton = frame => select(frame, "[data-add-form] button[type=submit]");
const cartState = frame => select(frame, "[data-cart-state]");
const status = frame => cartState(frame).querySelector("[data-state-title]").textContent;
const countCalls = (snapshot, method) => snapshot.cartCalls.filter(call => call.method === method).length;

function assert(ok, label) {
  if (!ok) throw new Error(label);
  checks.push(label);
}

async function until(condition, label) {
  for (let i = 0; i < 150; i++) {
    if (await condition()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timeout " + label);
}

async function open(path, width = 1070) {
  const frame = document.createElement("iframe");
  frame.style.cssText = `display:block;border:0;width:${width}px;height:900px`;
  frame.src = path;
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  document.body.append(frame);
  await loaded;
  frame.contentWindow.addEventListener("error", error => { if (error.error) errors.push(error.error.name); });
  frame.contentWindow.addEventListener("unhandledrejection", () => errors.push("promise rejection"));
  return frame;
}

async function enter(name) {
  const account = await open("/account/#login");
  const form = select(account, "[data-account-form=login]");
  form.elements.email.value = name + "@example.test";
  form.elements.password.value = "Test-Password123";
  form.requestSubmit();
  await until(() => !select(account, "[data-account-view=profile]").hidden, "login " + name);
  return account;
}

async function logout(frame) {
  select(frame, "[data-account-view=profile] [data-logout]").click();
  await until(() => !select(frame, "[data-account-view=login]").hidden, "logout");
  frame.remove();
}

async function showDetail() {
  const frame = await open(details);
  await until(() => !select(frame, "[data-detail]").hidden, "product details");
  return frame;
}

function inputValue(input, value) {
  input.value = value;
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

function closeDialog(frame, value) {
  select(frame, "[data-clear-cart]").click();
  const dialog = select(frame, "#clear-cart");
  assert(dialog.open, "clear requires dialog confirmation");
  dialog.querySelector(`[value="${value}"]`).click();
}

try {
  let cart = await open("/cart/");
  assert(!select(cart, "[data-cart-login]").hidden && status(cart).includes("Đăng nhập"), "anonymous cart has sign-in");
  let product = await showDetail();
  assert(addButton(product).disabled && !select(product, "[data-add-login]").hidden &&
    (await control()).cartCalls.length === 0, "anonymous add is blocked before any Cart API request");
  product.remove();
  cart.remove();

  const admin = await enter("admin");
  cart = await open("/cart/");
  product = await showDetail();
  await until(() => status(cart).includes("dành cho khách hàng") &&
    select(product, "[data-add-note]").textContent.includes("chỉ dành"), "Admin guard");
  assert(addButton(product).disabled && select(cart, "[data-cart-login]").hidden &&
    (await control()).cartCalls.length === 0, "Admin blocked locally on both pages");
  cart.remove();
  product.remove();
  await logout(admin);

  const customer = await enter("customer");
  cart = await open("/cart/");
  await until(() => status(cart) === "Giỏ hàng trống", "empty cart");
  assert(select(cart, "[data-cart-content]").hidden && select(cart, "[data-cart-count]").hidden, "empty Cart response");
  cart.remove();
  product = await showDetail();
  await until(() => !addButton(product).disabled, "customer can add");
  select(product, '[name="product-color"][value="6"]').click();
  assert(addButton(product).disabled &&
    select(product, "[data-variant-stock]").textContent === "Hết hàng", "sold-out variant cannot be added");
  select(product, '[name="product-color"][value="5"]').click();
  const addForm = select(product, "[data-add-form]");
  addForm.elements.quantity.value = "10";
  let before = await control();
  addForm.requestSubmit();
  assert(countCalls(await control(), "POST") === countCalls(before, "POST"), "quantity above product stock is not sent");
  addForm.elements.quantity.value = "2";
  addForm.requestSubmit();
  addForm.requestSubmit();
  await until(() => select(product, "[data-add-success]").textContent.includes("Đã thêm"), "successful add");
  let snapshot = await control();
  assert(countCalls(snapshot, "POST") === countCalls(before, "POST") + 1 &&
    snapshot.cartCalls.find(call => call.method === "POST").body.variantId === 5 &&
    snapshot.cart.items[0].quantity === 2 && select(product, "[data-cart-count]").textContent === "2",
    "add sends chosen variant and quantity once and updates badge from Backend");
  addForm.elements.quantity.value = "1";
  addForm.requestSubmit();
  await until(() => select(product, "[data-cart-count]").textContent === "3", "same variant added");
  assert((await control()).cart.items.length === 1, "adding same variant uses Backend accumulated quantity");
  await control("conflict-write");
  addForm.requestSubmit();
  await until(() => !select(product, "[data-add-error]").hidden, "rejected add");
  assert(select(product, "[data-add-error]").textContent.includes("Biến thể chỉ còn 1") &&
    select(product, "[data-add-success]").textContent === "" && (await control()).cart.items[0].quantity === 3,
    "stock conflict on add is never reported as success");
  await control("stock-restore");
  product.remove();

  cart = await open("/cart/");
  await until(() => lines(cart).length === 1, "cart data");
  assert(lines(cart)[0].querySelector("h3").textContent.includes("<b data-cart-injection>") &&
    !select(cart, "[data-cart-injection]") && select(cart, "[data-cart-subtotal]").textContent.includes("1.260.000"),
    "cart renders API text safely and uses Backend subtotal");
  const originalRow = lines(cart)[0];
  const originalId = Number(originalRow.dataset.itemId);
  originalRow.querySelector('[data-quantity-step="1"]').click();
  assert(originalRow.querySelector("input").value === "4" && !(await control()).cartCalls.some(call => call.method === "PUT"),
    "quantity step edits draft without sending a write");
  originalRow.querySelector("form").requestSubmit();
  originalRow.querySelector("form").requestSubmit();
  await until(() => select(cart, "[data-cart-total-items]").textContent === "4", "update");
  snapshot = await control();
  const updates = snapshot.cartCalls.filter(call => call.method === "PUT");
  assert(updates.length === 1 && updates[0].path === "cart/items/" + originalId && updates[0].body.quantity === 4,
    "quantity update sends one PUT using cartItemId");
  const cartApi = await cart.contentWindow.eval('import("/js/cart.js")');
  before = await control();
  let unknown;
  try { await cartApi.removeFromCart(99999); } catch (error) { unknown = error.code; }
  assert(unknown === "NOT_FOUND" && (await control()).cartCalls.length === before.cartCalls.length, "unknown cartItemId rejected locally");

  await control("stop-selling");
  await cartApi.loadCart();
  assert(select(cart, "[data-line-stock]").textContent === "Dòng hàng không còn khả dụng" &&
    !lines(cart)[0].querySelector("input") && !lines(cart)[0].querySelector("[data-remove-item]").disabled,
    "a discontinued line stays visible and removable");
  await control("resume-selling");
  await control("price-stock");
  await cartApi.loadCart();
  assert(lines(cart).length === 1 && select(cart, "[data-line-stock]").textContent.includes("Chỉ còn 2") &&
    select(cart, "[data-cart-total-items]").textContent === "0" && select(cart, "[data-cart-subtotal]").textContent.startsWith("0") &&
    select(cart, "[data-cart-count]").hidden && lines(cart)[0].textContent.includes("480.000"),
    "changed price and insufficient stock leave the line visible but excluded from totals");
  lines(cart)[0].querySelector('[data-quantity-step="-1"]').click();
  lines(cart)[0].querySelector("form").requestSubmit();
  await until(() => select(cart, "[data-cart-total-items]").textContent === "2", "reduce to stock");
  assert(select(cart, "[data-cart-subtotal]").textContent.includes("960.000"), "reducing a short line restores Backend totals");

  await control("conflict-write");
  const row = lines(cart)[0];
  await control("stock-restore");
  await cartApi.loadCart();
  inputValue(lines(cart)[0].querySelector("input"), "3");
  lines(cart)[0].querySelector("form").requestSubmit();
  await until(() => cartState(cart).dataset.state === "error", "409 stock conflict");
  assert(cartState(cart).textContent.includes("Biến thể chỉ còn 1") && select(cart, "[data-cart-total-items]").textContent === "0" &&
    lines(cart)[0] !== row && (await control()).cart.items[0].quantity === 2, "409 shows stock details and reloads without replay");

  await control("stock-zero");
  await cartApi.loadCart();
  assert(select(cart, "[data-line-stock]").textContent === "Hết hàng" && !lines(cart)[0].querySelector("input") &&
    !lines(cart)[0].querySelector("[data-remove-item]").disabled, "zero-stock line can be removed");
  const originalFetch = cart.contentWindow.fetch;
  cart.contentWindow.fetch = () => Promise.reject(new TypeError("offline"));
  before = await control();
  lines(cart)[0].querySelector("[data-remove-item]").click();
  await until(() => cartState(cart).textContent.includes("Không thể kết nối"), "network error");
  assert(lines(cart).length === 1 && (await control()).cartCalls.length === before.cartCalls.length, "network failure keeps a readable cart and avoids write retry");
  cart.contentWindow.fetch = originalFetch;
  select(cart, "[data-cart-state] [data-retry]").click();
  await until(() => cartState(cart).hidden && !lines(cart)[0].querySelector("[data-remove-item]").disabled, "retry after network");
  await control("deny-write");
  lines(cart)[0].querySelector("[data-remove-item]").click();
  await until(() => cartState(cart).textContent.includes("Yêu cầu bị từ chối."), "403 write");
  assert(lines(cart).length === 1 && (await control()).cart.items.length === 1, "403 keeps cart unchanged");

  await control("missing-write");
  lines(cart)[0].querySelector("[data-remove-item]").click();
  await until(() => cartState(cart).textContent.includes("Dòng hàng hoặc biến thể"), "404 write");
  assert(lines(cart).length === 0 && (await control()).cart.items.length === 0, "404 refreshes snapshot without deleting a different line");

  await control("stock-restore");
  await cartApi.addToCart(5, 1);
  await cartApi.addToCart(7, 1);
  before = await control("expire");
  lines(cart)[0].querySelector("[data-remove-item]").click();
  await until(() => lines(cart).length === 1, "DELETE after refresh");
  snapshot = await control();
  assert((snapshot.calls["auth/refresh"] ?? 0) === (before.calls["auth/refresh"] ?? 0) + 1 &&
    countCalls(snapshot, "DELETE") === countCalls(before, "DELETE") + 2 &&
    snapshot.cart.items[0].variantId === 7, "401 refreshes and retries item deletion once");
  before = await control();
  closeDialog(cart, "keep");
  await until(() => !select(cart, "#clear-cart").open, "cancel clear");
  assert((await control()).cartCalls.length === before.cartCalls.length, "cancel leaves the whole cart untouched");
  closeDialog(cart, "clear");
  await until(() => !cartState(cart).hidden && status(cart) === "Giỏ hàng trống", "DELETE cart");
  snapshot = await control();
  assert(snapshot.cartCalls.at(-1).path === "cart" && snapshot.cartCalls.at(-1).method === "DELETE" &&
    snapshot.cart.items.length === 0, "clear cart returns empty Backend snapshot");

  await cartApi.addToCart(5, 2);
  for (const width of [360, 390, 768, 1440]) {
    const frame = await open("/cart/", width);
    await until(() => lines(frame).length === 1, "responsive cart");
    assert(frame.contentWindow.innerWidth === width && frame.contentDocument.documentElement.scrollWidth <= width,
      "cart fits " + width + " px");
    frame.remove();
  }

  await control("revoke-session");
  lines(cart)[0].querySelector("[data-remove-item]").click();
  await until(() => !select(cart, "[data-cart-login]").hidden, "unrecoverable 401");
  assert(lines(cart).length === 0 && select(cart, "[data-cart-count]").hidden &&
    (await control()).cart.items.length === 1, "failed refresh hides the prior cart and prompts for login");
  assert(localStorage.length === 0 && sessionStorage.length === 0 && errors.length === 0,
    "no persisted cart or token and no JavaScript errors");
  cart.remove();
  customer.remove();
  await fetch("/__auth_qa", { method: "POST", body: `PASS ${checks.length} Cart browser checks` });
} catch (error) {
  await fetch("/__auth_qa", { method: "POST", body: `FAIL ${error.message} / ${checks.at(-1) ?? "startup"}` });
}
