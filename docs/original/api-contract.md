# SportShop – API Contract (ASP.NET Core Web API)

> **Phiên bản:** 1.1 – 17/11/2026 | **Tác giả:** Agent 3
> **Trạng thái:** Chờ duyệt. Chưa có code. Tên bảng/cột tham chiếu `database-design.md` (v1.2). Đồng bộ với `system-overview.md` (v1.5). Hai cột `Users.MustChangePassword` và `Users.TokenVersion` đã được `system-overview.md` v1.5 chốt nhưng **chưa có** trong `database-design.md` v1.2 (xem mục 12). Điểm chưa đủ căn cứ để kết luận được ghi `[NEEDS DECISION]` (mục 11). Thay đổi so với v1.0: mục 10.

---

## 1. Quy ước chung

| Mục | Quy ước |
|-----|---------|
| Base URL | `/api/v1` (ví dụ `https://localhost:5001/api/v1`). **Mọi** endpoint trong tài liệu này ghi đầy đủ tiền tố `/api/v1`. Đường dẫn ảnh `/uploads/...` là file tĩnh, không thuộc API |
| Định dạng | JSON (`Content-Type: application/json`), trừ upload ảnh dùng `multipart/form-data` |
| Đặt tên JSON | `camelCase` (`productId`, `unitPrice`) |
| Thời gian | ISO 8601, **UTC** (`2026-11-14T08:30:00Z`). Frontend tự đổi sang giờ Việt Nam khi hiển thị. Mọi so sánh với hạn thanh toán do **Backend** quyết định bằng đồng hồ SQL Server, không dùng giờ do client gửi |
| Tiền | Số (VND), ví dụ `450000`. Frontend tự định dạng `450.000đ` |
| Xác thực | Header `Authorization: Bearer <accessToken>` (access token). Refresh token đi riêng bằng cookie, xem mục 2.4 |
| Cookie | Chỉ có **refresh token**, qua `Set-Cookie` / `Cookie` ở nhóm `/api/v1/auth/*`. Refresh token **không bao giờ** xuất hiện trong JSON request hoặc response |
| Header riêng của `refresh`, `logout` | `X-Requested-With` và `Content-Type: application/json` (mục 2.5) |
| ID | Số nguyên, khớp khóa chính trong database |

### 1.1. Cấu trúc response thống nhất

**Thành công**
```json
{
  "success": true,
  "message": "OK",
  "data": { }
}
```

**Thành công có phân trang** (`data` luôn có `items` + `pagination`)
```json
{
  "success": true,
  "message": "OK",
  "data": {
    "items": [ ],
    "pagination": { "page": 1, "pageSize": 12, "totalItems": 47, "totalPages": 4 }
  }
}
```

**Lỗi**
```json
{
  "success": false,
  "message": "Dữ liệu không hợp lệ.",
  "errorCode": "VALIDATION_ERROR",
  "errors": [
    { "field": "email", "message": "Email không đúng định dạng." }
  ],
  "traceId": "00-8f1c..."
}
```
`errors` chỉ có khi lỗi validate. `message` là tiếng Việt, hiển thị trực tiếp được cho người dùng.

### 1.2. Phân trang, sắp xếp (áp dụng cho mọi API danh sách)

| Tham số | Mặc định | Quy tắc |
|---------|----------|---------|
| `page` | 1 | ≥ 1 |
| `pageSize` | 12 (admin: 20) | 1–50 |
| `sort` | tùy API | Dạng `field` hoặc `-field` (dấu `-` = giảm dần) |

**Ngoại lệ không phân trang (cập nhật v1.1).** Các API tra cứu nhỏ và dạng cây trả `data` là **mảng**, không có `items`/`pagination`: `GET /api/v1/categories` (cây), `GET /api/v1/brands`, `GET /api/v1/sizes`, `GET /api/v1/colors`, `GET /api/v1/payment-methods`, và `GET /api/v1/admin/categories` (danh sách phẳng kèm `parentCategoryId` và `isActive` để Admin tự dựng cây; cây không phân trang được theo trang). Mọi API danh sách còn lại (kể cả `GET /api/v1/admin/brands`, `/api/v1/admin/sizes`, `/api/v1/admin/colors`) tuân theo bảng trên.

---

## 2. Xác thực và phân quyền (JWT)

### 2.1. Cơ chế

- **Access token (JWT):** sống ngắn, **60 phút**. Trả trong **JSON** (`data.accessToken`), Frontend giữ trong **bộ nhớ JavaScript** (không dùng `localStorage`) và gửi kèm mọi request cần đăng nhập bằng header `Authorization: Bearer`.
- **Refresh token:** chuỗi ngẫu nhiên (≥ 32 byte), sống **7 ngày**, **chỉ** được gửi qua cookie `HttpOnly`, `Secure`, `SameSite=Strict` (mục 2.4); JavaScript không đọc được. Database chỉ lưu **hash** trong bảng `RefreshTokens` (`TokenHash`). Dùng để xin access token mới (`POST /api/v1/auth/refresh`); bị thu hồi khi đăng xuất, đổi mật khẩu, khóa tài khoản. **Mỗi lần refresh xoay vòng:** cấp refresh token mới (cookie mới) và thu hồi token cũ.
- **Claims trong JWT:**

| Claim | Nội dung |
|-------|----------|
| `sub` | `UserId` |
| `email` | Email của tài khoản |
| `role` | `Customer` hoặc `Admin` |
| `jti` | Mã định danh duy nhất của token |
| `iat` | Thời điểm cấp (theo `system-overview.md` mục 2.2) |
| `exp` | Thời điểm hết hạn (60 phút sau `iat`) |
| `tv` | `Users.TokenVersion` **tại lúc cấp** token |

  JWT **không** mang cờ `MustChangePassword`: cờ này luôn đọc từ database ở mỗi request (mục 2.3).
- Ký bằng khóa bí mật (HMAC SHA-256) đặt trong cấu hình/User Secrets, **không commit lên GitHub**. Khi xác thực, Backend kiểm tra cả `iss`/`aud` theo cấu hình.
- Đăng nhập khách và quản trị dùng **chung** `POST /api/v1/auth/login`. Frontend admin kiểm tra `role = Admin` trong response. Mọi API `/api/v1/admin/*` đều được server chặn lại nếu không phải Admin, nên ẩn nút ở frontend **không** thay thế phân quyền ở server.
- Giai đoạn 1 chỉ có **một Admin** (tạo bằng bước khởi tạo của hệ thống). Không có API tạo Admin và không có API đặt lại mật khẩu Admin (mục 5.5).

### 2.2. Ma trận phân quyền

| Nhóm API | Ẩn danh | Customer | Admin |
|----------|:------:|:--------:|:-----:|
| `/api/v1/auth/register`, `/api/v1/auth/login` | ✅ | ✅ | ✅ |
| `/api/v1/auth/refresh` (xác thực bằng **cookie**, không dùng access token) | ✅ | ✅ | ✅ |
| `/api/v1/auth/logout` | ❌ | ✅ | ✅ |
| `/api/v1/products`, `/api/v1/categories`, `/api/v1/brands`, `/api/v1/sizes`, `/api/v1/colors`, `/api/v1/payment-methods` (GET) | ✅ | ✅ | ✅ |
| `/api/v1/users/me/*` | ❌ | ✅ | ✅ (của chính mình) |
| `/api/v1/cart/*` | ❌ | ✅ | ❌ |
| `/api/v1/orders/*` | ❌ | ✅ (chỉ đơn của mình) | ❌ |
| `/api/v1/admin/*` | ❌ | ❌ | ✅ |

> Quy tắc sở hữu: Customer truy cập đơn/giỏ/địa chỉ của người khác → trả **404** (không tiết lộ là có tồn tại). Sai vai trò (ví dụ Customer gọi `/api/v1/admin/*`, Admin gọi `/api/v1/cart/*`) → **403 `FORBIDDEN`**. Đang bị buộc đổi mật khẩu → áp dụng thêm mục 2.3.

### 2.3. Kiểm tra access token ở Backend (mới v1.1)

JWT tự chứa chữ ký và hạn dùng, nên chỉ kiểm tra JWT thì token vẫn hợp lệ tới hết 60 phút kể cả khi mật khẩu đã đổi hoặc Admin vừa bị đặt lại mật khẩu. Vì vậy **mọi API yêu cầu đăng nhập** (bắt buộc với `/api/v1/admin/*`, áp dụng cả `logout`) chạy các bước sau, theo thứ tự:

| Bước | Kiểm tra | Nếu không đạt |
|------|----------|---------------|
| 1 | Chữ ký, `exp`, `iss`/`aud` của JWT (không cần DB) | `401 UNAUTHORIZED` hoặc `401 TOKEN_EXPIRED` |
| 2 | Đọc dòng `Users` theo `sub` (khóa chính, đọc thông thường, không giữ khóa) để lấy `TokenVersion` và `MustChangePassword` **hiện tại** | Không có tài khoản: `401 UNAUTHORIZED` |
| 3 | Claim `tv` **bằng** `Users.TokenVersion` hiện tại | **`401 TOKEN_REVOKED`**. Từ chối **mọi** API, kể cả 4 API được phép ở bước 4 |
| 4 | Nếu `Users.MustChangePassword` hiện tại = `TRUE`: API phải thuộc danh sách được phép (bảng dưới) | **`403 PASSWORD_CHANGE_REQUIRED`** |
| 5 | Phân quyền theo role và chủ sở hữu dữ liệu (mục 2.2) | `403 FORBIDDEN` hoặc `404` |

**Khi `MustChangePassword = TRUE` chỉ được gọi 4 API:**

| API | Lý do |
|-----|-------|
| `GET /api/v1/users/me` | Frontend đọc thông tin tài khoản |
| `PUT /api/v1/users/me/password` | Đổi mật khẩu (cách **duy nhất** đưa cờ về `FALSE`) |
| `POST /api/v1/auth/logout` | Thoát |
| `POST /api/v1/auth/refresh` | Giữ phiên trong lúc đổi mật khẩu (xem mục 5.3: refresh **không** vượt được yêu cầu đổi mật khẩu) |

Mọi API khác, kể cả `PUT /api/v1/users/me` và nhóm địa chỉ `/api/v1/users/me/addresses*`, trả `403 PASSWORD_CHANGE_REQUIRED`.

Quy tắc đi kèm:

- Cờ `MustChangePassword` và `TokenVersion` luôn lấy **từ database tại thời điểm request**, không lấy từ JWT và **không dùng cache có thời hạn** (cache mở lại đúng khoảng hở cần đóng).
- **Khi nào `Users.TokenVersion` tăng** (cùng transaction với thay đổi liên quan): (a) lệnh CLI đặt lại mật khẩu Admin (mục 5.5); (b) `PUT /api/v1/users/me/password` thành công. **Không** tăng khi đăng nhập, refresh, đăng xuất. Khóa tài khoản khách (mục 7.5) hiện cũng **không** tăng `TokenVersion` (`system-overview.md` mục 12.3, điểm chờ 8).
- Token cấp bởi `login` và `refresh` luôn mang `tv` hiện tại tại lúc cấp. Việc so sánh làm ở **lúc dùng**, nên token cũ bị từ chối ngay từ request kế tiếp sau khi `TokenVersion` đổi (request đang xử lý dở vẫn hoàn tất).
- Đăng xuất thông thường chỉ thu hồi refresh token; access token còn hiệu lực đến khi hết hạn (tối đa 60 phút), đây là đánh đổi chấp nhận được.
- Frontend nhận biết yêu cầu đổi mật khẩu từ `user.mustChangePassword = true` (response `login`/`refresh`) **hoặc** từ `403 PASSWORD_CHANGE_REQUIRED` của bất kỳ API nào; khi đó chuyển người dùng đến trang đổi mật khẩu, ẩn menu quản trị, chặn điều hướng sang trang khác. Việc chặn thật nằm ở Backend, không dựa vào Frontend.

### 2.4. Cookie refresh token (mới v1.1)

Thông số theo `system-overview.md` mục 2.3. Backend phát hành cookie ở `login` và `refresh`, xóa cookie ở `logout`.

| Thuộc tính | Giá trị | Ghi chú |
|-----------|---------|---------|
| Tên cookie | `refreshToken` | Đề xuất, Backend đặt ở cấu hình; Frontend **không** đọc/ghi cookie này |
| `HttpOnly` | Bật | JavaScript không đọc được |
| `Secure` | Bật ở **mọi** môi trường (API chạy HTTPS cả khi dev) | Chỉ gửi qua HTTPS |
| `SameSite` | `Strict` | Không gửi khi yêu cầu xuất phát từ trang khác site |
| `Path` | `/api/v1/auth` | Cookie chỉ đi kèm `/api/v1/auth/*`, không đi kèm các API khác |
| `Max-Age` | `604800` (7 ngày) | Khớp `RefreshTokens.ExpiresAt` |
| `Domain` | Không đặt | Chỉ áp dụng cho đúng host của API |
| Giá trị | Chuỗi ngẫu nhiên ≥ 32 byte | Database lưu hash |

Phát hành (`login`, `refresh`):

```http
Set-Cookie: refreshToken=<giá-trị-ngẫu-nhiên>; Max-Age=604800; Path=/api/v1/auth; Secure; HttpOnly; SameSite=Strict
```

Xóa (`logout`). Phải giữ **đúng** `Path` và không đặt `Domain`, vì cookie khác thuộc tính được trình duyệt coi là cookie khác:

```http
Set-Cookie: refreshToken=; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/api/v1/auth; Secure; HttpOnly; SameSite=Strict
```

### 2.5. CORS, credentials và chống CSRF cho `refresh`/`logout` (mới v1.1)

Backend chỉ dùng cookie ở `/api/v1/auth/*`; các API còn lại dùng header `Authorization` mà trình duyệt không tự gắn nên không có nguy cơ CSRF. Cách triển khai **không thay đổi** so với `system-overview.md` mục 2.4 và 2.5:

| Nội dung | Quy định |
|----------|----------|
| Cách triển khai | **Phương án A (cùng origin)** là khuyến nghị cho cả dev và chạy thật: không cần CORS. Phương án dự phòng B: khác origin nhưng **cùng site và cả hai HTTPS**, cùng một tên máy/tên miền gốc. Không dùng: `http` gọi `https`, hoặc khác site |
| CORS (chỉ khi dùng B) | Origin được phép là **danh sách tường minh theo môi trường** đọc từ cấu hình, **không** dùng `*`. Cho phép gửi cookie (`credentials`) **bật**. Method: `GET, POST, PUT, DELETE, OPTIONS`. Header: `Authorization`, `Content-Type`, `X-Requested-With`. Preflight cache khoảng 10 phút |
| Phía Frontend | Các lời gọi `/api/v1/auth/*` gọi `fetch` với `credentials: "include"` (để trình duyệt nhận và gửi cookie). Các API còn lại không cần |
| Kiểm tra ở `refresh`, `logout` | (1) Header `X-Requested-With` bắt buộc có (đề xuất giá trị `XMLHttpRequest`); (2) `Origin`/`Referer` (nếu có) thuộc danh sách origin cho phép; (3) `Content-Type: application/json` (thân rỗng hoặc `{}`). Thiếu/sai (1) hoặc (2) → `403 FORBIDDEN`; sai (3) → `415 UNSUPPORTED_MEDIA_TYPE` |
| Không nới lỏng cookie | Nếu cookie không được gửi (ví dụ `refresh` luôn `401` sau khi tải lại trang), **đổi cách triển khai**, không hạ `SameSite`, không bỏ `Secure` |

