"""Customer order history, detail and cancellation fixtures for Chromium."""

from copy import deepcopy

from checkout_fixture import CheckoutFixture


class OrdersFixture(CheckoutFixture):
    def __init__(self):
        super().__init__()
        self.history_calls = []
        self.next_list_error = None
        self.next_cancel = None
        self.history_orders = {n: self.make_order(n) for n in range(1, 5)}

    def make_order(self, number):
        order = {"orderId": number, "orderCode": f"SP20261114-{number:04}",
                 "createdAt": f"2026-11-{10 + number}T20:30:00Z", "recipientName": "Người nhận lúc đặt",
                 "recipientPhone": "0912345678", "shippingAddress": "12 Địa chỉ snapshot <i data-injected>QA</i>",
                 "note": "Giao chiều", "cancelReason": None, "subtotal": 900000, "shippingFee": 30000,
                 "totalAmount": 930000, "totalItems": 2,
                 "payment": {"methodCode": "BANK_TRANSFER" if number != 4 else "COD", "methodName": "Chuyển khoản" if number != 4 else "COD",
                             "paymentStatus": "Paid" if number in (1, 2) else "Pending", "amount": 930000,
                             "receivedAmount": 930000 if number in (1, 2) else None,
                             "expiresAt": "2026-11-20T20:30:00Z" if number != 4 else None, "isOverdue": False,
                             "transactionCode": "ADMIN-ONLY-QA"},
                 "items": [{"orderItemId": number, "variantId": 5, "productName": "Sản phẩm snapshot <b data-injected>QA</b>",
                             "sku": "SNAPSHOT-QA", "sizeName": "M", "colorName": "Đen", "imageUrl": None,
                             "unitPrice": 450000, "quantity": 2, "lineTotal": 900000}], "statusHistory": []}
        if number == 2:
            order["payment"]["instructions"] = "Hướng dẫn QA <span data-injected>ngân hàng</span>"
        self.transition(order, "PENDING", "Chờ xác nhận")
        if number == 4:
            self.transition(order, "SHIPPING", "Đang giao")
        return order

    @staticmethod
    def transition(order, code, name, reason=None):
        order["status"] = {"statusCode": code, "statusName": name}
        order["statusHistory"].append({**order["status"], "changedAt": order["createdAt"], "note": reason})

    def control(self, action):
        if action in ("orders-offline", "orders-denied"):
            self.next_list_error = action
        if action in ("cancel-conflict", "cancel-concurrency", "cancel-lost", "cancel-uncommitted", "cancel-invalid"):
            self.next_cancel = action
        return {**super().control(action), "historyCalls": self.history_calls[:]}

    def history(self, api, path, body, authorized, user, query):
        self.history_calls.append({"method": api.command, "path": path, "query": query, "body": body})
        if not authorized:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Customer":
            return api.error(403, "FORBIDDEN", "Không có quyền.")
        if path == "orders":
            if self.next_list_error:
                failure = self.next_list_error
                self.next_list_error = None
                if failure == "orders-denied":
                    return api.error(403, "FORBIDDEN", "Không có quyền xem đơn.")
                api.close_connection = True
                return None
            rows = list(self.history_orders.values())
            code = query.get("statusCode", [""])[0]
            if code:
                rows = [row for row in rows if row["status"]["statusCode"] == code]
            rows.sort(key=lambda row: row["createdAt"], reverse=query.get("sort", ["-createdAt"])[0] == "-createdAt")
            page, size = int(query["page"][0]), int(query["pageSize"][0])
            items = []
            for row in rows[(page - 1) * size:page * size]:
                items.append({**{field: row[field] for field in ("orderId", "orderCode", "status", "createdAt", "totalAmount", "totalItems")},
                              "paymentStatus": row["payment"]["paymentStatus"], "paymentMethodName": row["payment"]["methodName"]})
            return api.success({"items": items, "pagination": {"page": page, "pageSize": size,
                                "totalItems": len(rows), "totalPages": (len(rows) + size - 1) // size}})
        identifier = int(path.split("/")[1])
        row = self.history_orders.get(identifier)
        if not row:
            receipt = next((entry for entry in self.orders if entry["orderId"] == identifier), None)
            if receipt and api.command == "GET":
                return api.success({**self.make_order(identifier), **receipt})
            return api.error(404, "NOT_FOUND", "Không tìm thấy đơn hàng.")
        if api.command == "GET":
            return api.success(row)
        decision, self.next_cancel = self.next_cancel, None
        if decision == "cancel-invalid":
            return api.error(400, "VALIDATION_ERROR", "Lý do chưa hợp lệ.",
                             [{"field": "cancelReason", "message": "Kiểm tra lại lý do hủy."}])
        if decision == "cancel-conflict":
            self.transition(row, "CONFIRMED", "Đã xác nhận")
        if decision == "cancel-concurrency":
            return api.error(409, "CONCURRENCY_CONFLICT", "Đơn hàng đang thay đổi.")
        if row["status"]["statusCode"] != "PENDING":
            return api.error(409, "INVALID_STATUS_TRANSITION", "Không thể hủy.")
        if decision == "cancel-uncommitted":
            api.close_connection = True
            return None
        row["cancelReason"] = body["cancelReason"]
        self.transition(row, "CANCELLED", "Đã hủy", row["cancelReason"])
        row["payment"]["paymentStatus"] = "RefundPending" if row["payment"]["paymentStatus"] == "Paid" else "Failed"
        if decision == "cancel-lost":
            api.close_connection = True
            return None
        stale = deepcopy(row)
        stale["statusHistory"] = []
        return api.reply(200, {"success": True, "data": stale, "message": "Đã hủy đơn. Khoản thanh toán đang chờ hoàn tiền."})
