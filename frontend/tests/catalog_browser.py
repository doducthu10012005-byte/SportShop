"""Run Chromium catalog checks with a local HTTPS API fixture."""

import argparse
import json
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPSServer
from pathlib import Path
import os
import subprocess
import tempfile
import threading
import time
from urllib.parse import parse_qs, urlsplit


FRONTEND = Path(__file__).resolve().parents[1]
COLORS = [{"colorId": 1, "colorName": "Đen", "hexCode": "#25222B"},
          {"colorId": 2, "colorName": "Trắng", "hexCode": "#FFFFFF"}]
SIZES = [{"sizeId": 3, "sizeName": "M"}, {"sizeId": 4, "sizeName": "L"}]
BRANDS = [{"brandId": 1, "brandName": "Sport", "logoUrl": None},
          {"brandId": 2, "brandName": "Run", "logoUrl": None}]
CATEGORIES = [{"categoryId": 10, "categoryName": "Giày", "slug": "giay", "children": [
    {"categoryId": 11, "categoryName": "Chạy bộ", "slug": "chay-bo", "children": []}]},
    {"categoryId": 20, "categoryName": "Trang phục", "slug": "trang-phuc", "children": []}]


def variant(variant_id, size, color, price, quantity):
    return {"variantId": variant_id, "sku": f"SPORT-{variant_id}", "sizeId": SIZES[size]["sizeId"],
            "sizeName": SIZES[size]["sizeName"], "colorId": COLORS[color]["colorId"],
            "colorName": COLORS[color]["colorName"], "price": price, "quantityAvailable": quantity}


def detail(product_id):
    variants = ([variant(101, 0, 0, 420000, 7), variant(102, 1, 0, 450000, 0), variant(103, 0, 1, 480000, 2)]
                if product_id == 1 else [variant(product_id * 100, 0, 0, 500000 + product_id * 10000,
                                                0 if product_id == 3 else 3)])
    image = {3: "/uploads/products/missing.jpg", 4: "javascript:window.fixtureInjected=true"}.get(
        product_id, "/uploads/products/sample.jpg")
    images = [{"imageId": 1, "imageUrl": image, "colorId": None, "isPrimary": True}]
    if product_id == 1:
        images.append({"imageId": 2, "imageUrl": "/uploads/products/white.jpg", "colorId": 2, "isPrimary": False})
    return {"productId": product_id, "productName": 'Giày <img data-injected src=x>' if product_id == 2 else f"Giày chạy {product_id:02d}",
            "slug": f"giay-{product_id}", "description": "Thoáng khí\nPhù hợp chạy bộ.",
            "brand": {key: value for key, value in BRANDS[int(product_id != 1)].items() if key != "logoUrl"},
            "category": {"categoryId": 11, "categoryName": "Chạy bộ"} if product_id <= 7 else
                        {"categoryId": 20, "categoryName": "Trang phục"},
            "images": images, "variants": variants}


def card(product):
    variants = product["variants"]
    return {"productId": product["productId"], "productName": product["productName"], "slug": product["slug"],
            "brandName": product["brand"]["brandName"], "categoryName": product["category"]["categoryName"],
            "primaryImageUrl": product["images"][0]["imageUrl"], "minPrice": min(v["price"] for v in variants),
            "maxPrice": max(v["price"] for v in variants), "inStock": any(v["quantityAvailable"] for v in variants),
            "colors": [color for color in COLORS if any(v["colorId"] == color["colorId"] for v in variants)]}


