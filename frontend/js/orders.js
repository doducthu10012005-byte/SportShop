import { positiveInt } from "./catalog.js";

const orderPage = new URL("../orders/", import.meta.url);
const adminOrderPage = new URL("../admin/orders/", import.meta.url);
const vietnamTime = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh"
});
const payments = new Map([
  ["Pending", "Chờ thanh toán"], ["Paid", "Đã thanh toán"], ["Failed", "Thanh toán thất bại"],
  ["Expired", "Quá hạn thanh toán"], ["RefundPending", "Chờ hoàn tiền"], ["Refunded", "Đã hoàn tiền"]
]);

function detailUrl(page, id) {
  const orderId = positiveInt(id);
  if (!orderId) return null;
  const url = new URL(page);
  url.searchParams.set("id", orderId);
  return url.href;
}

export function orderDetailUrl(id) {
  return detailUrl(orderPage, id);
}

export function adminOrderDetailUrl(id) {
  return detailUrl(adminOrderPage, id);
}

export function adminPaymentUrl(id) {
  const url = new URL(adminOrderDetailUrl(id) ?? adminOrderPage);
  url.searchParams.set("view", "payments");
  return url.href;
}

export function orderTime(value) {
  const time = Date.parse(value);
  return Number.isFinite(time) ? vietnamTime.format(time) : "—";
}

export function paymentStatus(value) {
  return payments.get(value) ?? value;
}