Chi tiết kiểm thử trình duyệt thật (cookie đủ thuộc tính, F5 vẫn còn phiên, hai trình duyệt, gọi từ trang khác site bị chặn) nằm ở `system-overview.md` mục 2.4.

---

## 3. Mã HTTP và mã lỗi

| HTTP | Khi nào | `errorCode` thường gặp |
|------|---------|------------------------|
| 200 OK | Lấy/cập nhật/xóa thành công | |
| 201 Created | Tạo mới thành công | |
| 400 Bad Request | Dữ liệu sai | `VALIDATION_ERROR`, `CART_EMPTY` |
| 401 Unauthorized | Chưa đăng nhập / token hết hạn hoặc bị thu hồi / sai mật khẩu / refresh token không hợp lệ | `UNAUTHORIZED`, `TOKEN_EXPIRED`, `TOKEN_REVOKED`, `INVALID_CREDENTIALS` |
| 403 Forbidden | Đã đăng nhập nhưng không đủ quyền / tài khoản bị khóa / bị buộc đổi mật khẩu | `FORBIDDEN`, `ACCOUNT_LOCKED`, `PASSWORD_CHANGE_REQUIRED` |
| 404 Not Found | Không có dữ liệu (hoặc không thuộc về người gọi) | `NOT_FOUND` |
| 409 Conflict | Xung đột nghiệp vụ hoặc xung đột đồng thời | `EMAIL_ALREADY_EXISTS`, `INSUFFICIENT_STOCK`, `INVALID_STATUS_TRANSITION`, `DUPLICATE_VARIANT`, `DUPLICATE_SKU`, `DUPLICATE_NAME`, `IN_USE`, `PAYMENT_EXPIRED`, `PAYMENT_AMOUNT_MISMATCH`, `PAYMENT_ALREADY_CONFIRMED`, `DUPLICATE_TRANSACTION`, `CONCURRENCY_CONFLICT` |
| 413 Payload Too Large | Upload vượt dung lượng | `PAYLOAD_TOO_LARGE` |
| 415 Unsupported Media Type | Sai loại/nội dung file hoặc sai `Content-Type` | `UNSUPPORTED_MEDIA_TYPE` |
| 429 Too Many Requests | Vượt giới hạn tần suất (login, refresh, upload) | `TOO_MANY_REQUESTS` |
| 500 Internal Server Error | Lỗi hệ thống (không lộ chi tiết kỹ thuật ra ngoài) | `INTERNAL_ERROR` |

### 3.1. Quy tắc xử lý lỗi

1. Một **middleware xử lý lỗi tập trung** bắt mọi exception và trả đúng cấu trúc ở mục 1.1.
2. Lỗi 500: ghi log chi tiết ở server kèm `traceId`; response chỉ có thông báo chung + `traceId`. **Không** trả stack trace, câu SQL, tên bảng/cột, token, hash, khóa hay chuỗi kết nối ở bất kỳ lỗi nào.
3. Đăng nhập sai email hay sai mật khẩu đều trả cùng 1 thông báo ("Email hoặc mật khẩu không đúng") để không lộ email nào tồn tại.
4. Lỗi tồn kho trả thêm chi tiết biến thể nào thiếu (xem mục 6.3).
5. Frontend xử lý lỗi:
   - **401** (`UNAUTHORIZED`, `TOKEN_EXPIRED`, `TOKEN_REVOKED`) ở API thường: gọi `refresh` **một lần** (chỉ một lời gọi `refresh` chạy tại một thời điểm, các request khác chờ kết quả); nếu `refresh` cũng lỗi → xóa access token trong bộ nhớ và về trang đăng nhập. Riêng `login` trả `401 INVALID_CREDENTIALS` thì hiện thông báo, không gọi `refresh`. Sau khi đổi mật khẩu thành công thì về trang đăng nhập ngay, không gọi `refresh`.
   - **403 `PASSWORD_CHANGE_REQUIRED`** → chuyển đến trang đổi mật khẩu; **403** khác → báo không có quyền.
   - **409 `CONCURRENCY_CONFLICT`** → cho phép bấm "Thử lại" (có thể tự gọi lại 1 lần); **409/400** khác → hiển thị `message` (và `errors` cạnh từng ô nhập). **429** → báo thử lại sau.
6. Khi có xung đột đồng thời, Backend **đọc lại trạng thái mới nhất rồi mới chọn mã lỗi cuối** (`system-overview.md` mục 3.3): ví dụ hai người mua món cuối thì người thua nhận `INSUFFICIENT_STOCK`, không phải `CONCURRENCY_CONFLICT`.

### 3.2. Danh mục mã lỗi (mới v1.1)

Mỗi mã chỉ có **một** HTTP status và một ý nghĩa.

| `errorCode` | HTTP | Ý nghĩa |
|-------------|:----:|---------|
| `VALIDATION_ERROR` | 400 | Dữ liệu thiếu, sai định dạng hoặc vi phạm quy tắc mục 4; kèm `errors[]` |
| `CART_EMPTY` | 400 | Đặt hàng khi giỏ rỗng |
| `UNAUTHORIZED` | 401 | Thiếu token, chữ ký sai, tài khoản không tồn tại; hoặc refresh token (cookie) thiếu/sai/hết hạn/đã thu hồi |
| `TOKEN_EXPIRED` | 401 | JWT đúng chữ ký nhưng quá `exp` |
| `TOKEN_REVOKED` | 401 | JWT hợp lệ nhưng `tv` khác `Users.TokenVersion` hiện tại (sau khi đổi hoặc đặt lại mật khẩu) |
| `INVALID_CREDENTIALS` | 401 | Sai email hoặc mật khẩu khi đăng nhập |
| `FORBIDDEN` | 403 | Không đủ quyền theo vai trò; hoặc `refresh`/`logout` thiếu `X-Requested-With` hay `Origin` không được phép |
| `ACCOUNT_LOCKED` | 403 | `Users.IsActive = 0` |
| `PASSWORD_CHANGE_REQUIRED` | 403 | `Users.MustChangePassword = TRUE` (đọc từ DB) và API không thuộc 4 API được phép (mục 2.3) |
| `NOT_FOUND` | 404 | Không có dữ liệu, hoặc dữ liệu không thuộc người gọi |
| `EMAIL_ALREADY_EXISTS` | 409 | Email đã đăng ký |
| `INSUFFICIENT_STOCK` | 409 | Tồn kho thật sự không đủ (lỗi nghiệp vụ, thử lại không có ích trừ khi giảm số lượng) |
| `INVALID_STATUS_TRANSITION` | 409 | Chuyển trạng thái đơn hoặc thanh toán sai luồng, hoặc thao tác không áp dụng cho trạng thái hiện tại (đơn đã hủy, đơn COD gọi API xác nhận chuyển khoản, khoản không ở `RefundPending`...) |
| `DUPLICATE_VARIANT` | 409 | Trùng tổ hợp (Sản phẩm, Size, Color) |
| `DUPLICATE_SKU` | 409 | Trùng `Sku` |
| `DUPLICATE_NAME` | 409 | Trùng tên danh mục/thương hiệu/size/màu (kể cả bản đã xóa mềm) |
| `IN_USE` | 409 | Không vô hiệu hóa được vì còn dữ liệu đang dùng (sản phẩm đang bán, danh mục con, biến thể đang bán) |
| `PAYMENT_EXPIRED` | 409 | Khoản `BANK_TRANSFER` đã `Expired`, hoặc còn `Pending` nhưng `CurrentTime >= ExpiresAt` (hết hạn tuyệt đối, kể cả khi Background Service chưa chạy) |
| `PAYMENT_AMOUNT_MISMATCH` | 409 | `receivedAmount` khác `Orders.TotalAmount` (thiếu hoặc thừa). Xem `[NEEDS DECISION]` mục 11, điểm 3 |
| `PAYMENT_ALREADY_CONFIRMED` | 409 | Khoản đã `Paid` nhưng `transactionCode` hoặc `receivedAmount` khác bản ghi; hoặc khoản đã `RefundPending`/`Refunded` (đơn đã hủy) |
| `DUPLICATE_TRANSACTION` | 409 | `transactionCode` đã dùng cho khoản thanh toán khác (cùng phương thức) |
| `CONCURRENCY_CONFLICT` | 409 | Dữ liệu bị đổi giữa lúc đọc và ghi (`RowVersion`) hoặc deadlock, **sau khi** Backend đã tự thử lại tối đa 3 lần. Message: "Dữ liệu vừa thay đổi, vui lòng thử lại." |
| `PAYLOAD_TOO_LARGE` | 413 | Ảnh upload vượt 2 MB |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | Ảnh không phải JPG/PNG/WEBP thật (kiểm tra nội dung file), hoặc `refresh`/`logout` sai `Content-Type` |
| `TOO_MANY_REQUESTS` | 429 | Vượt giới hạn tần suất (rate limit) ở login, refresh, upload |
| `INTERNAL_ERROR` | 500 | Lỗi hệ thống; chỉ có thông báo chung + `traceId` |

---

## 4. Quy tắc validate dữ liệu

Validate ở **server** là bắt buộc. Frontend validate chỉ để trải nghiệm tốt hơn.

| Trường | Quy tắc |
|--------|---------|
| `email` | Bắt buộc, đúng định dạng email, ≤ 256 ký tự, chuyển chữ thường, duy nhất |
| `password`, `newPassword` | Bắt buộc, 8–64 ký tự, có ít nhất 1 chữ hoa, 1 chữ thường, 1 chữ số |
| `confirmPassword`, `confirmNewPassword` | Trùng `password` / `newPassword` |
| `fullName` | Bắt buộc, 2–100 ký tự |
| `phoneNumber` | Số điện thoại VN: `0` hoặc `+84` + 9 chữ số |
| `dateOfBirth` | Không ở tương lai, tuổi ≤ 120 |
| `gender` | `Male`, `Female`, `Other` |
| `quantity` (giỏ hàng) | Số nguyên 1–99, và không vượt tồn kho |
| `*Name` (category, brand, product, size, color) | Bắt buộc, không rỗng sau khi trim, độ dài theo DB, không trùng |
| `price` | Số ≥ 0 và ≤ 1.000.000.000 |
| `sku` | Bắt buộc, ≤ 50 ký tự, chỉ chữ/số/dấu `-`, duy nhất |
| `hexCode` | Dạng `#RRGGBB` |
| `sortOrder` (size, ảnh) | Số nguyên ≥ 0 |
| `isActive` | Boolean |
| `ImageFile` | JPG/PNG/WEBP (kiểm tra **nội dung thật**), ≤ 2 MB, ảnh ≤ 4000×4000 px, tối đa 10 ảnh mỗi sản phẩm (hai giới hạn sau là mặc định của `system-overview.md` mục 5.1 và 11), một file mỗi lần |
| `statusCode` | Thuộc `PENDING`, `CONFIRMED`, `SHIPPING`, `DELIVERED`, `CANCELLED` |
| `paymentStatus` (bộ lọc danh sách) | Thuộc `Pending`, `Paid`, `Failed`, `Expired`, `RefundPending`, `Refunded` |
| `paymentStatus` (body xác nhận thanh toán) | Chỉ nhận `Paid` (mục 7.4) |
| `transactionCode` | Bắt buộc khi xác nhận thanh toán; sau khi trim 1–100 ký tự (khớp `NVARCHAR(100)`) |
| `receivedAmount` | Bắt buộc, số ≥ 0, và phải **đúng bằng** `Orders.TotalAmount` (nghiệp vụ, mục 7.4) |
| `refundCompleted` | Bắt buộc, phải là `true` (mục 7.4) |
| `cancelReason` | Bắt buộc khi hủy, 5–255 ký tự |
| `from`, `to` (thống kê) | Ngày hợp lệ, `from ≤ to`, khoảng ≤ 366 ngày |
| `groupBy` (thống kê) | `day` hoặc `month` |

---

## 5. Nhóm API xác thực (Auth)

### 5.1. `POST /api/v1/auth/register` – Đăng ký *(ẩn danh)*

Request:
```json
{
  "fullName": "Nguyễn Văn An",
  "email": "an.nguyen@example.com",
  "password": "Abcd1234",
  "confirmPassword": "Abcd1234",
  "phoneNumber": "0912345678"
}
```
Response `201`:
```json
{
  "success": true,
  "message": "Đăng ký thành công.",
  "data": { "userId": 2, "email": "an.nguyen@example.com", "fullName": "Nguyễn Văn An" }
}
```
Lỗi: `400 VALIDATION_ERROR`, `409 EMAIL_ALREADY_EXISTS`.
Ghi chú: tạo `Users` (role `Customer`) và `Carts` rỗng. Không tự đăng nhập; frontend chuyển sang trang đăng nhập.
Ghi chú thêm (v1.1): luôn tạo tài khoản `Customer` (không có tham số role); `MustChangePassword = false`, `TokenVersion = 0` theo mặc định của database. Giai đoạn 1 không có cách tự đăng ký hay tự nâng quyền thành Admin.

### 5.2. `POST /api/v1/auth/login` – Đăng nhập *(ẩn danh)*

Request:
```json
{ "email": "an.nguyen@example.com", "password": "Abcd1234" }
```
Response `200` (body JSON **không** chứa refresh token):
```json
{
  "success": true,
  "message": "Đăng nhập thành công.",
  "data": {
    "accessToken": "eyJhbGciOi...",
    "accessTokenExpiresAt": "2026-11-14T09:30:00Z",
    "user": {
      "userId": 2,
      "fullName": "Nguyễn Văn An",
      "email": "an.nguyen@example.com",
      "role": "Customer",
      "mustChangePassword": false
    }
  }
}
```
Refresh token được gửi qua **header** của cùng response, không nằm trong JSON (thuộc tính cookie ở mục 2.4):
```http
Set-Cookie: refreshToken=<giá-trị-ngẫu-nhiên>; Max-Age=604800; Path=/api/v1/auth; Secure; HttpOnly; SameSite=Strict
```
Lỗi: `401 INVALID_CREDENTIALS`, `403 ACCOUNT_LOCKED`, `400 VALIDATION_ERROR`, `429 TOO_MANY_REQUESTS`.

Xử lý ở server:
- Kiểm tra email, hash mật khẩu và `IsActive`. Sai email hay sai mật khẩu đều trả cùng một thông báo.
- Access token mang `tv` = `Users.TokenVersion` **hiện tại**; `user.mustChangePassword` đọc từ `Users.MustChangePassword` **hiện tại** trong database.
- Tạo refresh token ngẫu nhiên, **chỉ lưu hash** vào `RefreshTokens`, gửi bản gốc qua `Set-Cookie`. Ghi `Users.LastLoginAt`.
- Frontend: `user.mustChangePassword = true` → chuyển ngay đến trang đổi mật khẩu (mục 2.3). Gọi `login` với `credentials: "include"` để trình duyệt nhận cookie.

### 5.3. `POST /api/v1/auth/refresh` – Làm mới token *(xác thực bằng cookie)*

Request: **không có body.** Trình duyệt tự gửi cookie `refreshToken`. Header bắt buộc: `X-Requested-With`, `Content-Type: application/json` (thân rỗng hoặc `{}`). Server **không nhận** refresh token trong request body; nếu body có trường `refreshToken` thì **bỏ qua**.

