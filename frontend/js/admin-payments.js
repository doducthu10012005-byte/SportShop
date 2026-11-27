import { element, field } from "./admin-catalog-form.js";
import { button } from "./admin-stock-ui.js";
import { money } from "./catalog.js";
import { orderTime, paymentStatus } from "./orders.js";

const moneyFields = new Set(["amount", "receivedAmount", "totalAmount", "subtotal", "shippingFee", "unitPrice", "lineTotal"]);
const integer = new Intl.NumberFormat("vi-VN");

export function paymentJson(key, value, context) {
  if (!moneyFields.has(key) || typeof value !== "number") return value;
  const literal = context?.source;
  if (/^\d+(?:\.\d+)?$/.test(literal)) return literal;
  // Above this range a Number can lose cents before the browser displays the value.
  return value < 2 ** 46 ? value : null;
}

export function paymentMoney(value) {
  if (value == null) return "Chưa có số tiền chính xác";
  if (typeof value !== "string") return money(value);
  const [whole, fraction = ""] = value.split(".");
  const decimals = fraction.replace(/0+$/, "");
  return integer.format(BigInt(whole)) + (decimals ? "," + decimals : "") + " ₫";
}

function date(value) {
  return value ? element("time", orderTime(value), { datetime: value }) : null;
}

function received(value) {
  const match = /^(\d+)(?:[.,](\d+))?$/.exec(value.trim());
  if (!match) return null;
  const whole = match[1].replace(/^0+(?=\d)/, "");
  const fraction = (match[2] ?? "").replace(/0+$/, "");
  if (whole.length > 16 || fraction.length > 2) return null;
  const numeric = whole + (fraction ? "." + fraction : "");
  if (typeof JSON.rawJSON === "function") return JSON.rawJSON(numeric);
  return String(Number(numeric)) === numeric ? Number(numeric) : null;
}

export function paymentPanel(order, { makeFacts, path, dialog, signal, reload, enableActions }) {
  const payment = order.payment;
  const values = payment ? [
    ["Phương thức", payment.methodName ?? payment.methodCode], ["Trạng thái", paymentStatus(payment.paymentStatus)],
    ["Số tiền phải trả", paymentMoney(payment.amount)], ["Đã nhận", payment.receivedAmount == null ? null : paymentMoney(payment.receivedAmount)],
    ["Mã giao dịch chuyển khoản", payment.methodCode === "BANK_TRANSFER" ? payment.transactionCode : null],
    ["Hạn thanh toán (giờ Việt Nam)", date(payment.expiresAt)], ["Ghi nhận thu tiền", date(payment.paidAt)],
    ["Ghi nhận hoàn tiền", date(payment.refundedAt)]
  ] : [["Thông tin", "Chưa có dữ liệu thanh toán"]];
  const panel = makeFacts("Thanh toán", values, "data-admin-order-payment");
  panel.classList.add("orders-admin-payment");
  if (!payment) return panel;

  const options = { reload, method: "PUT",
    unknownMessage: "Chưa xác định được kết quả. Kiểm tra trạng thái thanh toán vừa tải lại. Nếu vẫn chưa rõ, đối chiếu giao dịch thủ công trước khi thao tác tiếp.",
    reconcileRejection: true };
  if (payment.methodCode === "COD" && payment.paymentStatus === "Pending") {
    panel.append(element("p", "COD được ghi nhận khi đơn chuyển sang Đã giao."));
  }
  const canConfirm = payment.methodCode === "BANK_TRANSFER" && payment.paymentStatus === "Pending" &&
    payment.isOverdue === false && payment.expiresAt && payment.amount != null &&
    ["PENDING", "CONFIRMED"].includes(order.status.statusCode);
  if (enableActions && canConfirm) {
    const confirm = button("Xác nhận đã nhận chuyển khoản", { "data-confirm-transfer": "" });
    confirm.addEventListener("click", () => void dialog.open(signal, {
      ...options, path: path + "/payment", title: "Xác nhận đã nhận chuyển khoản",
      label: "Ghi nhận đã nhận tiền", success: "Đã ghi nhận thanh toán chuyển khoản.",
      render(fields) {
        fields.append(element("p", "Đơn " + order.orderCode + " · Phải thu " + paymentMoney(payment.amount),
          { class: "management-wide" }), element("p", "Chỉ ghi nhận sau khi đã đối chiếu giao dịch thực tế.",
          { class: "management-wide" }));
        field(fields, "transactionCode", "Mã giao dịch", { required: "", maxlength: 100, autocomplete: "off", wide: true });
        field(fields, "receivedAmount", "Số tiền thực nhận (₫)", { required: "", inputmode: "decimal", autocomplete: "off", maxlength: 32, wide: true });
      },
      payload(form) {
        const transactionCode = form.elements.transactionCode.value.trim();
        if (!transactionCode || transactionCode.length > 100)
          return { error: "Mã giao dịch phải từ 1 đến 100 ký tự.", field: "transactionCode" };
        const receivedAmount = received(form.elements.receivedAmount.value);
        if (receivedAmount === null)
          return { error: "Nhập số tiền không âm, tối đa 16 chữ số nguyên và 2 chữ số thập phân có nghĩa.", field: "receivedAmount" };
        return { body: { paymentStatus: "Paid", transactionCode, receivedAmount } };
      }
    }));
    panel.append(confirm);
  }
  if (payment.methodCode === "BANK_TRANSFER" && payment.paymentStatus === "Pending" && payment.isOverdue === true) {
    panel.append(element("p", "Khoản chuyển khoản đã quá hạn xác nhận."));
  }
  if (payment.paymentStatus === "RefundPending") {
    panel.append(element("p", "Đơn đã được đưa vào danh sách chờ hoàn tiền."));
  }
  if (enableActions && payment.paymentStatus === "RefundPending" && payment.receivedAmount != null) {
    const refund = button("Ghi nhận đã hoàn tiền", { "data-confirm-refund": "" });
    refund.addEventListener("click", () => void dialog.open(signal, {
      ...options, path: path + "/refund", title: "Ghi nhận đã hoàn tiền", label: "Xác nhận đã hoàn tiền",
      success: "Đã ghi nhận hoàn tiền.",
      render(fields) {
        fields.append(element("p", "Đơn " + order.orderCode + " · Số tiền phải hoàn " + paymentMoney(payment.receivedAmount),
          { class: "management-wide" }), element("p", "Thực hiện hoàn tiền ngoài SportShop. Thao tác này chỉ ghi nhận khoản đã hoàn đủ.",
          { class: "management-wide" }));
        field(fields, "refundCompleted", "Tôi xác nhận đã hoàn đủ tiền cho khách.", {
          type: "checkbox", value: false, wide: true
        });
      },
      payload(form) {
        return form.elements.refundCompleted.checked ? { body: { refundCompleted: true } }
          : { error: "Cần xác nhận đã hoàn đủ tiền cho khách.", field: "refundCompleted" };
      }
    }));
    panel.append(refund);
  }
  return panel;
}
