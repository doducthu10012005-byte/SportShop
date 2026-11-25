import "../store-session.js";
import { catalogError, element, getCatalog, money, positiveInt, productImage, setCatalogState } from "../catalog.js";

const state = document.querySelector("[data-detail-state]");
const detail = document.querySelector("[data-detail]");
const mainPhoto = detail.querySelector("[data-main-photo]");
const thumbs = detail.querySelector("[data-gallery-thumbs]");
const colors = detail.querySelector("[data-variant-colors]");
const sizes = detail.querySelector("[data-variant-sizes]");
const productId = positiveInt(new URLSearchParams(location.search).get("id"));

function option(name, value, label, chosen, outOfStock) {
  const wrapper = element("label", outOfStock ? "variant-choice variant-out" : "variant-choice");
  const input = element("input");
  Object.assign(input, { type: "radio", name, value, checked: chosen });
  wrapper.append(input, element("span", "", label));
  return wrapper;
}

function updateVariant(variant) {
  const price = detail.querySelector("[data-variant-price]");
  const stock = detail.querySelector("[data-variant-stock]");
  const quantity = variant.quantityAvailable;
  price.textContent = money(variant.price);
  stock.classList.toggle("stock-out", quantity === 0);
  stock.textContent = quantity === 0 ? "Hết hàng"
    : Number.isInteger(quantity) && quantity > 0 ? `Còn hàng · ${quantity} sản phẩm` : "Chưa có thông tin tồn kho";
}

function showGallery(images, name, relatedColor) {
  const related = images.filter(image => image.colorId === relatedColor);
  const shared = images.filter(image => image.colorId === null);
  const order = [...related, ...shared];
  const initial = related.find(image => image.isPrimary) ?? related[0] ?? shared.find(image => image.isPrimary) ?? shared[0];
  const buttons = order.map((image, index) => {
    const button = element("button", "gallery-thumb");
    button.type = "button";
    button.setAttribute("aria-label", `Xem ảnh ${index + 1}`);
    button.append(productImage(image.imageUrl));
    button.addEventListener("click", () => choose(image));
    return button;
  });
  function choose(image) {
    mainPhoto.replaceChildren(productImage(image?.imageUrl, image ? `${name} · Ảnh ${order.indexOf(image) + 1}` : "", true));
    buttons.forEach((button, index) => button.setAttribute("aria-pressed", String(image === order[index])));
  }
  thumbs.replaceChildren(...buttons);
  thumbs.hidden = buttons.length < 2;
  choose(initial);
}

function renderDetail(product) {
  const { productName, description, brand, category, variants, images } = product;
  if (!Array.isArray(variants) || !variants.length || !Array.isArray(images)) throw new Error("Invalid product");
  document.title = productName + " — SportShop";
  detail.querySelector("[data-product-name]").textContent = productName;
  detail.querySelector("[data-product-meta]").textContent = [brand?.brandName, category?.categoryName].filter(Boolean).join(" · ");
  const descriptionNode = detail.querySelector("[data-product-description]");
  descriptionNode.textContent = description ?? "";
  descriptionNode.hidden = !descriptionNode.textContent;

  const byColor = new Map();
  for (const variant of variants) {
    if (!byColor.has(variant.colorId)) byColor.set(variant.colorId, []);
    byColor.get(variant.colorId).push(variant);
  }
  let current = variants.find(variant => variant.quantityAvailable > 0) ?? variants[0];
  function selectColor(colorId) {
    const choices = byColor.get(colorId);
    const selected = choices.find(variant => variant.sizeId === current.sizeId) ??
      choices.find(variant => variant.quantityAvailable > 0) ?? choices[0];
    sizes.replaceChildren(...choices.map(variant => {
      const out = variant.quantityAvailable === 0;
      const label = out ? `${variant.sizeName} · Hết hàng` : variant.sizeName;
      return option("product-size", variant.variantId, label, selected === variant, out);
    }));
    current = selected;
    updateVariant(current);
    showGallery(images, productName, colorId);
  }
  colors.replaceChildren(...Array.from(byColor, ([id, choices]) => {
    const out = choices.every(variant => variant.quantityAvailable === 0);
    const name = choices[0].colorName;
    return option("product-color", id, out ? `${name} · Hết hàng` : name, id === current.colorId, out);
  }));
  colors.onchange = event => selectColor(Number(event.target.value));
  sizes.onchange = event => {
    current = byColor.get(current.colorId).find(variant => variant.variantId === Number(event.target.value));
    updateVariant(current);
  };
  selectColor(current.colorId);
}

async function loadDetail() {
  if (!productId) {
    setCatalogState(state, "empty", "Không tìm thấy sản phẩm", "Chọn một sản phẩm từ danh sách để xem chi tiết.");
    return;
  }
  setCatalogState(state, "loading", "Đang tải sản phẩm");
  try {
    const product = await getCatalog(`/products/${productId}`);
    renderDetail(product);
    detail.hidden = false;
    setCatalogState(state);
  } catch (error) {
    if (error.status === 404) setCatalogState(state, "empty", "Không tìm thấy sản phẩm", "Sản phẩm có thể đã ngừng hiển thị.");
    else setCatalogState(state, "error", "Chưa tải được chi tiết sản phẩm", catalogError(error), true);
  }
}

state.querySelector("[data-retry]").addEventListener("click", loadDetail);
void loadDetail();
