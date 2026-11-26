from copy import deepcopy
from email import policy
from email.parser import BytesParser
import socket
import threading


class AdminStockFixture:
    def __init__(self):
        self.requests = []
        self.mode = ""
        self.writes = threading.Event()
        self.writes.set()
        self.sizes = [{"sizeId": 1, "sizeName": "M"}, {"sizeId": 2, "sizeName": "L"}]
        self.colors = [{"colorId": 1, "colorName": "Xanh"}, {"colorId": 2, "colorName": "Trắng"}]
        self.variants = [
            {"variantId": 1, "sizeId": 1, "sizeName": "M", "colorId": 1, "colorName": "Xanh",
             "sku": "QA-BASE", "price": 100000, "quantityOnHand": 4, "lowStockThreshold": 5, "isActive": True}
        ]
        self.images = [
            {"imageId": 1, "colorId": None, "imageUrl": "/uploads/products/1/missing.png", "isPrimary": True, "sortOrder": 0}
        ]
        self.transactions = [dict(transactionId=i, transactionType="OrderRestock", quantityChange=1, quantityAfter=4,
                                  orderItemId=i, note=None, createdBy=None, createdAt="2026-11-26T02:30:00Z")
                             for i in range(1, 22)]

    def control(self, action):
        if action.startswith("stock-mode-"):
            self.mode = action.removeprefix("stock-mode-")
        elif action == "stock-hold":
            self.writes.clear()
        elif action == "stock-release":
            self.writes.set()
        elif action == "stock-full":
            self.images.extend({**self.images[0], "imageId": i, "isPrimary": False} for i in range(20, 30 - len(self.images)))
        return {"stockRequests": deepcopy(self.requests)}

    def dispatch(self, api, path, body, raw, authenticated, user, query):
        method = api.command
        if raw and api.headers.get("Content-Type", "").startswith("multipart/form-data"):
            prefix = f'Content-Type: {api.headers["Content-Type"]}\r\n\r\n'.encode()
            parts = BytesParser(policy=policy.default).parsebytes(prefix + raw)
            body = {}
            for part in parts.iter_parts():
                key = part.get_param("name", header="Content-Disposition")
                payload = part.get_payload(decode=True)
                body[key] = ({"fileName": part.get_filename(), "isPng": payload.startswith(b"\x89PNG\r\n\x1a\n")}
                             if part.get_filename() else payload.decode())
        self.requests.append(dict(method=method, path=path, body=deepcopy(body), query=query))
        if path in ("sizes", "colors"):
            return api.success(self.sizes if path == "sizes" else self.colors)
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin" or self.mode == "forbidden":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        if method != "GET" and not self.writes.wait(8):
            return api.error(500, "INTERNAL_ERROR", "QA timeout")
        if self.mode == "missing" or path.startswith("admin/products/") and path.split("/")[2] != "1":
            return api.error(404, "NOT_FOUND", "Không tìm thấy dữ liệu.")
        if path == "admin/products/1":
            return api.success(dict(productId=1, productName="Giày thử", variants=self.variants, images=self.images))
        if path == "admin/inventory":
            return self.inventory(api, query)
        if path == "admin/inventory/1/transactions":
            return self.page(api, list(reversed(self.transactions)), query)
        if path == "admin/inventory/1/adjustments":
            return self.adjust(api, body)
        if path.startswith("admin/products/1/variants"):
            return self.variant(api, path, body)
        if path.startswith("admin/products/1/images"):
            return self.photo(api, path, body)
        return api.error(404, "NOT_FOUND", "Không tìm thấy dữ liệu.")

    def variant(self, api, path, body):
        method = api.command
        row = next((row for row in self.variants if str(row["variantId"]) == path.split("/")[-1]), None)
        if method == "DELETE" and row:
            row["isActive"] = False
            return api.success()
        if any(v is not row and v["sku"].lower() == body["sku"].lower() for v in self.variants):
            return api.error(409, "DUPLICATE_SKU", "SKU đã được sử dụng.")
        if self.mode == "duplicate-variant":
            return api.error(409, "DUPLICATE_VARIANT", "Tổ hợp kích thước và màu đã tồn tại.")
        if self.mode == "validation":
            return api.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                             [{"field": "isActive", "message": "Kích thước hoặc màu đã ngừng dùng."}])
        if method == "PUT" and row:
            row.update(body)
            return api.success(row)
        size = next(size for size in self.sizes if size["sizeId"] == body["sizeId"])
        color = next(color for color in self.colors if color["colorId"] == body["colorId"])
        row = dict(variantId=len(self.variants) + 1, **body, quantityOnHand=body["initialStock"], lowStockThreshold=5,
                   sizeName=size["sizeName"], colorName=color["colorName"])
        self.variants.append(row)
        return api.success(row, status=201)

    def inventory(self, api, query):
        keyword = query.get("keyword", [""])[0].casefold()
        rows = [{**row, "productName": "Giày thử", "isLowStock": row["quantityOnHand"] <= row["lowStockThreshold"]}
                for row in self.variants if keyword in row["sku"].casefold() or keyword in "giày thử"]
        if query.get("lowStock") == ["true"]:
            rows = [row for row in rows if row["isLowStock"]]
        if query.get("outOfStock") == ["true"]:
            rows = [row for row in rows if row["quantityOnHand"] == 0]
        return self.page(api, rows, query)

    @staticmethod
    def page(api, rows, query):
        page = int(query.get("page", ["1"])[0])
        size = int(query.get("pageSize", ["20"])[0])
        start = (page - 1) * size
        return api.success(dict(items=rows[start:start+size], pagination=dict(page=page, pageSize=size,
                                totalItems=len(rows), totalPages=(len(rows) + size - 1) // size)))

    def adjust(self, api, body):
        if self.mode == "conflict":
            return api.error(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi.")
        variant = self.variants[0]
        current = variant["quantityOnHand"]
        quantity = body["quantity"]
        target = current + quantity if body["type"] == "Import" else quantity
        if target == current or target > 2147483647:
            return api.error(400, "VALIDATION_ERROR", "Số lượng không hợp lệ.",
                             [{"field": "quantity", "message": "Tồn kho mới phải khác tồn kho cũ."}])
        variant["quantityOnHand"] = target
        self.transactions.append(dict(transactionId=len(self.transactions) + 1, transactionType=body["type"],
                                      quantityChange=target-current, quantityAfter=target, orderItemId=None,
                                      createdBy=2, note=body["note"], createdAt="2026-11-26T02:31:00Z"))
        if self.mode == "lost":
            api.connection.shutdown(socket.SHUT_RDWR)
            return
        return api.success(dict(variantId=1, quantityOnHand=target))

    def photo(self, api, path, body):
        if api.command == "DELETE":
            self.images = [img for img in self.images if str(img["imageId"]) != path.split("/")[-1]]
            return api.success()
        statuses = {"too-large": (413, "PAYLOAD_TOO_LARGE", "Ảnh vượt quá 2 MB."),
                    "unsupported": (415, "UNSUPPORTED_MEDIA_TYPE", "Sai định dạng ảnh."),
                    "rate": (429, "TOO_MANY_REQUESTS", "Bạn đã gửi quá nhiều yêu cầu.")}
        if self.mode in statuses:
            return api.error(*statuses[self.mode])
        if not body.get("ImageFile", {}).get("isPng"):
            return api.error(400, "VALIDATION_ERROR", "Tệp ảnh không hợp lệ.")
        color = int(body["colorId"]) if "colorId" in body else None
        primary = body["isPrimary"] == "true"
        if primary:
            for img in self.images:
                img["isPrimary"] = False
        image_id = max((img["imageId"] for img in self.images), default=0) + 1
        img = dict(imageId=image_id, productId=1, colorId=color, imageUrl="/uploads/products/1/fixture.png",
                   isPrimary=primary, sortOrder=len(self.images))
        self.images.append(img)
        return api.success(img, status=201)
