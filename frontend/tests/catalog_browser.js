const checks = [];
const pageErrors = [];
const requests = async () => (await fetch("/__requests")).json();
const productCalls = async () => (await requests()).filter(call => call.route === "products");
const failNext = route => fetch("/__fail/" + route, { method: "POST" });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function assert(ok, name) {
  if (!ok) throw new Error(name);
  checks.push(name);
}

async function until(condition, name) {
  for (let i = 0; i < 135; i++) {
    if (await condition()) return;
    await sleep(30);
  }
  throw new Error("Timed out: " + name);
}

async function open(path, width = 1100) {
  const frame = document.createElement("iframe");
  frame.style.cssText = `display:block;border:0;width:${width}px;height:900px`;
  frame.src = path;
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  document.body.append(frame);
  await loaded;
  frame.contentWindow.addEventListener("error", event => { if (event.error) pageErrors.push(event.error.name); });
  frame.contentWindow.addEventListener("unhandledrejection", () => pageErrors.push("unhandled rejection"));
  return frame;
}

const find = (frame, selector) => frame.contentDocument.querySelector(selector);
const all = (frame, selector) => [...frame.contentDocument.querySelectorAll(selector)];
const ready = frame => find(frame, "[data-results-state]").hidden;
const formOf = frame => find(frame, "#catalog-filters");
const names = frame => all(frame, "[data-products] h3").map(item => item.textContent);
const stateOf = (frame, selector) => find(frame, selector).dataset.state;

