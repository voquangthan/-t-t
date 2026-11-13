# Không gian trò chuyện Đệ Tốt

Chạy giao diện dev từ PowerShell:

```powershell
Set-Location 'D:\-tui\detot-lab\frontend'
npm ci
npm run dev
```

Mở `http://127.0.0.1:5173/`. Để bật chat, mở PowerShell riêng và chạy `& 'D:\-tui\detot-lab\Start-Lab.ps1'`. Nhập khóa DeepSeek tại ô nhập ẩn và giữ cửa sổ này mở. Vite chuyển tiếp API sang ASP.NET Core tại cổng 5183.

Để chạy bản build, dùng `npm run build` trong thư mục frontend rồi mở `http://127.0.0.1:5183/workspace` khi lab đang chạy. Dùng `npm test` để chạy kiểm thử frontend. Bật `DETOT_ENABLE_CALENDAR_TRIAL` để dùng luồng lịch thử. Chỉ nhập dữ liệu tổng hợp.

Lịch sử nằm trong trình duyệt. Xóa bản lưu chỉ xóa bản sao trên trình duyệt. Mỗi lượt chat thường gửi tối đa tám tin gần nhất làm ngữ cảnh. API hiện trả JSON khi phản hồi hoàn tất.

## Gmail

Bật Gmail API trong Google Cloud và tạo OAuth client loại Web. Đăng ký callback theo địa chỉ workspace. Với cổng 5184 trên laptop, dùng `http://127.0.0.1:5184/api/google/callback`. Nạp file JSON bằng `Start-Lab.ps1 -WebPort 5184 -ImportGoogleOAuth <đường-dẫn>` hoặc đặt `DETOT_GOOGLE_CLIENT_ID` và `DETOT_GOOGLE_CLIENT_SECRET` khi khởi động. Gmail tự bật khi backend nạp đủ cấu hình.

Backend mã hóa thông tin tài khoản và refresh token trong `.local/google` bằng ASP.NET Core Data Protection với khóa bảo vệ bằng Windows DPAPI. Menu hỗ trợ nhiều tài khoản, chọn tài khoản tìm thư và thu hồi quyền. Kết quả tìm kiếm hiển thị người gửi và tiêu đề. Nội dung thư không được chuyển tới LLM.

## Dùng từ máy khác

Đặt một địa chỉ HTTPS cố định cho laptop, ví dụ `https://detot.example.com`. Cấu hình reverse proxy trên laptop để kết thúc TLS và chuyển tiếp đến `http://127.0.0.1:5183` (hoặc cổng đã chọn bằng `-WebPort`), giữ nguyên header `Host` công khai. Chỉ cấp quyền truy cập proxy cho các thiết bị được phép. Laptop cần bật và có mạng trong lúc sử dụng.

Trong Google Cloud, thêm callback Web OAuth `https://detot.example.com/api/google/callback` cho đúng địa chỉ HTTPS. Nếu dùng JSON tải từ Google, nạp cấu hình trên laptop từ thư mục `detot-lab`:

```powershell
& .\Start-Lab.ps1 -PublicOrigin 'https://detot.example.com' -ImportGoogleOAuth 'C:\path\google-oauth.json'
```

Đặt mật khẩu ngẫu nhiên dài ít nhất 24 ký tự qua biến môi trường của phiên khởi động:

```powershell
$password = Read-Host 'Mật khẩu Đệ Tốt' -AsSecureString
$env:DETOT_REMOTE_PASSWORD = [Net.NetworkCredential]::new('', $password).Password
$password.Dispose()
try {
    & .\Start-Lab.ps1 -PublicOrigin 'https://detot.example.com'
} finally {
    Remove-Item Env:DETOT_REMOTE_PASSWORD -ErrorAction SilentlyContinue
}
```

Mở `https://detot.example.com/workspace` từ máy được phép rồi đăng nhập. Phiên hết hạn sau 15 phút không có yêu cầu đến server hoặc sau 8 giờ, và bị hủy trên server khi đăng xuất. Token, tài khoản Gmail đã chọn và ghi chú mới được lưu mã hóa trên laptop. Ghi chú từ bản cũ còn nằm trong trình duyệt từng dùng và cần sao chép thủ công nếu muốn giữ lại. Workspace HTTPS tự lưu lịch sử chat mã hóa trên laptop. Khi mở workspace từ máy khác, lịch sử được tải lại vào bộ nhớ trình duyệt. Khi dùng máy dùng chung, đóng toàn bộ cửa sổ riêng tư sau khi đăng xuất để trình duyệt dọn dữ liệu của phiên riêng tư.