Response `200` (JSON giống `data` của login, **không** có refresh token):
```json
{
  "success": true,
  "message": "OK",
  "data": {
    "accessToken": "eyJhbGciOi...",
    "accessTokenExpiresAt": "2026-11-14T10:30:00Z",
    "user": {
      "userId": 2,
      "fullName": "Nguyễn Văn An",
      "email": "an.nguyen@example.com",
      "role": "Customer",
      "mustChangePassword": false
    }
  }
}
```
Kèm refresh token **mới** qua `Set-Cookie` (cùng thuộc tính mục 2.4). Refresh token cũ bị thu hồi.

Xử lý ở server, trong một transaction:
1. Đọc cookie, băm rồi tìm `RefreshTokens.TokenHash`. Token phải **chưa thu hồi** (`RevokedAt IS NULL`) và **chưa hết hạn** (`ExpiresAt`).
2. Đọc `Users` theo token: tài khoản phải `IsActive = 1`. Lấy `TokenVersion` và `MustChangePassword` **hiện tại từ database** (không lấy từ token cũ).
3. Thu hồi token cũ bằng cập nhật có điều kiện (`RevokedAt IS NULL`), tạo token mới (chỉ lưu hash), COMMIT. Gửi lại **cùng một cookie lần hai** → chỉ một lần thành công, lần kia `401` (đã thu hồi). Giai đoạn 1 chưa phát hiện "dùng lại token cũ" theo cả họ token.
4. Access token mới mang `tv` hiện tại; `user.mustChangePassword` là giá trị hiện tại trong database.

**`refresh` không phải đường vượt qua yêu cầu đổi mật khẩu:**
- Khi `MustChangePassword = TRUE`, `refresh` vẫn được phép (để giữ phiên lúc đổi mật khẩu) nhưng **luôn trả `user.mustChangePassword = true`**, và access token mới **vẫn bị chặn** ở mọi API ngoài 4 API được phép (mục 2.3, vì cờ đọc từ database ở mỗi request).
- `refresh` **không bao giờ** đổi hoặc xóa cờ. Chỉ `PUT /api/v1/users/me/password` thành công mới đưa cờ về `FALSE`.
- Sau đổi mật khẩu hoặc sau lệnh CLI đặt lại mật khẩu, **mọi** refresh token cũ đã bị thu hồi nên `refresh` trả `401`; người dùng phải đăng nhập lại.

Lỗi: `401 UNAUTHORIZED` (không có cookie, token sai/hết hạn/đã thu hồi, tài khoản không còn hợp lệ) → Frontend xóa trạng thái trong bộ nhớ, về trang đăng nhập; `403 FORBIDDEN` (thiếu `X-Requested-With` hoặc `Origin` không được phép); `415 UNSUPPORTED_MEDIA_TYPE`; `429 TOO_MANY_REQUESTS`.

Frontend: chỉ **một** lời gọi `refresh` chạy tại một thời điểm (các request khác chờ kết quả rồi gửi lại); nhiều tab dùng cơ chế khóa giữa các tab, vì token xoay vòng nên không được dùng cùng một cookie hai lần (`system-overview.md` mục 2.6).

### 5.4. `POST /api/v1/auth/logout` – Đăng xuất *(đã đăng nhập)*

Request: **không có body** và Frontend **không** gửi refresh token trong JSON. Cần header `Authorization: Bearer`, `X-Requested-With`, `Content-Type: application/json`; cookie `refreshToken` do trình duyệt tự gửi. `logout` vẫn được phép khi `MustChangePassword = TRUE` (mục 2.3).

Response `200`:
```json
{ "success": true, "message": "Đã đăng xuất.", "data": null }
```
Kèm header xóa cookie (mục 2.4):
```http
Set-Cookie: refreshToken=; Max-Age=0; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/api/v1/auth; Secure; HttpOnly; SameSite=Strict
```
Xử lý ở server:
- Lấy refresh token **từ cookie**, thu hồi trong database (`RefreshTokens.RevokedAt`). Chỉ thu hồi token **thuộc đúng tài khoản** trong access token.
- Gọi lặp lại hoặc không có cookie/cookie không hợp lệ vẫn trả `200` và vẫn xóa cookie (idempotent).
- Không tăng `TokenVersion`: access token còn hiệu lực đến khi hết hạn (tối đa 60 phút), đây là đánh đổi chấp nhận được.
- Frontend xóa access token khỏi bộ nhớ và cờ "đã từng đăng nhập", rồi về trang đăng nhập. Xem `[NEEDS DECISION]` mục 11, điểm 6 về trường hợp access token đã hết hạn.

Lỗi: `401` (access token không hợp lệ/hết hạn/bị thu hồi), `403 FORBIDDEN`, `415 UNSUPPORTED_MEDIA_TYPE`.

### 5.5. Khôi phục mật khẩu Admin: không có API, dùng lệnh CLI (mới v1.1)

Giai đoạn 1 **không có** API "quên mật khẩu", API đặt lại mật khẩu Admin hay API tạo Admin. Nếu Admin duy nhất quên mật khẩu, người vận hành chạy lệnh CLI **trực tiếp trên máy chủ** (`system-overview.md` mục 6.6). Lệnh chỉ nhận tài khoản role `Admin`; mật khẩu mới nhập tương tác, **không** ghi vào tham số dòng lệnh, biến môi trường, cấu hình hay log. Trong **một transaction**, lệnh:

| Việc | Cột/bảng |
|------|----------|
| Đặt mật khẩu mới (hash) | `Users.PasswordHash`, `Users.UpdatedAt` |
| Bắt buộc đổi mật khẩu | `Users.MustChangePassword = TRUE` |
| Vô hiệu hóa mọi access token cũ | `Users.TokenVersion` **tăng 1** |
| Thu hồi mọi refresh token | `RefreshTokens.RevokedAt` |

Ảnh hưởng lên API: access token cũ → `401 TOKEN_REVOKED` ở mọi API; `refresh` bằng cookie cũ → `401 UNAUTHORIZED`; `login` bằng mật khẩu mới trả `user.mustChangePassword = true`, sau đó mọi API ngoài 4 API ở mục 2.3 trả `403 PASSWORD_CHANGE_REQUIRED` cho tới khi `PUT /api/v1/users/me/password` thành công. Mật khẩu không bao giờ được ghi rõ vào log.

---

## 6. Nhóm API khách hàng

### 6.1. Thông tin cá nhân *(Customer/Admin đã đăng nhập)*

Khi `MustChangePassword = true`, trong nhóm này chỉ `GET /api/v1/users/me` và `PUT /api/v1/users/me/password` được gọi; `PUT /api/v1/users/me` và nhóm địa chỉ trả `403 PASSWORD_CHANGE_REQUIRED` (mục 2.3).

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/users/me` | Xem thông tin của mình |
| PUT | `/api/v1/users/me` | Cập nhật thông tin |
| PUT | `/api/v1/users/me/password` | Đổi mật khẩu |
| GET | `/api/v1/users/me/addresses` | Danh sách địa chỉ |
| POST | `/api/v1/users/me/addresses` | Thêm địa chỉ |
| PUT | `/api/v1/users/me/addresses/{addressId}` | Sửa địa chỉ |
| DELETE | `/api/v1/users/me/addresses/{addressId}` | Xóa địa chỉ |

`GET /api/v1/users/me` → `200`:
```json
{
  "success": true, "message": "OK",
  "data": {
    "userId": 2, "email": "an.nguyen@example.com", "fullName": "Nguyễn Văn An",
    "phoneNumber": "0912345678", "dateOfBirth": "2004-05-20", "gender": "Male", "role": "Customer"
  }
}
```

`PUT /api/v1/users/me` body: `{ "fullName": "Nguyễn Văn An", "phoneNumber": "0912345678", "dateOfBirth": "2004-05-20", "gender": "Male" }` (email **không** cho sửa).

`PUT /api/v1/users/me/password` body:
```json
{ "currentPassword": "Abcd1234", "newPassword": "Xyz98765", "confirmNewPassword": "Xyz98765" }
```
Lỗi: `400 VALIDATION_ERROR` (mật khẩu hiện tại sai cũng trả 400 với `field = currentPassword`).

Thành công (**một transaction**): cập nhật `PasswordHash`; đặt `MustChangePassword = false`; **tăng `Users.TokenVersion`**; **thu hồi toàn bộ** refresh token cũ của user. Response `200`:
```json
{ "success": true, "message": "Đổi mật khẩu thành công. Vui lòng đăng nhập lại.", "data": null }
```
Hệ quả: mọi access token cũ, **kể cả token vừa gọi API này**, lập tức bị `401 TOKEN_REVOKED` ở request kế tiếp; mọi refresh token cũ bị `401`. Frontend phải **xóa access token trong bộ nhớ** và cờ "đã từng đăng nhập", rồi chuyển về trang đăng nhập (không gọi `refresh`). Đây là cách duy nhất để đưa `MustChangePassword` về `false`.

`POST /api/v1/users/me/addresses` body:
```json
{
  "recipientName": "Nguyễn Văn An", "phoneNumber": "0912345678",
  "province": "Hà Nội", "district": "Hoàn Kiếm", "ward": "Hàng Bạc",
  "addressLine": "12 Hàng Gai", "isDefault": true
}
```
Response `201`: trả địa chỉ vừa tạo kèm `addressId`. Đặt `isDefault = true` sẽ tự bỏ mặc định của địa chỉ khác. Địa chỉ không thuộc user → `404`.

### 6.2. Xem sản phẩm, tìm kiếm, lọc *(ẩn danh)*

#### `GET /api/v1/products` – Danh sách sản phẩm

| Query | Kiểu | Mô tả |
|-------|------|-------|
| `keyword` | string | Tìm theo tên sản phẩm (chứa chuỗi, không phân biệt hoa/thường) |
| `categoryId` | int | Lọc theo danh mục (**bao gồm cả danh mục con**) |
| `brandId` | int | Lọc theo thương hiệu (cho phép nhiều: `brandId=1&brandId=2`) |
| `sizeId` | int | Có biến thể thuộc size này (cho phép nhiều) |
| `colorId` | int | Có biến thể thuộc màu này (cho phép nhiều) |
| `minPrice`, `maxPrice` | number | Khoảng giá (so với giá biến thể) |
| `inStock` | bool | `true` = chỉ sản phẩm còn hàng |
| `sort` | string | `newest` (mặc định), `price`, `-price`, `name` |
| `page`, `pageSize` | int | Phân trang |

Quy tắc: các bộ lọc khác loại kết hợp bằng **AND**; nhiều giá trị cùng loại kết hợp bằng **OR**. Chỉ trả sản phẩm `IsActive = 1` có biến thể đang bán.

Response `200`:
```json
{
  "success": true, "message": "OK",
  "data": {
    "items": [
      {
        "productId": 1,
        "productName": "Áo thun chạy bộ Nike Dri-FIT",
        "slug": "ao-thun-chay-bo-nike-dri-fit",
        "brandName": "Nike",
        "categoryName": "Áo thể thao",
        "primaryImageUrl": "/uploads/products/1/main.jpg",
        "minPrice": 450000,
        "maxPrice": 450000,
        "inStock": true,
        "colors": [
          { "colorId": 1, "colorName": "Đen", "hexCode": "#000000" },
          { "colorId": 2, "colorName": "Trắng", "hexCode": "#FFFFFF" }
        ]
      }
    ],
    "pagination": { "page": 1, "pageSize": 12, "totalItems": 3, "totalPages": 1 }
  }
}
```

#### `GET /api/v1/products/{productId}` – Chi tiết sản phẩm

Response `200`:
```json
{
  "success": true, "message": "OK",
  "data": {
    "productId": 1,
    "productName": "Áo thun chạy bộ Nike Dri-FIT",
    "slug": "ao-thun-chay-bo-nike-dri-fit",
    "description": "Chất liệu thoáng khí...",
    "brand": { "brandId": 1, "brandName": "Nike" },
    "category": { "categoryId": 2, "categoryName": "Áo thể thao" },
    "images": [
      { "imageId": 1, "imageUrl": "/uploads/products/1/main.jpg", "colorId": null, "isPrimary": true },
      { "imageId": 2, "imageUrl": "/uploads/products/1/white.jpg", "colorId": 2, "isPrimary": false }
    ],
    "variants": [
      { "variantId": 1, "sku": "NIKE-DF-M-BLK", "sizeId": 2, "sizeName": "M", "colorId": 1, "colorName": "Đen", "price": 450000, "quantityAvailable": 50 },
      { "variantId": 4, "sku": "NIKE-DF-L-WHT", "sizeId": 3, "sizeName": "L", "colorId": 2, "colorName": "Trắng", "price": 450000, "quantityAvailable": 0 }
    ]
  }
}
```
`variants` chỉ gồm biến thể đang bán (BR-22). Lỗi: `404 NOT_FOUND` (không có hoặc đã ẩn). Frontend nhóm `variants` theo màu/size để vẽ lựa chọn, ô nào `quantityAvailable = 0` thì làm mờ.

#### Danh mục tra cứu *(ẩn danh)*

| Method | Endpoint | Mô tả | Mẫu `data` |
|--------|----------|-------|-----------|
| GET | `/api/v1/categories` | Cây danh mục đang hoạt động | `[{ "categoryId":1, "categoryName":"Quần áo", "slug":"quan-ao", "children":[{ "categoryId":2, "categoryName":"Áo thể thao", "slug":"ao-the-thao", "children":[] }] }]` |
| GET | `/api/v1/brands` | Thương hiệu đang hoạt động | `[{ "brandId":1, "brandName":"Nike", "logoUrl":null }]` |
| GET | `/api/v1/sizes` | Kích thước đang dùng (`IsActive = 1`), đã sắp theo `sortOrder` | `[{ "sizeId":2, "sizeName":"M" }]` |
| GET | `/api/v1/colors` | Màu sắc đang dùng (`IsActive = 1`) | `[{ "colorId":1, "colorName":"Đen", "hexCode":"#000000" }]` |
| GET | `/api/v1/payment-methods` | Phương thức thanh toán đang bật | `[{ "paymentMethodId":1, "methodCode":"COD", "methodName":"Thanh toán khi nhận hàng" }]` |

Các API tra cứu ở bảng trên trả **mảng** trong `data`, không phân trang (ngoại lệ mục 1.2).

### 6.3. Giỏ hàng *(Customer)*

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/cart` | Xem giỏ hàng |
| POST | `/api/v1/cart/items` | Thêm vào giỏ |
| PUT | `/api/v1/cart/items/{cartItemId}` | Cập nhật số lượng |
| DELETE | `/api/v1/cart/items/{cartItemId}` | Xóa 1 dòng |
| DELETE | `/api/v1/cart` | Xóa toàn bộ giỏ |

`GET /api/v1/cart` → `200` (mọi API giỏ hàng đều trả lại cấu trúc này để frontend vẽ lại ngay):
```json
{
  "success": true, "message": "OK",
  "data": {
    "cartId": 1,
    "items": [
      {
        "cartItemId": 10, "variantId": 1, "productId": 1,
        "productName": "Áo thun chạy bộ Nike Dri-FIT",
        "sku": "NIKE-DF-M-BLK", "sizeName": "M", "colorName": "Đen",
        "imageUrl": "/uploads/products/1/main.jpg",
        "unitPrice": 450000, "quantity": 2, "lineTotal": 900000,
        "availableStock": 50, "isAvailable": true
      }
    ],
    "totalItems": 2,
    "subtotal": 900000
  }
}
```
- `unitPrice` là giá **hiện tại** của biến thể. `isAvailable = false` nếu sản phẩm hoặc biến thể ngừng bán (BR-22) hoặc tồn kho < số lượng trong giỏ (frontend cảnh báo, chặn thanh toán).

