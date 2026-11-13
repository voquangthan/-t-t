# 08 — Mô hình dữ liệu và vòng đời memory

PostgreSQL là nguồn dữ liệu chính của ứng dụng. Mọi bảng nghiệp vụ mang `user_id`. Dùng khóa ngoại kép `(user_id, id)` cho phiên, sự kiện nguồn và memory. Mọi thao tác dùng transaction với tenant context đặt từ danh tính đã xác thực và RLS. Kho nội bộ của Gateway được coi là dữ liệu phát sinh cần kiểm kê và quản lý retention riêng. [RLS PostgreSQL](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)

| Bảng | Trường chính và invariant |
|---|---|
| `sessions`, `messages`, `chat_turns` | Owner, ID phiên, sequence, role, nội dung mã hóa, request ID và phiên bản consent. Message/turn tham chiếu phiên cùng owner. |
| `memory_policy`, `consent_grants` | Công tắc recall/ghi/học, nguồn, mục đích, phạm vi phiên, loại dữ liệu, quyền egress tới provider, thời gian và revision. Mặc định deny. |
| `source_events`, `memory_evidence` | ID nguồn/phiên/message, timestamp quan sát, revision consent, hash đoạn đã chuẩn hóa, quan hệ memory revision ↔ bằng chứng. Provenance có thể đọc, truy vấn quyền lại khi export. |
| `memories`, `memory_revisions` | Loại, scope user/session, khóa chủ đề, nội dung mã hóa, confidence, status, valid_from/to, revision và supersedes ID. Status gồm pending, active, superseded, rejected, deleted. Chỉ active đủ điều kiện recall. |
| `memory_embeddings` | Owner, memory revision, model revision, vector 1024 chiều với mẫu đã duyệt, trạng thái index. Chỉ một vector cho mỗi model/revision. Bản sửa invalidates vector cũ cùng transaction. |
| `learning_runs`, `outbox`, `deletion_tombstones` | Job key duy nhất, lease/checkpoint, sự kiện đồng bộ projection, tombstone phiên bản và trạng thái purge. Tombstone giữ ID tối thiểu để chống tái tạo từ job hoặc restore cũ. |

**Semantic** là sự kiện hoặc tri thức bền có bằng chứng. **Episodic** ghi việc đã xảy ra, mốc thời gian và phạm vi. **Preference** lưu lựa chọn của người dùng, chủ đề và ngữ cảnh áp dụng. Một nhận xét được suy ra từ nhiều lượt chat chỉ là đề xuất có confidence và nguồn đến khi được duyệt. Trong milestone đầu, nội dung nhạy cảm ở ngoài kho retrieval và Daily Learning. Mở rộng loại dữ liệu cần consent và thiết kế lưu trữ được duyệt riêng.

## Ghi, mâu thuẫn và xóa

1. Kiểm tra schema, consent, loại dữ liệu và quyền đọc từng nguồn. Khi nguồn thiếu hoặc bộ phân loại không chắc chắn về mức nhạy cảm, không ghi ứng viên. LLM không được tự quyết định nhãn đồng ý hoặc kích hoạt memory.
2. Chống trùng chính xác bằng HMAC theo người dùng của payload chuẩn hóa, khóa chủ đề và scope. Ràng buộc unique/idempotency và khóa theo user/chủ đề xử lý retry và concurrent writes. Với trùng ngữ nghĩa, similarity chỉ tìm ứng viên. Hợp nhất thêm provenance khi cùng nghĩa và scope. Trường hợp không chắc chắn chờ duyệt.
3. Khi mâu thuẫn, ưu tiên xác nhận trực tiếp và scope chính xác. Giá trị mới được xác nhận tạo revision và supersede giá trị trước trong cùng transaction. Nếu hai nguồn hợp lệ xung đột, đánh dấu pending và đưa ra UI. Mâu thuẫn chưa giải quyết không đi vào context như một sự thật chắc chắn.
4. Sửa dùng optimistic concurrency. Xóa vô hiệu hóa recall ngay trong transaction, xóa vector, làm mới memory epoch và ghi tombstone. Job/outbox kiểm tra lại epoch, consent và revision trước ghi. Vô hiệu hóa continuation/context đã chuẩn bị, hủy lượt đang chạy nếu có thể. Lượt mới chỉ thấy memory còn hiệu lực. Dữ liệu đã gửi tới provider chịu chính sách retention của provider. Sau xóa, loại dữ liệu memory đã thu hồi khỏi prompt có lịch sử dẫn xuất và ngăn Daily Learning tái học từ nguồn cũ ngoài phê duyệt.
5. Công tắc tắt memory chặn retrieval, proposal, embedding và lịch học. Kho hiện có vẫn quản lý được để người dùng xóa hoặc xuất. Export JSON có schema version, nội dung và provenance người dùng có quyền đọc. Purge vật lý diễn ra qua worker có audit và retention đã duyệt, bao gồm ciphertext và projection. Tombstone không giữ nội dung bị xóa.

## Truy hồi theo ngân sách

- Lọc owner, scope, consent, status, thời hạn và model revision trước khi xếp hạng. Với memory dùng chung giữa các phiên của một user, scope mặc định là user chỉ khi đã được cấp quyền chia sẻ. Memory scope phiên chỉ hiện trong phiên tương ứng.
- Đề xuất thử exact cosine trên tập đã lọc bằng pgvector. Lấy tối đa 30 ứng viên. Xếp hạng relevance bằng cosine, độ khớp chủ đề đã chuẩn hóa, chất lượng bằng chứng và recency theo loại. Áp ngưỡng đo trên bộ câu tiếng Việt, giảm đoạn trùng nguồn và ưu tiên preference đã xác nhận khi điểm gần nhau. Dùng HNSW sau khi đo latency/recall và kiểm tra ảnh hưởng của filter sau ANN. [pgvector và filtering](https://github.com/pgvector/pgvector#filtering)
- Tối đa **6 đoạn hoặc 1.200 token memory** mỗi turn, tối đa 200 token mỗi đoạn, bao gồm kết quả các lần `memory.search`. Ngân sách khởi điểm khác gồm 2.400 token lịch sử và 1.024 token trả lời, reserve hệ thống/schema tính riêng và cắt theo tokenizer/model thực tế. Khi không đo chắc được token, dùng ước lượng có biên an toàn và giảm ngân sách. Ghi metric token, không ghi nội dung.
- Retrieval timeout hoặc embedding lỗi dẫn đến lượt chat không có memory và trạng thái rõ trên UI. Khi đổi model embedding, index song song theo model revision rồi chuyển sau khi đủ coverage. Không trộn vector từ hai model trong một phép đo.
