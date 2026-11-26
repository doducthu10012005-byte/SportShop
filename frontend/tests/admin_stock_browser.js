const checks = [];
const errors = [];
const manage = "/admin/catalog/manage/";
const productPath = manage + "?productId=1";
const inventoryPath = manage + "?view=inventory";
const control = async (action = "") => (await fetch("/__fixture?action=" + action)).json();
const requests = async () => (await control()).stockRequests;
const writes = async () => (await requests()).filter(entry => entry.method !== "GET");
const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const text = (frame, selector) => find(frame, selector)?.textContent.trim() ?? "";
const shown = (frame, selector) => find(frame, selector)?.hidden === false;
const click = (frame, selector) => find(frame, selector).click();
const form = frame => find(frame, "[data-operation-form]");
const save = "[data-operation-save]";
const close = "[data-operation-close]";
const error = "[data-operation-error]";
const row = id => `[data-variant-id="${id}"]`;
const submit = frame => form(frame).requestSubmit();

function assert(ok, label) {
  if (!ok) throw Error(label);
  checks.push(label);
}

async function until(predicate, label) {
  for (let i = 0; i < 160; i++) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw Error("Timeout " + label);
}

async function open(path, width = 1440) {
  const frame = document.createElement("iframe");
  frame.width = width;
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
  const login = find(frame, "[data-account-form=login]");
  login.elements.email.value = account + "@example.test";
  login.elements.password.value = "Test-Password123";
  login.requestSubmit();
}

function fields(frame, values) {
  for (const [name, value] of Object.entries(values)) {
    const control = form(frame).elements.namedItem(name);
    if (typeof value === "boolean") control.checked = value;
    else control.value = String(value);
    control.dispatchEvent(new Event("change"));
  }
}

async function png(width = 1, height = 1) {
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  return await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
}

function chooseFile(frame, file) {
  const transfer = new DataTransfer();
  transfer.items.add(file);
  form(frame).elements.ImageFile.files = transfer.files;
  form(frame).elements.ImageFile.dispatchEvent(new Event("change"));
}

function quantity(frame) {
  return find(frame, row(1) + " [data-quantity]")?.firstChild?.textContent;
}