`POST /api/v1/cart/items` body: `{ "variantId": 1, "quantity": 2 }`
- Đã có biến thể trong giỏ → **cộng dồn** số lượng.
- Response `200` trả giỏ hàng mới. Lỗi: `404` (biến thể không tồn tại/ẩn), `409 INSUFFICIENT_STOCK` (vượt tồn kho).

`PUT /api/v1/cart/items/{cartItemId}` body: `{ "quantity": 3 }` (**đặt** số lượng mới, không cộng dồn). `DELETE` các API còn lại trả giỏ mới.

Mẫu lỗi tồn kho:
```json
{
  "success": false,
  "message": "Một số sản phẩm không đủ số lượng.",
  "errorCode": "INSUFFICIENT_STOCK",
  "errors": [
    { "field": "variantId:6", "message": "Giày chạy bộ Adidas Ultraboost (41, Đen) chỉ còn 8 sản phẩm." }
  ]
}
```

### 6.4. Đặt hàng, thanh toán, lịch sử, theo dõi *(Customer)*

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| POST | `/api/v1/orders` | Đặt hàng từ giỏ hàng (kèm chọn phương thức thanh toán) |
| GET | `/api/v1/orders` | Lịch sử đơn hàng của tôi |
| GET | `/api/v1/orders/{orderId}` | Chi tiết đơn + theo dõi trạng thái đơn và thanh toán |
| POST | `/api/v1/orders/{orderId}/cancel` | Hủy đơn (chỉ khi `PENDING`) |

Trạng thái đơn dùng `StatusCode`: `PENDING`, `CONFIRMED`, `SHIPPING`, `DELIVERED`, `CANCELLED`. Trạng thái thanh toán (`paymentStatus`, khớp `Payments.PaymentStatus`) gồm 6 giá trị:

| `paymentStatus` | Ý nghĩa |
|-----------------|---------|
| `Pending` | Chờ thanh toán (COD: chờ giao hàng; chuyển khoản: chờ trong hạn `expiresAt`) |
| `Paid` | Đã nhận đủ tiền |
| `Failed` | Không thu được: đơn bị hủy **khi khoản còn trong hạn** hoặc không có hạn (COD) |
| `Expired` | Chuyển khoản quá hạn tuyệt đối (`CurrentTime >= ExpiresAt`) |
| `RefundPending` | Đã thu tiền nhưng đơn bị hủy, **chưa hoàn tiền xong** |
| `Refunded` | **Đã hoàn tiền thực tế xong**, Admin đã xác nhận (mục 7.4) |

#### `POST /api/v1/orders` – Đặt hàng và thanh toán

Request (server lấy sản phẩm từ **giỏ hàng** của người đang đăng nhập; **không** nhận giá, tồn kho, thành tiền, tổng tiền hay phí ship từ client):
```json
{
  "recipientName": "Nguyễn Văn An",
  "recipientPhone": "0912345678",
  "shippingAddress": "12 Hàng Gai, Hàng Bạc, Hoàn Kiếm, Hà Nội",
  "paymentMethodId": 2,
  "note": "Giao giờ hành chính"
}
```
Xử lý ở server (**một transaction**, đúng trình tự `database-design.md` mục 6.4 và `system-overview.md` mục 3.2):
1. Đọc giỏ theo `UserId` trong JWT; giỏ rỗng → `400 CART_EMPTY`.
2. Với từng dòng, theo `VariantId` **tăng dần**: kiểm tra biến thể **bán được** (BR-22) và lấy `Price` từ database.
3. **Trừ kho có điều kiện, nguyên tử** trên `Inventories` (chỉ thành công khi tồn kho hiện tại đủ). Thiếu ở dòng nào → ROLLBACK cả đơn, giỏ giữ nguyên.
4. Tính `Subtotal`, `ShippingFee` (30.000đ, miễn phí khi `Subtotal` ≥ 1.000.000đ, cấu hình ở `appsettings`), `TotalAmount` từ dữ liệu database.
5. Tạo `Orders` (`PENDING`), `OrderItems` (chụp tên, SKU, size, màu, ảnh, `UnitPrice`), `OrderStatusHistories`, `Payments` (`Pending`, `Amount = TotalAmount`; với `BANK_TRANSFER` có thêm `ExpiresAt`).
6. Ghi `InventoryTransactions` loại `Sale` cho từng `OrderItems` (gắn `OrderItemId`), xóa `CartItems`, COMMIT.

Xung đột đồng thời: Backend tự thử lại tối đa 3 lần (mỗi lần một transaction mới, đọc lại toàn bộ); hết lượt mới trả `409 CONCURRENCY_CONFLICT`. Ảnh chụp vào `OrderItems.ImageUrl` là ảnh đầu tiên (theo `SortOrder`) của màu biến thể, nếu không có thì ảnh đại diện sản phẩm (`database-design.md` mục 6.9).

Response `201` với `BANK_TRANSFER` (có `payment.expiresAt`):
```json
{
  "success": true,
  "message": "Đặt hàng thành công.",
  "data": {
    "orderId": 4,
    "orderCode": "SP20261114-0001",
    "status": { "statusCode": "PENDING", "statusName": "Chờ xác nhận" },
    "subtotal": 450000,
    "shippingFee": 30000,
    "totalAmount": 480000,
    "payment": {
      "methodCode": "BANK_TRANSFER",
      "paymentStatus": "Pending",
      "expiresAt": "2026-11-15T02:20:00Z",
      "instructions": "Chuyển khoản 480.000đ vào tài khoản ngân hàng của cửa hàng (số tài khoản lấy từ cấu hình), nội dung: SP20261114-0001"
    },
    "createdAt": "2026-11-14T02:20:00Z"
  }
}
```
Với `COD`, `payment` là `{ "methodCode": "COD", "paymentStatus": "Pending", "expiresAt": null, "instructions": null }`.

Quy tắc `payment.expiresAt`:
- **`BANK_TRANSFER`:** thời điểm tạo đơn + thời hạn cấu hình, **mặc định 24 giờ** (`Payment:BankTransferTimeoutMinutes = 1440`), tính bằng đồng hồ UTC của SQL Server và được **chụp vào `Payments.ExpiresAt` lúc tạo đơn** (đổi cấu hình sau này không ảnh hưởng đơn đang chờ). **COD:** `expiresAt = null`, không có hạn.
- Frontend dùng `expiresAt` để hiển thị thời gian còn lại; Frontend **không** tự quyết định đơn còn hạn hay không.
- **Hết hạn tuyệt đối:** từ `CurrentTime >= ExpiresAt` khoản thanh toán là hết hạn **ngay cả khi Background Service chưa chạy**: Admin không xác nhận được (`409 PAYMENT_EXPIRED`, mục 7.4). Background Service (quét khoảng 5 phút, cấu hình được) sẽ hủy đơn, chuyển `Payments` sang `Expired` và hoàn kho đúng một lần; đơn khi đó `CANCELLED` với lý do "Hết hạn thanh toán chuyển khoản", dòng `statusHistory` do hệ thống ghi.
- **Tiền chuyển muộn** (sau `ExpiresAt`) **không** được tự xác nhận và không khôi phục giao dịch đã hết hạn; xử lý thủ công ngoài hệ thống (thường là hoàn tiền cho khách).

Lỗi: `400 CART_EMPTY`, `400 VALIDATION_ERROR` (phương thức thanh toán không tồn tại/đang tắt; giỏ còn dòng của sản phẩm/biến thể đã ngừng bán, `field = variantId:<id>`), `409 INSUFFICIENT_STOCK` (không đặt đơn nào, giỏ giữ nguyên), `409 CONCURRENCY_CONFLICT` (hết 3 lượt thử lại).

> Thanh toán online (VNPay/MoMo) là giai đoạn sau. Khi bật, response sẽ có thêm `payment.paymentUrl` và cổng thanh toán gọi `POST /api/v1/payments/callback/{provider}` (ẩn danh, **xác minh chữ ký**), dùng **cùng quy trình xác nhận và cùng quy tắc hết hạn** như mục 7.4 (callback đến sau `ExpiresAt` không được xác nhận). Hiện chưa thiết kế chi tiết vì phương thức đang tắt, và endpoint này **không** nằm trong 65 endpoint của mục 9.

#### `GET /api/v1/orders` – Lịch sử đơn hàng

Query: `statusCode` (lọc), `page`, `pageSize`, `sort` (mặc định `-createdAt`). `paymentStatus` trong mỗi dòng nhận một trong 6 giá trị ở bảng trên.
```json
{
  "success": true, "message": "OK",
  "data": {
    "items": [
      {
        "orderId": 4, "orderCode": "SP20261114-0001",
        "status": { "statusCode": "PENDING", "statusName": "Chờ xác nhận" },
        "totalAmount": 480000, "totalItems": 1,
        "paymentStatus": "Pending", "paymentMethodName": "Chuyển khoản ngân hàng",
        "createdAt": "2026-11-14T02:20:00Z"
      },
      {
        "orderId": 3, "orderCode": "SP20261112-0001",
        "status": { "statusCode": "CANCELLED", "statusName": "Đã hủy" },
        "totalAmount": 3200000, "totalItems": 1,
        "paymentStatus": "RefundPending", "paymentMethodName": "Chuyển khoản ngân hàng",
        "createdAt": "2026-11-12T03:00:00Z"
      }
    ],
    "pagination": { "page": 1, "pageSize": 2, "totalItems": 4, "totalPages": 2 }
  }
}
```

#### `GET /api/v1/orders/{orderId}` – Chi tiết và theo dõi

Ví dụ 1: đơn COD đã giao.
```json
{
  "success": true, "message": "OK",
  "data": {
    "orderId": 1, "orderCode": "SP20261105-0001",
    "status": { "statusCode": "DELIVERED", "statusName": "Đã giao thành công" },
    "recipientName": "Nguyễn Văn An", "recipientPhone": "0912345678",
    "shippingAddress": "12 Hàng Gai, Hàng Bạc, Hoàn Kiếm, Hà Nội",
    "note": null,
    "cancelReason": null,
    "subtotal": 1180000, "shippingFee": 0, "totalAmount": 1180000,
    "payment": {
      "methodCode": "COD", "methodName": "Thanh toán khi nhận hàng",
      "paymentStatus": "Paid", "amount": 1180000, "receivedAmount": 1180000,
      "expiresAt": null, "isOverdue": false,
      "paidAt": "2026-11-08T08:45:00Z", "refundedAt": null
    },
    "items": [
      { "orderItemId": 1, "variantId": 1, "productName": "Áo thun chạy bộ Nike Dri-FIT", "sku": "NIKE-DF-M-BLK", "sizeName": "M", "colorName": "Đen", "imageUrl": "/uploads/products/1/3f9c1a7e.jpg", "unitPrice": 450000, "quantity": 2, "lineTotal": 900000 },
      { "orderItemId": 2, "variantId": 8, "productName": "Quần short tập gym Puma", "sku": "PUMA-SH-M-RED", "sizeName": "M", "colorName": "Đỏ", "imageUrl": "/uploads/products/3/c04b6e18.jpg", "unitPrice": 280000, "quantity": 1, "lineTotal": 280000 }
    ],
    "statusHistory": [
      { "statusCode": "PENDING", "statusName": "Chờ xác nhận", "changedAt": "2026-11-05T02:10:00Z", "note": null },
      { "statusCode": "CONFIRMED", "statusName": "Đã xác nhận", "changedAt": "2026-11-05T03:00:00Z", "note": null },
      { "statusCode": "SHIPPING", "statusName": "Đang giao hàng", "changedAt": "2026-11-06T01:30:00Z", "note": null },
      { "statusCode": "DELIVERED", "statusName": "Đã giao thành công", "changedAt": "2026-11-08T08:45:00Z", "note": null }
    ],
    "createdAt": "2026-11-05T02:10:00Z"
  }
}
```
Ví dụ 2: đơn chuyển khoản đã thu tiền rồi bị hủy, chờ hoàn tiền (chỉ phần khác ví dụ 1 được nêu đầy đủ).
```json
{
  "success": true, "message": "OK",
  "data": {
    "orderId": 3, "orderCode": "SP20261112-0001",
    "status": { "statusCode": "CANCELLED", "statusName": "Đã hủy" },
    "recipientName": "Nguyễn Văn An", "recipientPhone": "0912345678",
    "shippingAddress": "12 Hàng Gai, Hàng Bạc, Hoàn Kiếm, Hà Nội",
    "note": null,
    "cancelReason": "Khách yêu cầu hủy, cần hoàn tiền",
    "subtotal": 3200000, "shippingFee": 0, "totalAmount": 3200000,
    "payment": {
      "methodCode": "BANK_TRANSFER", "methodName": "Chuyển khoản ngân hàng",
      "paymentStatus": "RefundPending", "amount": 3200000, "receivedAmount": 3200000,
      "expiresAt": "2026-11-13T03:00:00Z", "isOverdue": false,
      "paidAt": "2026-11-12T08:30:00Z", "refundedAt": null
    },
    "items": [
      { "orderItemId": 4, "variantId": 6, "productName": "Giày chạy bộ Adidas Ultraboost", "sku": "ADS-UB-41-BLK", "sizeName": "41", "colorName": "Đen", "imageUrl": "/uploads/products/2/7a5e90d3.jpg", "unitPrice": 3200000, "quantity": 1, "lineTotal": 3200000 }
    ],
    "statusHistory": [
      { "statusCode": "PENDING", "statusName": "Chờ xác nhận", "changedAt": "2026-11-12T03:00:00Z", "note": null },
      { "statusCode": "CONFIRMED", "statusName": "Đã xác nhận", "changedAt": "2026-11-12T09:00:00Z", "note": null },
      { "statusCode": "CANCELLED", "statusName": "Đã hủy", "changedAt": "2026-11-13T01:00:00Z", "note": "Khách yêu cầu hủy, cần hoàn tiền" }
    ],
    "createdAt": "2026-11-12T03:00:00Z"
  }
}
```
Các trường `payment` (server trả **khoản thanh toán mới nhất** của đơn):

| Trường | Ý nghĩa |
|--------|---------|
| `methodCode`, `methodName` | Phương thức thanh toán |
| `paymentStatus` | Một trong 6 trạng thái thanh toán |
| `amount` | Số tiền phải trả (`Payments.Amount`, chụp từ `Orders.TotalAmount` lúc đặt) |
| `receivedAmount` | Số tiền thực nhận; `null` khi `Pending`, `Failed`, `Expired` |
| `expiresAt` | Hạn thanh toán tuyệt đối; chỉ có với `BANK_TRANSFER`, COD là `null` |
| `isOverdue` | **Trường suy ra, chỉ đọc:** `true` khi `paymentStatus = Pending`, có `expiresAt` và `CurrentTime >= expiresAt` (đồng hồ SQL Server): "quá hạn, chờ hủy", hàng vẫn đang giữ cho tới khi Background Service hủy. Xem `[NEEDS DECISION]` mục 11, điểm 9 |
| `paidAt` | Thời điểm **hệ thống ghi nhận** thanh toán (không phải lúc khách chuyển tiền); với chuyển khoản luôn nhỏ hơn `expiresAt`; `null` nếu chưa thu |
| `refundedAt` | Thời điểm hoàn tiền thực tế **hoàn tất** được xác nhận; chỉ có khi `Refunded` |
| `transactionCode` | **Chỉ có trong response của Admin** (mục 7.4), không có ở API khách hàng |

