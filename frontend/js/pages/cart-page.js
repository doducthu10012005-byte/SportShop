import "../store-session.js";
import "../cart-badge.js";
import { element, money, positiveInt, productImage, setCatalogState } from "../catalog.js";
import { cartMessage, clearCart, loadCart, removeFromCart, setCartQuantity, watchCart } from "../cart.js";

const statePanel = document.querySelector("[data-cart-state]");
const login = statePanel.querySelector("[data-cart-login]");
const retry = statePanel.querySelector("[data-retry]");
const layout = document.querySelector("[data-cart-content]");
const list = layout.querySelector("[data-cart-items]");
const clearButton = layout.querySelector("[data-clear-cart]");
const dialog = document.querySelector("#clear-cart");
const notice = document.querySelector("[data-cart-notice]");
const heading = document.querySelector("h1");
let current;
let shown;

function button(label, type = "button") {
  const node = element("button", "button button-outline", label);
  node.type = type;
  return node;
}

function createLine(item) {
  const row = element("li", "cart-line");
  row.dataset.itemId = item.cartItemId;
  row.dataset.quantity = item.quantity;
  const info = element("div");
  const title = element("h3");
  const product = element("a", "", item.productName);
  if (positiveInt(item.productId)) product.href = "../products/detail.html?id=" + item.productId;
  title.append(product);
  const variant = element("p", "catalog-meta", [item.colorName, item.sizeName, item.sku].filter(Boolean).join(" · "));
  const unit = element("p", "catalog-meta", "Đơn giá " + money(item.unitPrice));
  const lowStock = Number.isInteger(item.availableStock) && item.availableStock < item.quantity;
  const stock = element("p", item.isAvailable ? "stock-label" : "stock-label stock-out", item.isAvailable
    ? `Còn ${item.availableStock} sản phẩm`
    : lowStock ? (item.availableStock === 0 ? "Hết hàng" : `Chỉ còn ${item.availableStock} sản phẩm`) : "Dòng hàng không còn khả dụng");
  stock.dataset.lineStock = "";
  const total = element("p", "product-price", item.isAvailable ? money(item.lineTotal) : "Không tính vào tạm tính");
  total.dataset.lineTotal = "";
  info.append(title, variant, unit, stock, total);

  const controls = element("div", "cart-adjustments");
  const remove = button("Xóa");
  remove.dataset.removeItem = "";
  remove.setAttribute("aria-label", "Xóa " + item.productName + " " + variant.textContent);
  remove.onclick = () => runWrite(() => removeFromCart(item.cartItemId), "Đã xóa sản phẩm.", item.cartItemId, "[data-remove-item]");
  if (Number.isInteger(item.availableStock) && item.availableStock > 0 && (item.isAvailable || lowStock)) {
    controls.append(quantityForm(item, variant.textContent));
  } else controls.append(element("p", "catalog-meta", `Số lượng ${item.quantity}`));
  controls.append(remove);
  info.append(controls);
  row.append(productImage(item.imageUrl), info);
  return row;
}

function quantityForm(item, variant) {
  const form = element("form", "cart-quantity");
  const field = element("label", "", "Số lượng");
  const input = element("input");
  const max = Math.min(99, item.availableStock);
  Object.assign(input, { name: "quantity", type: "number", min: "1", max: String(max), step: "1",
    value: String(item.quantity), inputMode: "numeric", required: true });
  input.setAttribute("aria-label", "Số lượng " + item.productName + " " + variant);
  const save = button("Cập nhật", "submit");
  save.dataset.saveQuantity = "";
  const decrease = button("−");
  const increase = button("+");
  decrease.dataset.quantityStep = "-1";
  decrease.setAttribute("aria-label", "Giảm số lượng " + item.productName + " " + variant);
  increase.dataset.quantityStep = "1";
  increase.setAttribute("aria-label", "Tăng số lượng " + item.productName + " " + variant);
  for (const control of [decrease, increase]) {
    control.onclick = () => {
      const value = Number.isInteger(input.valueAsNumber) ? input.valueAsNumber : item.quantity;
      input.valueAsNumber = Math.max(1, Math.min(max, value + Number(control.dataset.quantityStep)));
      syncQuantity(rowFor(input), current.busy);
    };
  }
  input.oninput = () => syncQuantity(rowFor(input), current.busy);
  field.append(input);
  const step = element("div", "cart-step");
  step.append(decrease, field, increase);
  form.append(step, save);
  form.onsubmit = event => {
    event.preventDefault();
    if (current.busy || save.disabled || !input.reportValidity()) return;
    void runWrite(() => setCartQuantity(item.cartItemId, input.valueAsNumber), "Đã cập nhật số lượng.", item.cartItemId, "input");
  };
  return form;
}

