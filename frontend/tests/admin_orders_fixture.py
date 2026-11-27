from copy import deepcopy
from datetime import datetime, timedelta, timezone
import socket
import secrets
import threading


LABELS = dict(PENDING="Chờ xác nhận", CONFIRMED="Đã xác nhận", SHIPPING="Đang giao", DELIVERED="Đã giao", CANCELLED="Đã hủy")
FLOW = {"PENDING": {"CONFIRMED", "CANCELLED"}, "CONFIRMED": {"SHIPPING", "CANCELLED"}, "SHIPPING": {"DELIVERED"}}
VIETNAM = timezone(timedelta(hours=7))


class AdminOrdersFixture:
    def __init__(self):
        self.requests = []
        self.mode = ""
        self.wait_write = threading.Event()
        self.wait_write.set()
        self.lock = threading.Lock()
        self.second_admin_token = secrets.token_hex(16)
        self.orders = {i: self.make_order(i) for i in range(1, 24)}

    @staticmethod
    def make_order(i):
        state = "CONFIRMED" if i in (2, 3) else "PENDING"
        method = "BANK_TRANSFER" if i in (2, 3) else "COD"
        paid = i == 3
        created = f"2026-11-{i:02}T22:00:00Z"
        status = dict(statusCode=state, statusName=LABELS[state])
        return dict(orderId=i, orderCode=f"SP202611{i:02}-0001", status=status, createdAt=created,
                    customer=dict(userId=80 + i, fullName="Khách thử " + str(i)), recipientName="Người nhận QA",
                    recipientPhone="0901234567", shippingAddress="Snapshot giao hàng <b data-injected>QA</b>",
                    note="Giao giờ hành chính", cancelReason=None, subtotal=220000.5, shippingFee=28000, totalAmount=248000.5,
                    payment=dict(methodCode=method, methodName="Chuyển khoản" if method == "BANK_TRANSFER" else "Thu hộ",
                                 paymentStatus="Paid" if paid else "Pending", amount=248000.5,
                                 receivedAmount=248000.5 if paid else None, transactionCode="QA-BANK-3" if paid else None),
                    items=[dict(orderItemId=i, variantId=1, productName="Giày snapshot <i data-injected>QA</i>",
                                sku="SNAP-1", sizeName="42", colorName="Đen", imageUrl=None, unitPrice=110000.25, quantity=2,
                                lineTotal=220000.5)],
                    statusHistory=[dict(status, changedAt=created, note="Đã nhận yêu cầu")])

    def control(self, action):
        if action.startswith("admin-order-mode-"):
            self.mode = action.removeprefix("admin-order-mode-")
        elif action == "admin-order-hold":
            self.wait_write.clear()
        elif action == "admin-order-release":
            self.wait_write.set()
        result = {"adminOrderRequests": deepcopy(self.requests)}
        if action == "admin-order-competition":
            result["competingToken"] = self.second_admin_token
        return result

    def dispatch(self, api, path, body, authenticated, user, query):
        method = api.command
        self.requests.append(dict(method=method, path=path, body=deepcopy(body), query=deepcopy(query), actor=user and user["userId"]))
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin" or self.mode == "forbidden":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        if self.mode == "server-read" and method == "GET":
            return api.error(500, "INTERNAL_ERROR", "Chi tiết nội bộ của máy chủ.")
        if path == "admin/orders" and method == "GET":
            return self.list_orders(api, query)
        parts = path.split("/")
        order = self.orders.get(int(parts[2])) if parts[2].isdigit() else None
        if not order:
            return api.error(404, "NOT_FOUND", "Không tìm thấy đơn hàng.")
        if len(parts) == 3 and method == "GET":
            with self.lock:
                return api.success(deepcopy(order))
        if parts[3:] == ["status"] and method == "PUT":
            if user["userId"] != 3 and not self.wait_write.wait(8):
                return api.error(500, "INTERNAL_ERROR", "QA timeout.")
            return self.change(api, order, body)
        return api.error(404, "NOT_FOUND", "Không tìm thấy API.")

    def list_orders(self, api, query):
        param = lambda key, default="": query.get(key, [default])[0]
        keyword = param("keyword").lower()
        state, payment = param("statusCode"), param("paymentStatus")
        start, end = param("from"), param("to")

        def includes(order):
            created = datetime.fromisoformat(order["createdAt"]).astimezone(VIETNAM).date().isoformat()
            matches_keyword = not keyword or any(keyword in order[key].lower() for key in ("orderCode", "recipientName", "recipientPhone"))
            return (
                matches_keyword
                and (not state or order["status"]["statusCode"] == state)
                and (not payment or order["payment"]["paymentStatus"] == payment)
                and (not start or start <= created)
                and (not end or created <= end)
            )

        with self.lock:
            items = [dict(orderId=o["orderId"], orderCode=o["orderCode"], status=o["status"], customer=o["customer"],
                          recipientName=o["recipientName"], totalAmount=o["totalAmount"], totalItems=2, createdAt=o["createdAt"],
                          paymentStatus=o["payment"]["paymentStatus"], paymentMethodCode=o["payment"]["methodCode"])
                     for o in self.orders.values() if includes(o)]
            items.sort(key=lambda order: (order["createdAt"], order["orderId"]), reverse=param("sort", "-createdAt") == "-createdAt")
            page, size = int(param("page", "1")), int(param("pageSize", "20"))
            offset = (page - 1) * size
            return api.success(dict(items=items[offset:offset+size], pagination=dict(page=page, pageSize=size,
                totalItems=len(items), totalPages=(len(items)+size-1)//size)))

    def change(self, api, order, body):
        target = body.get("statusCode")
        reason = (body.get("cancelReason") or "").strip()
        if self.mode == "validation" or target == "CANCELLED" and not 5 <= len(reason) <= 255:
            return api.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                             [{"field": "cancelReason", "message": "Lý do hủy phải từ 5 đến 255 ký tự."}])
        if self.mode == "conflict":
            return api.error(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi.")
        if self.mode == "server-write":
            return api.error(500, "INTERNAL_ERROR", "Chi tiết nội bộ của máy chủ.")
        with self.lock:
            state = order["status"]["statusCode"]
            payment = order["payment"]
            if target not in FLOW.get(state, set()) or (
                target in ("SHIPPING", "DELIVERED") and payment["methodCode"] == "BANK_TRANSFER" and payment["paymentStatus"] != "Paid"
            ):
                return api.error(409, "INVALID_STATUS_TRANSITION", "Không thể chuyển trạng thái đơn hàng.")
            order["status"] = dict(statusCode=target, statusName=LABELS[target])
            order["statusHistory"].append(dict(order["status"], note=reason if target == "CANCELLED" else body.get("note"),
                                               changedAt="2026-11-26T01:00:00Z"))
            if target == "CANCELLED":
                order["cancelReason"] = reason
                payment["paymentStatus"] = "RefundPending" if payment["paymentStatus"] == "Paid" else "Failed"
            if target == "DELIVERED" and payment["methodCode"] == "COD":
                payment.update(paymentStatus="Paid", receivedAmount=payment["amount"])
            if self.mode == "lost":
                api.connection.shutdown(socket.SHUT_RDWR)
                return
            return api.success(deepcopy(order))
