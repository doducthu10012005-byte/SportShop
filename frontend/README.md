# SportShop Frontend

Từ thư mục gốc của dự án, dùng .NET SDK đã cài và Python 3.14 trở lên để chạy máy chủ tĩnh HTTPS. Kiểm tra chứng chỉ Development bằng `dotnet dev-certs https --check --trust`. Nếu máy chưa tin cậy chứng chỉ, thiết lập bằng `dotnet dev-certs https --trust` trước khi xuất.

~~~powershell
$frontendTls = Join-Path $env:TEMP "SportShop-FrontendTls"
New-Item -ItemType Directory -Force $frontendTls | Out-Null
$frontendCert = Join-Path $frontendTls "localhost.pem"
$frontendKey = Join-Path $frontendTls "localhost.key"
dotnet dev-certs https --export-path $frontendCert --format PEM --no-password
python -m http.server 5500 --bind 127.0.0.1 --directory frontend --tls-cert $frontendCert --tls-key $frontendKey
~~~

Mở `https://localhost:5500/` cho cửa hàng hoặc `https://localhost:5500/admin/` cho giao diện quản trị. Dùng tên `localhost` cho cả hai origin. Giữ chứng chỉ và khóa riêng trong thư mục tạm ngoài Git.

API Development được cấu hình trong `js/config.js`, mặc định là `https://localhost:7132/api/v1`. Khi Backend chạy, nút **Kiểm tra kết nối** ở trang quản trị gọi `GET /payment-methods` để kiểm tra phản hồi. Backend Development đã cho phép origin `https://localhost:5500`.

`js/api.js` cung cấp `request`, `ApiError`, `setAccessToken` và `clearAccessToken`. Truyền đường dẫn tính từ `/api/v1`, ví dụ `request("/payment-methods", { auth: false })`. Phương thức trả JSON envelope và ném `ApiError` khi thất bại. Các trang đăng nhập, khôi phục phiên và đăng xuất sẽ sử dụng lớp này ở giai đoạn tiếp theo.