const rowFor = input => input.closest(".cart-line");

function syncQuantity(row, busy) {
  const input = row.querySelector("input");
  if (!input) return;
  const original = Number(row.dataset.quantity);
  const value = input.valueAsNumber;
  input.disabled = busy;
  row.querySelector('[data-quantity-step="-1"]').disabled = busy || value <= 1;
  row.querySelector('[data-quantity-step="1"]').disabled = busy || value >= Number(input.max);
  row.querySelector("[data-save-quantity]").disabled = busy || !input.validity.valid || value === original;
}

function renderCart(data) {
  list.replaceChildren(...(data?.items ?? []).map(createLine));
  layout.querySelector("[data-cart-total-items]").textContent = data?.totalItems ?? "";
  layout.querySelector("[data-cart-subtotal]").textContent = data ? money(data.subtotal) : "";
  layout.querySelector("[data-unavailable-note]").hidden = !data?.items.some(item => item.isAvailable === false);
}

function syncControls(busy) {
  clearButton.disabled = busy;
  for (const row of list.children) {
    row.querySelector("[data-remove-item]").disabled = busy;
    syncQuantity(row, busy);
  }
}

watchCart(snapshot => {
  current = snapshot;
  const { access, data, busy, error } = snapshot;
  if (shown !== data) {
    shown = data;
    renderCart(data);
  }
  retry.disabled = busy;
  login.hidden = access !== "guest";
  layout.hidden = !data?.items.length;
  layout.setAttribute("aria-busy", String(busy));
  syncControls(busy);
  if (access !== "customer" && dialog.open) dialog.close("keep");
  if (access === "guest") {
    setCatalogState(statePanel, "empty", "Đăng nhập để xem giỏ hàng", "Dùng tài khoản khách hàng để quản lý giỏ hàng của bạn.");
  } else if (access === "forbidden") {
    setCatalogState(statePanel, "empty", "Giỏ hàng dành cho khách hàng", "Tài khoản hiện tại không có quyền sử dụng giỏ hàng.");
  } else if (busy && !data) {
    setCatalogState(statePanel, "loading", "Đang tải giỏ hàng");
  } else if (error) {
    setCatalogState(statePanel, "error", "Không thể hoàn tất yêu cầu", cartMessage(error), true);
  } else if (data && !data.items.length) {
    setCatalogState(statePanel, "empty", "Giỏ hàng trống", "Tìm sản phẩm bạn cần trong danh mục.");
  } else if (access === "checking") {
    setCatalogState(statePanel, "loading", "Đang kiểm tra phiên đăng nhập");
  } else setCatalogState(statePanel);
});

async function runWrite(write, message, itemId, focusSelector) {
  if (current.access !== "customer" || current.busy) return;
  notice.textContent = "";
  try {
    await write();
    notice.textContent = message;
  } catch {
    // The cart state shows the server error and exposes a read-only reload.
  } finally {
    const row = itemId ? list.querySelector(`[data-item-id="${itemId}"]`) : null;
    (row?.querySelector(focusSelector) ?? (current.error ? retry : heading)).focus();
  }
}

retry.onclick = async () => {
  try {
    await loadCart();
    heading.focus();
  } catch {
    if (!retry.hidden) retry.focus();
  }
};
clearButton.addEventListener("click", () => { dialog.returnValue = "keep"; });
dialog.addEventListener("close", () => {
  if (dialog.returnValue === "clear") void runWrite(clearCart, "Đã xóa toàn bộ giỏ hàng.");
});
