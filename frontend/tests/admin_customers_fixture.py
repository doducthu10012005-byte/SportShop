from copy import deepcopy
import socket
import threading


class AdminCustomersFixture:
    def __init__(self):
        self.mode = ""
        self.requests = []
        self.ready = threading.Event()
        self.ready.set()
        self.rows = {
            i: dict(userId=i, fullName=f"Khách QA {i:02}" if i != 22 else "<b data-injected>QA</b>",
                    email=f"customer-{i:02}@example.test", phoneNumber=f"09012345{i:02}" if i % 2 else None,
                    isActive=i % 2 != 0, createdAt=f"2026-11-{i:02}T22:00:00Z")
            for i in range(1, 24)
        }

    def control(self, action):
        if action.startswith("customers-mode-"):
            self.mode = action.removeprefix("customers-mode-")
        elif action == "customers-hold":
            self.ready.clear()
        elif action == "customers-release":
            self.ready.set()
        return {"customerRequests": deepcopy(self.requests)}

    def dispatch(self, api, path, body, authenticated, user, query):
        method = api.command
        self.requests.append(dict(method=method, path=path, body=deepcopy(body), query=deepcopy(query)))
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin" or self.mode == "forbidden":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        if self.mode == "server":
            return api.error(500, "INTERNAL_ERROR", "Nội bộ QA")
        if method == "GET" and self.mode == "malformed":
            return api.success(dict(items=[], pagination=None))
        if path == "admin/customers" and method == "GET":
            return self.list(api, query)
        parts = path.split("/")
        customer = self.rows.get(int(parts[2])) if len(parts) > 2 and parts[2].isdigit() else None
        if customer is None:
            return api.error(404, "NOT_FOUND", "Không tìm thấy khách hàng.")
        if method == "GET" and len(parts) == 3:
            return api.success(dict(customer, dateOfBirth="1992-11-03", gender="Female",
                                    lastLoginAt="2026-11-25T04:30:00Z", totalOrders=7, totalSpent=250000.75,
                                    passwordHash="fixture-ignored", tokenVersion=43))
        if method == "PUT" and parts[3:] == ["status"]:
            if not self.ready.wait(8):
                return api.error(500, "INTERNAL_ERROR", "QA timeout")
            if self.mode == "invalid" or set(body) != {"isActive"} or type(body["isActive"]) is not bool:
                return api.error(400, "VALIDATION_ERROR", "Trạng thái không hợp lệ.",
                                 [{"field":"isActive", "message":"Giá trị không hợp lệ."}])
            if self.mode == "conflict":
                customer["isActive"] = body["isActive"]
                return api.error(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi.")
            customer["isActive"] = body["isActive"]
            if self.mode == "lost":
                api.connection.shutdown(socket.SHUT_RDWR)
                return
            return api.success({"userId":customer["userId"], "isActive":customer["isActive"]})
        return api.error(404, "NOT_FOUND", "Không tìm thấy API.")

    def list(self, api, query):
        def value(key, default):
            return query.get(key, [default])[0]

        keyword = value("keyword", "").casefold()
        state = value("isActive", "")
        ordering = value("sort", "-createdAt")
        key = ordering.lstrip("-")
        if key not in ("userId", "fullName", "email", "createdAt", "isActive"):
            return api.error(400, "VALIDATION_ERROR", "Sắp xếp không hợp lệ.")
        items = [row for row in self.rows.values()
                 if (not keyword or any(keyword in (row[field] or "").casefold() for field in ("fullName", "email", "phoneNumber")))
                 and (not state or row["isActive"] == (state == "true"))]
        items.sort(key=lambda row: row[key], reverse=ordering.startswith("-"))
        page, size = int(value("page", 1)), int(value("pageSize", 20))
        start = (page - 1) * size
        return api.success(dict(items=items[start:start+size], pagination=dict(page=page, pageSize=size,
            totalItems=len(items), totalPages=(len(items)+size-1)//size)))
