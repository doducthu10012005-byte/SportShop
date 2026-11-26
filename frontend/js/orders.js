import { positiveInt } from "./catalog.js";

const orderPage = new URL("../orders/", import.meta.url);
const vietnamTime = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short", timeStyle: "short", timeZone: "Asia/Ho_Chi_Minh"
});
const payments = new Map([
  ["Pending", "Chờ thanh toán"], ["Paid", "Đã thanh toán"], ["Failed", "Thanh toán thất bại"],
  ["Expired", "Quá hạn thanh toán"], ["RefundPending", "Chờ hoàn tiền"], ["Refunded", "Đã hoàn tiền"]
]);

export function orderDetailUrl(id) {
  const orderId = positiveInt(id);
  if (!orderId) return null;
  const url = new URL(orderPage);
  url.searchParams.set("id", orderId);
  return url.href;
}

export function orderTime(value) {
  const time = Date.parse(value);
  return Number.isFinite(time) ? vietnamTime.format(time) : "—";
}

export function paymentStatus(value) {
  return payments.get(value) ?? value;
}
