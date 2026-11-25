"""Synthetic checkout responses for Chromium, without a real backend or database."""

import time
from datetime import datetime, timedelta, timezone

from cart_fixture import CartFixture


METHODS = [{"paymentMethodId": 3, "methodCode": "COD", "methodName": "Thanh toán khi nhận hàng"},
           {"paymentMethodId": 7, "methodCode": "BANK_TRANSFER", "methodName": "Chuyển khoản ngân hàng"}]


class CheckoutFixture(CartFixture):
    def __init__(self):
        super().__init__()
        self.methods = METHODS
        self.orders = []
        self.order_requests = []
        self.decision = None
        self.address_fail = False
        self.payment_fail = False

    def control(self, action):
        with self.lock:
            if action == "checkout-reset":
                self.items = {21: (5, 2)}
                self.stock, self.price, self.selling = 7, 420000, True
                self.decision = None
                self.methods = METHODS
            elif action == "empty-checkout":
                self.items.clear()
            elif action == "cod-only":
                self.methods = METHODS[:1]
            elif action == "no-methods":
                self.methods = []
            elif action == "address-error":
                self.address_fail = True
            elif action == "method-error":
                self.payment_fail = True
            elif action in ("disable-on-submit", "missing-bank", "forbidden", "missing-order", "stock-on-submit",
                            "concurrency", "price-on-submit", "response-dropped", "malformed-result", "wrong-success-status",
                            "empty-on-submit"):
                self.decision = action
        return {**super().control(action), "orders": list(self.orders), "orderRequests": list(self.order_requests)}

    def checkout(self, api, path, input, authorized, user, query):
        if path == "payment-methods":
            if self.payment_fail:
                self.payment_fail = False
                return api.error(500, "INTERNAL_ERROR", "Đã xảy ra lỗi hệ thống.")
            return api.success(self.methods)
        if path == "orders":
            with self.lock:
                self.order_requests.append(input)
        if not authorized:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Customer":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.")
        if path == "users/me/addresses":
            if self.address_fail:
                self.address_fail = False
                return api.error(500, "INTERNAL_ERROR", "Đã xảy ra lỗi hệ thống.")
            page = int(query.get("page", ["1"])[0])
            address = {"addressId": page + 15, "recipientName": f"Người nhận QA {page}",
                       "phoneNumber": "+84912345678", "province": "Hà Nội", "district": "Hoàn Kiếm",
                       "ward": "Hàng Bạc", "addressLine": f"{page} Đường thử", "isDefault": page == 1}
            return api.success({"items": [address], "pagination": {"page": page, "pageSize": 12,
                                                                  "totalItems": 13, "totalPages": 2}})
        time.sleep(.18)
        with self.lock:
            decision, self.decision = self.decision, None
            if decision == "forbidden":
                return api.error(403, "FORBIDDEN", "Yêu cầu đặt hàng bị từ chối.")
            if decision == "missing-order":
                return api.error(404, "NOT_FOUND", "Không tìm thấy.")
            if decision == "concurrency":
                return api.error(409, "CONCURRENCY_CONFLICT", "Giỏ hàng đã thay đổi.")
            if decision == "empty-on-submit":
                self.items.clear()
            if not self.items:
                return api.error(400, "CART_EMPTY", "Giỏ hàng đang trống.")
            if decision == "disable-on-submit":
                self.methods = METHODS[:1]
            payment = next((method for method in self.methods if method["paymentMethodId"] == input.get("paymentMethodId")), None)
            if not payment:
                return api.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                                 [{"field": "paymentMethodId", "message": "Phương thức thanh toán không khả dụng."}])
            bank = payment["methodCode"] == "BANK_TRANSFER"
            if bank and decision == "missing-bank":
                return api.error(500, "INTERNAL_ERROR", "Đã xảy ra lỗi hệ thống.")
            if decision == "stock-on-submit":
                self.stock = 1
                return api.error(409, "INSUFFICIENT_STOCK", "Một số sản phẩm không đủ số lượng.",
                                 [{"field": "variantId:5", "message": "Giày QA chỉ còn 1 sản phẩm."},
                                  {"field": "variantId:7", "message": "Một biến thể khác vừa hết hàng."}])
            if decision == "price-on-submit":
                self.price = 480000
            subtotal = self.snapshot()["subtotal"]
            order_id = len(self.orders) + 40
            now = datetime.now(timezone.utc)
            order = {"orderId": order_id, "orderCode": f"SP20261125-{order_id:04}",
                     "status": {"statusCode": "PENDING", "statusName": "Chờ xác nhận"},
                     "subtotal": subtotal, "shippingFee": 27000, "totalAmount": subtotal + 27000,
                     "createdAt": now.isoformat(),
                     "payment": {"methodCode": payment["methodCode"], "paymentStatus": "Pending",
                                 "instructions": "Ngân hàng thử QA, tài khoản 0000000000 <b data-injected>QA</b>" if bank else None,
                                 "expiresAt": (now + timedelta(hours=24)).isoformat() if bank else None}}
            self.orders.append(order)
            self.items.clear()
            if decision == "response-dropped":
                api.close_connection = True
                return
            return api.success({"orderId": order_id} if decision == "malformed-result" else order,
                               status=200 if decision == "wrong-success-status" else 201)
