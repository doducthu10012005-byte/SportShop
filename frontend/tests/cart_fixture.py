"""Synthetic Cart API shared with the HTTPS Auth browser runner."""

import threading
import time


class CartFixture:
    def __init__(self):
        self.lock = threading.Lock()
        self.items = {}
        self.calls = []
        self.error = None
        self.price = 420000
        self.stock = 9
        self.selling = True
        self.next_id = 21

    def snapshot(self):
        items = []
        for cart_item_id, (variant, quantity) in self.items.items():
            price, stock = (self.price, self.stock) if variant == 5 else (510000, 5)
            items.append({"cartItemId": cart_item_id, "variantId": variant, "productId": 1,
                          "productName": 'Giày <b data-cart-injection>QA</b>', "sku": f"QA-{variant}",
                          "sizeName": "M", "colorName": "Đen" if variant == 5 else "Xanh",
                          "imageUrl": "/uploads/products/missing.jpg", "unitPrice": price,
                          "quantity": quantity, "lineTotal": price * quantity, "availableStock": stock,
                          "isAvailable": quantity <= stock and (self.selling or variant != 5)})
        available = [item for item in items if item["isAvailable"]]
        return {"cartId": 1, "items": items, "subtotal": sum(item["lineTotal"] for item in available),
                "totalItems": sum(item["quantity"] for item in available)}

    def control(self, action):
        with self.lock:
            if action == "price-stock":
                self.price, self.stock = 480000, 2
            elif action == "stock-zero":
                self.stock = 0
            elif action == "stock-restore":
                self.stock = 9
            elif action == "stop-selling":
                self.selling = False
            elif action == "resume-selling":
                self.selling = True
            elif action in ("deny-write", "missing-write", "conflict-write"):
                self.error = action
            return {"cartCalls": self.calls[:], "cart": self.snapshot()}

    def dispatch(self, api, path, body, authorized, user):
        if path == "products/1":
            return api.success({"productId": 1, "productName": "Giày QA", "description": "",
                                "brand": {"brandId": 1, "brandName": "QA"},
                                "category": {"categoryId": 1, "categoryName": "Giày"},
                                "images": [],
                                "variants": [{"variantId": variant, "colorId": variant, "colorName": color,
                                              "sizeId": 1, "sizeName": "M", "price": price, "quantityAvailable": stock}
                                             for variant, color, price, stock in
                                             [(5, "Đen", self.price, self.stock),
                                              (6, "Đỏ", 470000, 0), (7, "Xanh", 510000, 5)]]})
        method = api.command
        with self.lock:
            self.calls.append({"path": path, "method": method, "body": body})
        if not authorized:
            return api.error(401, "TOKEN_EXPIRED", "Phiên đã hết hạn.")
        if user["role"] != "Customer":
            return api.error(403, "FORBIDDEN", "Tài khoản không có quyền sử dụng giỏ hàng.")
        if method == "PUT":
            time.sleep(.2)
        with self.lock:
            if self.error and method != "GET":
                action, self.error = self.error, None
                if action == "deny-write":
                    return api.error(403, "FORBIDDEN", "Yêu cầu bị từ chối.")
                if action == "missing-write":
                    self.items.pop(int(path.rsplit("/", 1)[-1]), None)
                    return api.error(404, "NOT_FOUND", "Dòng không còn trong giỏ.")
                self.stock = 1
            if path == "cart":
                if method == "DELETE":
                    self.items.clear()
                elif method != "GET":
                    return api.error(404, "NOT_FOUND", "Không tìm thấy.")
                return api.success(self.snapshot())
            if path == "cart/items" and method == "POST":
                variant = body.get("variantId")
                if variant not in (5, 6, 7):
                    return api.error(404, "NOT_FOUND", "Biến thể không tồn tại.")
                cart_item_id = next((key for key, value in self.items.items() if value[0] == variant), self.next_id)
                quantity = body.get("quantity", 0) + self.items.get(cart_item_id, (None, 0))[1]
            else:
                cart_item_id = int(path.rsplit("/", 1)[-1])
                if cart_item_id not in self.items:
                    return api.error(404, "NOT_FOUND", "Dòng không còn trong giỏ.")
                if method == "DELETE":
                    del self.items[cart_item_id]
                    return api.success(self.snapshot())
                variant, _ = self.items[cart_item_id]
                quantity = body.get("quantity", 0)
            if variant == 5 and not self.selling:
                return api.error(404, "NOT_FOUND", "Biến thể đã ngừng bán.")
            if not isinstance(quantity, int) or not 1 <= quantity <= 99:
                return api.error(400, "VALIDATION_ERROR", "Số lượng từ 1 đến 99.")
            stock = {5: self.stock, 6: 0, 7: 5}[variant]
            if quantity > stock:
                return api.error(409, "INSUFFICIENT_STOCK", "Tồn kho không đủ.",
                                 [{"field": f"variantId:{variant}", "message": f"Biến thể chỉ còn {stock} sản phẩm."}])
            self.items[cart_item_id] = (variant, quantity)
            if cart_item_id == self.next_id:
                self.next_id += 1
            return api.success(self.snapshot())
