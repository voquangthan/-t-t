# Không gian trò chuyện Đệ Tốt

Từ thư mục gốc của repo, chạy:

```powershell
npm --prefix detot-lab/frontend ci
npm --prefix detot-lab/frontend run build
npm --prefix detot-lab/frontend test
```

Khởi động lab bằng quy trình hiện có và mở `http://127.0.0.1:5183/workspace`. ASP.NET Core phục vụ cả frontend và API cùng origin. Bật `DETOT_ENABLE_CALENDAR_TRIAL` để dùng luồng lịch thử. Chỉ nhập dữ liệu tổng hợp.

Lịch sử nằm trong trình duyệt. Xóa bản lưu chỉ xóa bản sao trên trình duyệt. Mỗi lượt chat thường gửi tối đa tám tin gần nhất làm ngữ cảnh. API hiện trả JSON khi phản hồi hoàn tất.