try {
  const home = await open("/");
  await until(() => all(home, "[data-featured-products] .product-card").length === 4, "featured products");
  const featured = (await productCalls()).find(call => call.query.pageSize?.[0] === "4");
  assert(featured?.query.sort[0] === "newest" && featured.query.page[0] === "1" &&
    find(home, ".hero h1") && find(home, "[data-account-link]").textContent === "Đăng nhập", "home keeps hero and renders four newest products");
  const featuredPhoto = find(home, "[data-featured-products] .product-photo");
  featuredPhoto.scrollIntoView();
  await until(() => featuredPhoto.hasAttribute("data-loaded"), "backend image");
  assert(new URL(featuredPhoto.querySelector("img").src).origin !== home.contentWindow.location.origin,
    "product image loads from API origin");
  home.remove();

  const list = await open("/products/?sort=price");
  const form = formOf(list);
  await until(() => ready(list) && !find(list, "[data-lookups]").disabled, "catalog and lookups loaded");
  assert(names(list).length === 12 && names(list)[0] === "Giày chạy 01" &&
    find(list, "[data-page-label]").textContent === "Trang 1 / 2", "catalog honors server sorting and pagination");
  assert(all(list, '[name="categoryId"] option').some(option => option.value === "11" && option.textContent === "Giày / Chạy bộ") &&
    all(list, '[name="brandId"]').length === 2 && all(list, '[name="sizeId"]').length === 2 &&
    all(list, '[name="colorId"]').length === 2, "nested categories and all lookup DTOs");
  assert(names(list)[1] === "Giày <img data-injected src=x>" && !find(list, "[data-injected]"),
    "product names displayed as text");
  const brokenCard = all(list, ".product-card")[2];
  const rejectedCard = all(list, ".product-card")[3];
  brokenCard.scrollIntoView();
  await until(() => !brokenCard.querySelector("img"), "broken image replaced");
  assert(brokenCard.querySelector(".photo-placeholder").textContent === "Chưa có ảnh" &&
    brokenCard.querySelector(".stock-out").textContent === "Hết hàng" &&
    !rejectedCard.querySelector("img"), "broken and unsafe images fall back and card shows stock");

  const api = await list.contentWindow.eval('import("/js/api.js")');
  api.setAccessToken("catalog-test-token");
  find(list, "[data-next]").click();
  await until(() => names(list).length === 3 && find(list, "[data-page-label]").textContent === "Trang 2 / 2", "second page");
  let last = (await productCalls()).at(-1);
  assert(last.query.page[0] === "2" && last.query.sort[0] === "price" && !find(list, "[data-next]").hasAttribute("href"),
    "pagination preserves sorting");
  list.contentWindow.history.back();
  await until(() => ready(list) && names(list).length === 12, "history page one");
  assert(form.elements.sort.value === "price", "browser history restores form and results");

  form.elements.keyword.value = "Giày chạy";
  form.elements.categoryId.value = "10";
  for (const key of ["brandId", "sizeId", "colorId"]) {
    for (const input of all(list, `[name="${key}"]`)) input.checked = true;
  }
  form.elements.minPrice.value = "440000";
  form.elements.maxPrice.value = "500000";
  form.elements.inStock.checked = true;
  form.requestSubmit();
  await until(() => ready(list) && names(list).length === 1, "combined filters");
  last = (await productCalls()).at(-1);
  assert(last.query.keyword[0] === "Giày chạy" && last.query.categoryId[0] === "10" &&
    last.query.brandId.join() === "1,2" && last.query.sizeId.join() === "3,4" &&
    last.query.colorId.join() === "1,2" && last.query.minPrice[0] === "440000" &&
    last.query.maxPrice[0] === "500000" && last.query.inStock[0] === "true" &&
    last.query.page[0] === "1" && names(list)[0] === "Giày chạy 01", "filters send grouped repeated parameters and reset page");
  const resultPrice = find(list, ".product-price").textContent;
  assert(resultPrice.includes("420.000") && resultPrice.includes("480.000"), "card preserves Backend price range");
  const beforeInvalid = (await productCalls()).length;
  form.elements.maxPrice.value = "1";
  form.requestSubmit();
  assert(!find(list, "[data-filter-error]").hidden && (await productCalls()).length === beforeInvalid,
    "invalid price range does not send a request");

  const productLink = find(list, ".product-link");
  assert(new URL(productLink.href).searchParams.get("id") === "1", "product link uses API productId");
  const detail = await open(productLink.href);
  await until(() => !find(detail, "[data-detail]").hidden, "product detail");
  assert(find(detail, "[data-product-name]").textContent === "Giày chạy 01" &&
    find(detail, "[data-product-description]").textContent.includes("Thoáng khí") &&
    find(detail, "[data-variant-price]").textContent.includes("420.000") &&
    find(detail, "[data-variant-stock]").textContent.includes("7 sản phẩm"), "detail renders API description and selected price and stock");
  const soldOut = find(detail, '[name="product-size"][value="102"]');
  soldOut.focus();
  soldOut.click();
  assert(detail.contentDocument.activeElement === soldOut &&
    find(detail, "[data-variant-price]").textContent.includes("450.000") &&
    find(detail, "[data-variant-stock]").textContent === "Hết hàng", "out-of-stock variant remains focusable and uses own price");
  find(detail, '[name="product-color"][value="2"]').click();
  assert(all(detail, '[name="product-size"]').length === 1 &&
    find(detail, '[name="product-size"]:checked').value === "103" &&
    find(detail, "[data-main-photo] img").src.endsWith("white.jpg") &&
    find(detail, "[data-variant-price]").textContent.includes("480.000") &&
    find(detail, "[data-variant-stock]").textContent.includes("2 sản phẩm"), "color change selects existing size and related image");
  const secondary = all(detail, ".gallery-thumb")[1];
  secondary.click();
  assert(secondary.getAttribute("aria-pressed") === "true" &&
    find(detail, "[data-main-photo] img").src.endsWith("sample.jpg") &&
    find(detail, ".catalog-buy button").disabled, "gallery selection and disabled cart action");
  detail.remove();

  const missing = await open("/products/detail.html?id=999");
  await until(() => stateOf(missing, "[data-detail-state]") === "empty", "product 404");
  assert(find(missing, "[data-detail]").hidden && find(missing, "[data-detail-state] [data-retry]").hidden,
    "404 product shows unavailable state");
  missing.remove();
  const emptyStock = await open("/products/detail.html?id=3");
  await until(() => !find(emptyStock, "[data-detail]").hidden, "sold-out product");
  assert(find(emptyStock, "[data-variant-stock]").textContent === "Hết hàng" &&
    find(emptyStock, ".catalog-buy button").disabled, "all variants out of stock");
  emptyStock.remove();

  find(list, "[data-clear-filters]").click();
  await until(() => ready(list), "clear filters");
  assert(form.elements.keyword.value === "" && !form.elements.inStock.checked, "clear resets all filters");
  form.elements.keyword.value = "Chạy chậm";
  form.requestSubmit();
  await until(async () => (await productCalls()).at(-1).query.keyword?.[0] === "Chạy chậm", "old request began");
  form.elements.keyword.value = "Không có kết quả";
  form.requestSubmit();
  await until(() => stateOf(list, "[data-results-state]") === "empty", "search has no results");
  await sleep(450);
  assert(!names(list).length && find(list, "[data-result-count]").textContent === "0 sản phẩm" &&
    stateOf(list, "[data-results-state]") === "empty", "empty results stay visible when slow request finishes");
  list.remove();

  await failNext("products");
  const outage = await open("/products/");
  await until(() => stateOf(outage, "[data-results-state]") === "error", "list API error");
  assert(!find(outage, "[data-results-state] [data-retry]").hidden, "API error has retry");
  find(outage, "[data-results-state] [data-retry]").click();
  await until(() => ready(outage), "list retries");
  assert(names(outage).length === 12, "retry restores product list");
  outage.remove();

  await failNext("brands");
  const lookupError = await open("/products/?brandId=1");
  await until(() => ready(lookupError) && !find(lookupError, "[data-lookup-retry]").hidden, "lookup API error");
  formOf(lookupError).requestSubmit();
  await until(() => ready(lookupError), "list remains available during lookup error");
  assert(names(lookupError).length === 1 && (await productCalls()).at(-1).query.brandId[0] === "1", "lookup failure preserves active filters");
  find(lookupError, "[data-lookup-retry]").click();
  await until(() => !find(lookupError, "[data-lookups]").disabled, "lookups retry");
  assert(find(lookupError, '[name="brandId"][value="1"]').checked, "lookup retry restores checked controls");
  lookupError.remove();

  for (const width of [360, 390, 601, 768, 1440]) {
    const homePage = await open("/", width);
    const productList = await open("/products/", width);
    const productPage = await open("/products/detail.html?id=1", width);
    await until(() => ready(productList) && !find(productPage, "[data-detail]").hidden &&
      all(homePage, "[data-featured-products] .product-card").length === 4, "responsive content");
    for (const [name, frame] of [["home", homePage], ["list", productList], ["detail", productPage]]) {
      const doc = frame.contentDocument;
      assert(frame.contentWindow.innerWidth === width && doc.documentElement.scrollWidth <= width,
        `${name} fits ${width}px (viewport ${frame.contentWindow.innerWidth}, content ${doc.documentElement.scrollWidth})`);
    }
    assert(find(productList, "[data-filter-panel]").open === (width > 640), `responsive filter disclosure ${width}px`);
    for (const frame of [homePage, productList, productPage]) frame.remove();
  }

  assert(!(await requests()).some(call => call.authenticated), "public requests omit bearer and cookies");
  assert(pageErrors.length === 0, "ES modules run without JavaScript errors");
  await fetch("/__catalog_qa", { method: "POST", body: `PASS ${checks.length} Catalog browser checks` });
} catch (error) {
  await fetch("/__catalog_qa", { method: "POST", body: `FAIL ${error.message}\nLast passed: ${checks.at(-1) ?? "none"}` });
}