`statusHistory` chính là dữ liệu cho tính năng **theo dõi trạng thái đơn hàng** (dòng thời gian); dòng do hệ thống tự ghi (hủy do hết hạn) không có người thao tác. Giá/tên/ảnh lấy từ `OrderItems` (đã chụp), không từ sản phẩm hiện tại (BR-09). Đơn không thuộc user → `404`.

#### `POST /api/v1/orders/{orderId}/cancel`

Body: `{ "cancelReason": "Tôi đặt nhầm size" }`. Response `200` trả chi tiết đơn mới (`CANCELLED`, theo cấu trúc ví dụ ở trên).

Chỉ hủy được khi đơn đang `PENDING` (BR-10). Server chạy **quy trình hủy chuẩn** (`database-design.md` mục 6.7.3, một transaction): giành quyền xử lý đơn bằng cập nhật có điều kiện theo `Orders.RowVersion` và trạng thái; cập nhật `Payments` có điều kiện; ghi `OrderStatusHistories`; hoàn kho từng dòng đơn (`InventoryTransactions` loại `OrderRestock`, tối đa một lần cho mỗi `OrderItemId`, BR-19). Kết quả lên thanh toán:

| Khoản thanh toán trước khi hủy | Sau khi hủy |
|--------------------------------|-------------|
| `Pending`, còn hạn hoặc không có `ExpiresAt` (COD) | `Failed` |
| `Pending`, đã `CurrentTime >= ExpiresAt` (quá hạn, job chưa chạy) | `Expired` |
| `Paid` (chuyển khoản đã được Admin xác nhận) | **`RefundPending`** (không phải `Refunded`; chưa hoàn tiền xong, mục 7.4) |

Khi kết quả là `RefundPending`, `message` nên nêu rõ: "Đã hủy đơn. Khoản thanh toán đang chờ hoàn tiền." Nếu cập nhật `Payments` không thành công (ví dụ khoản vừa đổi trạng thái) thì **toàn bộ** thao tác bị ROLLBACK, đơn không bị hủy.

Lỗi: `400 VALIDATION_ERROR` (`cancelReason`), `404`, `409 INVALID_STATUS_TRANSITION` (đơn không còn ở `PENDING`, kể cả khi vừa bị Admin xác nhận/hủy hoặc job hủy xen vào: Backend đọc lại rồi mới chọn mã lỗi), `409 CONCURRENCY_CONFLICT` (hết lượt thử lại).

---

---

## 7. Nhóm API quản trị viên *(tất cả yêu cầu role Admin; ngoài ra 401/403 như mục 3)*

> Đăng nhập trang quản trị dùng `POST /api/v1/auth/login` (mục 5.2).

### 7.1. Quản lý sản phẩm

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/admin/products` | Danh sách (cả đã ẩn). Query: `keyword`, `categoryId`, `brandId`, `isActive`, `page`, `pageSize` |
| GET | `/api/v1/admin/products/{productId}` | Xem chi tiết đầy đủ (kể cả biến thể ẩn, tồn kho) |
| POST | `/api/v1/admin/products` | Thêm sản phẩm (kèm biến thể ban đầu) |
| PUT | `/api/v1/admin/products/{productId}` | Sửa thông tin chung |
| DELETE | `/api/v1/admin/products/{productId}` | Xóa mềm (`IsActive = 0`) |
| POST | `/api/v1/admin/products/{productId}/variants` | Thêm biến thể |
| PUT | `/api/v1/admin/products/{productId}/variants/{variantId}` | Sửa biến thể (giá, SKU, bật/tắt) |
| DELETE | `/api/v1/admin/products/{productId}/variants/{variantId}` | Xóa mềm biến thể |
| POST | `/api/v1/admin/products/{productId}/images` | Upload ảnh (`multipart/form-data`: `ImageFile`, `colorId?`, `isPrimary?`) |
| DELETE | `/api/v1/admin/products/{productId}/images/{imageId}` | Xóa ảnh khỏi sản phẩm (chỉ xóa dòng trong DB, không xóa file ngay) |

`POST /api/v1/admin/products` body:
```json
{
  "productName": "Áo thun chạy bộ Nike Dri-FIT",
  "categoryId": 2,
  "brandId": 1,
  "description": "Chất liệu thoáng khí...",
  "isActive": true,
  "variants": [
    { "sizeId": 2, "colorId": 1, "sku": "NIKE-DF-M-BLK", "price": 450000, "initialStock": 50 },
    { "sizeId": 3, "colorId": 1, "sku": "NIKE-DF-L-BLK", "price": 450000, "initialStock": 30 }
  ]
}
```
Response `201`: sản phẩm kèm các `variantId` mới. Server tự tạo `slug`, tạo `Inventories` (và `InventoryTransactions` loại `Import` nếu `initialStock > 0`).
Lỗi: `400 VALIDATION_ERROR` (danh mục/thương hiệu/size/màu không tồn tại hoặc đang ngừng dùng), `409 DUPLICATE_SKU`, `409 DUPLICATE_VARIANT` (trùng tổ hợp Size+Color).

`PUT /api/v1/admin/products/{productId}/variants/{variantId}` body: `{ "sku": "NIKE-DF-M-BLK", "price": 480000, "isActive": true }`. **Không** sửa tồn kho ở đây (dùng API tồn kho mục 7.3). Đổi giá **không** ảnh hưởng đơn cũ (BR-09). Bật lại biến thể (`isActive = true`) chỉ được khi Size và Color của nó đang dùng, nếu không → `400 VALIDATION_ERROR` (`field = isActive`).

Xóa mềm sản phẩm/biến thể (`IsActive = 0`; **không bao giờ xóa cứng** biến thể hay sản phẩm đã có thể được tham chiếu): sản phẩm biến mất khỏi trang khách; còn trong giỏ của khách thì hiện `isAvailable = false`. Đơn cũ không đổi (đã chụp dữ liệu, BR-09), tồn kho và lịch sử kho được giữ, hủy đơn cũ vẫn hoàn kho cho biến thể đã tắt.

#### `POST /api/v1/admin/products/{productId}/images` – Upload ảnh

Request `multipart/form-data`: `ImageFile` (bắt buộc, **một file mỗi lần**), `colorId` (tùy chọn; phải là màu mà sản phẩm có biến thể), `isPrimary` (tùy chọn, mặc định `false`). Chỉ **Admin** gọi được.

Server kiểm tra và xử lý theo `system-overview.md` mục 5:
1. Kiểm tra theo **nội dung thật** (magic bytes), không tin phần mở rộng hay `Content-Type` do client gửi: chỉ JPG, PNG, WEBP. Sai → `415 UNSUPPORTED_MEDIA_TYPE`. Quá **2 MB** → `413 PAYLOAD_TOO_LARGE`.
2. Đọc kích thước ảnh: quá 4000×4000 px, không giải mã được hoặc đã đủ 10 ảnh của sản phẩm → `400 VALIDATION_ERROR` (hai giới hạn 4000×4000 và 10 ảnh là mặc định chờ duyệt, mục 11).
3. **Giải mã rồi mã hóa lại** ảnh để loại bỏ EXIF và dữ liệu nhúng.
4. Đặt tên file là **GUID do server sinh** kèm phần mở rộng theo loại ảnh thật; không dùng tên file người dùng, **không ghi đè** file cũ, không cho phép đường dẫn tương đối hay `..` (chống path traversal). Lưu vào `/uploads/products/{productId}/{guid}.{ext}`.
5. Tạo dòng `ProductImages`: `ImageUrl` duy nhất; `SortOrder` server gán tiếp theo trong nhóm cùng `colorId`; `isPrimary = true` thì bỏ cờ của ảnh đại diện cũ trong cùng transaction (mỗi sản phẩm tối đa một ảnh đại diện).

Response `201`:
```json
{
  "success": true, "message": "Đã tải ảnh lên.",
  "data": { "imageId": 9, "productId": 1, "colorId": 1, "imageUrl": "/uploads/products/1/3f9c1a7e.jpg", "isPrimary": false, "sortOrder": 2 }
}
```
Lỗi: `400`, `404` (sản phẩm), `413`, `415`, `429 TOO_MANY_REQUESTS` (giới hạn tần suất upload).

#### `DELETE /api/v1/admin/products/{productId}/images/{imageId}` – Xóa ảnh

Chỉ xóa **dòng tham chiếu** trong `ProductImages`; **không xóa file vật lý ngay**. Response `200`: `{ "success": true, "message": "Đã xóa ảnh.", "data": null }`. Ảnh không thuộc sản phẩm → `404`. Ảnh đã chụp trong `OrderItems.ImageUrl` của đơn cũ **không bị hỏng** (BR-28).

Tiến trình nền chỉ dọn file khi **không còn** `ProductImages` **và** `OrderItems.ImageUrl` nào tham chiếu và đã qua thời gian chờ **24 giờ**; nghi ngờ thì giữ file. Việc này không phải API.

### 7.2. Quản lý danh mục, thương hiệu, kích thước, màu sắc

Bốn nhóm có cùng khuôn mẫu CRUD:

| Nhóm | List | Thêm | Sửa | Xóa |
|------|------|------|-----|-----|
| Danh mục | `GET /api/v1/admin/categories` | `POST /api/v1/admin/categories` | `PUT /api/v1/admin/categories/{id}` | `DELETE /api/v1/admin/categories/{id}` |
| Thương hiệu | `GET /api/v1/admin/brands` | `POST /api/v1/admin/brands` | `PUT /api/v1/admin/brands/{id}` | `DELETE /api/v1/admin/brands/{id}` |
| Kích thước | `GET /api/v1/admin/sizes` | `POST /api/v1/admin/sizes` | `PUT /api/v1/admin/sizes/{id}` | `DELETE /api/v1/admin/sizes/{id}` |
| Màu sắc | `GET /api/v1/admin/colors` | `POST /api/v1/admin/colors` | `PUT /api/v1/admin/colors/{id}` | `DELETE /api/v1/admin/colors/{id}` |

Trường nhập:

| Nhóm | Request body |
|------|--------------|
| Danh mục | `{ "categoryName": "Giày chạy bộ", "parentCategoryId": 4, "description": "...", "isActive": true }` |
| Thương hiệu | `{ "brandName": "Nike", "logoUrl": null, "description": "...", "isActive": true }` |
| Kích thước | `{ "sizeName": "XL", "sortOrder": 4, "isActive": true }` |
| Màu sắc | `{ "colorName": "Xanh dương", "hexCode": "#1E40AF", "isActive": true }` |

Response mẫu `POST /api/v1/admin/categories` → `201`:
```json
{ "success": true, "message": "Đã thêm danh mục.", "data": { "categoryId": 5, "categoryName": "Giày chạy bộ", "parentCategoryId": 4, "slug": "giay-chay-bo", "isActive": true } }
```
Quy tắc xóa:
- Danh mục/thương hiệu: xóa mềm. Còn sản phẩm đang bán (hoặc còn danh mục con) → `409 IN_USE`.
- Kích thước/màu sắc: **xóa mềm** (`IsActive = 0`), API **không** xóa cứng (`database-design.md` mục 6.8, BR-23). Chỉ vô hiệu hóa được khi **không còn biến thể đang bán** dùng nó; còn → `409 IN_USE`. Biến thể đã ngừng bán không chặn việc vô hiệu hóa. Size/màu đã tắt: không chọn được cho biến thể mới (`400 VALIDATION_ERROR`), không hiện ở API công khai, vẫn giữ chỗ tên (tạo trùng → `409 DUPLICATE_NAME`); muốn dùng lại thì `PUT` với `isActive = true`. Đơn cũ và biến thể cũ không đổi. Response `DELETE` `200` với `data: null`.
- Các danh sách Admin trả cả bản ghi đã tắt kèm `isActive`; `GET /api/v1/admin/categories` là ngoại lệ không phân trang (mục 1.2).
- Tên trùng → `409 DUPLICATE_NAME`. Danh mục không được chọn chính nó hay con của nó làm cha.

### 7.3. Quản lý tồn kho

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/admin/inventory` | Tồn kho theo biến thể. Query: `keyword` (tên/SKU), `lowStock=true`, `outOfStock=true`, `page`, `pageSize` |
| POST | `/api/v1/admin/inventory/{variantId}/adjustments` | Nhập kho hoặc điều chỉnh |
| GET | `/api/v1/admin/inventory/{variantId}/transactions` | Lịch sử nhập/xuất (phân trang) |

`GET /api/v1/admin/inventory` item mẫu:
```json
{ "variantId": 4, "sku": "NIKE-DF-L-WHT", "productName": "Áo thun chạy bộ Nike Dri-FIT", "sizeName": "L", "colorName": "Trắng", "quantityOnHand": 0, "lowStockThreshold": 5, "isLowStock": true }
```

`POST /api/v1/admin/inventory/{variantId}/adjustments` body:
```json
{ "type": "Import", "quantity": 100, "note": "Nhập lô tháng 11" }
```
- `type = Import`: **cộng thêm** `quantity` (> 0).
- `type = Adjustment`: **đặt lại** tồn kho đúng bằng `quantity` (≥ 0), dùng khi kiểm kê. Server ghi chênh lệch vào `QuantityChange`.
- Response `200`: `{ "variantId": 4, "quantityOnHand": 100 }`. Bắt buộc có `note` khi `Adjustment`. Tồn kho không được âm.
- Tồn kho **chỉ** thay đổi qua API này, đặt hàng (trừ), hủy đơn hoặc hết hạn thanh toán (hoàn).
- `Adjustment` mà `quantity` bằng tồn kho hiện tại (chênh lệch 0) → `400 VALIDATION_ERROR` (database không cho `QuantityChange = 0`). Xung đột với đơn đặt hàng đang chạy: tự thử lại tối đa 3 lần, hết lượt → `409 CONCURRENCY_CONFLICT`.

`GET /api/v1/admin/inventory/{variantId}/transactions` item mẫu (`createdBy = null` nghĩa là do hệ thống, ví dụ hoàn kho khi hủy đơn):
```json
{ "transactionId": 7, "transactionType": "OrderRestock", "quantityChange": 1, "quantityAfter": 6, "orderItemId": 4, "note": null, "createdBy": null, "createdAt": "2026-11-13T01:00:00Z" }
```
`transactionType` thuộc `Import`, `Sale`, `OrderRestock`, `Adjustment`.

