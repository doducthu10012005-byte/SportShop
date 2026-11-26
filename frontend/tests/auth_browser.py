"""Run the Auth browser tests against two isolated localhost HTTPS origins."""

import argparse
import json
import mimetypes
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import threading
from http.cookies import SimpleCookie
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPSServer
from urllib.parse import parse_qs, urlsplit


FRONTEND = Path(__file__).resolve().parents[1]


class Fixture:
    def __init__(self):
        self.calls = {}
        self.user = None
        self.tokens = set()
        self.cookie = None
        self.profile_error = None
        self.pause = threading.Event()
        self.pause.set()
        self.concurrent = 0
        self.max_concurrent = 0
        self.mutex = threading.Lock()

    def session(self):
        token = "fake-access-" + secrets.token_hex(10)
        self.tokens.add("Bearer " + token)
        self.cookie = "fake-refresh-" + secrets.token_hex(32)
        cookie = f"refreshToken={self.cookie}; Max-Age=604800; Path=/api/v1/auth; HttpOnly; Secure; SameSite=Strict"
        return (
            {"accessToken": token, "accessTokenExpiresAt": "2099-01-01T00:00:00Z", "user": self.user},
            cookie,
        )


def run(args, temp_path):
    fixture = Fixture()
    cart = None
    dashboard = None
    catalog_admin = None
    admin_stock = None
    test_module = "auth_browser.js"
    if args.dashboard:
        from dashboard_fixture import DashboardFixture
        dashboard = DashboardFixture()
        test_module = "dashboard_browser.js"
    elif args.admin_catalog:
        from admin_catalog_fixture import AdminCatalogFixture
        catalog_admin = AdminCatalogFixture()
        test_module = "admin_catalog_browser.js"
    elif args.admin_stock:
        from admin_stock_fixture import AdminStockFixture
        admin_stock = AdminStockFixture()
        test_module = "admin_stock_browser.js"
    elif args.orders:
        from orders_fixture import OrdersFixture
        cart = OrdersFixture()
        test_module = "orders_browser.js"
    elif args.checkout:
        from checkout_fixture import CheckoutFixture
        cart = CheckoutFixture()
        test_module = "checkout_browser.js"
    elif args.cart:
        from cart_fixture import CartFixture
        cart = CartFixture()
        test_module = "cart_browser.js"
    tls = {"certfile": args.cert, "keyfile": args.key}
    completed = threading.Event()
    report = None

    class Api(SimpleHTTPRequestHandler):
        def log_message(self, fmt, *values):
            pass

        def end_headers(self):
            if self.headers.get("Origin") == front_origin:
                self.send_header("Access-Control-Allow-Origin", front_origin)
                self.send_header("Access-Control-Allow-Credentials", "true")
                self.send_header("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS")
                self.send_header("Access-Control-Allow-Headers", "accept, authorization, content-type, x-requested-with")
                self.send_header("Vary", "Origin")
            super().end_headers()

        def do_OPTIONS(self):
            self.send_response(204)
            self.end_headers()

        def reply(self, status, envelope, cookie=None):
            body = json.dumps(envelope).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            if cookie:
                self.send_header("Set-Cookie", cookie)
            try:
                self.end_headers()
                self.wfile.write(body)
            except ConnectionError:
                pass

        def success(self, data=None, cookie=None, status=200):
            self.reply(status, {"success": True, "message": "OK", "data": data}, cookie)

        def error(self, status, code, message, fields=None):
            payload = {"success": False, "errorCode": code, "message": message, "traceId": "qa"}
            if fields:
                payload["errors"] = fields
            self.reply(status, payload)

        def dispatch(self):
            path = urlsplit(self.path).path.removeprefix("/api/v1/")
            size = int(self.headers.get("Content-Length", 0))
            raw = self.rfile.read(size) if size else b""
            content_type = self.headers.get("Content-Type", "")
            body = json.loads(raw) if raw and not content_type.startswith("multipart/form-data") else {}
            cookie = SimpleCookie(self.headers.get("Cookie", "")).get("refreshToken")
            incoming_cookie = cookie.value if cookie else None
            bearer = self.headers.get("Authorization")
            with fixture.mutex:
                fixture.calls[path] = fixture.calls.get(path, 0) + 1
            if dashboard and path in ("admin/statistics/revenue", "admin/orders"):
                return dashboard.dispatch(self, path, bearer in fixture.tokens, fixture.user, parse_qs(urlsplit(self.path).query))
            if admin_stock and (path in ("sizes", "colors") or path.startswith(("admin/products/", "admin/inventory"))):
                return admin_stock.dispatch(self, path, body, raw, bearer in fixture.tokens, fixture.user,
                                            parse_qs(urlsplit(self.path).query))
            if catalog_admin and (path.split("/")[0] in ("sizes", "colors") or
                                  path.split("/")[:2] in (["admin", "products"], ["admin", "categories"], ["admin", "brands"])):
                return catalog_admin.dispatch(self, path, body, bearer in fixture.tokens, fixture.user,
                                              parse_qs(urlsplit(self.path).query))
            if args.orders and (path.startswith("orders/") or (path == "orders" and self.command == "GET")):
                return cart.history(self, path, body, bearer in fixture.tokens, fixture.user, parse_qs(urlsplit(self.path).query))
            if (args.checkout or args.orders) and path in ("orders", "payment-methods", "users/me/addresses"):
                return cart.checkout(self, path, body, bearer in fixture.tokens, fixture.user, parse_qs(urlsplit(self.path).query))
            if cart and (path == "cart" or path.startswith(("cart/", "products/"))):
                return cart.dispatch(self, path, body, bearer in fixture.tokens, fixture.user)

            if path in ("auth/refresh", "auth/logout"):
                if self.headers.get("Origin") != front_origin or self.headers.get("X-Requested-With") != "XMLHttpRequest":
                    return self.error(403, "FORBIDDEN", "Origin hoặc header không hợp lệ.")
                if self.headers.get("Content-Type") != "application/json" or body:
                    return self.error(415, "UNSUPPORTED_MEDIA_TYPE", "Yêu cầu JSON rỗng")
            if path == "auth/register":
                if self.headers.get("X-Requested-With"):
                    return self.error(403, "FORBIDDEN", "Header không hợp lệ")
                if body["email"] == "invalid@example.test":
                    return self.error(400, "VALIDATION_ERROR", "Dữ liệu không hợp lệ.",
                                      [{"field": "email", "message": "Email không hợp lệ."}])
                return self.success({"userId": 1, "email": body["email"], "fullName": body["fullName"]}, status=201)
            if path == "auth/login":
                if self.headers.get("X-Requested-With"):
                    return self.error(403, "FORBIDDEN", "Header không hợp lệ")
                name = body["email"].split("@")[0]
                if name in ("locked", "inactive"):
                    code = "ACCOUNT_LOCKED" if name == "locked" else "ACCOUNT_INACTIVE"
                    return self.error(403, code, "Tài khoản chưa hoạt động.")
                if body["password"] != "Test-Password123":
                    return self.error(401, "INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.")
                fixture.user = {"userId": 3 if name == "admin-other" else 2 if name in ("admin", "forced") else 1,
                                "role": "Admin" if name in ("admin", "admin-other", "forced") else "Customer",
                                "mustChangePassword": name == "forced", "email": body["email"],
                                "fullName": "Người dùng thử", "phoneNumber": "0912345678"}
                return self.success(*fixture.session())
            if path == "auth/refresh":
                with fixture.mutex:
                    fixture.concurrent += 1
                    fixture.max_concurrent = max(fixture.max_concurrent, fixture.concurrent)
                resumed = fixture.pause.wait(15)
                with fixture.mutex:
                    fixture.concurrent -= 1
                    result = fixture.session() if resumed and incoming_cookie and incoming_cookie == fixture.cookie else None
                if not resumed:
                    return self.error(500, "INTERNAL_ERROR", "QA timeout")
                if result:
                    return self.success(*result)
                return self.error(401, "UNAUTHORIZED", "Không có phiên.")
            if path not in ("auth/logout", "users/me", "users/me/password"):
                return self.error(404, "NOT_FOUND", "Không tìm thấy.")
            if bearer not in fixture.tokens:
                return self.error(401, "TOKEN_EXPIRED", "Mã phiên đã hết hạn.")
            if path == "auth/logout":
                if incoming_cookie == fixture.cookie:
                    fixture.cookie = None
                return self.success(cookie="refreshToken=; Max-Age=0; Path=/api/v1/auth; HttpOnly; Secure; SameSite=Strict")
            if path == "users/me":
                if fixture.profile_error:
                    return self.error(*fixture.profile_error)
                return self.success({k: v for k, v in fixture.user.items() if k != "mustChangePassword"})
            if body["currentPassword"] != "Test-Password123":
                return self.error(400, "VALIDATION_ERROR", "Mật khẩu hiện tại không đúng.",
                                  [{"field": "currentPassword", "message": "Mật khẩu hiện tại không đúng."}])
            fixture.cookie = None
            fixture.tokens.clear()
            return self.success()

        do_GET = do_POST = do_PUT = do_DELETE = dispatch

    class Front(SimpleHTTPRequestHandler):
        def __init__(self, *values, **options):
            super().__init__(*values, directory=FRONTEND, **options)

        def log_message(self, fmt, *values):
            pass

        def send_text(self, content, content_type):
            body = content.encode()
            self.send_response(200)
            self.send_header("Content-Type", content_type + "; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            parsed = urlsplit(self.path)
            if parsed.path == "/js/config.js":
                return self.send_text(f'export const API_BASE_URL = "{api_origin}/api/v1";', "text/javascript")
            if parsed.path == "/__auth_qa":
                return self.send_text('<!doctype html><meta charset="utf-8"><pre id="qa">RUNNING</pre>'
                                      f'<script type="module" src="/tests/{test_module}"></script>', "text/html")
            if parsed.path == "/api/v1/auth/__inspect":
                return self.send_text('<!doctype html><meta charset="utf-8"><title>Cookie QA</title>', "text/html")
            if parsed.path == "/__fixture":
                action = parse_qs(parsed.query).get("action", [""])[0]
                with fixture.mutex:
                    if action == "expire":
                        fixture.tokens.clear()
                    elif action == "pause":
                        fixture.pause.clear()
                    elif action == "resume":
                        fixture.pause.set()
                    elif action == "force":
                        fixture.profile_error = (403, "PASSWORD_CHANGE_REQUIRED", "Bạn cần đổi mật khẩu.")
                    elif action == "deny":
                        fixture.profile_error = (403, "FORBIDDEN", "Không có quyền truy cập.")
                    elif action == "revoke":
                        fixture.profile_error = (401, "TOKEN_REVOKED", "Phiên đã bị thu hồi.")
                    elif action == "normal":
                        fixture.profile_error = None
                    elif action == "revoke-session":
                        fixture.cookie = None
                        fixture.tokens.clear()
                    result = {"calls": fixture.calls, "hasCookie": fixture.cookie is not None,
                              "concurrent": fixture.concurrent, "maxConcurrent": fixture.max_concurrent}
                    if cart:
                        result.update(cart.control(action))
                    if dashboard:
                        result.update(dashboard.control(action))
                    if catalog_admin:
                        result.update(catalog_admin.control(action))
                    if admin_stock:
                        result.update(admin_stock.control(action))
                    return self.send_text(json.dumps(result), "application/json")
            return super().do_GET()

        def do_POST(self):
            nonlocal report
            if self.path != "/__auth_qa" or self.headers.get("Origin") != front_origin:
                return self.send_error(404)
            length = int(self.headers.get("Content-Length", 0))
            if not 0 < length <= 2000:
                return self.send_error(400)
            report = self.rfile.read(length).decode("utf-8")
            self.send_response(204)
            self.end_headers()
            completed.set()

    mimetypes.add_type("text/javascript", ".js")
    with ThreadingHTTPSServer(("127.0.0.1", 0), Api, **tls) as api, \
         ThreadingHTTPSServer(("127.0.0.1", 0), Front, **tls) as front:
        api_origin = f"https://localhost:{api.server_port}"
        front_origin = f"https://localhost:{front.server_port}"
        threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in (api, front)]
        for thread in threads:
            thread.start()
        try:
            with subprocess.Popen(
                [args.chrome, "--headless=new", "--no-first-run", "--disable-extensions", "--disable-background-networking",
                 f"--user-data-dir={temp_path}", front_origin + "/__auth_qa"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            ) as chrome:
                try:
                    completed.wait(60)
                finally:
                    chrome.terminate()
                    chrome.wait(timeout=10)
        finally:
            for server in (api, front):
                server.shutdown()
            for thread in threads:
                thread.join()
        print(report or "FAIL: no Chromium report")
        if not report or not report.startswith("PASS "):
            print("Auth request counts:", fixture.calls)
        return 0 if report and report.startswith("PASS ") else 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--cert", required=True)
    parser.add_argument("--key", required=True)
    suite = parser.add_mutually_exclusive_group()
    suite.add_argument("--cart", action="store_true", help="Run the Customer cart scenarios with the Auth fixture.")
    suite.add_argument("--checkout", action="store_true", help="Run the Checkout scenarios with the Auth and Cart fixtures.")
    suite.add_argument("--orders", action="store_true", help="Run the Customer Orders scenarios.")
    suite.add_argument("--dashboard", action="store_true", help="Run the Admin Dashboard scenarios.")
    suite.add_argument("--admin-catalog", action="store_true", help="Run the Admin catalog management scenarios.")
    suite.add_argument("--admin-stock", action="store_true", help="Run the Admin variants, inventory and image scenarios.")
    parser.add_argument("--chrome", default=os.path.join(os.environ.get("ProgramFiles", ""), "Google", "Chrome",
                                                       "Application", "chrome.exe"))
    with tempfile.TemporaryDirectory(prefix="SportShop-AuthChromium-", ignore_cleanup_errors=True) as profile:
        raise SystemExit(run(parser.parse_args(), profile))
