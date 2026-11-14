# Đệ Tốt AI Lab

Frontend và ASP.NET Core cùng origin tại `http://127.0.0.1:5183/`. Backend gọi OpenResponses của Gateway tại `127.0.0.1:18793`. Đây là lựa chọn cho lab một người dùng để kiểm tra client-side function tools qua hai lượt. Gateway có bearer token ngẫu nhiên theo lần chạy. Bearer HTTP của OpenClaw có quyền operator đầy đủ, nên token chỉ ở backend và Gateway, với cổng loopback riêng cho lab. Backend tự khai báo và chỉ thực thi `detot_lab_fixture`, trả mã cố định `DETOT-LAB-003`. Frontend hiển thị text, không render HTML từ model.

Nút **Ca trực giả lập** chạy một chế độ riêng, không gọi Gateway hay tool. Chế độ này mở một tình huống IT hư cấu (Helpdesk/máy in, Windows DNS, SQL connection pool, API token, Docker env hoặc tài khoản dịch vụ), giữ trạng thái theo cuộc trò chuyện và chỉ trả log/kết quả dựng sẵn. Thu thập ít nhất hai dữ kiện rồi nêu nguyên nhân để nhận phản hồi; nói “kết thúc ca” để xem lời giải. Không có lệnh, sửa hệ thống, database hay thao tác Git nào được thực thi. Mã `DETOT-LAB-003` chỉ thuộc bài thử function tool ở chế độ chat thường.

Sau khi kết thúc ca, hồ sơ tự học chỉ lưu số tổng hợp theo chủ đề (ca hoàn tất, chẩn đoán, lượt kiểm tra, bàn giao và kết thúc sớm), được mã hóa cục bộ. Đệ Tốt ưu tiên chủ đề chưa luyện hoặc có điểm yếu; sau các ca đạt yêu cầu, tăng số dữ kiện cần kiểm tra từ 2 lên 3. Hồ sơ không lưu nội dung chat, log hoặc mã nguồn; mục **Ghi chú học tập** vẫn dành cho ghi chú do người dùng tự nhập.

## Môi trường

Git 2.56.1 đã có trên máy. Node.js 24.22.3, .NET SDK 10.0.404 và OpenClaw 2026.11.3 được đặt trong `.local/`. OpenClaw chạy trực tiếp trên Windows vì trình cài WSL trả mã 1603. Binding fs-safe x64 đã nạp thành công sau khi bổ sung DLL runtime có chữ ký Microsoft riêng trong lab.

## Chạy thử

Trong Windows PowerShell 5.1 hoặc PowerShell 7, mở thư mục này và chạy bản lab đã build:

```powershell
./Start-Lab.ps1
```

Nhập API key cấp trực tiếp bởi DeepSeek tại prompt ẩn cục bộ. Gateway dùng `deepseek-flash` và nhận key qua biến môi trường của tiến trình. Script dùng state riêng trong `.state/`, có thể chứa hội thoại thử. Chỉ dùng dữ liệu giả.

Sau khi đổi cấu hình provider, dừng lab bằng Ctrl+C trong terminal cũ rồi chạy lại launcher để Gateway nhận key của phiên mới.

Mở [trang lab](http://127.0.0.1:5183/) và gửi câu đã điền. Đạt khi thấy “Đã gọi công cụ giả lập” và câu trả lời có `DETOT-LAB-003`. Ctrl+C tại terminal chạy script để dừng.

Ghim OpenClaw `2026.11.3` cho mục đích thử nghiệm trên loopback. Cấu hình tắt Control UI, memory search, cron, heartbeat, Tool Search, Code Mode và tool truy cập máy. ASP.NET Core từ chối Origin ngoài địa chỉ lab và không chuyển tiếp URL hoặc tool tùy ý. Bản Windows dùng chung ranh giới tài khoản Windows của người chạy. Khi WSL2 sẵn sàng, có thể chuyển Gateway sang môi trường tách riêng sau khi kiểm tra lại loopback và quyền mount.

## Kết quả kiểm thử

- Build ASP.NET Core thành công, 0 lỗi và 0 cảnh báo.
- Edge headless gửi câu mẫu qua ASP.NET Core, Gateway OpenClaw thật và provider giả lập trên loopback. Provider chỉ nhận `detot_lab_fixture`. Frontend hiển thị `DETOT-LAB-003`.
- Backend từ chối origin khác và tên tool ngoài allowlist.
- DeepSeek thật còn chờ key nhập ở terminal. WSL2 còn chờ cài đặt bằng quyền quản trị.