### 7.4. Quản lý đơn hàng

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/admin/orders` | Danh sách mọi đơn. Query: `statusCode`, `keyword` (mã đơn/tên/SĐT), `from`, `to`, `paymentStatus` (một trong 6 trạng thái), `page`, `pageSize`, `sort` |
| GET | `/api/v1/admin/orders/{orderId}` | Chi tiết đơn (như mục 6.4 + thông tin khách `customer` + `payment.transactionCode`) |
| PUT | `/api/v1/admin/orders/{orderId}/status` | Cập nhật trạng thái đơn |
| PUT | `/api/v1/admin/orders/{orderId}/payment` | Xác nhận đã nhận tiền chuyển khoản (chỉ `BANK_TRANSFER`) |
| PUT | `/api/v1/admin/orders/{orderId}/refund` | **Mới v1.1.** Xác nhận đã hoàn tiền thực tế (`RefundPending → Refunded`) |

Item mẫu của `GET /api/v1/admin/orders` (như item ở mục 6.4, thêm người đặt và phương thức):
```json
{
  "orderId": 3, "orderCode": "SP20261112-0001",
  "status": { "statusCode": "CANCELLED", "statusName": "Đã hủy" },
  "customer": { "userId": 2, "fullName": "Nguyễn Văn An" },
  "recipientName": "Nguyễn Văn An",
  "totalAmount": 3200000, "totalItems": 1,
  "paymentMethodCode": "BANK_TRANSFER", "paymentStatus": "RefundPending",
  "createdAt": "2026-11-12T03:00:00Z"
}
```
**Danh sách đơn cần hoàn tiền** dùng chính API này: `GET /api/v1/admin/orders?paymentStatus=RefundPending` (không thêm endpoint mới). Mọi response của nhóm này trả chi tiết đơn đầy đủ như `GET /api/v1/admin/orders/{orderId}`, trong đó `payment` có thêm `transactionCode`.

#### `PUT /api/v1/admin/orders/{orderId}/status` – Cập nhật trạng thái đơn

Body:
```json
{ "statusCode": "CONFIRMED", "note": "Đã kiểm tra hàng" }
```
Khi hủy: `{ "statusCode": "CANCELLED", "cancelReason": "Hết hàng thực tế" }`.
Response `200`: chi tiết đơn mới (gồm `statusHistory`).

Quy tắc: chỉ theo luồng `PENDING → CONFIRMED → SHIPPING → DELIVERED`, và `PENDING/CONFIRMED → CANCELLED`. Sai luồng → `409 INVALID_STATUS_TRANSITION`. Mọi lần đổi **bắt đầu bằng giành quyền xử lý đơn** (cập nhật có điều kiện theo `Orders.RowVersion` và trạng thái hiện tại, BR-25); mọi bước sau chỉ chạy khi đã giành được quyền. Hai yêu cầu cùng đổi một đơn: người đến sau nhận `INVALID_STATUS_TRANSITION` nếu trạng thái mới khiến yêu cầu không còn hợp lệ; còn hợp lệ thì Backend tự thử lại tối đa 3 lần, hết lượt → `409 CONCURRENCY_CONFLICT`.

| Chuyển | Điều kiện thêm | Hệ quả lên thanh toán |
|--------|----------------|-----------------------|
| `PENDING → CONFIRMED` | Không | Không đổi |
| `CONFIRMED → SHIPPING` | Đơn `BANK_TRANSFER` phải đã `Paid` (BR-13); chưa `Paid` → `409 INVALID_STATUS_TRANSITION` (message nêu rõ lý do) | Không đổi |
| `SHIPPING → DELIVERED` | Tự điền `DeliveredAt` | **COD:** `Payments` `Pending → Paid` (`ReceivedAmount = Amount`, `PaidAt` theo giờ SQL Server) trong **cùng transaction** (BR-14). Chuyển khoản: khoản đã `Paid` từ trước |
| `PENDING/CONFIRMED → CANCELLED` | Bắt buộc `cancelReason` | **Quy trình hủy chuẩn** như mục 6.4 (`Pending → Failed` hoặc `Expired` nếu đã quá hạn; `Paid → RefundPending`) và hoàn kho đúng một lần (`OrderRestock`) |

Admin **không** đặt được trạng thái `Expired`/`Refunded` ở API này: `Expired` do Background Service (hoặc quy trình hủy khi gặp khoản đã quá hạn), `Refunded` chỉ qua `PUT .../refund`. Các đơn chuyển khoản quá hạn được Background Service tự hủy mà không cần Admin thao tác.

#### `PUT /api/v1/admin/orders/{orderId}/payment` – Xác nhận thanh toán chuyển khoản

Body:
```json
{
  "paymentStatus": "Paid",
  "transactionCode": "FT2611120001",
  "receivedAmount": 480000
}
```
| Trường | Quy tắc |
|--------|---------|
| `paymentStatus` | Bắt buộc, chỉ nhận `Paid` (`Pending → Paid`). Xem `[NEEDS DECISION]` mục 11, điểm 2 về `Pending → Failed` |
| `transactionCode` | Bắt buộc, 1–100 ký tự. Một mã chỉ dùng cho **một** khoản (cùng phương thức) |
| `receivedAmount` | Bắt buộc. Phải **đúng bằng** `Orders.TotalAmount` (đọc từ database, **không** tin số tiền do Frontend tự tính); thiếu hoặc thừa đều bị từ chối |

Response `200`: chi tiết đơn mới; phần `payment` như sau (ví dụ đơn 4 ở mục 6.4):
```json
{
  "methodCode": "BANK_TRANSFER", "methodName": "Chuyển khoản ngân hàng",
  "paymentStatus": "Paid", "amount": 480000, "receivedAmount": 480000,
  "transactionCode": "FT2611120001",
  "expiresAt": "2026-11-15T02:20:00Z", "isOverdue": false,
  "paidAt": "2026-11-14T08:10:00Z", "refundedAt": null
}
```
**Phạm vi và quy tắc nghiệp vụ**
- **Chỉ áp dụng cho `BANK_TRANSFER`.** COD không có thao tác "xác nhận thanh toán" của Admin: khoản COD chỉ `Pending → Paid` tự động khi đơn sang `DELIVERED`. Gọi API này cho đơn COD → `409 INVALID_STATUS_TRANSITION`.
- Việc xác nhận thanh toán và xác nhận đơn (`CONFIRMED`) là **hai hành động riêng**; hệ thống không tự đổi trạng thái đơn khi nhận tiền. Xác nhận thanh toán thực hiện được khi đơn `PENDING` hoặc `CONFIRMED`.
- **Chỉ xác nhận khi `CurrentTime < Payments.ExpiresAt`**, `CurrentTime` là đồng hồ UTC của SQL Server đọc ngay trong câu lệnh cập nhật, bên trong transaction. Từ `CurrentTime >= ExpiresAt` → `409 PAYMENT_EXPIRED`, kể cả khi Background Service chưa chạy và dữ liệu vẫn ghi `Pending`. Khoản đã `Expired` **không bao giờ** chuyển lại thành `Paid`.
- Không xác nhận thanh toán cho đơn đã hủy. Tiền chuyển khoản đến muộn (sau hạn) được **xử lý thủ công**, hệ thống không tự khôi phục giao dịch đã hết hạn. Khách chuyển trong hạn nhưng Admin chỉ xác nhận sau hạn vẫn bị từ chối (hệ quả của chính sách hết hạn tuyệt đối).
- `PaidAt` do hệ thống ghi theo giờ SQL Server, luôn nhỏ hơn `ExpiresAt`.

**Quy trình xử lý** (`system-overview.md` mục 4.3.4, `database-design.md` mục 6.5 và 6.7.2; thứ tự khóa **`Orders` → `Payments` → `Inventories`**):

1. **Đọc sơ bộ, không khóa:** đọc đơn (kèm `RowVersion`) và khoản `Payments` hiện tại bằng mức đọc thông thường, **không giữ khóa**, chưa khóa `Orders`. Chỉ dùng để từ chối sớm các trường hợp **không thể quay lại** (bảng quyết định). **Không bao giờ** trả `200` chỉ dựa trên lần đọc này.
2. **Giành quyền cập nhật `Orders`:** mở transaction, cập nhật có điều kiện `Orders` (ghi `UpdatedAt`) với `RowVersion` đúng giá trị đã đọc **và `OrderStatus IN ('PENDING', 'CONFIRMED')`** (giữ nguyên điều kiện của `database-design.md` mục 6.7.2; **không** đổi thành `!= 'CANCELLED'`). **0 dòng** → ROLLBACK, đọc lại đơn và khoản:
   - đơn đã `CANCELLED` → chọn mã lỗi theo bảng quyết định (khoản `Expired` → `PAYMENT_EXPIRED`; `RefundPending`/`Refunded` → `PAYMENT_ALREADY_CONFIRMED`; `Failed` hoặc không còn khoản chờ → `INVALID_STATUS_TRANSITION`);
   - đơn đã sang `SHIPPING`/`DELIVERED` → xem `[NEEDS DECISION]` mục 11, điểm 1;
   - đơn vẫn `PENDING`/`CONFIRMED` (chỉ `RowVersion` đổi) → quay lại bước 1, **tối đa 3 lần**; hết lượt → `409 CONCURRENCY_CONFLICT`.
3. **Đọc lại `Payments` khi `Orders` đã bị khóa** và áp dụng bảng quyết định lần cuối. **Đây mới là căn cứ cho mọi kết quả `200`.**
4. Khoản `Pending` còn hạn: kiểm tra `receivedAmount` bằng `Orders.TotalAmount` (đọc sau khi đã khóa đơn; sai → ROLLBACK, `409 PAYMENT_AMOUNT_MISMATCH`), rồi **cập nhật có điều kiện `Payments`** `Pending → Paid` với điều kiện `PaymentStatus = 'Pending'` **và** `CurrentTime < ExpiresAt` (đồng hồ SQL Server ngay trong câu lệnh), ghi `ReceivedAmount`, `PaidAt = CurrentTime`, `TransactionCode`, `UpdatedAt`. **0 dòng** → ROLLBACK, đọc lại và áp dụng lại bảng quyết định (thường là `PAYMENT_EXPIRED`). Vi phạm UX mã giao dịch → ROLLBACK, `409 DUPLICATE_TRANSACTION`.
5. COMMIT và trả `200`. **Mọi kết quả khác đều ROLLBACK**, kể cả phần giành quyền ở bước 2, nên đơn không bị ghi gì (cả `UpdatedAt`). Nạn nhân deadlock được thử lại theo quy tắc bước 2.

**Bảng quyết định** (dùng ở bước 1 để từ chối sớm và ở bước 3 để quyết định cuối):

| Trạng thái khoản hiện tại | Dữ liệu yêu cầu so với bản ghi | Kết quả | HTTP / `errorCode` | Kết luận được từ lần đọc sơ bộ? |
|---------------------------|--------------------------------|---------|--------------------|-------------------------------|
| Đơn `COD` | Bất kỳ | Từ chối: COD chỉ `Paid` khi `DELIVERED` | 409 `INVALID_STATUS_TRANSITION` | Có (phương thức không đổi) |
| `Pending`, `CurrentTime < ExpiresAt` | Không xét | Đi tiếp bước 4 | 200 nếu thành công | **Không.** Phải giành quyền và đọc lại |
| `Pending`, `CurrentTime >= ExpiresAt` | Không xét | Từ chối: hết hạn (thứ tự kiểm tra: hạn **trước** số tiền) | 409 `PAYMENT_EXPIRED` | Có (thời gian không quay lại) |
| `Expired` | Bất kỳ | Từ chối: hết hạn | 409 `PAYMENT_EXPIRED` | Có (trạng thái cuối) |
| `Paid` | `transactionCode` **và** `receivedAmount` trùng bản ghi | **Trả kết quả hiện tại, không ghi gì** (idempotent, kể cả sau `ExpiresAt`) | **200** | **Không.** Chỉ trả `200` ở bước 3 |
| `Paid` | Khác `transactionCode` hoặc `receivedAmount` | Xung đột, không đổi gì | 409 `PAYMENT_ALREADY_CONFIRMED` | Có |
| `RefundPending`, `Refunded` | Bất kỳ | Xung đột: đơn đã hủy, khoản đã thu đang/đã được hoàn | 409 `PAYMENT_ALREADY_CONFIRMED` | Có |
| `Failed`, hoặc đơn `CANCELLED` không còn khoản chờ | Bất kỳ | Từ chối | 409 `INVALID_STATUS_TRANSITION` | Có |

**Idempotency.** Gửi lại một yêu cầu xác nhận **đã thành công** không phải xác nhận mới. Chỉ trả `200` khi, **sau khi đã giành quyền `Orders` và đọc lại `Payments`**, khoản hiện tại **vẫn là `Paid`** và `transactionCode` cùng `receivedAmount` trùng bản ghi; khi đó **không ghi gì** (`PaidAt`, `UpdatedAt` giữ nguyên, phần giành quyền được ROLLBACK). Không bao giờ trả `200` dựa trên dữ liệu `Payments` đọc sơ bộ đã cũ. Hai yêu cầu giống hệt chạy đồng thời: một yêu cầu ghi `Paid`, yêu cầu còn lại giành quyền sau khi yêu cầu đầu COMMIT, đọc lại thấy `Paid` trùng khớp → `200`.

Lỗi của API này:

| HTTP | `errorCode` | Khi nào |
|------|-------------|---------|
| 400 | `VALIDATION_ERROR` | Thiếu/sai `transactionCode`, `receivedAmount`; `paymentStatus` khác `Paid` |
| 404 | `NOT_FOUND` | Đơn không tồn tại |
| 409 | `INVALID_STATUS_TRANSITION` | Đơn COD; khoản `Failed`/không còn khoản chờ |
| 409 | `PAYMENT_EXPIRED` | Khoản `Expired`, hoặc `Pending` mà `CurrentTime >= ExpiresAt` |
| 409 | `PAYMENT_AMOUNT_MISMATCH` | `receivedAmount` ≠ `Orders.TotalAmount` (trường lỗi: `receivedAmount`) |
| 409 | `PAYMENT_ALREADY_CONFIRMED` | Khoản đã `Paid` với dữ liệu khác; hoặc đã `RefundPending`/`Refunded` |
| 409 | `DUPLICATE_TRANSACTION` | `transactionCode` đã dùng cho khoản khác |
| 409 | `CONCURRENCY_CONFLICT` | Hết 3 lượt thử lại |
| 401/403 | — | Mục 2.3 (kể cả `TOKEN_REVOKED`, `PASSWORD_CHANGE_REQUIRED`) |

#### `PUT /api/v1/admin/orders/{orderId}/refund` – Xác nhận đã hoàn tiền (mới v1.1)

Quy trình đã thống nhất: **`Paid → RefundPending → Refunded`**. Khi đơn đã thu tiền bị hủy, khoản chuyển sang `RefundPending` ngay trong transaction hủy đơn (mục 6.4, 7.4) và kho đã được hoàn. Việc hoàn tiền thực tế (chuyển khoản trả khách) làm **ngoài hệ thống**. Endpoint này chỉ dùng để Admin **xác nhận sau khi đã hoàn tiền xong**; hệ thống không tự chuyển `RefundPending` thành `Refunded` khi hủy đơn.

Body:
```json
{ "refundCompleted": true }
```
`refundCompleted` bắt buộc là `true`, nghĩa là Admin xác nhận tiền **đã được hoàn thực tế**; thiếu hoặc `false` → `400 VALIDATION_ERROR`. Database hiện chỉ có `RefundedAt` cho việc hoàn tiền, nên **không** có trường mã giao dịch hoàn tiền hay người xác nhận (người xác nhận được ghi vào nhật ký ứng dụng, `system-overview.md` mục 6.7); xem `[NEEDS DECISION]` mục 11, điểm 4.

Response `200`: chi tiết đơn mới; phần `payment`:
```json
{
  "methodCode": "BANK_TRANSFER", "methodName": "Chuyển khoản ngân hàng",
  "paymentStatus": "Refunded", "amount": 3200000, "receivedAmount": 3200000,
  "transactionCode": "FT26111200123",
  "expiresAt": "2026-11-13T03:00:00Z", "isOverdue": false,
  "paidAt": "2026-11-12T08:30:00Z", "refundedAt": "2026-11-14T03:00:00Z"
}
```
Quy tắc:
- **Chỉ Admin** gọi được (`/api/v1/admin/*`, mục 2.2).
- **Chỉ** khoản đang `RefundPending` mới được xác nhận. Hoàn **đủ** `ReceivedAmount` (hoàn một phần chưa hỗ trợ). Khoản `Pending`, `Paid`, `Failed`, `Expired` → `409 INVALID_STATUS_TRANSITION`. Đơn không có khoản "sống" nào (chỉ `Failed`/`Expired`) → `409 INVALID_STATUS_TRANSITION`.
- Cập nhật `Payments` **có điều kiện** `RefundPending → Refunded` (điều kiện `PaymentStatus = 'RefundPending'`), ghi `RefundedAt` theo đồng hồ UTC của SQL Server và `UpdatedAt`. Không đụng `Orders` (đã `CANCELLED`, trạng thái cuối) và không đụng kho (đã hoàn lúc hủy đơn); thứ tự khóa chỉ gồm `Payments`.
- **Idempotency khi gọi lặp:** cập nhật có điều kiện chỉ ghi `Refunded` **một lần**. Nếu cập nhật được 0 dòng thì đọc lại khoản: đã `Refunded` → trả `200` với dữ liệu hiện tại (message "Khoản thanh toán đã được xác nhận hoàn tiền trước đó."), **không ghi lại**, `RefundedAt` giữ nguyên giá trị lần đầu; đang ở trạng thái khác → `409 INVALID_STATUS_TRANSITION`. Trả `200` ở nhánh này là an toàn vì `Refunded` là trạng thái cuối, không thể quay lại.
- Deadlock hoặc xung đột đồng thời: tự thử lại tối đa 3 lần, hết lượt → `409 CONCURRENCY_CONFLICT`.

Lỗi: `400 VALIDATION_ERROR`, `404 NOT_FOUND`, `409 INVALID_STATUS_TRANSITION`, `409 CONCURRENCY_CONFLICT`, và `401`/`403` theo mục 2.3. Tiền đến muộn sau hạn (không có khoản `Paid`) **không** đi qua endpoint này.

### 7.5. Quản lý tài khoản khách hàng

| Method | Endpoint | Mô tả |
|--------|----------|-------|
| GET | `/api/v1/admin/customers` | Danh sách khách. Query: `keyword` (tên/email/SĐT), `isActive`, `page`, `pageSize` |
| GET | `/api/v1/admin/customers/{userId}` | Chi tiết khách + tổng số đơn, tổng chi tiêu (đơn `DELIVERED`) |
| PUT | `/api/v1/admin/customers/{userId}/status` | Khóa/mở khóa tài khoản |

Body khóa: `{ "isActive": false }`. Khóa sẽ thu hồi toàn bộ refresh token của khách. Admin **không** xem được mật khẩu, không sửa được email/mật khẩu của khách (khách tự quản lý). Không khóa được tài khoản Admin bằng API này.
Lưu ý (v1.1): khóa tài khoản thu hồi refresh token nhưng **không tăng `TokenVersion`**, nên access token đã cấp còn hiệu lực tối đa 60 phút (giữ nguyên quyết định hiện tại, `system-overview.md` mục 12.3 điểm chờ 8). `Users.IsActive = 0` làm `login` trả `403 ACCOUNT_LOCKED` và `refresh` trả `401`.

### 7.6. Thống kê doanh thu

`GET /api/v1/admin/statistics/revenue?from=2026-11-01&to=2026-11-30&groupBy=day`

| Query | Mô tả |
|-------|-------|
| `from`, `to` | Khoảng ngày (theo `Orders.DeliveredAt` đổi sang giờ Việt Nam, UTC+7) |
| `groupBy` | `day` (mặc định) hoặc `month` |

Doanh thu **tách riêng 3 chỉ số** theo BR-15 và `database-design.md` mục 9, không gộp thành một con số mơ hồ:

| Trường | Nguồn | Ý nghĩa |
|--------|-------|---------|
| `subtotal` | `SUM(Orders.Subtotal)` | **Tiền hàng**, doanh thu bán hàng thuần (đề xuất dùng làm số "Doanh thu" chính) |
| `shippingFee` | `SUM(Orders.ShippingFee)` | Phí vận chuyển thu của khách |
| `totalAmount` | `SUM(Orders.TotalAmount)` | Tổng khách thanh toán = `subtotal + shippingFee` |

Response `200` (dữ liệu mẫu tháng 11/2026 của `database-design.md`: chỉ đơn 1 đã giao):
```json
{
  "success": true, "message": "OK",
  "data": {
    "from": "2026-11-01", "to": "2026-11-30",
    "summary": { "subtotal": 1180000, "shippingFee": 0, "totalAmount": 1180000, "totalOrders": 1, "averageSubtotal": 1180000 },
    "series": [
      { "period": "2026-11-08", "subtotal": 1180000, "shippingFee": 0, "totalAmount": 1180000, "orderCount": 1 }
    ],
    "ordersByStatus": [
      { "statusCode": "PENDING", "count": 1 },
      { "statusCode": "CONFIRMED", "count": 0 },
      { "statusCode": "SHIPPING", "count": 0 },
      { "statusCode": "DELIVERED", "count": 1 },
      { "statusCode": "CANCELLED", "count": 2 }
    ]
  }
}
```
**Chỉ tính đơn `DELIVERED`** (BR-15; đồng nghĩa đã `Paid`), nhóm theo `DeliveredAt`. Đơn `CANCELLED` (kể cả hết hạn, kể cả chờ hoàn tiền `RefundPending`) và đơn chưa giao **không** được tính; hoàn trả sau khi đã giao chưa hỗ trợ. `averageSubtotal` = `subtotal` / `totalOrders` (0 nếu không có đơn). `ordersByStatus` đếm đơn **tạo** trong khoảng thời gian, để admin có cái nhìn tổng quan (phần này tùy chọn, có thể bỏ nếu muốn gọn).

---

## 8. Bảng đối chiếu API ↔ Database

| API | Bảng đọc/ghi chính | Cột/quy tắc quan trọng |
|-----|-------------------|------------------------|
| Mọi API cần đăng nhập | `Users` (đọc theo khóa chính) | `TokenVersion` so với claim `tv`; `MustChangePassword` (hai cột **chờ bổ sung** vào DB, mục 12) |
| `/api/v1/auth/register` | `Users`, `Carts` | `Email` UQ, `PasswordHash`, role Customer, `MustChangePassword = 0` |
| `/api/v1/auth/login` | `Users`, `RefreshTokens` | `IsActive`, `PasswordHash`, `LastLoginAt`, `TokenVersion`, `MustChangePassword`; ghi `TokenHash`, `ExpiresAt` |
| `/api/v1/auth/refresh` | `RefreshTokens`, `Users` | Đối chiếu `TokenHash`, `RevokedAt IS NULL`, `ExpiresAt`; thu hồi token cũ + tạo token mới; đọc `IsActive`, `TokenVersion`, `MustChangePassword` |
| `/api/v1/auth/logout` | `RefreshTokens` | `RevokedAt` |
| `/api/v1/users/me` (GET, PUT), `/api/v1/users/me/addresses*` | `Users`, `UserAddresses` | `IsDefault` tối đa 1 |
| `PUT /api/v1/users/me/password` | `Users`, `RefreshTokens` | `PasswordHash`, `MustChangePassword = 0`, `TokenVersion + 1`, `UpdatedAt`; `RevokedAt` cho mọi token của user (một transaction) |
| `GET /api/v1/products*` | `Products`, `ProductVariants`, `Inventories`, `ProductImages`, `Categories`, `Brands`, `Sizes`, `Colors` | `minPrice` = MIN(`Price`) biến thể bán được; `inStock` = có `QuantityOnHand > 0`; `quantityAvailable` = `QuantityOnHand` |
| `/api/v1/cart*` | `Carts`, `CartItems`, `ProductVariants`, `Inventories` | UQ(`CartId`,`VariantId`); không lưu giá; `isAvailable` theo BR-22 |
| `POST /api/v1/orders` | `Orders`, `OrderItems`, `Payments`, `OrderStatusHistories`, `Inventories`, `InventoryTransactions`, `CartItems` | Một transaction theo DB 6.4; chụp `UnitPrice`; `Payments.ExpiresAt` (chỉ `BANK_TRANSFER`); `Sale` gắn `OrderItemId` |
| `GET /api/v1/orders*` | `Orders`, `OrderItems`, `Payments`, `OrderStatusHistories`, `OrderStatuses` | Đọc giá từ `OrderItems`; `Payments`: `PaymentStatus`, `ReceivedAmount`, `ExpiresAt`, `PaidAt`, `RefundedAt` |
| `POST /api/v1/orders/{orderId}/cancel` và `PUT /api/v1/admin/orders/{orderId}/status` | `Orders`, `OrderStatusHistories`, `Payments`, `Inventories`, `InventoryTransactions` | BR-10 → BR-14, BR-19, BR-25; `RowVersion`; `Payments`: `Pending → Failed/Expired`, `Paid → RefundPending`; `OrderRestock` |
| `PUT /api/v1/admin/orders/{orderId}/payment` | `Orders`, `Payments` | `Orders.RowVersion`, `StatusId IN (PENDING, CONFIRMED)`, `TotalAmount`; `Payments`: `PaymentStatus`, `ReceivedAmount`, `TransactionCode`, `ExpiresAt`, `PaidAt`, `UpdatedAt`; `UX_Payments_Order_Live`, UX (`PaymentMethodId`, `TransactionCode`), CHECK `PaidAt < ExpiresAt`; BR-20, BR-21, BR-26 |
| `PUT /api/v1/admin/orders/{orderId}/refund` | `Payments` | `PaymentStatus` `RefundPending → Refunded`, `RefundedAt`, `UpdatedAt`; CHECK (`Refunded` ⇔ `RefundedAt` NOT NULL); BR-27; index (`UpdatedAt`) WHERE `RefundPending` |
| `GET /api/v1/admin/orders*` | `Orders`, `OrderItems`, `Payments`, `Users` | Lọc `paymentStatus` theo khoản mới nhất |
| `/api/v1/admin/products*` | `Products`, `ProductVariants`, `ProductImages`, `Inventories` | UQ `Sku`, UQ(`ProductId`,`SizeId`,`ColorId`); xóa mềm `IsActive` (BR-23) |
| `POST/DELETE /api/v1/admin/products/{productId}/images*` | `ProductImages` (đọc thêm `OrderItems.ImageUrl`) | UQ `ImageUrl`, UX (`ProductId`) WHERE `IsPrimary = 1`, UQ(`ProductId`,`ColorId`,`SortOrder`); xóa chỉ xóa dòng DB, BR-28 |
| `/api/v1/admin/categories*`, `/api/v1/admin/brands*` | `Categories`, `Brands` | Xóa mềm `IsActive` (BR-16) |
| `/api/v1/admin/sizes*`, `/api/v1/admin/colors*` | `Sizes`, `Colors` | Xóa mềm `IsActive`; UQ `SizeName`/`ColorName` (kể cả đã tắt); BR-23 |
| `/api/v1/admin/inventory*` | `Inventories`, `InventoryTransactions` | `RowVersion`, `LowStockThreshold`; `TransactionType` `Import`/`Adjustment`; `CreatedBy` |
| `/api/v1/admin/customers*` | `Users`, `Orders`, `RefreshTokens` | `IsActive`; khóa thu hồi `RefreshTokens` |
| `GET /api/v1/admin/statistics/revenue` | `Orders` | `StatusId = DELIVERED`, `DeliveredAt`, `Subtotal`, `ShippingFee`, `TotalAmount` |

Trạng thái đơn trong API dùng `StatusCode` (`PENDING`…), khớp `OrderStatuses.StatusCode`. Trạng thái thanh toán dùng `Pending/Paid/Failed/Expired/RefundPending/Refunded`, khớp `Payments.PaymentStatus`. Loại giao dịch kho: `Import/Sale/OrderRestock/Adjustment`. Background Service (hết hạn chuyển khoản, dọn file ảnh) **không** phải API và không nằm trong danh sách endpoint.

---

## 9. Danh sách endpoint tổng hợp (65 endpoint)

| # | Method | Endpoint | Quyền |
|--:|--------|----------|-------|
| 1 | POST | `/api/v1/auth/register` | Ẩn danh |
| 2 | POST | `/api/v1/auth/login` | Ẩn danh |
| 3 | POST | `/api/v1/auth/refresh` | Cookie |
| 4 | POST | `/api/v1/auth/logout` | Đăng nhập |
| 5 | GET | `/api/v1/users/me` | Đăng nhập |
| 6 | PUT | `/api/v1/users/me` | Đăng nhập |
| 7 | PUT | `/api/v1/users/me/password` | Đăng nhập |
| 8 | GET | `/api/v1/users/me/addresses` | Đăng nhập |
| 9 | POST | `/api/v1/users/me/addresses` | Đăng nhập |
| 10 | PUT | `/api/v1/users/me/addresses/{addressId}` | Đăng nhập |
| 11 | DELETE | `/api/v1/users/me/addresses/{addressId}` | Đăng nhập |
| 12 | GET | `/api/v1/products` | Ẩn danh |
| 13 | GET | `/api/v1/products/{productId}` | Ẩn danh |
| 14 | GET | `/api/v1/categories` | Ẩn danh |
| 15 | GET | `/api/v1/brands` | Ẩn danh |
| 16 | GET | `/api/v1/sizes` | Ẩn danh |
| 17 | GET | `/api/v1/colors` | Ẩn danh |
| 18 | GET | `/api/v1/payment-methods` | Ẩn danh |
| 19 | GET | `/api/v1/cart` | Customer |
| 20 | POST | `/api/v1/cart/items` | Customer |
| 21 | PUT | `/api/v1/cart/items/{cartItemId}` | Customer |
| 22 | DELETE | `/api/v1/cart/items/{cartItemId}` | Customer |
| 23 | DELETE | `/api/v1/cart` | Customer |
| 24 | POST | `/api/v1/orders` | Customer |
| 25 | GET | `/api/v1/orders` | Customer |
| 26 | GET | `/api/v1/orders/{orderId}` | Customer |
| 27 | POST | `/api/v1/orders/{orderId}/cancel` | Customer |
| 28 | GET | `/api/v1/admin/products` | Admin |
| 29 | GET | `/api/v1/admin/products/{productId}` | Admin |
| 30 | POST | `/api/v1/admin/products` | Admin |
| 31 | PUT | `/api/v1/admin/products/{productId}` | Admin |
| 32 | DELETE | `/api/v1/admin/products/{productId}` | Admin |
| 33 | POST | `/api/v1/admin/products/{productId}/variants` | Admin |
| 34 | PUT | `/api/v1/admin/products/{productId}/variants/{variantId}` | Admin |
| 35 | DELETE | `/api/v1/admin/products/{productId}/variants/{variantId}` | Admin |
| 36 | POST | `/api/v1/admin/products/{productId}/images` | Admin |
| 37 | DELETE | `/api/v1/admin/products/{productId}/images/{imageId}` | Admin |
| 38 | GET | `/api/v1/admin/categories` | Admin |
| 39 | POST | `/api/v1/admin/categories` | Admin |
| 40 | PUT | `/api/v1/admin/categories/{id}` | Admin |
| 41 | DELETE | `/api/v1/admin/categories/{id}` | Admin |
| 42 | GET | `/api/v1/admin/brands` | Admin |
| 43 | POST | `/api/v1/admin/brands` | Admin |
| 44 | PUT | `/api/v1/admin/brands/{id}` | Admin |
| 45 | DELETE | `/api/v1/admin/brands/{id}` | Admin |
| 46 | GET | `/api/v1/admin/sizes` | Admin |
| 47 | POST | `/api/v1/admin/sizes` | Admin |
| 48 | PUT | `/api/v1/admin/sizes/{id}` | Admin |
| 49 | DELETE | `/api/v1/admin/sizes/{id}` | Admin |
| 50 | GET | `/api/v1/admin/colors` | Admin |
| 51 | POST | `/api/v1/admin/colors` | Admin |
| 52 | PUT | `/api/v1/admin/colors/{id}` | Admin |
| 53 | DELETE | `/api/v1/admin/colors/{id}` | Admin |
| 54 | GET | `/api/v1/admin/inventory` | Admin |
| 55 | POST | `/api/v1/admin/inventory/{variantId}/adjustments` | Admin |
| 56 | GET | `/api/v1/admin/inventory/{variantId}/transactions` | Admin |
| 57 | GET | `/api/v1/admin/orders` | Admin |
| 58 | GET | `/api/v1/admin/orders/{orderId}` | Admin |
| 59 | PUT | `/api/v1/admin/orders/{orderId}/status` | Admin |
| 60 | PUT | `/api/v1/admin/orders/{orderId}/payment` | Admin |
| 61 | PUT | `/api/v1/admin/orders/{orderId}/refund` | Admin (**mới v1.1**) |
| 62 | GET | `/api/v1/admin/customers` | Admin |
| 63 | GET | `/api/v1/admin/customers/{userId}` | Admin |
| 64 | PUT | `/api/v1/admin/customers/{userId}/status` | Admin |
| 65 | GET | `/api/v1/admin/statistics/revenue` | Admin |

| Nhóm | Số lượng |
|------|---------:|
| Auth | 4 |
| Thông tin cá nhân + địa chỉ | 7 |
| Catalog công khai (products ×2, categories, brands, sizes, colors, payment-methods) | 7 |
| Giỏ hàng | 5 |
| Đơn hàng (Customer) | 4 |
| Admin – sản phẩm, biến thể, ảnh | 10 |
| Admin – danh mục, thương hiệu, size, màu | 16 |
| Admin – tồn kho | 3 |
| Admin – đơn hàng | 5 |
| Admin – khách hàng | 3 |
| Admin – thống kê | 1 |
| **Tổng** | **65** |

---

## 10. Nhật ký thay đổi v1.0 → v1.1

| # | Nội dung | Mục |
|---|----------|-----|
| 1 | Refresh token chuyển sang cookie `HttpOnly`/`Secure`/`SameSite=Strict`/`Path=/api/v1/auth`: `login` và `refresh` không trả refresh token trong JSON (bỏ cả `refreshTokenExpiresAt`), `refresh`/`logout` đọc cookie, không nhận body; thêm quy tắc cookie, CORS, credentials, `X-Requested-With`, `Origin` | 1, 2.4, 2.5, 5.2–5.4 |
| 2 | Thêm `MustChangePassword` và `TokenVersion`: claims `tv`, `iat`; `user.mustChangePassword`; quy trình kiểm tra 5 bước mỗi request; `401 TOKEN_REVOKED`; `403 PASSWORD_CHANGE_REQUIRED`; 4 API được phép; đổi mật khẩu tăng `TokenVersion` và thu hồi mọi refresh token; mục CLI đặt lại mật khẩu Admin (không có API) | 2.1, 2.3, 5.5, 6.1 |
| 3 | `PUT .../api/v1/orders/{orderId}/payment`: thêm `receivedAmount`; chỉ `BANK_TRANSFER`; hết hạn tuyệt đối `PAYMENT_EXPIRED`; giữ nguyên `StatusId IN (PENDING, CONFIRMED)`; đọc sơ bộ → giành quyền → đọc lại `Payments`; idempotency; bảng quyết định; retry/rollback | 7.4 |
| 4 | Thêm `PUT .../api/v1/orders/{orderId}/refund` (`RefundPending → Refunded`, `RefundedAt`, idempotent). Tổng endpoint 64 → 65 | 7.4, 9 |
| 5 | Đồng bộ 6 trạng thái thanh toán; thêm mã lỗi `TOKEN_REVOKED`, `PASSWORD_CHANGE_REQUIRED`, `PAYMENT_EXPIRED`, `PAYMENT_AMOUNT_MISMATCH`, `PAYMENT_ALREADY_CONFIRMED`, `DUPLICATE_TRANSACTION`, `CONCURRENCY_CONFLICT`, `PAYLOAD_TOO_LARGE`, `UNSUPPORTED_MEDIA_TYPE`, `TOO_MANY_REQUESTS`; danh mục mã lỗi 1 mã = 1 HTTP | 3, 4, 6.4, 7.4, 8 |
| 6 | `payment.expiresAt` (BANK_TRANSFER; COD `null`), `payment.amount/receivedAmount/isOverdue/refundedAt`, `cancelReason` trong chi tiết đơn; ví dụ `RefundPending` | 6.4 |
| 7 | Trình tự `POST /api/v1/orders` sửa đúng DB 6.4 (trừ kho có điều kiện trước, tạo đơn sau); thêm retry `CONCURRENCY_CONFLICT`; hủy đơn theo quy trình 6.7.3 (`Failed`/`Expired`/`RefundPending`) | 6.4, 7.4 |
| 8 | Upload/xóa ảnh theo `system-overview.md` mục 5 và DB 6.9.1: magic bytes, 2 MB, 4000×4000, re-encode bỏ EXIF, tên GUID, không ghi đè, chống path traversal, ≤ 10 ảnh; xóa ảnh chỉ xóa dòng DB, file dọn sau ≥ 24 giờ | 7.1 |
| 9 | Size/Color: xóa cứng → **xóa mềm** (`IsActive`), thêm `isActive` vào body; biến thể không bao giờ xóa cứng; Size/Color tắt không dùng cho biến thể mới | 6.2, 7.1, 7.2 |
| 10 | Rà soát: ngoại lệ phân trang cho danh mục dạng cây; tiền tố `/api/v1` mọi endpoint; doanh thu tách `subtotal`/`shippingFee`/`totalAmount` (BR-15); tồn kho `Adjustment` không đổi → 400; sổ kho mẫu; sửa bảng bị vỡ do ký tự `\|` trong code ở mục 8; ví dụ đơn hàng khớp dữ liệu mẫu DB (v1.0 trùng mã `SP20261112-0001` với đơn khác) | 1.2, 7.3, 7.6, 8 |

---

## 11. Điểm cần quyết định `[NEEDS DECISION]`

| # | Nội dung | Căn cứ mâu thuẫn / thiếu | Cách đang áp dụng trong v1.1 |
|---|----------|--------------------------|------------------------------|
| 1 | **Gửi lại xác nhận thanh toán khi đơn đã `SHIPPING`/`DELIVERED`** | `system-overview.md` 4.3.4 dùng điều kiện giành quyền "đơn chưa `CANCELLED`" để khóa được đơn và trả `200` idempotent; `database-design.md` 6.7.2 và yêu cầu của bạn giữ `StatusId IN (PENDING, CONFIRMED)`. Với điều kiện này, đơn đã `SHIPPING`/`DELIVERED` **không** giành được quyền, nên không có bước "đọc lại sau khi khóa đơn" | Giữ `IN (PENDING, CONFIRMED)`. Tạm trả `409 INVALID_STATUS_TRANSITION`, **không** trả `200` dựa trên lần đọc sơ bộ; Frontend gọi `GET .../api/v1/orders/{orderId}` để thấy khoản đã `Paid`. Cần bạn chọn: giữ như vậy, hoặc cho phép đọc lại trong transaction mới (khoản `Paid` của đơn đã giao không thể đổi) |
| 2 | **`Pending → Failed` qua API thanh toán** | v1.0 và yêu cầu nêu "Pending → Paid hoặc Failed", nhưng `database-design.md` 3.6 chỉ cho `Failed` khi **hủy đơn** (cùng transaction hủy); đặt `Failed` riêng sẽ để đơn `PENDING` giữ hàng với khoản chết | `paymentStatus` chỉ nhận `Paid`; `Failed` → `400`. Muốn "không thu được" thì hủy đơn qua `PUT .../status` |
| 3 | **HTTP của `PAYMENT_AMOUNT_MISMATCH`** | `system-overview.md` 4.3.4 ghi `400 VALIDATION_ERROR` cho số tiền sai; yêu cầu v1.1 thêm mã riêng, cho phép 409 | `409` (so với trạng thái đơn trong DB, giống các lỗi nghiệp vụ khác của API này). Phương án khác: `400` |
| 4 | **Thông tin hoàn tiền** | DB chỉ có `RefundedAt` (`database-design.md` 13.2 điểm 4; `system-overview.md` 12.3 điểm 5): chưa có mã giao dịch hoàn tiền, người xác nhận | Body chỉ có `refundCompleted: true`; người xác nhận ghi nhật ký ứng dụng. Không tạo cột |
| 5 | **Mã lỗi và header chống CSRF; mã lỗi COD** | `system-overview.md` chưa chọn mã cho thiếu `X-Requested-With`/`Origin` sai, giá trị header, và mã khi COD gọi xác nhận thanh toán (điểm chờ 2) | `403 FORBIDDEN`, `415` sai `Content-Type`, giá trị header đề xuất `XMLHttpRequest`; COD → `409 INVALID_STATUS_TRANSITION` |
| 6 | **`logout` khi access token đã hết hạn hoặc bị thu hồi** | v1.0 yêu cầu đăng nhập; refresh token nằm ở cookie nên có thể cho `logout` chạy chỉ bằng cookie | Giữ yêu cầu access token. Frontend: nếu `logout` trả `401`, gọi `refresh` rồi `logout` lại một lần; vẫn lỗi thì xóa trạng thái cục bộ |
| 7 | **Chính sách mật khẩu Admin khi đổi** | CLI yêu cầu ≥ 12 ký tự cho Admin; v1.0 chung 8–64 | Giữ 8–64 cho `PUT /api/v1/users/me/password` |
| 8 | **Ảnh sản phẩm** | Không có: ảnh đầu tiên có tự thành ảnh đại diện không; endpoint đổi ảnh đại diện/thứ tự mà không upload lại (DB 3.2 cho phép sửa); 10 ảnh và 4000×4000 mới là mặc định chờ duyệt | `isPrimary` mặc định `false`; không thêm endpoint; dùng các giá trị mặc định |
| 9 | **Cờ `payment.isOverdue`** | `system-overview.md` 12.2 điểm 5 chỉ nêu "có thể thêm" | Có thêm (trường suy ra, chỉ đọc); có thể bỏ |
| 10 | **Xóa cứng Size/Color chưa dùng** | DB BR-23 cho phép; yêu cầu ưu tiên vô hiệu hóa | API không xóa cứng; dọn dữ liệu thử thực hiện ngoài API |
| 11 | **Danh sách Admin brands/sizes/colors có phân trang không** | v1.0 chỉ ghi "List" | Phân trang theo mục 1.2; `GET /api/v1/admin/categories` là ngoại lệ không phân trang |

---

## 12. Cần cập nhật ngược vào tài liệu khác

**`database-design.md` (v1.2)**

| # | Nội dung |
|---|----------|
| 1 | **Bắt buộc:** thêm `Users.MustChangePassword` (BIT NN, mặc định 0) và `Users.TokenVersion` (INT NN, mặc định 0) vào mục 3.1, ERD, quan hệ, BR (`TokenVersion` tăng khi đổi mật khẩu và khi CLI đặt lại, không tăng khi login/refresh/logout); dữ liệu mẫu Admin `MustChangePassword = 1` |
| 2 | Mục 6.5 và 6.7.2: bỏ trả `Paid` idempotent từ lần đọc sơ bộ (đọc lại sau khi giành quyền `Orders`), nêu rõ đọc sơ bộ không giữ khóa. **Điều kiện giành quyền: giữ `IN (PENDING, CONFIRMED)`** (xem điểm 1 mục 11) |
| 3 | Mục 13.3: đánh dấu các điểm lệch của `api-contract.md` đã xử lý ở v1.1 |

**`system-overview.md` (v1.5)**

| # | Nội dung |
|---|----------|
| 4 | Mục 4.3.4 và 12.3 điểm chờ 1: đổi điều kiện giành quyền từ "đơn chưa `CANCELLED`" sang `IN (PENDING, CONFIRMED)` theo quyết định đã chọn, và sửa nhánh gửi lại sau `SHIPPING`/`DELIVERED` theo điểm 1 mục 11 |
| 5 | Mục 4.3.4: số tiền sai `400 VALIDATION_ERROR` → `409 PAYMENT_AMOUNT_MISMATCH` (nếu chọn 409). Chốt tên mã `PAYMENT_ALREADY_CONFIRMED`, `DUPLICATE_TRANSACTION`, `TOKEN_REVOKED`, `PASSWORD_CHANGE_REQUIRED` |
| 6 | Mục 4.3.4: bảng quyết định (khoản `Expired`/`RefundPending` → `PAYMENT_EXPIRED`/`PAYMENT_ALREADY_CONFIRMED`) lệch với sơ đồ (đơn `CANCELLED` → `INVALID_STATUS_TRANSITION`); v1.1 chọn mã theo trạng thái khoản trước |
| 7 | Mục 12.2 điểm 1–11 (danh sách việc của `api-contract.md`) có thể đánh dấu đã xử lý |

---

## 13. Đối chiếu và tự kiểm tra

| Thay đổi trong v1.1 | `system-overview.md` v1.5 | `database-design.md` v1.2 |
|---------------------|---------------------------|---------------------------|
| Cookie refresh token, CORS, CSRF | 2.2–2.5 | `RefreshTokens` (3.1) |
| `tv`, `TOKEN_REVOKED`, `MustChangePassword`, 4 API được phép | 2.7, 6.5, 6.6 | **Chưa có** hai cột (mục 12) |
| Xác nhận thanh toán, bảng quyết định, idempotency | 3.2, 4.3.2, 4.3.4 | 6.5, 6.7.1, 6.7.2, BR-20, BR-21, BR-25, BR-26 |
| Xác nhận hoàn tiền | 3.2, 4.3.3 | 3.6, 6.7.5, BR-27 |
| Hủy đơn, `RefundPending` | 3.5 | 6.7.3, BR-11, BR-19 |
| `expiresAt` | 4.3.1 | `Payments.ExpiresAt`, G10 |
| Upload/xóa ảnh | 5.1–5.4 | 6.9.1, BR-28 |
| Size/Color/Variant xóa mềm | — | 6.8, BR-22, BR-23 |
| Doanh thu | — | 9, BR-15 |

Kiểm tra nhất quán (đã chạy trên file này): mọi khối JSON hợp lệ; mọi hàng bảng đủ số cột; không còn endpoint thiếu tiền tố `/api/v1`; 65 endpoint ở mục 9 khớp số liệu theo nhóm; mọi `errorCode` dùng trong tài liệu có trong mục 3.2; trạng thái thanh toán thống nhất 6 giá trị; điều kiện giành quyền xác nhận thanh toán thống nhất `IN (PENDING, CONFIRMED)`.
