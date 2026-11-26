import "../store-session.js";
import { authorizedRequest, sessionMessage, watchSession } from "../auth.js";
import { orderDetailUrl, paymentStatus } from "../orders.js";
import { cartMessage, loadCart, watchCart } from "../cart.js";
import { element, getCatalog, money, positiveInt, productImage, setCatalogState } from "../catalog.js";

const form = document.querySelector("[data-checkout-form]");
const panel = document.querySelector("[data-checkout-state]");
const login = panel.querySelector("[data-checkout-login]");
const retry = panel.querySelector("[data-retry]");
const issues = panel.querySelector("[data-checkout-issues]");
const result = document.querySelector("[data-checkout-result]");
const list = form.querySelector("[data-checkout-lines]");
const submit = form.querySelector("[type=submit]");
const methodSelect = form.elements.paymentMethodId;
const addressSelect = form.elements.savedAddress;
const addressStatus = form.querySelector("[data-address-status]");
const moreAddresses = form.querySelector("[data-more-addresses]");
const fieldErrors = [...form.querySelectorAll("[data-field-error]")];
const allowedMethods = ["COD", "BANK_TRANSFER"];
const localTime = new Intl.DateTimeFormat("vi-VN", { dateStyle: "short", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh" });

let session;
let activeUser;
let cart;
let renderedCart;
let methods = null;
let loadingMethods = false;
let methodError;
let addresses = new Map();
let addressPage = 1;
let outcome;
let posting = false;
let expiryTimer;

function renderCart(data) {
  list.replaceChildren(...(data?.items ?? []).map(item => {
    const row = element("li");
    const body = element("div");
    const variant = [item.sizeName, item.colorName].filter(Boolean).join(" · ");
    body.append(element("strong", "", item.productName),
      element("p", "", [variant, `Số lượng ${item.quantity}`, money(item.unitPrice)].filter(Boolean).join(" · ")),
      element("p", item.isAvailable === true ? "product-price" : "stock-out",
        item.isAvailable === true ? money(item.lineTotal) : "Không khả dụng"));
    row.append(productImage(item.imageUrl), body);
    return row;
  }));
  form.querySelector("[data-preview-subtotal]").textContent = data ? money(data.subtotal) : "";
}

function render() {
  const { phase, user } = session ?? {};
  const customer = phase === "authenticated" && user.role === "Customer" && !user.mustChangePassword;
  const data = cart?.data;
  const blocked = data?.items.some(item => item.isAvailable !== true);
  if (data !== renderedCart) {
    renderedCart = data;
    renderCart(data);
  }
  const finished = outcome?.kind === "success";
  const uncertain = outcome?.kind === "unknown";
  const canShowForm = customer && Boolean(data?.items.length) && !finished && !uncertain;
  form.hidden = !canShowForm;
  result.hidden = !customer || !finished;
  form.querySelector("[data-checkout-stock]").hidden = !blocked;
  form.setAttribute("aria-busy", String(posting));
  submit.disabled = !canShowForm || posting || cart.busy || Boolean(cart.error) || loadingMethods ||
    Boolean(methodError) || !methods?.length || blocked;
  submit.textContent = posting ? "Đang đặt hàng…" : "Đặt hàng";
  login.hidden = phase !== "anonymous";
  retry.disabled = posting || Boolean(cart?.busy) || loadingMethods;

  if (phase === "anonymous") {
    setCatalogState(panel, "empty", "Đăng nhập để đặt hàng", session.message || "Bạn cần tài khoản khách hàng để tiếp tục.");
  } else if (phase !== "authenticated" || user.mustChangePassword) {
    setCatalogState(panel, "loading", "Đang kiểm tra phiên đăng nhập");
  } else if (!customer) {
    setCatalogState(panel, "empty", "Chỉ khách hàng mới có thể đặt hàng");
  } else if (finished) {
    setCatalogState(panel);
  } else if (uncertain) {
    setCatalogState(panel, "error", "Chưa xác định được kết quả đặt hàng", outcome.message);
  } else if (outcome?.kind === "error") {
    setCatalogState(panel, "error", "Đặt hàng chưa thành công", outcome.message, Boolean(cart?.error || methodError));
  } else if (cart?.error || methodError) {
    setCatalogState(panel, "error", "Chưa tải được thông tin đặt hàng", sessionMessage(cart?.error ?? methodError), true);
  } else if ((!data && cart?.busy) || loadingMethods || !methods) {
    setCatalogState(panel, "loading", "Đang tải thông tin đặt hàng");
  } else if (!data?.items.length) {
    setCatalogState(panel, "empty", "Giỏ hàng trống", "Thêm sản phẩm vào giỏ trước khi đặt hàng.");
  } else if (!methods.length) {
    setCatalogState(panel, "empty", "Chưa có phương thức thanh toán", "Thử tải lại thông tin hoặc quay về giỏ hàng.", true);
  } else setCatalogState(panel);
  const messages = customer ? outcome?.issues ?? [] : [];
  issues.replaceChildren(...messages.map(message => element("li", "", message)));
  issues.hidden = messages.length === 0;
}

async function fetchMethods(owner) {
  const selected = methodSelect.value;
  loadingMethods = true;
  methodError = null;
  render();
  try {
    const data = await getCatalog("/payment-methods", { signal: owner.signal });
    if (owner !== activeUser) return;
    methods = data.filter(item => allowedMethods.includes(item.methodCode) && positiveInt(item.paymentMethodId));
    methodSelect.replaceChildren(new Option("Chọn phương thức", ""), ...methods.map(item =>
      new Option(item.methodName, item.paymentMethodId)));
    methodSelect.value = methods.some(item => String(item.paymentMethodId) === selected) ? selected : "";
  } catch (error) {
    if (owner === activeUser) methodError = error;
  } finally {
    if (owner === activeUser) {
      loadingMethods = false;
      render();
    }
  }
}

function formattedAddress(address) {
  return [address.addressLine, address.ward, address.district, address.province].filter(Boolean).join(", ");
}

async function fetchAddresses(owner) {
  moreAddresses.hidden = true;
  addressStatus.textContent = "Đang tải địa chỉ đã lưu…";
  try {
    const { data } = await authorizedRequest("/users/me/addresses", {
      query: { page: addressPage, pageSize: 12, sort: "-isDefault" },
      signal: AbortSignal.any([owner.signal, AbortSignal.timeout(10000)])
    });
    if (owner !== activeUser) return;
    for (const address of data.items) {
      if (addresses.has(address.addressId)) continue;
      addresses.set(address.addressId, address);
      addressSelect.add(new Option(formattedAddress(address) + (address.isDefault ? " (Mặc định)" : ""), address.addressId));
    }
    form.querySelector("[data-address-select]").hidden = !addresses.size;
    moreAddresses.hidden = addressPage >= data.pagination.totalPages;
    moreAddresses.textContent = "Xem thêm địa chỉ";
    addressPage++;
    addressStatus.textContent = "";
  } catch {
    if (owner !== activeUser) return;
    addressStatus.textContent = "Bạn có thể nhập địa chỉ giao hàng trực tiếp.";
    moreAddresses.textContent = "Thử tải lại địa chỉ";
    moreAddresses.hidden = false;
  }
}

function showFieldErrors(fields) {
  for (const node of fieldErrors) {
    const name = node.dataset.fieldError;
    const message = fields.find(field => field.field === name)?.message;
    node.textContent = message ?? "";
    node.hidden = !message;
    if (message) form.elements[name].setAttribute("aria-invalid", "true");
    else form.elements[name].removeAttribute("aria-invalid");
  }
}

function readOrder() {
  const { recipientName, recipientPhone, shippingAddress, note, paymentMethodId } = form.elements;
  return { recipientName: recipientName.value.trim(), recipientPhone: recipientPhone.value.trim(),
    shippingAddress: shippingAddress.value.trim(), paymentMethodId: positiveInt(paymentMethodId.value),
    note: note.value.trim() || null };
}

function validate(input, payment) {
  const fields = [];
  function add(field, message) { fields.push({ field, message }); }
  if (!input.recipientName || input.recipientName.length > 100) add("recipientName", "Nhập họ tên người nhận, tối đa 100 ký tự.");
  if (!/^(?:0|\+84)[0-9]{9}$/.test(input.recipientPhone)) add("recipientPhone", "Nhập số bắt đầu bằng 0 hoặc +84, theo sau là 9 chữ số.");
  if (!input.shippingAddress || input.shippingAddress.length > 500) add("shippingAddress", "Nhập địa chỉ giao hàng, tối đa 500 ký tự.");
  if (input.note?.length > 500) add("note", "Ghi chú tối đa 500 ký tự.");
  if (!payment) add("paymentMethodId", "Chọn phương thức thanh toán khả dụng.");
  return fields;
}

function validReceipt(data, method) {
  return positiveInt(data?.orderId) && typeof data.orderCode === "string" && data.orderCode.trim() &&
    typeof data.status?.statusName === "string" && data.status.statusName.trim() &&
    [data.subtotal, data.shippingFee, data.totalAmount].every(value => Number.isFinite(value) && value >= 0) &&
    data.payment?.methodCode === method && typeof data.payment.paymentStatus === "string" &&
    (method !== "BANK_TRANSFER" || (typeof data.payment.instructions === "string" && data.payment.instructions.trim()));
}

function showReceipt(data) {
  const payment = data.payment;
  const bank = payment.methodCode === "BANK_TRANSFER";
  const values = { code: data.orderCode, status: data.status.statusName, subtotal: money(data.subtotal),
    shipping: money(data.shippingFee), total: money(data.totalAmount),
    payment: (bank ? "Chuyển khoản" : "Thanh toán khi nhận hàng") + " · " + paymentStatus(payment.paymentStatus) };
  for (const [field, value] of Object.entries(values)) result.querySelector(`[data-order-${field}]`).textContent = value;
  result.querySelector("[data-bank-instructions]").hidden = !bank;
  result.querySelector("[data-bank-text]").textContent = bank ? payment.instructions : "";
  const link = result.querySelector("[data-order-detail-link]");
  link.href = orderDetailUrl(data.orderId);
  link.hidden = false;

  const deadline = result.querySelector("[data-bank-deadline]");
  const remaining = result.querySelector("[data-bank-remaining]");
  const timestamp = bank && payment.expiresAt ? Date.parse(payment.expiresAt) : NaN;
  deadline.hidden = !Number.isFinite(timestamp);
  remaining.textContent = "";
  if (!deadline.hidden) {
    const time = deadline.querySelector("time");
    time.dateTime = payment.expiresAt;
    time.textContent = localTime.format(timestamp);
    function updateRemaining() {
      const minutes = Math.ceil((timestamp - Date.now()) / 60000);
      remaining.textContent = minutes > 0 ? `Còn khoảng ${minutes} phút theo đồng hồ thiết bị.`
        : "Đã tới mốc hiển thị theo đồng hồ thiết bị. Trạng thái thanh toán do hệ thống xác nhận.";
      if (minutes <= 0) clearInterval(expiryTimer);
    }
    expiryTimer = setInterval(updateRemaining, 15000);
    updateRemaining();
  }
}

function reset(owner) {
  clearInterval(expiryTimer);
  const link = result.querySelector("[data-order-detail-link]");
  link.removeAttribute("href");
  link.hidden = true;
  result.querySelectorAll("dd, [data-bank-text], [data-bank-remaining], time").forEach(node => { node.textContent = ""; });
  form.reset();
  form.elements.recipientName.value = owner ? session.user.fullName || "" : "";
  form.elements.recipientPhone.value = owner ? session.user.phoneNumber || "" : "";
  addressSelect.replaceChildren(new Option("Nhập địa chỉ khác", ""));
  methodSelect.replaceChildren();
  addressStatus.textContent = "";
  form.querySelector("[data-address-select]").hidden = true;
  moreAddresses.hidden = true;
  showFieldErrors([]);
  addresses = new Map();
  addressPage = 1;
  methods = null;
  methodError = null;
  loadingMethods = posting = false;
  outcome = null;
}

function checkoutError(error) {
  return error.code === "CART_EMPTY" ? "Giỏ hàng đã trống. Vui lòng quay lại giỏ hàng."
    : error.code === "NOT_FOUND" ? "Không tìm thấy dữ liệu cần thiết để đặt hàng. Vui lòng kiểm tra giỏ hàng."
      : error.code === "INSUFFICIENT_STOCK" ? error.message : cartMessage(error);
}

function uncertainMessage(method, serverError) {
  const message = serverError && method === "BANK_TRANSFER"
    ? "Máy chủ chưa xác nhận đơn chuyển khoản. Chỉ chuyển tiền khi có hướng dẫn thanh toán đã được xác nhận."
    : "Chưa nhận được xác nhận từ máy chủ.";
  return message + " Vui lòng kiểm tra giỏ hàng hoặc liên hệ cửa hàng để xác minh trước khi đặt lại.";
}

form.addEventListener("submit", async event => {
  event.preventDefault();
  if (submit.disabled || !activeUser) return;
  const owner = activeUser;
  const input = readOrder();
  const payment = methods.find(item => item.paymentMethodId === input.paymentMethodId);
  const fields = validate(input, payment);
  showFieldErrors(fields);
  if (fields.length) {
    form.elements[fields[0].field].focus();
    return;
  }
  posting = true;
  outcome = null;
  render();
  try {
    const { data } = await authorizedRequest("/orders", {
      method: "POST", body: input, expectedStatus: 201, signal: AbortSignal.any([owner.signal, AbortSignal.timeout(20000)])
    });
    if (owner !== activeUser) return;
    if (validReceipt(data, payment.methodCode)) {
      showReceipt(data);
      outcome = { kind: "success" };
      void loadCart().catch(() => {});
    } else outcome = { kind: "unknown", message: uncertainMessage(payment.methodCode, false) };
  } catch (error) {
    if (owner !== activeUser) return;
    if (!error.status || error.status >= 500 || (error.status >= 200 && error.status < 300)) {
      outcome = { kind: "unknown", message: uncertainMessage(payment.methodCode, error.status >= 500) };
    } else {
      outcome = { kind: "error", message: checkoutError(error),
        issues: error.fields?.filter(field => field.field.startsWith("variantId:")).map(field => field.message) };
      showFieldErrors(error.fields ?? []);
      if ([400, 404, 409].includes(error.status)) void loadCart().catch(() => {});
      if (error.fields?.some(field => field.field === "paymentMethodId")) void fetchMethods(owner);
    }
  } finally {
    if (owner === activeUser) {
      posting = false;
      render();
      if (outcome?.kind === "success") result.querySelector("h2").focus();
      else if (outcome?.kind === "unknown") panel.querySelector("[data-state-title]").focus();
    }
  }
});

addressSelect.addEventListener("change", () => {
  const address = addresses.get(Number(addressSelect.value));
  form.elements.recipientName.value = address?.recipientName ?? session.user.fullName ?? "";
  form.elements.recipientPhone.value = address?.phoneNumber ?? session.user.phoneNumber ?? "";
  form.elements.shippingAddress.value = address ? formattedAddress(address) : "";
  showFieldErrors([]);
});
moreAddresses.addEventListener("click", () => { if (activeUser) void fetchAddresses(activeUser); });
retry.addEventListener("click", () => {
  if (!activeUser) return;
  outcome = null;
  void loadCart().catch(() => {});
  void fetchMethods(activeUser);
});

watchCart(snapshot => { cart = snapshot; render(); });
watchSession(snapshot => {
  session = snapshot;
  const id = snapshot.phase === "authenticated" && snapshot.user.role === "Customer" &&
    !snapshot.user.mustChangePassword ? snapshot.user.userId : null;
  if (id !== (activeUser?.id ?? null)) {
    activeUser?.abort();
    activeUser = id === null ? null : Object.assign(new AbortController(), { id });
    reset(activeUser);
    if (activeUser) {
      void fetchMethods(activeUser);
      void fetchAddresses(activeUser);
    }
  }
  render();
});
