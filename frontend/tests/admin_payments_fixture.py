from copy import deepcopy
import socket

from admin_orders_fixture import AdminOrdersFixture, LABELS


class AdminPaymentsFixture(AdminOrdersFixture):
    def __init__(self):
        super().__init__()
        self.orders[2]["payment"]["expiresAt"] = "2026-11-30T00:00:00Z"
        for index, state, order_state, overdue in [
            (4, "RefundPending", "CANCELLED", False), (5, "Expired", "CANCELLED", True),
            (6, "Failed", "CANCELLED", False), (7, "Refunded", "CANCELLED", False),
            (8, "Pending", "CONFIRMED", True), (9, "Pending", "SHIPPING", False),
            (10, "Pending", "PENDING", False), (11, "RefundPending", "CANCELLED", False),
            (12, "Pending", "CONFIRMED", False),
        ]:
            item = self.orders[index]
            item["status"] = dict(statusCode=order_state, statusName=LABELS[order_state])
            item["payment"].update(methodCode="BANK_TRANSFER", methodName="Chuyển khoản", paymentStatus=state,
                                   isOverdue=overdue, expiresAt="2026-11-30T00:00:00Z")
            if state in ("RefundPending", "Refunded"):
                item["payment"].update(receivedAmount=item["totalAmount"], transactionCode=f"QA-PREV-{index}",
                                       paidAt="2026-11-05T00:00:00Z")
        self.orders[7]["payment"]["refundedAt"] = "2026-11-14T00:00:00Z"
        self.payment_writes = 0
        self.refund_writes = 0
        for order in self.orders.values():
            order["payment"].setdefault("isOverdue", False)

    def control(self, action):
        state = super().control(action)
        state.update(paymentCommits=self.payment_writes, refundCommits=self.refund_writes)
        return state

    def dispatch(self, api, path, body, authenticated, user, query):
        if api.command != "PUT" or not path.endswith(("/payment", "/refund")):
            return super().dispatch(api, path, body, authenticated, user, query)
        self.requests.append(dict(method=api.command, path=path, body=deepcopy(body), query=deepcopy(query), actor=user and user["userId"]))
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin" or self.mode == "forbidden":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        if user["userId"] != 3 and not self.wait_write.wait(8):
            return api.error(500, "INTERNAL_ERROR", "QA timeout")
        order = self.orders.get(int(path.split("/")[2]))
        if not order or self.mode == "missing":
            return api.error(404, "NOT_FOUND", "Không tìm thấy đơn hàng.")
        if self.mode == "server-write":
            return api.error(500, "INTERNAL_ERROR", "Nội bộ QA")
        with self.lock:
            payment = order["payment"]
            if path.endswith("/refund"):
                if body.get("refundCompleted") is not True:
                    return api.error(400, "VALIDATION_ERROR", "Phải xác nhận đã hoàn tiền.")
                if payment["paymentStatus"] == "Refunded":
                    return api.success(deepcopy(order))
                if payment["paymentStatus"] != "RefundPending":
                    return api.error(409, "INVALID_STATUS_TRANSITION", "Khoản thanh toán không chờ hoàn tiền.")
                payment.update(paymentStatus="Refunded", refundedAt="2026-11-27T06:00:00Z")
                self.refund_writes += 1
            else:
                code, amount = body["transactionCode"], body["receivedAmount"]
                if self.mode == "validation":
                    return api.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                                     [{"field":"receivedAmount", "message":"Số tiền nhận không hợp lệ."}])
                if self.mode == "overdue" or payment["isOverdue"]:
                    payment["isOverdue"] = True
                    return api.error(409, "PAYMENT_EXPIRED", "Khoản thanh toán đã hết hạn.")
                if payment["methodCode"] != "BANK_TRANSFER" or order["status"]["statusCode"] not in ("PENDING", "CONFIRMED"):
                    return api.error(409, "INVALID_STATUS_TRANSITION", "Không thể xác nhận thanh toán.")
                if payment["paymentStatus"] == "Paid":
                    if code.casefold() == payment["transactionCode"].casefold() and amount == payment["receivedAmount"]:
                        return api.success(deepcopy(order))
                    return api.error(409, "PAYMENT_ALREADY_CONFIRMED", "Khoản thanh toán đã được xác nhận.")
                if amount != order["totalAmount"]:
                    return api.error(409, "PAYMENT_AMOUNT_MISMATCH", "Số tiền nhận phải bằng tổng tiền đơn hàng.",
                                     [{"field":"receivedAmount", "message":"Số tiền nhận không khớp."}])
                if any((item["payment"].get("transactionCode") or "").casefold() == code.casefold()
                       for item in self.orders.values() if item is not order):
                    return api.error(409, "DUPLICATE_TRANSACTION", "Mã giao dịch đã được sử dụng.")
                payment.update(paymentStatus="Paid", transactionCode=code, receivedAmount=amount, paidAt="2026-11-27T06:00:00Z")
                self.payment_writes += 1
            if self.mode == "lost":
                api.connection.shutdown(socket.SHUT_RDWR)
                return
            return api.success(deepcopy(order))
