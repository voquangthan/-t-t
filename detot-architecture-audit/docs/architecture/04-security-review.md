# 04 — Bảo mật, quyền và khôi phục

Gateway có xác thực, danh tính thiết bị, role/scope và pairing. Tool policy kiểm soát bề mặt agent. Sandbox và host exec approvals bảo vệ các lớp khác nhau. Windows node thêm quyền theo capability và kiểm soát thực thi tại máy đích. Với mô hình chủ sở hữu tin cậy của Gateway, dùng một instance riêng cho Đệ Tốt và giới hạn đường truy cập từ ASP.NET Core. [Security](https://docs.openclaw.ai/gateway/security) · [Exec approvals](https://docs.openclaw.ai/tools/exec-approvals) · [Nodes tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/nodes/index.md)

| Bề mặt | Rủi ro | Kiểm soát đề xuất |
|---|---|---|
| Shell và Windows node | Chạy lệnh dưới quyền người dùng host, đọc secrets hoặc thay đổi máy. | Tắt host exec và elevated trong MVP. Dùng tài khoản thường, sandbox riêng, capability allowlist và phê duyệt tại node nếu mở quyền sau này. Mở Computer Agent với thao tác có schema hẹp trước. |
| Browser và web fetch | Prompt injection, lộ cookie, ghi biểu mẫu, truy cập mạng nội bộ ngoài dự kiến. | Profile riêng, cấm dùng session cá nhân, chặn đích nội bộ ở lớp mạng, giới hạn download, duyệt trước submit/đăng/tải dữ liệu lên. Coi nội dung web là dữ liệu không đáng tin. |
| File system và optimizer | Đọc ngoài workspace, symlink/junction, thay đổi đích giữa preview và apply. | Sandbox theo agent, quyền đọc tối thiểu, loại thư mục cá nhân. Canonicalize path và kiểm lại đối tượng trước khi ghi. Dùng quarantine có thời hạn, manifest và rollback. |
| Gateway API, MCP, plugin | Scope quá rộng, tool bị gọi ngoài UI, plugin chạy cùng tiến trình. | Gateway private/loopback, TLS nếu qua mạng, giới hạn origin và token theo scope. Chặn đường gọi tool trực tiếp ngoài policy nghiệp vụ. Chỉ tải extension đã thẩm định, ghim phiên bản, dùng MCP riêng tiến trình và lọc egress. |
| Memory, lịch và voice | Nội dung riêng bị gửi tới provider hoặc trở thành trí nhớ lâu dài. | Consent theo nguồn và đích, giảm dữ liệu trước khi gửi, TTL cho audio/transcript, audit việc ghi/quên. Vô hiệu hóa tự động học cho đến khi kiểm tra cấu hình Dreaming tại đúng tag. |
| Secrets và nhật ký | Key/token xuất hiện trong repo, UI, tool output, log hoặc archive. | Secret store của hệ điều hành, cấp secrets ở backend hoặc qua SecretRef được hỗ trợ. Tách credentials, redact log và kiểm tra backup. Luân chuyển key khi có sự cố. |

[Sandbox coverage](https://docs.openclaw.ai/gateway/sandboxing/what-gets-sandboxed) · [Tools Invoke API](https://docs.openclaw.ai/gateway/tools-invoke-http-api) · [Mã audit tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/src/security/audit.ts)

## Phê duyệt và nhật ký

Mỗi yêu cầu ghi trình bày thao tác, đối tượng, dữ liệu sẽ gửi hoặc thay đổi và khả năng hoàn tác. Approval gắn danh tính, bản chuẩn hóa tham số, hạn hiệu lực và một lần sử dụng. Executor kiểm lại quyền và trạng thái đối tượng. Nếu timeout, tham số đổi hoặc quyền bị thu hồi, chặn thực thi. Job định kỳ chỉ đọc sau khi được cấp nguồn, còn ghi tạo đề xuất chờ duyệt theo chính sách. Log lưu actor, tool, đích đã che dữ liệu nhạy cảm, ID duyệt, thời điểm, kết quả và correlation ID.

## License và advisories

OpenClaw tag khảo sát và Windows Hub công bố MIT. Giữ thông báo bản quyền khi phân phối lại. Trước phát hành, tạo SBOM từ lockfile/npm/NuGet của bản chọn, rà license bắc cầu, notices và điều khoản dữ liệu Google/provider riêng. Upstream đã công bố advisory về tính nhất quán môi trường trong luồng node exec approvals. Đối chiếu toàn bộ dải phiên bản bị ảnh hưởng và bản vá cho cả Gateway, Hub và dependency tại thời điểm chọn bản. Khảo sát tĩnh này chưa có kết quả quét dependency hay kết luận phiên bản đạt chuẩn production. [OpenClaw LICENSE](https://github.com/openclaw/openclaw/blob/v2026.11.3/LICENSE) · [Hub LICENSE](https://github.com/openclaw/openclaw-windows-node/blob/main/LICENSE) · [GHSA về node approvals](https://github.com/openclaw/openclaw/security/advisories/GHSA-8f46-3xx3-8c9m) · [Advisories upstream](https://github.com/openclaw/openclaw/security/advisories)

## Git, backup và restore

Dùng Git repository riêng cho mã Đệ Tốt, tài liệu, migration và mẫu cấu hình. Loại credentials, workspace cá nhân, file thiết bị, transcripts và archive khỏi Git. Backup cục bộ mã hóa, ACL hẹp, bản sao offline và khóa khôi phục cất riêng. Lưu phiên bản Gateway, Hub, ứng dụng và schema kèm manifest. Dùng backup/verify được hỗ trợ cho OpenClaw, cơ chế snapshot nhất quán cho DB ứng dụng và export cấu hình Hub đã duyệt. Tránh sao chép file SQLite đang mở. Cloud backup cần quyết định riêng của chủ dữ liệu.

Khôi phục vào thư mục/profile cách ly, xác minh manifest và DB, khởi động với job và egress tắt, kiểm tra session/memory/registry và quyền thiết bị. Chỉ chuyển sang state mới sau khi người vận hành duyệt. Xác định nhu cầu đổi token hoặc pair lại. Diễn tập RPO 24 giờ và RTO 2 giờ như mục tiêu ban đầu cần phê duyệt. [Backups tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/install/backups.md) · [Hướng dẫn backup hiện hành](https://docs.openclaw.ai/install/backups)
