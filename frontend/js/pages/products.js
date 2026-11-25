import "../store-session.js";
import { catalogError, element, getCatalog, positiveInt, renderProducts, setCatalogState } from "../catalog.js";

const form = document.querySelector("#catalog-filters");
const filtersPanel = document.querySelector("[data-filter-panel]");
const lookupFields = form.querySelector("[data-lookups]");
const filterError = form.querySelector("[data-filter-error]");
const state = document.querySelector("[data-results-state]");
const grid = document.querySelector("[data-products]");
const count = document.querySelector("[data-result-count]");
const pager = document.querySelector(".catalog-pagination");
const previous = pager.querySelector("[data-prev]");
const next = pager.querySelector("[data-next]");
const multiFilters = [
  { key: "brandId", name: "brandName", path: "/brands" },
  { key: "sizeId", name: "sizeName", path: "/sizes" },
  { key: "colorId", name: "colorName", path: "/colors" }
];
let query = readQuery(new URLSearchParams(location.search));
let active;
filtersPanel.open = !matchMedia("(max-width: 640px)").matches;

function readQuery(params) {
  const sort = params.get("sort");
  const values = {
    keyword: params.get("keyword")?.trim() || undefined,
    categoryId: positiveInt(params.get("categoryId")),
    sort: ["newest", "price", "-price", "name"].includes(sort) ? sort : "newest",
    page: positiveInt(params.get("page")) ?? 1,
    pageSize: Math.min(positiveInt(params.get("pageSize")) ?? 12, 50)
  };
  for (const { key } of multiFilters) values[key] = [...new Set(params.getAll(key).map(positiveInt).filter(Boolean))];
  for (const name of ["minPrice", "maxPrice"]) {
    const price = params.get(name);
    if (price?.trim() && Number.isFinite(Number(price)) && Number(price) >= 0) values[name] = price;
  }
  if (params.get("inStock") === "true") values.inStock = true;
  return values;
}

function listUrl(values) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    for (const entry of Array.isArray(value) ? value : [value]) {
      if (entry != null) params.append(key, entry);
    }
  }
  return location.pathname + (params.size ? "?" + params : "");
}

function syncLookupFields() {
  form.elements.categoryId.value = query.categoryId ?? "";
  for (const { key } of multiFilters) {
    for (const input of lookupFields.querySelectorAll(`[name="${key}"]`)) input.checked = query[key].includes(Number(input.value));
  }
}

function syncForm() {
  for (const key of ["keyword", "sort", "minPrice", "maxPrice"]) form.elements[key].value = query[key] ?? "";
  form.elements.inStock.checked = query.inStock === true;
  filterError.hidden = true;
  syncLookupFields();
}

function addCategories(select, items, parent = "") {
  for (const category of items) {
    const label = parent + category.categoryName;
    select.append(new Option(label, category.categoryId));
    addCategories(select, category.children, label + " / ");
  }
}

async function loadLookups() {
  const message = form.querySelector("[data-lookup-message]");
  const retry = form.querySelector("[data-lookup-retry]");
  message.textContent = "Đang tải bộ lọc…";
  retry.hidden = true;
  try {
    const [categories, ...options] = await Promise.all([
      getCatalog("/categories"), ...multiFilters.map(({ path }) => getCatalog(path))
    ]);
    const select = form.elements.categoryId;
    select.replaceChildren(new Option("Tất cả danh mục", ""));
    addCategories(select, categories);
    multiFilters.forEach(({ key, name }, index) => {
      const group = lookupFields.querySelector(`[data-options="${key}"]`);
      group.replaceChildren(...options[index].map(item => {
        const label = element("label", "filter-choice");
        const input = element("input");
        Object.assign(input, { type: "checkbox", name: key, value: item[key] });
        label.append(input, element("span", "", item[name]));
        return label;
      }));
      if (!options[index].length) group.append(element("p", "catalog-meta", "Chưa có lựa chọn"));
    });
    syncLookupFields();
    lookupFields.disabled = false;
    message.textContent = "";
  } catch {
    message.textContent = "Chưa tải được danh mục và thuộc tính sản phẩm.";
    retry.hidden = false;
  }
}

function renderPager({ page, totalPages }) {
  pager.hidden = totalPages === 0 || (totalPages === 1 && page === 1);
  pager.querySelector("[data-page-label]").textContent = page > totalPages ? `Trang ${page}` : `Trang ${page} / ${totalPages}`;
  setPageLink(previous, page > 1 && totalPages > 0 ? Math.min(page - 1, totalPages) : null);
  setPageLink(next, page < totalPages ? page + 1 : null);
}

function setPageLink(link, page) {
  if (page == null) {
    link.removeAttribute("href");
    link.setAttribute("aria-disabled", "true");
  } else {
    link.href = listUrl({ ...query, page });
    link.removeAttribute("aria-disabled");
  }
}

async function loadProducts() {
  active?.abort();
  const { signal } = (active = new AbortController());
  grid.replaceChildren();
  count.textContent = "";
  pager.hidden = true;
  setCatalogState(state, "loading", "Đang tải sản phẩm");
  try {
    const { items, pagination } = await getCatalog("/products", { query, signal });
    if (signal.aborted) return;
    renderProducts(grid, items);
    count.textContent = `${pagination.totalItems} sản phẩm`;
    renderPager(pagination);
    if (items.length) setCatalogState(state);
    else if (pagination.totalItems > 0)
      setCatalogState(state, "empty", "Trang này không có sản phẩm", "Dùng phân trang để quay lại kết quả.");
    else setCatalogState(state, "empty", "Không tìm thấy sản phẩm", "Thử từ khóa khác hoặc bỏ bớt bộ lọc.");
  } catch (error) {
    if (!signal.aborted) setCatalogState(state, "error", "Chưa tải được sản phẩm", catalogError(error), true);
  }
}

function navigate(values, focusResults = false) {
  const url = listUrl(values);
  if (url !== location.pathname + location.search) history.pushState(null, "", url);
  query = values;
  syncForm();
  if (focusResults) document.querySelector("#results-title").focus();
  void loadProducts();
}

form.addEventListener("submit", event => {
  event.preventDefault();
  const values = readQuery(new URLSearchParams(new FormData(form)));
  if (values.minPrice != null && values.maxPrice != null && Number(values.minPrice) > Number(values.maxPrice)) {
    filtersPanel.open = true;
    filterError.textContent = "Giá tối đa phải lớn hơn hoặc bằng giá tối thiểu.";
    filterError.hidden = false;
    form.elements.maxPrice.focus();
    return;
  }
  if (lookupFields.disabled) {
    values.categoryId = query.categoryId;
    for (const { key } of multiFilters) values[key] = query[key];
  }
  navigate({ ...values, pageSize: query.pageSize }, true);
});

function followPage(event) {
  if (event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
  event.preventDefault();
  const href = event.currentTarget.getAttribute("href");
  if (href) navigate(readQuery(new URL(href, location.href).searchParams), true);
}

for (const link of [previous, next, form.querySelector("[data-clear-filters]")]) link.addEventListener("click", followPage);
window.addEventListener("popstate", () => {
  query = readQuery(new URLSearchParams(location.search));
  syncForm();
  void loadProducts();
});
state.querySelector("[data-retry]").addEventListener("click", loadProducts);
form.querySelector("[data-lookup-retry]").addEventListener("click", loadLookups);
syncForm();
void loadLookups();
void loadProducts();
