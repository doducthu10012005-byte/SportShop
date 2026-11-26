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

API Development được cấu hình trong `js/config.js`, mặc định là `https://localhost:7132/api/v1`. Backend Development đã cho phép origin `https://localhost:5500`.

## Xác thực

Mở `https://localhost:5500/account/` để đăng nhập hoặc đăng ký Customer. Sau khi đăng nhập, trang tài khoản hiển thị hồ sơ từ Backend và cho phép đổi mật khẩu hoặc đăng xuất. Nếu tài khoản phải đổi mật khẩu, Frontend mở thẳng biểu mẫu đổi mật khẩu. Đăng nhập lại bằng mật khẩu mới sau khi đổi thành công.

`js/auth.js` giữ access token trong bộ nhớ và khôi phục phiên bằng cookie do Backend cấp. Với các yêu cầu cần xác thực, dùng `authorizedRequest` để dùng chung lần refresh khi có nhiều phản hồi 401 và thử lại tối đa một lần. Frontend dùng khóa trình duyệt để đồng bộ refresh và logout giữa các tab khi Web Locks có sẵn. Trình duyệt thiếu Web Locks chỉ đồng bộ các thao tác trong từng tab. Dấu hiệu phiên trong `localStorage` là giá trị ngẫu nhiên, dùng để nhận biết và đồng bộ phiên giữa các tab.

Để thử thủ công, dùng tài khoản Customer và Admin có sẵn trong Backend thử nghiệm riêng. Thử đăng nhập, tải lại trang tài khoản để kiểm tra khôi phục phiên, rồi mở `/admin/` với từng vai trò. Thử đổi mật khẩu bằng tài khoản thử và kiểm tra đăng nhập lại. Sau khi đăng xuất, tải lại trang để xác nhận phiên đã kết thúc.

## Danh mục sản phẩm

Mở `https://localhost:5500/` để xem tối đa bốn sản phẩm mới nhất hoặc vào `https://localhost:5500/products/` để tìm kiếm và lọc. Chọn một thẻ để xem chi tiết, ảnh, màu, kích thước, giá và tồn kho của từng biến thể.

Các trang dùng API Public Catalog trên Backend đang cấu hình. Ảnh sản phẩm được tải từ origin của Backend. Để duyệt dữ liệu giả mà không cần Backend hoặc SQL Server, chạy bộ kiểm thử Catalog bên dưới.

## Giỏ hàng

Đăng nhập bằng tài khoản Customer trong môi trường thử, mở chi tiết sản phẩm còn hàng, chọn biến thể và số lượng rồi bấm **Thêm vào giỏ**. Mở `https://localhost:5500/cart/` hoặc bấm biểu tượng giỏ để xem giá hiện tại, sửa số lượng, xóa từng dòng hoặc xóa toàn bộ sau khi xác nhận. Số lượng trên biểu tượng và tạm tính lấy từ phản hồi của Backend. Giỏ hàng không giữ tồn kho.

## Đặt hàng

Từ giỏ hàng có đủ hàng, chọn **Tiến hành đặt hàng** hoặc mở `https://localhost:5500/checkout/`. Nhập thông tin nhận hàng hoặc chọn một địa chỉ đã lưu và kiểm tra lại trước khi chọn phương thức thanh toán. Backend chốt phí giao hàng và tổng tiền khi tạo đơn. Với chuyển khoản, chỉ dùng hướng dẫn hiển thị trong kết quả đơn hàng đã xác nhận.

Nếu mất kết nối khi gửi đơn, hãy xác minh kết quả trước khi tự đặt lại. Giao diện không tự gửi lại yêu cầu tạo đơn sau lỗi mạng. Chạy bộ kiểm thử Checkout giả lập để trải nghiệm luồng COD và chuyển khoản bằng dữ liệu thử.

## Đơn hàng của tôi

Đăng nhập bằng tài khoản Customer, rồi chọn **Đơn hàng của tôi** trong trang tài khoản hoặc mở `https://localhost:5500/orders/`. Sau khi đặt hàng thành công, có thể mở chi tiết ngay từ kết quả Checkout. Trang chi tiết hiển thị thông tin đã lưu khi đặt hàng và cho phép gửi lý do hủy nếu đơn đang chờ xác nhận.

## Kiểm thử Chromium với API giả lập

Dùng chứng chỉ HTTPS đã xuất ở trên. Bộ kiểm thử cần Chrome hoặc Chromium và Python 3.14. Hai máy chủ HTTPS dùng cổng ngẫu nhiên trên localhost và dữ liệu giả lập.

~~~powershell
python frontend/tests/auth_browser.py --cert $frontendCert --key $frontendKey
python frontend/tests/catalog_browser.py --cert $frontendCert --key $frontendKey
python -B frontend/tests/auth_browser.py --cart --cert $frontendCert --key $frontendKey
python -B frontend/tests/auth_browser.py --checkout --cert $frontendCert --key $frontendKey
python -B frontend/tests/auth_browser.py --orders --cert $frontendCert --key $frontendKey
python -B frontend/tests/auth_browser.py --dashboard --cert $frontendCert --key $frontendKey
python -B frontend/tests/auth_browser.py --admin-catalog --cert $frontendCert --key $frontendKey
~~~

## Tổng quan quản trị

Mở `https://localhost:5500/admin/` và đăng nhập bằng tài khoản Admin trong môi trường thử. Dashboard mặc định xem bảy ngày gần nhất, tính cả ngày hôm nay theo giờ Việt Nam. Bộ lọc doanh thu hỗ trợ tối đa 366 ngày và nhóm theo ngày hoặc tháng. Bảng đơn gần đây hiển thị tối đa năm đơn và có nút tải lại riêng.

### Quản lý sản phẩm, danh mục và thương hiệu

Từ Dashboard, chọn **Sản phẩm**, **Danh mục** hoặc **Thương hiệu**. Có thể mở thẳng `https://localhost:5500/admin/catalog/`. Trang sản phẩm tìm theo tên hoặc SKU của biến thể và hỗ trợ lọc theo danh mục, thương hiệu và trạng thái. Khi tạo sản phẩm, nhập ít nhất một biến thể ban đầu. Dùng **Ẩn** để ngừng hoạt động và chọn **Đang hoạt động** trong form sửa để bật lại. API kiểm tra tham chiếu khi ẩn danh mục hoặc thương hiệu.

Nếu giao diện chưa xác nhận được kết quả ghi, kiểm tra lại danh sách trước khi gửi thao tác mới.
