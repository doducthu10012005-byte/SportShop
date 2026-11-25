import { ApiError, request } from "./api.js";
import { API_BASE_URL } from "./config.js";
import { renderState } from "./ui.js";

const currency = new Intl.NumberFormat("vi-VN", { style: "currency", currency: "VND", maximumFractionDigits: 2 });
const apiOrigin = new URL(API_BASE_URL, location.href).origin;
const productsPage = new URL("../products/", import.meta.url);

export async function getCatalog(path, { query, signal } = {}) {
  const timeout = AbortSignal.timeout(10000);
  const { data } = await request(path, { auth: false, query, signal: signal ? AbortSignal.any([signal, timeout]) : timeout });
  return data;
}

export function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

export function positiveInt(value) {
  return /^\d+$/.test(value ?? "") && Number(value) > 0 && Number(value) <= 2147483647 ? Number(value) : undefined;
}

export function money(value) {
  return Number.isFinite(value) ? currency.format(value) : "Chưa có giá";
}

export function productImage(value, alt = "", eager = false) {
  const picture = element("div", "product-photo");
  const fallback = element("span", "photo-placeholder", "Chưa có ảnh");
  picture.append(fallback);
  let url;
  if (typeof value === "string" && value.trim()) {
    try { url = new URL(value, apiOrigin); } catch { /* Show the placeholder. */ }
  }
  if (url?.origin !== apiOrigin || url?.protocol !== "https:" || !url.pathname.startsWith("/uploads/")) return picture;

  const image = element("img");
  image.alt = alt;
  image.loading = eager ? "eager" : "lazy";
  image.decoding = "async";
  image.addEventListener("load", () => { fallback.hidden = true; picture.dataset.loaded = ""; }, { once: true });
  image.addEventListener("error", () => image.remove(), { once: true });
  image.src = url.href;
  picture.append(image);
  return picture;
}

export function renderProducts(grid, items) {
  grid.replaceChildren(...items.map(product => {
    const card = element("li", "product-card");
    const link = element("a", "product-link");
    const detail = new URL("detail.html", productsPage);
    detail.searchParams.set("id", product.productId);
    link.href = detail.href;
    const info = element("div", "product-card-info");
    const label = [product.brandName, product.categoryName].filter(Boolean).join(" · ");
    const price = Number.isFinite(product.minPrice) && Number.isFinite(product.maxPrice) && product.minPrice !== product.maxPrice
      ? money(product.minPrice) + " – " + money(product.maxPrice) : money(product.minPrice);
    const availability = product.inStock === true ? "Còn hàng" : product.inStock === false ? "Hết hàng" : "Chưa có thông tin tồn kho";
    info.append(element("p", "catalog-meta", label), element("h3", "", product.productName),
      element("p", "product-price", price),
      element("span", product.inStock === true ? "stock-label" : "stock-label stock-out", availability));
    if (product.colors?.length) {
      const colors = element("ul", "product-colors");
      colors.setAttribute("aria-label", "Màu sắc");
      for (const color of product.colors) {
        const item = element("li");
        if (/^#[0-9a-f]{6}$/i.test(color.hexCode)) item.style.setProperty("--swatch", color.hexCode);
        item.title = color.colorName;
        item.append(element("span", "visually-hidden", color.colorName));
        colors.append(item);
      }
      info.append(colors);
    }
    link.append(productImage(product.primaryImageUrl), info);
    card.append(link);
    return card;
  }));
}

export function setCatalogState(panel, type, title = "", message = "", allowRetry = false) {
  panel.hidden = !type;
  if (!type) return;
  renderState(panel, type, title, message);
  const retry = panel.querySelector("[data-retry]");
  if (retry) retry.hidden = !allowRetry;
}

export function catalogError(error) {
  if (error.name === "TimeoutError") return "Máy chủ phản hồi chậm. Bạn có thể thử lại.";
  return error instanceof ApiError ? error.message : "Không thể đọc dữ liệu sản phẩm. Bạn có thể thử lại.";
}
