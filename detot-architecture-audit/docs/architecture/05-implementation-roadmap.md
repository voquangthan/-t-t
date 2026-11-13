# 05 — Lộ trình và nghiệm thu

Các phase dưới đây là đề xuất. Theo chỉ đạo hiện tại của Product Owner, tiếp tục triển khai tuần tự và tự chủ sau khi cổng nghiệm thu phase trước đạt; không cần dừng xin duyệt từng thay đổi kỹ thuật thông thường. Các điều kiện cần quyền/tài khoản/quota bên ngoài vẫn phải được thỏa mãn trước khi dùng dữ liệu hoặc dịch vụ đó. Không bỏ qua lỗi nghiêm trọng để chuyển phase.

| Phase | Phạm vi | Độ khó và phụ thuộc | Tiêu chí nghiệm thu |
|---|---|---|---|
| 0 — Xác minh và chốt thiết kế | Clone vào thư mục riêng bằng môi trường Git có mạng đã được phép. Checkout tag, đối chiếu SHA; đọc cây nguồn, lockfile, runtime, browser, MCP, Windows Hub. Chốt threat model, data flow, version matrix và Git repo Đệ Tốt. | Vừa. Cần Git và truy cập GitHub. | SHA và đường dẫn kiểm chứng được; chốt license, advisories, quyền, bản vá, bề mặt RPC và quy tắc dùng dữ liệu. Hai bên duyệt phạm vi thử nghiệm. |
| 1 — MVP chỉ đọc | React, ASP.NET Core, Gateway, model gọi tool giả lập, memory và cron trong workspace thử, audit và backup cục bộ. | Vừa. Sau phase 0. | Chat tiếng Việt stream và hủy được. Schema tool đúng; câu diễn đạt khác nhau gọi đúng tool. Không có key trong frontend/log. Memory lưu qua restart. Restore sandbox đạt mục tiêu được duyệt. |
| 2 — Web, Google và Daily Learning | Web profile cách ly; Maps/Weather; Calendar đọc sau consent; cron tạo đề xuất memory có nguồn. | Cao. Cần phase 1, quota và quyền Google đã được cấp. | Test 401/429, timeout, DST và trang web có nội dung không đáng tin. Chạy lại job không trùng. Thu hồi nguồn chặn thu thập và cho phép xử lý dữ liệu dẫn xuất. |
| 3 — Windows, registry và ghi có duyệt | Pair Hub, registry thiết bị, capability hẹp, preview optimizer, phê duyệt ghi Calendar và thao tác host đã chọn. | Cao. Cần phase 1, cơ chế consent và audit; Calendar ghi sau phase 2. | Pair/revoke hoạt động. Từ chối approval hết hạn, phát lại hoặc đổi tham số. Thử junction và đường dẫn ngoài phạm vi trên fixture. Khôi phục file thử từ quarantine. |
| 4 — Voice và Chibi Pet | STT/TTS tiếng Việt, điều khiển mic, Chibi desktop nhận sự kiện và gửi yêu cầu có giới hạn. | Vừa đến cao. Cần phase 1 và hợp đồng sự kiện Windows phase 3. | Quyền mic tắt thì dừng thu; kiểm tra độ trễ/ngắt lời; pet dùng được khi mất kết nối và không có token Gateway trong tiến trình UI. |

## TASK 002 đề xuất

**Xác minh OpenClaw tại commit ghim và chốt hợp đồng MVP chỉ đọc.** Ưu tiên hoàn tất clone và đối chiếu SHA, đọc chuỗi Gateway → agent runtime → tool executor, rà luồng policy/MCP và kiểm tra release Hub tương thích. Giao sơ đồ dữ liệu, ma trận RPC/scope, danh sách tool MVP, biên bản license/advisory và bộ tiêu chí nghiệm thu. Nếu hai bên duyệt chạy thử trong môi trường cô lập, dùng dữ liệu giả và mock provider để kiểm tra chat streaming, tool schema, job và restore. Thử model thật, tài khoản Google hoặc quyền Windows cần phê duyệt phạm vi tương ứng.

TASK 001 dừng tại báo cáo. Cổng Milestone 1 hiện vẫn FAIL theo báo cáo remediation số 12; không bắt đầu Milestone 2 cho tới khi đạt các tiêu chí bắt buộc. Sau khi đạt, tiếp tục phase 2 theo thứ tự roadmap, giữ dữ liệu giả cho đến khi các điều kiện bảo mật và quyền truy cập của phase đó được kiểm chứng.