def listing(query):
    def first(key, default=""):
        return query.get(key, [default])[0]

    def matches(product):
        if first("keyword").casefold() not in product["productName"].casefold():
            return False
        if first("categoryId") and product["category"]["categoryId"] not in (
                {10, 11} if first("categoryId") == "10" else {int(first("categoryId"))}):
            return False
        if "brandId" in query and str(product["brand"]["brandId"]) not in query["brandId"]:
            return False
        return any((not query.get("sizeId") or str(v["sizeId"]) in query["sizeId"]) and
                   (not query.get("colorId") or str(v["colorId"]) in query["colorId"]) and
                   float(first("minPrice", 0)) <= v["price"] <= float(first("maxPrice", "inf")) and
                   (first("inStock") != "true" or v["quantityAvailable"] > 0) for v in product["variants"])

    items = [card(product) for product in map(detail, range(1, 16)) if matches(product)]
    sort = first("sort", "newest")
    field = {"price": "minPrice", "-price": "minPrice", "name": "productName", "newest": "productId"}[sort]
    items.sort(key=lambda p: p[field], reverse=sort in {"-price", "newest"})
    page, size = int(first("page", 1)), int(first("pageSize", 12))
    start = (page - 1) * size
    return {"items": items[start:start + size], "pagination": {"page": page, "pageSize": size, "totalItems": len(items),
                                                              "totalPages": (len(items) + size - 1) // size}}


def run(args, temp_path):
    result = None
    completed = threading.Event()
    requests = []
    failures = {"products": 0, "brands": 0}
    tls = {"certfile": args.cert, "keyfile": args.key}

    class Handler(SimpleHTTPRequestHandler):
        def __init__(self, *values, **options):
            super().__init__(*values, directory=FRONTEND, **options)

        def log_message(self, *_):
            pass

        def send(self, status, payload, content_type):
            try:
                self.send_response(status)
                if self.headers.get("Origin") == web_origin:
                    self.send_header("Access-Control-Allow-Origin", web_origin)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            except ConnectionError:
                pass

        def json(self, payload, status=200):
            self.send(status, json.dumps(payload).encode(), "application/json")

    class Api(Handler):
        def do_GET(self):
            parsed = urlsplit(self.path)
            if parsed.path in {"/uploads/products/sample.jpg", "/uploads/products/white.jpg"}:
                return self.send(200, (FRONTEND / "assets" / "hero.jpg").read_bytes(), "image/jpeg")
            route = parsed.path.removeprefix("/api/v1/")
            query = parse_qs(parsed.query)
            requests.append({"route": route, "query": query,
                             "authenticated": bool(self.headers.get("Authorization") or self.headers.get("Cookie"))})
            if failures.get(route):
                failures[route] -= 1
                return self.json({"success": False, "errorCode": "INTERNAL_ERROR", "message": "Fixture failure"}, 500)
            lookups = {"categories": CATEGORIES, "brands": BRANDS, "sizes": SIZES, "colors": COLORS}
            if route == "products":
                if query.get("keyword") == ["Chạy chậm"]:
                    time.sleep(.4)
                    query = {**query, "keyword": ["Giày chạy"]}
                data = listing(query)
            elif route in lookups:
                data = lookups[route]
            elif route.startswith("products/") and route[9:].isdigit() and 1 <= int(route[9:]) <= 15:
                data = detail(int(route[9:]))
            else:
                return self.json({"success": False, "errorCode": "NOT_FOUND", "message": "Không tìm thấy."}, 404)
            return self.json({"success": True, "message": "OK", "data": data})

    class Web(Handler):
        def do_GET(self):
            if self.path == "/js/config.js":
                return self.send(200, f'export const API_BASE_URL = "{api_origin}/api/v1";'.encode(), "text/javascript")
            if self.path == "/__catalog_qa":
                return self.send(200, b'<!doctype html><meta charset="utf-8"><body>'
                                      b'<script type="module" src="/tests/catalog_browser.js"></script>', "text/html")
            if self.path == "/__requests":
                return self.json(requests)
            return super().do_GET()

        def do_POST(self):
            nonlocal result
            if self.path in {"/__fail/products", "/__fail/brands"}:
                failures[self.path.rsplit("/", 1)[-1]] = 1
                return self.send(204, b"", "text/plain")
            if self.path != "/__catalog_qa" or self.headers.get("Origin") != web_origin:
                return self.send_error(404)
            result = self.rfile.read(int(self.headers.get("Content-Length", "0"))).decode("utf-8")
            self.send(204, b"", "text/plain")
            completed.set()

    with ThreadingHTTPSServer(("127.0.0.1", 0), Api, **tls) as api, \
         ThreadingHTTPSServer(("127.0.0.1", 0), Web, **tls) as web:
        api_origin = f"https://localhost:{api.server_port}"
        web_origin = f"https://localhost:{web.server_port}"
        servers = (api, web)
        threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in servers]
        for thread in threads:
            thread.start()
        try:
            with subprocess.Popen([args.chrome, "--headless=new", "--no-first-run", "--disable-extensions",
                                   "--disable-background-networking", f"--user-data-dir={temp_path}",
                                   web_origin + "/__catalog_qa"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as chrome:
                try:
                    completed.wait(55)
                finally:
                    chrome.terminate()
                    chrome.wait(timeout=10)
        finally:
            for server in servers:
                server.shutdown()
            for thread in threads:
                thread.join()
    print(result or "FAIL: no Chromium catalog report")
    return 0 if result and result.startswith("PASS ") else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    parser.add_argument("--chrome", default=os.path.join(os.environ.get("ProgramFiles", ""), "Google", "Chrome",
                                                       "Application", "chrome.exe"))
    with tempfile.TemporaryDirectory(prefix="SportShop-CatalogChromium-", ignore_cleanup_errors=True) as profile:
        raise SystemExit(run(parser.parse_args(), profile))
