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
