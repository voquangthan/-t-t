# 10 — Bảo mật, kiểm thử và cổng phê duyệt

## Ranh giới bảo mật

- **Danh tính và quyền.** Dùng cookie cùng origin với Secure, HttpOnly và CSRF cho ghi. Token Gateway lưu backend theo tenant và qua secret store, xoay độc lập. HTTPS khi qua mạng. ASP.NET Core chỉ chuyển tiếp OpenResponses đến địa chỉ đã đăng ký. Tách tài khoản migrate/backup khỏi runtime. Bật và ép RLS, app role không có BYPASSRLS hoặc quyền owner, dùng tenant context cục bộ theo transaction, mặc định deny khi thiếu context. RLS giảm tác động lỗi truy vấn, còn backend và secret store vẫn là ranh giới tin cậy. [RLS PostgreSQL](https://www.postgresql.org/docs/current/ddl-rowsecurity.html) · [Trust model OpenClaw](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/gateway/security/trust-model.md)
- **Dữ liệu và key.** TLS cho DB khi qua mạng. Mã hóa trường nội dung và nguồn có dữ liệu riêng bằng AEAD, gắn user/record/revision vào associated data, dùng DEK theo user được bọc bởi KEK trong kho bí mật. Key/version và bản backup key quản lý riêng. Vector phải đọc được để pgvector tính khoảng cách. Chỉ index dữ liệu ít nhạy cảm được phép, dùng mã hóa ổ đĩa/backup, giới hạn quyền và xem vector là dữ liệu riêng. Gateway state và log dùng thư mục riêng, ACL hẹp, retention xác định. Provider chỉ nhận context và đoạn nguồn đã cấp quyền cho mục đích tương ứng. Thẩm định retention của DeepSeek trước khi dùng dữ liệu thật.
- **Ghi và thu hồi.** Approve gắn user, phiên bản dữ liệu, phạm vi và lần dùng. Audit lưu actor, event, reason code, ID và thời gian. Tránh nội dung prompt, vector, key, token và raw lỗi upstream trong log. Thu hồi cập nhật epoch, vô hiệu hóa projection và replay, và chặn job dùng consent cũ. Kiểm kê dữ liệu xuất hiện ở transcript nguồn, câu trả lời dẫn xuất, continuation, queue, index và backup. Xóa memory chặn sử dụng từ lượt mới. Xóa lịch sử nguồn là thao tác riêng có UI rõ và chính sách purge. Dữ liệu đã rời hệ thống cần xử lý theo điều khoản provider đã duyệt.
- **Công cụ.** Gateway giữ deny ở lớp cấu hình và backend kiểm tra lại từng lời gọi. Những tool shell, process, browser automation, file write, Windows control và quản trị nằm ngoài cả schema lẫn executor. Nguồn nhập và memory có thể chứa prompt injection. Chỉ đưa trích đoạn theo quyền và không cho nội dung dữ liệu thay đổi cấu hình hay consent.

## Backup và khôi phục

Backup PostgreSQL theo lịch bằng logical dump nhất quán, mã hóa artifact và giữ trong đích được phê duyệt. Manifest có schema, PostgreSQL/pgvector version, model revision, hash, watermark tombstone và bản kê kho dữ liệu. Sao lưu policy, memory, evidence, vector hoặc khả năng rebuild index, lịch sử ứng dụng và job ledger. Với Gateway, chỉ backup state cần phục hồi bằng quy trình upstream đã kiểm chứng, lưu riêng và không sao chép DB đang chạy. Khóa khôi phục cất ở đích độc lập, có bản offline. Mục tiêu đề xuất RPO 24h và RTO 2h. [pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html) · [OpenClaw backup tại tag](https://github.com/openclaw/openclaw/blob/v2026.11.3/docs/install/backups.md)

Restore vào DB/instance cách ly và tắt egress/job. Kiểm checksum, schema và RLS, giải mã mẫu, rebuild index nếu cần. Đối chiếu deletion ledger lưu độc lập với dump và replay tombstone/thu hồi phát sinh sau mốc backup trước khi cho phục vụ truy vấn. Nếu ledger thiếu hoặc lỗi, giữ môi trường offline. Xác nhận bản ghi đã xóa không xuất hiện lại, sau đó người vận hành duyệt chuyển traffic. Job chỉ bật lại sau khi reconcile lease/checkpoint. Archive theo lịch hủy đã duyệt và không dùng để phục hồi dữ liệu đã bị thu hồi.

## Bộ nghiệm thu bắt buộc

| Kiểm thử | Điều kiện đạt và bằng chứng |
|---|---|
| Nhớ sở thích nhiều phiên | User A xác nhận một preference ở phiên 1. Phiên 2 sau restart nhận đúng preference và trích dẫn nguồn. Phiên riêng tư hoặc khi tắt recall không nhận đoạn memory. |
| Truy hồi theo diễn đạt khác | Corpus giả tiếng Việt có paraphrase, phủ định và nhiễu. Đề xuất Recall@6 ≥ 90% trên bộ câu giữ lại, không trả ký ức sai owner, không vượt 1.200 token memory và đo p95 ở máy lab. So với Memory Core theo cùng corpus. |
| Hai user tách biệt | A và B có preference mâu thuẫn. Truy vấn qua API, tool, event, worker và restore. Không có dữ liệu chéo qua ID đoán được, request đồng thời hoặc kết nối DB tái sử dụng. Xác minh với hai Gateway thật và hai bộ state. |
| Consent và dữ liệu nhạy cảm | Dùng chuỗi nhạy cảm giả và dữ liệu chưa opt-in. Không có ứng viên, vector, log chứa payload hay outbound request mang chuỗi đó. Thu hồi giữa job làm commit bị chặn. |
| Xóa và ngăn tái sử dụng | Xóa memory khi có context chuẩn bị và job đang chạy. Lượt mới, cache, continuation và reindex không truy hồi được bản ghi. Restore backup cũ và replay tombstone cho cùng kết quả. |
| Công cụ trái quyền | Mock model đề xuất shell/browser/file write/Windows control, input thừa, ID phiên khác hoặc tool sau thu hồi. Backend từ chối trước tác động. Kiểm tra schema mà Gateway gửi và không có lệnh, file hoặc kết nối thiết bị phát sinh. |
| Daily Learning và mâu thuẫn | Dữ liệu giả có câu đùa và bằng chứng xung đột. Job chỉ tạo pending có nguồn và confidence. Replay/double-run không nhân bản. Chỉ preference được duyệt mới active, revision cũ superseded đúng lúc. |
| Restore | Khôi phục backup mã hóa vào instance mới. Kiểm checksum, giải mã, recall, tenant isolation, tombstone và policy. Mất key, backup hỏng hoặc ledger thiếu thì chặn cutover. Ghi RPO/RTO đo được. |

## Milestone sau duyệt

1. **Đối chứng và khóa hợp đồng.** So Memory Core/Dreaming với corpus giả, kiểm kê nơi Gateway lưu transcript/continuation và chạy test OpenResponses với DeepSeek. Chốt version, license, advisory và quy tắc retention. Nếu không xác nhận được xóa hoặc cách ly, giữ dữ liệu giả.
2. **Memory Orchestrator và DB.** Dựng schema, consent, mã hóa, RLS, provenance, revision, xóa và truy hồi. Chạy integration test độc lập với PostgreSQL/pgvector và embedding giả, rồi benchmark model cục bộ trên corpus tiếng Việt.
3. **Chat, React và tool calling.** Nối hai Gateway cô lập với frontend đa phiên, audit và quyền quản lý memory. Chạy test end-to-end quyền, retrieval, replay, tool schema và luồng từ chối.
4. **Daily Learning và vận hành.** Kiểm tra lịch, lease, retry, DST, dedup và thu hồi consent. Diễn tập restore, đo RPO/RTO và trình bằng chứng nghiệm thu. Nếu cân nhắc Mem0, thực hiện spike theo điều kiện ở tài liệu 07.

**Chờ phê duyệt** Product Owner chốt consent, retention, dữ liệu được gửi provider và thao tác cần duyệt. System Architect chốt topology Gateway, kho DB/keys, ngưỡng benchmark và tiêu chí restore. Tất cả bài kiểm thử ở trên là tiêu chí cho các milestone sau duyệt.
