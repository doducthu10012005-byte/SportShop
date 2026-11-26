from copy import deepcopy
import socket
import threading


class AdminCatalogFixture:
    def __init__(self):
        self.requests = []
        self.mode = ""
        self.ready = threading.Event()
        self.ready.set()
        self.tables = {
            "categories": [
                dict(categoryId=1, categoryName="Giày", parentCategoryId=None, description=None, isActive=True),
                dict(categoryId=2, categoryName="Giày chạy", parentCategoryId=1, description="Mô tả", isActive=True),
            ],
            "brands": [
                dict(brandId=1, brandName="Runner", logoUrl=None, description="Thiết bị", isActive=True),
                dict(brandId=2, brandName="Nhãn cũ", logoUrl=None, description=None, isActive=False),
            ],
            "products": [
                dict(productId=i, productName=f"Giày Sport {i:02}" if i != 22 else "<img src=x onerror=alert(1)>",
                     categoryId=2, brandId=1, description="Chất liệu thoáng khí", isActive=i % 2 == 0,
                     createdAt=f"2026-11-{i:02}T10:00:00Z", variants=[
                         dict(variantId=i, sizeId=1, sizeName="M", colorId=1, colorName="Đen",
                              sku=f"SP-{i}", price=250000, isActive=True)
                     ]) for i in range(1, 23)
            ],
        }

    def control(self, action):
        if action == "catalog-hold":
            self.ready.clear()
        elif action == "catalog-release":
            self.ready.set()
        elif action.startswith("catalog-mode-"):
            self.mode = action.removeprefix("catalog-mode-")
        return {"catalogRequests": list(self.requests)}

    def dispatch(self, api, path, payload, authenticated, user, query):
        method = api.command
        self.requests.append(dict(method=method, path=path, body=deepcopy(payload), query=query))
        if path == "sizes":
            return api.success([{"sizeId": 1, "sizeName": "M"}, {"sizeId": 2, "sizeName": "L"}])
        if path == "colors":
            return api.success([{"colorId": 1, "colorName": "Đen"}, {"colorId": 2, "colorName": "Trắng"}])
        if not authenticated:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Admin":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        parts = path.split("/")
        resource = parts[1]
        if resource == "products" and not self.ready.wait(12):
            return api.error(500, "INTERNAL_ERROR", "QA timeout")
        table = self.tables[resource]
        name = resource.removesuffix("s") if resource != "categories" else "category"
        id_field, name_field = f"{name}Id", f"{name}Name"
        row = next((item for item in table if len(parts) == 3 and str(item[id_field]) == parts[2]), None)
        if self.mode == "forbidden" and resource == "products":
            return api.error(403, "FORBIDDEN", "Bạn không có quyền.")
        if self.mode == "missing" and len(parts) == 3:
            return api.error(404, "NOT_FOUND", "Không tìm thấy dữ liệu.")
        if len(parts) == 3 and not row:
            return api.error(404, "NOT_FOUND", "Không tìm thấy dữ liệu.")

        def view(item, listing=False):
            extra = {}
            if resource == "products":
                extra = {field: next(ref[field] for ref in self.tables[collection] if ref[key] == item[key])
                         for field, key, collection in [("categoryName", "categoryId", "categories"), ("brandName", "brandId", "brands")]}
            exclusions = {"description", "variants"} if resource == "products" and listing else set()
            return {**{key: value for key, value in item.items() if key not in exclusions}, **extra}

        if method == "GET":
            if row:
                return api.success(view(row))
            items = table
            if resource == "products":
                search = query.get("keyword", [""])[0].lower()
                items = [p for p in items if search in p[name_field].lower() or any(search in v["sku"].lower() for v in p["variants"])]
                for key in ("isActive", "categoryId", "brandId"):
                    if key in query:
                        items = [p for p in items if str(p[key]).lower() == query[key][0]]
            if resource == "categories":
                return api.success([view(item) for item in items])
            sort = query.get("sort", [name_field])[0]
            items = sorted(items, key=lambda item: item[sort.lstrip("-")], reverse=sort.startswith("-"))
            page, size = int(query.get("page", [1])[0]), int(query.get("pageSize", [20])[0])
            start = (page - 1) * size
            return api.success(dict(items=[view(item, listing=True) for item in items[start:start + size]],
                                    pagination=dict(page=page, pageSize=size, totalItems=len(items), totalPages=(len(items) + size - 1) // size)))
        if self.mode == "validation":
            return api.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                             [{"field": "Variants[0].Price", "message": "Giá không hợp lệ."}])
        if self.mode == "conflict":
            return api.error(409, "CONCURRENCY_CONFLICT", "Dữ liệu vừa thay đổi.")
        if self.mode == "duplicate-sku":
            return api.error(409, "DUPLICATE_SKU", "SKU đã được sử dụng.")
        if self.mode == "failure":
            return api.error(500, "INTERNAL_ERROR", "Internal details")
        if method == "DELETE":
            used_category = resource == "categories" and any(item["parentCategoryId"] == row[id_field] for item in table)
            used_brand = resource == "brands" and any(
                item["isActive"] and item["brandId"] == row[id_field] for item in self.tables["products"]
            )
            if used_category or used_brand:
                return api.error(409, "IN_USE", "Dữ liệu đang được sử dụng.")
            row["isActive"] = False
            return api.success()
        if any(item is not row and item[name_field].casefold() == payload[name_field].casefold() for item in table):
            return api.error(409, "DUPLICATE_NAME", "Tên đã được sử dụng.")
        if method == "PUT":
            row.update(payload)
            return api.success(view(row))
        row = {id_field: max(item[id_field] for item in table) + 1, **payload}
        if resource == "products":
            row["createdAt"] = "2026-11-26T10:00:00Z"
            for index, variant in enumerate(row["variants"]):
                variant.update(variantId=100 + index, sizeName="M", colorName="Đen", isActive=True)
        table.append(row)
        if self.mode == "lost":
            api.connection.shutdown(socket.SHUT_RDWR)
            return
        return api.success(view(row), status=201)