try {
  const product = await open(productPath);
  await until(() => shown(product, "[data-account-view=login]"), "guest");
  assert((await requests()).length === 0 && product.contentWindow.location.search.includes("productId=1"),
    "guest is redirected with the product return path");
  login(product, "customer");
  await until(() => shown(product, "[data-account-view=profile]"), "customer login");
  const blocked = await open(inventoryPath);
  await until(() => text(blocked, "[data-guard-message]").includes("không có quyền"), "customer blocked");
  assert((await requests()).length === 0, "Customer cannot fetch the Admin inventory");
  blocked.remove();
  click(product, "[data-account-view=profile] [data-logout]");
  await until(() => shown(product, "[data-account-view=login]"), "customer logout");
  login(product, "forced");
  await until(() => shown(product, "[data-account-view=password]"), "forced password");
  assert((await requests()).length === 0, "forced password change blocks product detail");
  click(product, "[data-account-view=password] [data-logout]");
  await until(() => shown(product, "[data-account-view=login]"), "forced logout");
  login(product, "admin");
  await until(() => shown(product, "[data-assets-output]"), "product details");
  assert(text(product, row(1)).includes("QA-BASE") && text(product, row(1)).includes("Xanh") &&
    text(product, row(1)).includes("Đang hoạt động") && text(product, row(1)).includes("100.000"),
    "detail displays SKU, size, color, price, in-stock quantity and active status");
  await until(() => !find(product, "[data-image-id] img"), "broken image fallback");
  assert(text(product, "[data-image-id]").includes("Chưa có ảnh"), "missing image falls back safely");

  click(product, "[data-add-product-variant]");
  await until(() => form(product).elements.sizeId, "variant lookups");
  fields(product, {sizeId: 1, colorId: 1, sku: "QA-NEW", price: "123456.75", initialStock: 0});
  submit(product);
  assert(text(product, error).includes("Tổ hợp") && (await writes()).length === 0, "client blocks a duplicate size and color");
  fields(product, {sizeId: 2});
  await control("stock-mode-duplicate-variant");
  submit(product);
  await until(() => text(product, error).includes("Tổ hợp"), "server duplicate variant");
  assert(find(product, "[data-operation]").open, "server duplicate variant stays in the form");
  await control("stock-mode-normal");
  submit(product);
  await until(() => find(product, row(2)), "created");
  const creation = (await writes()).at(-1);
  assert(creation.path === "admin/products/1/variants" && JSON.stringify(creation.body) ===
    '{"sku":"QA-NEW","price":123456.75,"isActive":true,"sizeId":2,"colorId":1,"initialStock":0}',
    "variant create sends the supported DTO");
  click(product, row(2) + " [data-edit-variant]");
  fields(product, {sku: "qa-base"});
  submit(product);
  await until(() => text(product, error).includes("SKU"), "duplicate SKU");
  assert(find(product, "[data-operation]").open, "duplicate SKU is surfaced from 409");
  fields(product, {sku: "QA-EDIT", price: "125000.5", isActive: false});
  submit(product);
  await until(() => text(product, row(2)).includes("QA-EDIT"), "updated");
  const edit = (await writes()).at(-1);
  assert(edit.method === "PUT" && Object.keys(edit.body).sort().join() === "isActive,price,sku" &&
    text(product, row(2)).includes("Đã ngừng bán"), "variant edit changes price and status without inventory fields");
  click(product, row(2) + " [data-edit-variant]");
  fields(product, {isActive: true});
  await control("stock-mode-validation");
  submit(product);
  await until(() => text(product, error).includes("Kích thước hoặc màu"), "validation 400");
  assert(form(product).elements.isActive.getAttribute("aria-invalid") === "true", "Backend validation annotates the affected field");
  await control("stock-mode-normal");
  submit(product);
  await until(() => find(product, row(2) + " [data-hide-variant]"), "reactivated");
  click(product, row(2) + " [data-hide-variant]");
  assert(text(product, "[data-operation-fields]").includes("lịch sử giao dịch"), "deactivation has confirmation");
  click(product, save);
  await until(() => text(product, row(2)).includes("Đã ngừng bán"), "soft deleted");
  assert((await writes()).at(-1).method === "DELETE" && !!find(product, row(2)), "inactive variant remains in Admin list");

  const inventory = await open(inventoryPath);
  await until(() => shown(inventory, "[data-inventory-output]"), "inventory load");
  assert(quantity(inventory) === "4" && text(inventory, row(1)).includes("Sắp hết") && text(inventory, row(2)).includes("Hết hàng"),
    "inventory renders server stock, threshold and availability");
  const filters = find(inventory, "[data-stock-filters]");
  filters.elements.keyword.value = "QA-EDIT";
  filters.elements.lowStock.checked = filters.elements.outOfStock.checked = true;
  filters.requestSubmit();
  await until(() => find(inventory, row(2)) && !find(inventory, row(1)), "inventory filter");
  const query = (await requests()).at(-1).query;
  assert(query.keyword[0] === "QA-EDIT" && query.lowStock[0] === "true" && query.outOfStock[0] === "true",
    "inventory sends keyword and both documented filters");
  filters.reset();
  filters.requestSubmit();
  await until(() => find(inventory, row(1)), "inventory reset");
  click(inventory, row(1) + " [data-history]");
  await until(() => shown(inventory, "[data-history-output]"), "history list");
  assert(text(inventory, "[data-history-output]").includes("Hệ thống") && text(inventory, "[data-history-output]").includes("Hoàn kho"),
    "history displays transaction type and system actor");
  click(inventory, "[data-history-output] [data-next]");
  await until(() => text(inventory, "[data-history-output]").includes("2/2"), "history second page");
  assert((await requests()).at(-1).query.page[0] === "2", "history pagination uses requested page");
  click(inventory, row(1) + " [data-adjust]");
  fields(inventory, {quantity: 0});
  assert(!form(inventory).elements.quantity.checkValidity(), "Import requires positive whole units");
  fields(inventory, {quantity: 3, note: "  Lô QA  "});
  const beforeImport = (await writes()).length;
  await control("stock-hold");
  click(inventory, save);
  click(inventory, save);
  await until(async () => (await writes()).length === beforeImport + 1, "one import pending");
  assert(find(inventory, save).disabled && (await writes()).length === beforeImport + 1, "double-click does not duplicate a stock write");
  await control("stock-release");
  await until(() => quantity(inventory) === "7" && text(inventory, "[data-history-output]").includes("Lô QA"), "import succeeded");
  assert(JSON.stringify((await writes()).at(-1).body) === '{"type":"Import","quantity":3,"note":"Lô QA"}',
    "Import sends an increment and reloads stock and history");
  click(inventory, row(1) + " [data-adjust]");
  fields(inventory, {type: "Adjustment", quantity: 0, note: "  "});
  submit(inventory);
  assert(text(inventory, error).includes("lý do"), "Adjustment requires a nonblank note");
  fields(inventory, {quantity: 7, note: "Kiểm kê"});
  submit(inventory);
  await until(() => text(inventory, error).includes("khác tồn kho cũ"), "zero delta validation");
  assert(quantity(inventory) === "7", "Backend rejects a no-op adjustment");
  fields(inventory, {quantity: 0});
  await control("stock-mode-conflict");
  submit(inventory);
  await until(() => text(inventory, error).includes("vừa thay đổi"), "concurrency error");
  assert(find(inventory, "[data-operation]").open && !find(inventory, save).disabled, "409 concurrency is a visible, definitive rejection");
  await control("stock-mode-normal");
  submit(inventory);
  await until(() => quantity(inventory) === "0" && text(inventory, "[data-history-output]").includes("Kiểm kê"), "adjust zero");
  assert((await writes()).at(-1).body.type === "Adjustment", "Adjustment sets the absolute balance, including zero");
  click(inventory, row(1) + " [data-adjust]");
  fields(inventory, {quantity: 2});
  await control("stock-mode-lost");
  const beforeLoss = (await writes()).length;
  submit(inventory);
  await until(() => text(inventory, error).includes("Chưa xác định") && quantity(inventory) === "2" &&
    text(inventory, "[data-history-output]").includes("+2"), "reconcile unknown result");
  assert((await writes()).length === beforeLoss + 1 && find(inventory, save).disabled,
    "lost response triggers reads of balance and history but no write replay");
  click(inventory, close);
  await control("stock-mode-normal");

  click(product, "[data-upload-image]");
  const imageWrites = (await writes()).length;
  chooseFile(product, new File(["not an image"], "fake.png", {type:"image/png"}));
  await until(() => text(product, error).includes("JPG"), "bad image bytes");
  chooseFile(product, new File([new Uint8Array(2 * 1024 * 1024 + 1)], "large.png", {type:"image/png"}));
  await until(() => text(product, error).includes("2 MB"), "oversized local image");
  chooseFile(product, new File([await png(4001)], "wide.png", {type:"image/png"}));
  await until(() => text(product, error).includes("4000"), "wide local image");
  assert((await writes()).length === imageWrites && !find(product, "[data-image-preview]").src,
    "bad format, byte size and dimensions are rejected before upload");
  chooseFile(product, new File([await png()], "qa.png", {type:"image/png"}));
  await until(() => find(product, "[data-image-preview]")?.src.startsWith("blob:"), "preview");
  assert(!form(product).elements.isPrimary.checked, "valid image has a preview and defaults to non-primary");
  fields(product, {colorId: 1, isPrimary: true});
  for (const [mode, message] of [["too-large", "2 MB"], ["unsupported", "Sai định dạng"], ["rate", "quá nhiều"]]) {
    await control("stock-mode-" + mode);
    submit(product);
    await until(() => text(product, error).includes(message), mode);
  }
  assert((await writes()).length === imageWrites + 3 && find(product, "[data-operation]").open,
    "upload exposes Backend 413, 415 and 429 without claiming success");
  await control("stock-mode-normal");
  submit(product);
  await until(() => find(product, '[data-image-id="2"]'), "upload completed");
  assert(JSON.stringify((await writes()).at(-1).body) ===
    '{"ImageFile":{"fileName":"qa.png","isPng":true},"colorId":"1","isPrimary":"true"}',
    "upload sends real multipart ImageFile, colorId and isPrimary");
  assert(text(product, '[data-image-id="2"]').includes("Ảnh đại diện") && !text(product, '[data-image-id="1"]').includes("Ảnh đại diện") &&
    !find(product, "[data-image-preview]"), "server primary transition is reflected and preview is cleared");
  click(product, '[data-image-id="2"] [data-delete-image]');
  click(product, save);
  await until(() => shown(product, "[data-assets-output]") && !find(product, '[data-image-id="2"]'), "image deleted");
  assert((await writes()).at(-1).path === "admin/products/1/images/2" && !text(product, '[data-image-id="1"]').includes("Ảnh đại diện"),
    "image deletion leaves other images and does not assign a primary");
  await control("stock-full");
  const full = await open(productPath);
  await until(() => shown(full, "[data-assets-output]"), "full gallery");
  assert(find(full, "[data-upload-image]").disabled, "10 images disables further uploads");
  full.remove();

  await control("stock-mode-forbidden");
  filters.requestSubmit();
  await until(() => text(inventory, "[data-state-title]").includes("Không có quyền"), "403");
  assert(!shown(inventory, "[data-inventory-output]"), "403 hides inaccessible stock data");
  await control("stock-mode-normal");
  const missing = await open(manage + "?productId=999");
  await until(() => text(missing, "[data-state-title]").includes("Không tìm thấy"), "404");
  assert(!shown(missing, "[data-assets-output]"), "404 does not show product data");
  missing.remove();
  const beforeRefresh = (await control("expire")).calls["auth/refresh"];
  filters.requestSubmit();
  await until(() => shown(inventory, "[data-inventory-output]"), "read retries after refresh");
  assert((await control()).calls["auth/refresh"] === beforeRefresh + 1, "401 renews the session through existing refresh flow");

  for (const width of [360, 390, 768, 1440]) {
    product.width = inventory.width = String(width);
    await new Promise(resolve => requestAnimationFrame(resolve));
    assert([product, inventory].every(frame => frame.contentDocument.documentElement.scrollWidth <= width + 1),
      "product and inventory fit " + width + " px");
  }
  const old = product.contentDocument;
  click(product, "[data-admin-logout]");
  await until(() => shown(product, "[data-account-view=login]") && shown(inventory, "[data-account-view=login]"), "logout both tabs");
  assert(!old.querySelector("[data-workspace]").children.length && !old.querySelector("[data-admin-email]").textContent,
    "logout clears sensitive view data in old document");
  assert(!Object.values(localStorage).some(value => /fake-(access|refresh)/.test(value)) && errors.length === 0,
    errors.join(", ") || "no token in storage and no unhandled JavaScript errors");
  await fetch("/__auth_qa", {method: "POST", body: `PASS ${checks.length}/${checks.length} Admin Stock Chromium checks`});
} catch (error) {
  await control("stock-release");
  await fetch("/__auth_qa", {method: "POST", body: `FAIL after ${checks.length} checks: ${error.message}`});
}
