# 11 — TASK 004B Milestone 1 · Compatibility & Baseline

**Ngày kiểm tra** 12/11/2026. **Trạng thái** Đã hoàn tất các phép thử cục bộ bên dưới. Nghiệm thu Milestone 1 còn mở vì lượt DeepSeek thật chưa chạy. Dữ liệu thật bị chặn bởi các phát hiện dependency và phần kiểm thử retention chưa hoàn tất. Dừng trước Milestone 2.

## Mốc và phạm vi

- Source Đệ Tốt tại lúc bắt đầu là `f7a5478d9aab6e5ede37d008e2a795445dbd5085`. Không tạo commit mới cho lần kiểm tra này.
- Gói đã cài báo OpenClaw `2026.11.3`. SHA-512 của tarball cục bộ khớp integrity ở release SHA `847611dcaa3bf0e2783335f961dd1fd0580e4d9f`. Runtime kiểm thử dùng Node.js `24.22.3`. [Release](https://github.com/openclaw/openclaw/releases/tag/v2026.11.3#release-verification)
- Mỗi lần chạy tạo state riêng dưới `detot-lab/.local/m1-*` và cổng loopback động. Dùng fixture tiếng Việt, server embedding/completions giả lập và bearer ngẫu nhiên. Gateway dùng cấu hình lab với schema duy nhất `detot_lab_fixture`. Profile đối chứng Dreaming chỉ bật Memory Core và cron riêng của fixture. Không thay đổi cấu hình Lab đang dùng.

## Kết quả thực thi

| Kiểm thử | Kết quả | Bằng chứng |
|---|---|---|
| Gateway thật khởi động | PASS | `/readyz` trả HTTP 200. |
| Phân quyền HTTP và continuation không hợp lệ | PASS | Bearer sai trả HTTP 401, response ID không tồn tại trả HTTP 400. |
| Memory Core index và search | PASS | `memory index` và `memory search` chạy trên workspace giả. Câu hỏi “đồ uống khoái khẩu” xếp mục trà sen trong `MEMORY.md` đầu tiên. Server nhận 3 yêu cầu embedding. Vector fixture được định trước, kết quả xác minh đường đi, index và xếp hạng với vector đã biết. Chất lượng embedding tiếng Việt vẫn cần benchmark bằng model cục bộ. |
| Dreaming deep-phase preview | PASS | `memory promote --json` trả 0 ứng viên, nội dung `MEMORY.md` giữ nguyên. |
| Dreaming managed sweep | PASS | Gateway tạo job `Memory Dreaming Promotion`. `cron run … --wait` báo `succeeded`. Ghi chú giả chưa được xác nhận không thay đổi `MEMORY.md`. Ca này xác minh chạy lịch và quyết định không thăng hạng với bằng chứng yếu. |
| Tool calling và continuation với provider giả lập | PASS | Gateway gửi đúng một schema được phép. Lượt đầu trả một `function_call` hợp lệ. Lượt sau dùng `previous_response_id` và `function_call_output`, trả mã `DETOT-M1-731`. Cả hai lượt HTTP 200, đúng hai yêu cầu upstream. |
| DeepSeek thật | BLOCKED | Không có `DEEPSEEK_API_KEY` được cấp trong môi trường kiểm thử. DeepSeek công bố Tool Calls, còn đường gọi thật qua OpenClaw vẫn cần chạy với key do người dùng nhập cục bộ. [DeepSeek Tool Calls](https://api-docs.deepseek.com/guides/tool_calls/) |
| Cổng dependency trước dữ liệu thật | FAIL | `npm audit --omit=dev` của lockfile đã cài trả exit 1, tổng 27 mục gồm 4 high, 23 moderate, 0 critical. |

Bằng chứng máy đọc được ở `detot-lab/.local/m1-3wod7k/results.json` và `detot-lab/.local/m1-HWGTfs/results.json`. Tóm tắt advisory ở `detot-lab/.local/m1-dependency-summary.json`. Đây là tệp cục bộ đã được Git bỏ qua.

## Retention và quyền

- Sau khi Gateway mock dừng, kiểm tra SQLite chỉ đọc cho thấy `transcript_events` có 13 hàng, `session_transcript_active_events` có 12 hàng và `session_transcript_fts_content` có 3 hàng. Số hàng là sự kiện/index của lượt thử, không phải số hội thoại. Profile thử đã ghi dữ liệu phát sinh dù frontend không dùng history API.
- Tài liệu bản ghim cho biết OpenResponses có thể giữ continuation mapping tối đa 30 ngày và 5.000 mục, scope theo auth subject, agent và session. Mapping sống qua restart, còn incognito không tạo mapping theo response ID. Session maintenance có thể archive transcript mà vẫn giữ nội dung. Chưa chạy phép thử runtime cho TTL, restart/replay, incognito, purge hoặc restore. Trước dữ liệu thật cần kiểm chứng xóa transcript, FTS và continuation, cùng ranh giới hai Gateway theo thiết kế. [OpenResponses](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/openresponses-http-api.md) · [Session retention](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/config-agents/sessions.md)
- OpenResponses dùng bearer có quyền operator rộng, nên giữ token ở backend và cổng Gateway ở loopback. Cấu hình giữ deny cho shell, file write, browser và Windows control. Test schema fixture không mở các công cụ này. Ma trận quyền hai người dùng và kiểm tra tool trái quyền end-to-end thuộc milestone sau. [Trust model](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/security/trust-model.md)

## Dependency và quyết định

Lockfile của package đã cài có SHA-256 `2861628cfc01d555f91bfeae64626d04618fe2d8d244786214469e5c63b5e8f0`. Audit báo high ở `openclaw`, `zod`, `undici` và `brace-expansion`. Bản ghi high của `openclaw` là kết quả tổng hợp dependency trong dữ liệu audit này. `undici` và `brace-expansion` high nằm dưới nhánh npm đóng gói. `zod` high nằm tại `node_modules/zod`. Báo cáo cài đặt hiện tại khác phạm vi evidence workspace upstream tại [TASK 002](06-task-002-source-gateway-security.md). Rà đường sử dụng runtime và chọn artifact có bản vá trước dữ liệu thật. Giữ nguyên bản đã duyệt trong phép thử này. [Zod advisory](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5) · [Undici advisory](https://github.com/advisories/GHSA-2m5v-pch6-74hx) · [Brace-expansion advisory](https://github.com/advisories/GHSA-qhr7-859c-m2p7)

## Tái chạy và nghiệm thu

Từ thư mục project, dùng Node đã cài của Lab:

```powershell
& .\detot-lab\.local\node-v24.22.3-win-x64\node.exe .\detot-lab\tests\milestone1.mjs
& .\detot-lab\.local\node-v24.22.3-win-x64\node.exe .\detot-lab\tests\milestone1.mjs --dreaming
.\detot-lab\tests\Run-DeepSeekBaseline.ps1
```

Lệnh cuối hỏi key ở prompt ẩn và gửi đúng câu thử cùng schema fixture. Key đi qua environment của tiến trình thử và Gateway, không nằm trong config, argv hay kết quả. Wrapper đã qua kiểm tra cú pháp PowerShell 5.1, chưa chạy với key thật. Product Owner và System Architect xem kết quả PASS/FAIL/BLOCKED và quyết định xử lý các điều kiện còn mở trước Milestone 2.
