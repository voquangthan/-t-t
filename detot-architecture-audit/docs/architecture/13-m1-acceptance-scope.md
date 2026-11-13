# Phạm vi nghiệm thu M1 trên Windows

Chốt theo cấu hình Đệ Tốt một người dùng, chạy qua `Start-Lab.ps1`. Dùng dữ liệu tổng hợp trên cấu hình này cho tới khi các ca bắt buộc đạt. Kiểm tra backup và archive giới hạn ở các vị trí do Đệ Tốt quản lý hoặc được cấu hình rõ.

| Kho quản lý | Phạm vi kiểm tra |
|---|---|
| Gateway `detot-lab/.state` | State SQLite và sidecar, session/transcript, continuation, archive/trajectory, workspace và log. Nếu bật Memory Core thì bao gồm nguồn ký ức, chỉ mục tìm kiếm, vector, cache và bản sao nội bộ Dreaming. |
| Trình duyệt | Lịch sử chat trong localStorage và ID cuộc trò chuyện đang mở trong sessionStorage. Nghiệm thu qua nút xóa của giao diện và thử lại sau khi tải trang. |
| ASP.NET Core | Lịch thử và thông tin chống tạo trùng trong bộ nhớ, khi tính năng lịch thử bật. |
| Backup và archive | Các thư mục tuyệt đối trong cấu hình kiểm kê. Kiểm tra manifest, chạy quét trước, ghi log kết quả và kiểm tra lại sau purge. Từ chối xử lý tự động đối với bản sao thiếu nguồn, không đọc được hoặc định dạng không hỗ trợ. |
| Kho thông tin xác thực | Secret DeepSeek và cấu hình OAuth trong `.local`. Khi mở Gmail, thêm kho tài khoản và refresh token mã hóa. Tách khỏi bản sao hội thoại. Kho thu hồi và khóa bản sao cũng nằm ngoài state được restore. |

## Tiêu chí đóng cổng

1. Chốt cấu hình launcher, runtime, profile trình duyệt và thư mục backup dự kiến. Với máy không cấu hình backup, ghi rõ không có thư mục backup do ứng dụng quản lý.
2. Tạo session thử từ workspace thật. Xóa qua giao diện, xác minh không đọc lại được qua chat, lịch sử Gateway, continuation hoặc các kho đã bật sau restart. Ca đối chứng phải còn.
3. Khởi động Gateway cô lập từ snapshot cũ có dữ liệu đã thu hồi và xác nhận bị chặn trước khi mở cổng. Snapshot sạch phải phục vụ được session đối chứng. Xác minh archive và backup thuộc phạm vi quản lý sau purge hoặc thu hồi khóa.
4. Chạy suite M1, Dreaming và DeepSeek trên runtime dự kiến. Ghi phiên bản, kết quả và đánh giá 22 cảnh báo moderate hiện còn trong hồ sơ M1.

## Quyết định hiện tại

Cập nhật: Edge đã PASS trên cổng mặc định 5183/18793 với state tổng hợp riêng. Gửi chat thành công, nút Xóa trả HTTP 204 và lịch sử không xuất hiện sau reload. Bản vá restore được sửa để có thể áp dụng nhiều lần mà không chèn thêm bộ kiểm tra. State cũ `.state` vẫn chưa khởi động lại được vì Gateway không xác minh được khóa sở hữu Windows, dù cổng đã trống.

Chưa nghiệm thu cấu hình mặc định. Đã chạy Edge trên launcher với cổng cô lập 20383/20393: gửi chat thành công, bấm Xóa nhận HTTP 204 từ Gateway, tải lại trang không còn lịch sử thử. Ca launcher restore riêng đã xác nhận state chứa dấu thu hồi bị chặn trước khi mở cổng. Ca Gateway restore đã kiểm tra đọc lại lịch sử qua RPC.

Launcher cố định candidate đã vá và từ chối khởi động nếu cổng bị chiếm. Lab cũ vẫn giữ cổng mặc định và Windows từ chối quyền dừng tiến trình từ Codex. Cần dừng từ phiên sở hữu tiến trình trước khi nghiệm thu trên cổng mặc định. Không có thư mục backup được khai báo trong cấu hình launcher hiện tại. Kiểm kê theo tên trong state không thấy file archive hoặc backup, và hai vị trí `detot-lab/backups` và `backups` không tồn tại. Kết quả này chỉ áp dụng cho cấu hình trong repo.

Rủi ro còn lại cần ghi nhận khi chốt là bản sao do người dùng tự xuất ngoài các vị trí đã cấu hình, dấu vết vật lý sau khi unlink và dữ liệu đã chuyển ra nhà cung cấp. Phạm vi nghiệm thu là khả năng truy xuất qua Đệ Tốt và các kho do Đệ Tốt quản lý.

Sau khi M1 đạt, thử Gmail riêng với OAuth đọc thư theo quyền tối thiểu của luồng tìm thư. Kiểm tra chọn tài khoản, phạm vi kết quả, thu hồi quyền và lỗi cấp quyền. Không chuyển kết quả thư sang LLM trong luồng hiện tại.
