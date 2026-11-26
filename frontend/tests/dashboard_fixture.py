"""Synthetic revenue and recent orders for the localhost Chromium suite."""

from threading import Event


class DashboardFixture:
    def __init__(self):
        self.requests = []
        self.behavior = "normal"
        self.release = Event()

    def control(self, action):
        if action == "dashboard-release":
            self.release.set()
        elif action.startswith("dashboard-"):
            self.behavior = action.removeprefix("dashboard-")
            if self.behavior == "pause":
                self.release.clear()
        return {"dashboardRequests": self.requests[:]}

    def dispatch(self, api, path, authenticated, user, query):
        self.requests.append({"path": path, "query": query})
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền truy cập.")
        behavior = self.behavior
        if behavior == "pause" and path.endswith("revenue") and not self.release.wait(15):
            return api.error(500, "INTERNAL_ERROR", "Lỗi máy chủ thử nghiệm.")
        if behavior == "forbidden" and path.endswith("revenue"):
            return api.error(403, "FORBIDDEN", "Không có quyền xem thống kê.")
        if behavior == "server-error":
            return api.error(500, "INTERNAL_ERROR", "Nội bộ QA.")
        if path == "admin/orders":
            items = [] if behavior == "empty" else [{
                "orderId": 204, "orderCode": "SP-QA-<b data-injected>204</b>",
                "createdAt": "2026-11-15T21:00:00Z",
                "status": {"statusCode": "CONFIRMED", "statusName": "Đã xác nhận"},
                "paymentStatus": "Pending", "paymentMethodCode": "BANK_TRANSFER", "totalAmount": 375500.25,
                "customer": {"userId": 22, "fullName": "QA riêng tư"}, "recipientName": "QA không hiển thị"
            }]
            return api.success({"items": items, "pagination": {"page": 1, "pageSize": 5,
                                "totalItems": len(items), "totalPages": int(bool(items))}})
        if behavior == "invalid":
            return api.error(400, "VALIDATION_ERROR", "Khoảng thời gian không hợp lệ.",
                             [{"field": "to", "message": "Kiểm tra ngày kết thúc."}])
        if not all(field in query for field in ("from", "to", "groupBy")):
            return api.error(400, "VALIDATION_ERROR", "Thiếu khoảng đánh giá.")
        start, end = query["from"][0], query["to"][0]
        group = query["groupBy"][0]
        periods = [start, end] if group == "day" else list(dict.fromkeys([start[:7], end[:7]]))
        series = [{"period": period, "subtotal": 150000.25 * (index + 1), "shippingFee": 30000,
                   "totalAmount": 150000.25 * (index + 1) + 30000, "orderCount": index + 1}
                  for index, period in enumerate(periods)] if behavior != "empty" else []
        total = sum(row["subtotal"] for row in series)
        count = sum(row["orderCount"] for row in series)
        result = {"from": start, "to": end, "series": series,
                  "summary": {"subtotal": total, "shippingFee": sum(row["shippingFee"] for row in series),
                              "totalAmount": sum(row["totalAmount"] for row in series), "totalOrders": count,
                              "averageSubtotal": total / count if count else 0}}
        if behavior != "no-status":
            result["ordersByStatus"] = [{"statusCode": "PENDING", "count": 8}, {"statusCode": "DELIVERED", "count": 4}] if series else []
        return api.success(result)
