const checks = [];
const failures = [];
const page = "/admin/catalog/";
const control = async (action = "") => (await fetch("/__fixture?action=" + action)).json();
const requests = async () => (await control()).catalogRequests;
const writes = async () => (await requests()).filter(item => item.method !== "GET");
const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const visible = (frame, selector) => find(frame, selector)?.hidden === false;
const all = (frame, selector) => Array.from(frame.contentDocument.querySelectorAll(selector));
const click = (frame, selector) => find(frame, selector).click();
const row = (frame, id) => find(frame, `[data-id="${id}"]`);
const submit = (frame, selector) => find(frame, selector).requestSubmit();
const text = (frame, selector) => find(frame, selector)?.textContent.trim() ?? "";

function assert(ok, label) {
  if (!ok) throw new Error(label);
  checks.push(label);
}

async function until(condition, label) {
  for (let i = 0; i < 170; i++) {
    if (await condition()) return;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error("Timeout " + label);
}

async function open(path, width = 1440) {
  const frame = document.createElement("iframe");
  frame.width = width;
  frame.height = 850;
  frame.addEventListener("load", () => {
    frame.contentWindow.addEventListener("error", event => { if (event.error) failures.push(event.error.name); });
    frame.contentWindow.addEventListener("unhandledrejection", () => failures.push("unhandled rejection"));
  });
  frame.src = path;
  document.body.append(frame);
  await until(() => frame.contentDocument?.readyState === "complete" && frame.contentWindow.location.pathname !== "blank", path);
  return frame;
}

function enter(frame, name) {
  const form = find(frame, "[data-account-form=login]");
  form.elements.email.value = name + "@example.test";
  form.elements.password.value = "Test-Password123";
  form.requestSubmit();
}

async function editor(frame, selector) {
  click(frame, selector);
  await until(() => visible(frame, "[data-editor-form]"), "form ready");
  return find(frame, "[data-editor-form]").elements;
}

function product(fields, name) {
  fields.productName.value = name;
  fields.categoryId.value = "2";
  fields.brandId.value = "1";
  fields.sizeId.value = "1";
  fields.colorId.value = "1";
  fields.sku.value = "QA-SPORT";
  fields.price.value = "125000.75";
  fields.initialStock.value = "4";
}

async function saved(frame, id) {
  await until(() => visible(frame, "[data-notice]") && row(frame, id), "synchronization after saving");
}

try {
  const products = await open(page);
  await until(() => visible(products, "[data-account-view=login]"), "guest redirect");
  assert((await requests()).length === 0 && products.contentWindow.location.search.includes("view=products"), "guest is redirected before catalog reads");
  enter(products, "customer");
  await until(() => visible(products, "[data-account-view=profile]"), "Customer profile");
  const denied = await open(page);
  await until(() => text(denied, "[data-guard-message]").includes("không có quyền"), "Customer denied");
  assert((await requests()).length === 0 && !visible(denied, "[data-list-output]"), "Customer cannot read or operate on Admin catalog");
  denied.remove();
  click(products, "[data-account-view=profile] [data-logout]");
  await until(() => visible(products, "[data-account-view=login]"), "logout Customer");
  enter(products, "forced");
  await until(() => visible(products, "[data-account-view=password]"), "mandatory password");
  assert((await requests()).length === 0, "MustChangePassword blocks catalog requests");
  click(products, "[data-account-view=password] [data-logout]");
  await until(() => visible(products, "[data-account-view=login]"), "logout forced");
  enter(products, "admin");
  await until(() => row(products, 22), "Admin catalog products");
  assert(text(products, "[data-admin-email]") === "admin@example.test" && all(products, "[data-rows] tr").length === 20,
    "Admin sees catalog and default page size");
  assert(row(products, 22).textContent.includes("<img") && !row(products, 22).querySelector("img") &&
    !row(products, 21).textContent.includes("SP-21"), "API text is escaped, and list does not infer product SKU");
  click(products, "[data-next]");
  await until(() => row(products, 1), "second page");
  assert(text(products, "[data-page-info]").includes("2/2") && find(products, "[data-next]").disabled, "pagination uses the API metadata");
  await until(() => !find(products, "[data-filters]").elements.categoryId.disabled, "category and brand options");
  const filters = find(products, "[data-filters]").elements;
  Object.assign(filters.keyword, { value: "  SP-1 " });
  filters.isActive.value = "true";
  filters.categoryId.value = "2";
  filters.brandId.value = "1";
  filters.sort.value = "productName";
  filters.pageSize.value = "10";
  submit(products, "[data-filters]");
  await until(() => row(products, 10), "filtered list");
  const query = (await requests()).filter(item => item.path === "admin/products").at(-1).query;
  assert(["SP-1", "true", "2", "1", "productName", "1", "10"].every((value, index) =>
    query[["keyword", "isActive", "categoryId", "brandId", "sort", "page", "pageSize"][index]][0] === value) &&
    !row(products, 11), "filters, trimming, sort and reset to page 1");
  filters.keyword.value = "absent";
  submit(products, "[data-filters]");
  await until(() => find(products, "[data-list-state]").dataset.state === "empty", "empty catalog");
  assert(!visible(products, "[data-list-output]"), "search empty state clears old results");
  find(products, "[data-filters]").reset();
  filters.sort.value = "-createdAt";
  submit(products, "[data-filters]");
  await until(() => row(products, 22), "reset filters");

  let fields = await editor(products, "[data-create]");
  product(fields, "  ");
  submit(products, "[data-editor-form]");
  assert(text(products, "[data-editor-error]").includes("nhập tên") && (await writes()).length === 0, "client rejects blank product name");
  fields.productName.value = "Giày QA mới";
  fields.sku.value = "sku ?";
  assert(!fields.sku.checkValidity(), "SKU validation follows backend character set");
  fields.sku.value = "QA-SPORT";
  click(products, "[data-add-variant]");
  const variant2 = all(products, "[data-variant]")[1];
  for (const [key, value] of Object.entries({ sizeId: "1", colorId: "1", sku: "QA-SPORT-2", price: "0", initialStock: "0" })) {
    variant2.querySelector(`[name=${key}]`).value = value;
  }
  submit(products, "[data-editor-form]");
  assert(text(products, "[data-editor-error]").includes("Tổ hợp kích thước") && (await writes()).length === 0,
    "client blocks duplicate size and color within one product");
  variant2.querySelector("[name=sizeId]").value = "2";
  await control("catalog-mode-validation");
  submit(products, "[data-editor-form]");
  await until(() => text(products, "[data-editor-error]").includes("Giá không hợp lệ"), "server validation");
  assert(find(products, "[data-variant] [name=price]").getAttribute("aria-invalid") === "true",
    "400 field validation displays and marks the variant field");
  await control("catalog-mode-normal");
  await control("catalog-hold");
  click(products, "[data-save]");
  click(products, "[data-save]");
  await until(async () => (await writes()).length === 2, "double click sends one pending create");
  assert(find(products, "[data-save]").disabled && (await writes()).length === 2, "double submit is blocked in flight");
  await control("catalog-release");
  await saved(products, 23);
  const createBody = (await writes()).at(-1).body;
  assert(Object.keys(createBody).sort().join() === "brandId,categoryId,description,isActive,productName,variants" &&
    JSON.stringify(createBody.variants) === '[{"sizeId":1,"colorId":1,"sku":"QA-SPORT","price":125000.75,"initialStock":4},{"sizeId":2,"colorId":1,"sku":"QA-SPORT-2","price":0,"initialStock":0}]',
    "POST product uses exact creation and initial variant DTO");

  fields = await editor(products, '[data-id="23"] [data-edit]');
  assert((await requests()).at(-1).path === "admin/products/23" && text(products, "[data-existing-variants]").includes("QA-SPORT") &&
    !visible(products, "[data-variants]"), "edit loads product detail, showing existing SKU read-only");
  fields.description.value = "Mô tả cập nhật";
  fields.productName.value = "Giày QA đã sửa";
  await control("catalog-mode-conflict");
  submit(products, "[data-editor-form]");
  await until(() => text(products, "[data-editor-error]").includes("Dữ liệu vừa thay đổi"), "concurrency conflict");
  assert(!row(products, 23).textContent.includes("đã sửa"), "409 concurrency conflict does not show an optimistic update");
  await control("catalog-mode-normal");
  submit(products, "[data-editor-form]");
  await until(() => row(products, 23)?.textContent.includes("đã sửa"), "updated product");
  const update = (await writes()).at(-1);
  assert(update.method === "PUT" && update.body.description === "Mô tả cập nhật" &&
    Object.keys(update.body).sort().join() === "brandId,categoryId,description,isActive,productName", "PUT only sends allowed common fields");
  click(products, '[data-id="23"] [data-disable]');
  assert(find(products, "[data-confirm]").open && text(products, "[data-confirm-text]").includes("Đơn cũ"), "soft hide requires explicit confirmation");
  click(products, "[data-hide]");
  await until(() => row(products, 23)?.textContent.includes("Đã ẩn"), "soft deleted");
  assert((await writes()).at(-1).method === "DELETE" && !row(products, 23).querySelector("[data-disable]"), "hidden product remains in the list");
  fields = await editor(products, '[data-id="23"] [data-edit]');
  fields.isActive.checked = true;
  submit(products, "[data-editor-form]");
  await until(() => row(products, 23)?.querySelector("[data-disable]"), "reactivated");
  assert((await writes()).at(-1).body.isActive === true, "reactivation uses update API");

  await control("catalog-mode-missing");
  click(products, '[data-id="23"] [data-edit]');
  await until(() => text(products, "[data-editor-state]").includes("Không tìm thấy"), "missing detail");
  assert(!visible(products, "[data-editor-form]"), "404 detail blocks edit");
  click(products, "[data-editor-dismiss]");
  await control("catalog-mode-forbidden");
  submit(products, "[data-filters]");
  await until(() => find(products, "[data-list-state]").dataset.state === "error", "403 list");
  assert(!visible(products, "[data-list-output]") && text(products, "[data-state-title]").includes("Không có quyền"), "403 does not show stale products");
  await control("catalog-mode-normal");
  const refreshBefore = (await control()).calls["auth/refresh"];
  await control("expire");
  click(products, "[data-reload]");
  await until(() => row(products, 23), "refresh and retry GET");
  assert((await control()).calls["auth/refresh"] === refreshBefore + 1, "401 refreshes session and reloads using the new token");

  fields = await editor(products, "[data-create]");
  product(fields, "Giày QA lỗi máy chủ");
  await control("catalog-mode-duplicate-sku");
  submit(products, "[data-editor-form]");
  await until(() => text(products, "[data-editor-error]").includes("SKU đã được sử dụng"), "duplicate SKU");
  assert(!find(products, "[data-save]").disabled && find(products, "[data-editor]").open, "SKU conflict is correctable in the same form");
  await control("catalog-mode-failure");
  const before500 = (await writes()).length;
  submit(products, "[data-editor-form]");
  await until(() => text(products, "[data-editor-error]").includes("Chưa thể xác nhận"), "500 outcome");
  assert(find(products, "[data-save]").disabled && !products.contentDocument.body.textContent.includes("Internal details") &&
    (await writes()).length === before500 + 1, "500 hides internals and does not replay write");
  click(products, "[data-editor-close]");
  fields = await editor(products, "[data-create]");
  product(fields, "Giày QA mất phản hồi");
  await control("catalog-mode-lost");
  const beforeLost = (await writes()).length;
  submit(products, "[data-editor-form]");
  await until(() => text(products, "[data-editor-error]").includes("Chưa thể xác nhận") && row(products, 24), "lost response reconciles by GET");
  assert((await writes()).length === beforeLost + 1 && find(products, "[data-save]").disabled && !visible(products, "[data-notice]"),
    "lost response is reconciled without repeat or success claim");
  click(products, "[data-editor-close]");
  await control("catalog-mode-normal");

  const categories = await open(page + "?view=categories");
  await until(() => row(categories, 2), "category list");
  assert(row(categories, 2).textContent.includes("Giày") && !visible(categories, "[data-pagination]"), "flat categories resolve parent names without pagination");
  fields = await editor(categories, '[data-id="1"] [data-edit]');
  assert(fields.parentCategoryId.options.length === 1, "a category cannot select itself or its child as parent");
  click(categories, "[data-editor-close]");
  click(categories, '[data-id="1"] [data-disable]');
  click(categories, "[data-hide]");
  await until(() => text(categories, "[data-confirm-error]").includes("đang được sử dụng"), "category in use");
  assert(row(categories, 1).querySelector("[data-disable]"), "IN_USE preserves referenced category");
  click(categories, "[data-confirm-close]");
  fields = await editor(categories, "[data-create]");
  fields.categoryName.value = "Phụ kiện";
  fields.parentCategoryId.value = "1";
  submit(categories, "[data-editor-form]");
  await saved(categories, 3);
  assert((await writes()).at(-1).body.parentCategoryId === 1, "create category with documented parent field");
  fields = await editor(categories, '[data-id="3"] [data-edit]');
  fields.categoryName.value = "Phụ kiện chạy bộ";
  submit(categories, "[data-editor-form]");
  await until(() => row(categories, 3)?.textContent.includes("Phụ kiện chạy bộ"), "category updated");
  click(categories, '[data-id="3"] [data-disable]');
  click(categories, "[data-hide]");
  await until(() => row(categories, 3)?.textContent.includes("Đã ẩn"), "category hidden");
  assert((await writes()).at(-1).path === "admin/categories/3", "category edit and hide use exact item routes");

  const brands = await open(page + "?view=brands");
  await until(() => row(brands, 1), "brands list");
  assert(visible(brands, "[data-pagination]") && row(brands, 2).textContent.includes("Đã ẩn"), "brands return paginated data including disabled entries");
  click(brands, '[data-id="1"] [data-disable]');
  click(brands, "[data-hide]");
  await until(() => text(brands, "[data-confirm-error]").includes("đang được sử dụng"), "brand referenced");
  assert(row(brands, 1).querySelector("[data-disable]"), "brand in use stays active");
  click(brands, "[data-confirm-close]");
  fields = await editor(brands, "[data-create]");
  fields.brandName.value = "Nhãn cũ";
  submit(brands, "[data-editor-form]");
  await until(() => text(brands, "[data-editor-error]").includes("đã được sử dụng"), "duplicate even when hidden");
  assert(find(brands, "[data-editor]").open, "409 duplicate name keeps the form open");
  fields.brandName.value = "Endurance QA";
  fields.logoUrl.value = "https://example.test/qa.svg";
  submit(brands, "[data-editor-form]");
  await saved(brands, 3);
  assert((await writes()).at(-1).body.logoUrl === "https://example.test/qa.svg", "brand create sends documented logoUrl");
  fields = await editor(brands, '[data-id="3"] [data-edit]');
  fields.description.value = "Nhãn hiệu thử";
  submit(brands, "[data-editor-form]");
  await until(() => row(brands, 3)?.textContent.includes("Nhãn hiệu thử"), "brand updated");
  click(brands, '[data-id="3"] [data-disable]');
  click(brands, "[data-hide]");
  await until(() => row(brands, 3)?.textContent.includes("Đã ẩn"), "brand hidden");
  assert((await writes()).at(-1).path === "admin/brands/3", "brand edit and hide use exact item routes");

  for (const width of [360, 390, 768, 1440]) {
    products.width = String(width);
    await new Promise(resolve => requestAnimationFrame(resolve));
    const viewport = products.contentDocument.documentElement;
    const table = find(products, ".admin-table-scroll");
    assert(viewport.scrollWidth <= width + 1 && table.clientWidth <= width && table.tabIndex === 0,
      `table scroll stays inside viewport at ${width} (document ${viewport.scrollWidth}, table ${table.clientWidth})`);
  }
  products.width = "360";
  click(products, "[data-admin-menu]");
  assert(find(products, "[data-admin-menu]").getAttribute("aria-expanded") === "true", "mobile navigation expands");
  find(products, "#admin-nav").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
  assert(find(products, "[data-admin-menu]").getAttribute("aria-expanded") === "false", "Escape closes mobile menu");
  fields = await editor(products, "[data-create]");
  assert(find(products, "[data-editor]").getBoundingClientRect().width <= 360, "product form fits mobile viewport");
  click(products, "[data-editor-close]");

  const oldPage = products.contentDocument;
  await control("catalog-hold");
  submit(products, "[data-filters]");
  click(products, "[data-admin-logout]");
  assert(!visible(products, "[data-list-output]") && text(products, "[data-admin-email]") === "" &&
    all(products, "[data-rows] tr").length === 0, "logout clears catalog data before pending read completes");
  await control("catalog-release");
  await until(() => visible(products, "[data-account-view=login]") && visible(brands, "[data-account-view=login]"),
    "logout synchronizes across tabs");
  enter(products, "admin-other");
  await until(() => text(products, "[data-admin-email]") === "admin-other@example.test" && row(products, 24), "second Admin");
  assert(oldPage.querySelector("[data-rows]").children.length === 0 &&
    !oldPage.querySelector("[data-admin-email]").textContent, "a late response cannot restore data from the previous account");
  // Check the expiry notice in the tab that receives the 401, with no other tabs restoring the cookie.
  categories.remove();
  brands.remove();
  await control("revoke-session");
  submit(products, "[data-filters]");
  await until(() => visible(products, "[data-account-view=login]"), "expired redirect");
  assert(text(products, "[data-session-notice]").includes("Phiên quản trị đã kết thúc"), "unrecoverable 401 redirects to login");
  assert(Object.entries(localStorage).every(([key, value]) => !/fake-(access|refresh)|QA-SPORT/i.test(key + value)),
    "tokens and Admin catalog values are absent from localStorage");
  assert(failures.length === 0, failures.join(", ") || "no uncaught errors or promise rejections");
  await fetch("/__auth_qa", { method: "POST", body: `PASS ${checks.length}/${checks.length} Admin Catalog Chromium checks` });
} catch (error) {
  await control("catalog-release");
  await fetch("/__auth_qa", { method: "POST", body: `FAIL after ${checks.length} checks: ${error.message}` });
}
