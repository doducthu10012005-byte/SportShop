import { ApiError } from "./api.js";
import { authorizedRequest, sessionMessage, watchSession } from "./auth.js";
import { positiveInt } from "./catalog.js";

const listeners = new Set();
let cart = { access: "checking", data: null, busy: false, error: null };
let owner = null;
let flight;

function publish(changes) {
  cart = { ...cart, ...changes };
  for (const listener of listeners) listener(cart);
}

export function watchCart(listener) {
  listeners.add(listener);
  listener(cart);
}

function sessionAccess({ phase, user }) {
  if (phase === "authenticated") return user.mustChangePassword || user.role !== "Customer" ? "forbidden" : "customer";
  return phase === "anonymous" ? "guest" : "checking";
}

async function send(path, method, body) {
  const { data } = await authorizedRequest(path, { method, body, signal: AbortSignal.timeout(12000) });
  if (!Array.isArray(data?.items) || !Number.isFinite(data.subtotal) || !Number.isInteger(data.totalItems))
    throw new ApiError("Phản hồi giỏ hàng không hợp lệ.", { code: "INVALID_RESPONSE" });
  return data;
}

async function run(path, method, body) {
  if (cart.access !== "customer")
    throw new ApiError("Giỏ hàng chỉ dành cho khách hàng đã đăng nhập.", { code: "FORBIDDEN" });
  if (flight) {
    if (method === "GET") return flight;
    throw new ApiError("Giỏ hàng đang được cập nhật.", { code: "CART_BUSY" });
  }

  const userId = owner;
  function assertCurrent() {
    if (flight !== task || owner !== userId) throw new DOMException("Phiên đã thay đổi.", "AbortError");
  }
  const task = Promise.resolve().then(async () => {
    try {
      assertCurrent();
      const data = await send(path, method, body);
      assertCurrent();
      publish({ data, error: null });
      return data;
    } catch (error) {
      if (flight === task && owner === userId) {
        // A stale item or stock conflict needs a fresh snapshot, never a replay of the write.
        if (method !== "GET" && [404, 409].includes(error.status)) {
          try {
            const data = await send("/cart", "GET");
            if (flight === task && owner === userId) publish({ data });
          } catch { /* Keep the original write error for the user. */ }
        }
        if (flight === task && owner === userId) publish({ error });
      }
      throw error;
    } finally {
      if (flight === task) {
        flight = undefined;
        publish({ busy: false });
      }
    }
  });
  flight = task;
  publish({ busy: true, error: null });
  return task;
}

export const loadCart = () => run("/cart", "GET");
export const clearCart = () => run("/cart", "DELETE");

function quantityValue(quantity) {
  if (Number.isInteger(quantity) && quantity >= 1 && quantity <= 99) return quantity;
  throw new ApiError("Số lượng phải là số nguyên từ 1 đến 99.", { code: "VALIDATION_ERROR" });
}

export function addToCart(variantId, quantity) {
  if (!Number.isInteger(variantId) || !positiveInt(variantId))
    throw new ApiError("Biến thể không hợp lệ.", { code: "VALIDATION_ERROR" });
  return run("/cart/items", "POST", { variantId, quantity: quantityValue(quantity) });
}

function itemPath(itemId) {
  if (!positiveInt(itemId) || !cart.data?.items.some(item => item.cartItemId === itemId))
    throw new ApiError("Dòng hàng này không còn trong giỏ.", { status: 404, code: "NOT_FOUND" });
  return "/cart/items/" + itemId;
}

export function setCartQuantity(itemId, quantity) {
  return run(itemPath(itemId), "PUT", { quantity: quantityValue(quantity) });
}

export function removeFromCart(itemId) {
  return run(itemPath(itemId), "DELETE");
}

export function cartMessage(error) {
  if (error.code === "INSUFFICIENT_STOCK") return error.fields?.[0]?.message || "Sản phẩm không đủ tồn kho.";
  if (error.code === "NOT_FOUND") return "Dòng hàng hoặc biến thể không còn khả dụng. Vui lòng kiểm tra lại.";
  if (error.code === "CONCURRENCY_CONFLICT") return "Giỏ hàng vừa thay đổi. Vui lòng kiểm tra số lượng trước khi thử lại.";
  return sessionMessage(error);
}

watchSession(session => {
  const access = sessionAccess(session);
  const userId = access === "customer" ? session.user.userId : null;
  if (owner === userId && access === cart.access) return;
  owner = userId;
  flight = undefined;
  publish({ access, data: null, busy: false, error: null });
  if (owner !== null) void loadCart().catch(() => {});
});

window.addEventListener("pageshow", event => {
  if (event.persisted && cart.access === "customer") void loadCart().catch(() => {});
});
