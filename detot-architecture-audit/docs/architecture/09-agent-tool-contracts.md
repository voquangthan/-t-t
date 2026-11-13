# 09 — Hợp đồng Gateway, công cụ và API người dùng

**Hợp đồng thiết kế** ASP.NET Core là executor và điểm kiểm tra quyền. OpenClaw điều phối suy luận, nhận schema công cụ và trả structured function call. Giữ tool search, Code Mode và elevated tắt trong cấu hình Đệ Tốt. Tắt tool truy cập shell/process, file system, browser, Windows/node, tự quản trị Gateway, cron, agent-to-agent và plugin tool ngoài danh sách. Gateway giữ local/private. [OpenResponses tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/openresponses-http-api.md) · [Tool policy tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/config-tools/tool-policy.md)

## Tool dành cho model

Tên dưới đây là tên logic. Dùng tên function trên wire bằng chữ, số và gạch dưới để tương thích model. Mọi schema từ chối trường thừa. Owner, session và quyền lấy từ auth context phía backend, tuyệt đối tránh cho model chọn chúng.

| Tool và tên wire | Input và output tối thiểu | Quyền và tác động |
|---|---|---|
| `memory.search` → `detot_memory_search` | `query` 1–500 ký tự, `kind` tùy chọn, `limit` 1–6. Trả trích đoạn, loại, độ chắc chắn, ID nguồn có thể dùng trong lượt và số token đã dùng. | Recall bật, consent mục đích recall và egress còn hiệu lực, lọc owner/scope trước query và trước khi đưa context vào request. |
| `memory.propose` → `detot_memory_propose` | `kind`, `claim` 1–500 ký tự, `evidenceRefs` tối đa 5 ID do backend cấp trong lượt. Trả proposal ID hoặc mã từ chối. | Chỉ tạo pending. Kiểm tra nguồn, mức nhạy cảm, scope và write consent. User xác nhận ở UI để kích hoạt. |
| `detot_lab_fixture` | Object rỗng, trả mã giả. | Chỉ trong test fixture để kiểm tra round trip và từ chối tool ngoài schema. |

Người dùng quản lý memory qua API xác thực. Đề xuất endpoints gồm `GET /api/sessions`, `POST /api/sessions`, `GET /api/sessions/{id}/messages`, `POST /api/sessions/{id}/turns`, `GET /api/memories`, `PATCH/DELETE /api/memories/{id}`, `POST /api/memory/proposals/{id}/approve`, `POST /api/memory/export` và `PUT /api/memory/policy`. Consent theo nguồn có endpoint riêng. Lấy user từ principal, áp RLS, CSRF cho các phương thức ghi, kiểm tra ETag/revision và audit. ID phiên không thuộc user trả phản hồi giống ID không tồn tại. Export yêu cầu phiên xác thực mới và chỉ tải trong chính phiên đó. Model không có tool sửa, xóa, xuất, đổi policy hoặc tự phê duyệt.

## Vòng đời một lượt

1. Xác thực user và quyền đối với session. Tạo turn ID duy nhất, ghi idempotency record và snapshot phiên bản consent. Lấy các schema hiện được phép và ngữ cảnh theo ngân sách.
2. Gọi Gateway được ánh xạ phía server. Client không được chọn model, Gateway URL, bearer, Gateway session key, continuation ID hoặc role. Khi streaming, UI chỉ nhận event đã lọc theo session và turn. Ẩn token, raw reasoning và nội dung lỗi upstream có thể chứa dữ liệu nhạy cảm.
3. Với mỗi `function_call`, xác thực tool theo allowlist và schema, recheck consent, owner và lease turn. Dùng khóa `(user_id, turn_id, call_id)` để chỉ thực thi một lần. Cùng call ID với payload khác bị chặn. Kiểm tra bằng chứng theo phiên; coi output nguồn và văn bản đã lưu là dữ liệu không đáng tin.
4. Gửi `function_call_output` cho Gateway trong đúng continuation. Giữ schema đã duyệt trong phạm vi turn để tương thích với allowlist trên bản ghim. Khi quyền giảm giữa lượt, hủy lượt hoặc dựng lại context và schema từ đầu với quyền hiện tại. Tối đa 3 vòng và 4 lời gọi tool mỗi turn. Hủy khi vượt ngân sách hoặc timeout. Lỗi trả mã ổn định như `TOOL_DENIED`, `SCHEMA_INVALID`, `CONSENT_REVOKED`, `BUDGET_EXCEEDED`.
5. Hết hạn continuation thì dựng lại phần lịch sử và context hợp lệ, theo hợp đồng OpenResponses. Backend chỉ tự thử lại bước không có tác động. Trạng thái tool đã commit được đọc lại từ idempotency record. Lỗi Gateway hoặc provider phải hiển thị trạng thái có thể xử lý trên UI.

**Phép thử tương thích trước triển khai** Đối chiếu tên wire và schema ở DeepSeek, trường `function_call` và `call_id`, continuation cùng Gateway, chế độ incognito/retention, retry, hủy và event filtering. Dùng cổng mô phỏng trước, sau đó OpenClaw thật với dữ liệu giả và provider được cấp quyền. Bằng chứng TASK 003 mới xác nhận cấu hình DeepSeek và khởi động Gateway cách ly bằng key giả, nên live tool round trip vẫn là điều kiện nghiệm thu.
