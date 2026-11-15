# Đệ Tốt Pro — checkpoint

## Memory Core V2 — M2 Semantic Memory

- M2 commit local `7f2889d`. M3 đã bắt đầu với API review phát hiện bản ghi có thể trùng dựa trên nội dung chuẩn hóa, kèm nguồn và version theo owner. Chưa có hàng đợi Dreaming hay duyệt/từ chối đề xuất.

- Finalcheck build sạch. BGE-M3 thật: A/B PASS qua tìm kiếm, tra ID, lịch sử và CRUD; hai tệp vector riêng. Restart với revision mới tạo index mã hóa thứ hai và giữ checksum tệp cũ. Sửa/xóa/đổi trạng thái PASS. Chat mơ hồ hỏi lại, không liên quan trả rỗng, model không tồn tại fallback từ khóa. DeepSeek thật chọn trích đoạn nguyên văn phù hợp, Backend xác thực ID/đoạn trích và tự gắn nguồn.
- Regression cuối trên tiến trình M2 cổng 5193: M1 prepare/resume PASS, Gmail search, Calendar read và Drive refresh đều 200. BGE-M3 mặc định khi Start-Lab phát hiện model có digest trên Ollama cục bộ. Không đổi cấu hình model nếu người vận hành đã chọn rõ.

- Benchmark thật 36 câu mới (26 có đáp án, 10 ngoài phạm vi) trên cùng 4 ký ức. Nomic cũ Recall@2 70,5%, Precision@2 48,1%, FPR 70%, p50/p95 77/162 ms. Nomic với ngưỡng cuối 40,4%, 32,7%, 10%, 78/188 ms; câu canh chua vẫn sai. BGE-M3 cùng ngưỡng đạt 80,8%, 55,8%, 0%, 163/265 ms; câu canh chua trả 0 kết quả. BGE-M3 đã tải cục bộ, chưa đổi model mặc định. Bộ câu hỏi mới dùng cùng corpus nhỏ, cần kiểm tra thêm corpus độc lập.
- Backend holdout build sạch. E2E xuyên chat Nomic + DeepSeek thật PASS: DeepSeek phân loại, Backend trả đúng ID và nguồn. Chưa có lượt DeepSeek tổng hợp nội dung từ bằng chứng. A/B run dừng vì test đọc ownerId ở DTO kết quả tìm kiếm; đã chuyển assertion sang API chi tiết, cần chạy lại. Chưa chạy rebuild và hồi quy Google trên bản cuối. Chưa commit M2 hoặc chuyển M3.

- Benchmark Ollama thật cùng 8 câu hỏi, 6 có đáp án. Chuẩn hóa cụm Việt/Anh, bỏ từ đệm và chỉnh điểm lai đưa Recall@2 từ 91,7% lên 100% và Precision@2 từ 58,3% lên 66,7% trên câu có đáp án. Latency HTTP trước 221–1.290 ms, sau 81–788 ms trong lượt seed. Sửa/xóa và lọc trạng thái với vector thật PASS.
- Một câu về nấu ăn vẫn trả một mục sai. Đã nâng ngưỡng cosine khi không có giao từ khóa và sửa chuẩn hóa quầy lễ tân; chưa build/đo lại sau hai thay đổi cuối. Cần dùng bộ holdout tránh tối ưu theo đúng 8 câu rồi kiểm thử xuyên chat, A/B và hồi quy Google/M1. M2 chưa commit, M3 chưa bắt đầu.

- Đã chạy Ollama Windows standalone và tải `nomic-embed-text:latest` dưới `detot-lab/.local/ollama-m2`. Dùng digest model làm revision. Backend build sạch.
- Embedding thật qua API Backend cổng 5192: bộ thử bốn ký ức, năm câu hỏi. Recall@2 trên bốn câu có đáp án lần lượt 1, 1, 1, 0,5 (trung bình 87,5%). Precision@2 trên bốn câu lần lượt 1, 0,5, 0,5, 0,5 (62,5%). Câu không liên quan trả 0 mục. Cold 1.119 ms, các lượt còn lại 49–147 ms. Index có dạng Data Protection và giữ nguyên checksum qua restart, truy vấn sau restart trả cùng điểm.
- Chưa nghiệm thu: câu tiếng Việt chưa lấy đủ hai ký ức, còn false positives. Cần cải thiện lựa chọn model/xếp hạng, mở rộng bộ nhãn và chạy E2E xuyên chat, edit/delete/rebuild, A/B và Google regression trên bản M2. M2 chưa commit, M3 chưa bắt đầu.

- Đã nối adapter Ollama loopback, cấu hình bằng `DETOT_MEMORY_EMBED_MODEL` và `DETOT_MEMORY_EMBED_REVISION`; không dùng DeepSeek Chat để sinh vector. Chưa thấy sqlite-vec có sẵn trong hồ sơ lab kiểm tra được. Index dẫn xuất mã hóa theo owner, dùng model/revision và hash văn bản để bỏ vector cũ, cập nhật dần tối đa 32 mục mỗi lần truy xuất. Kho Memory M1 giữ là nguồn dữ liệu chính.
- API tìm kiếm và truy xuất chat dùng điểm từ khóa + cosine, lọc owner trước khi tạo embedding; API thêm lọc loại, trạng thái và thời gian. Kết quả mang ID, nguồn, hội thoại, thời điểm và confidence; ca nhiều kết quả hỏi lại.
- Backend build đạt trước chỉnh sửa dọn index cuối. Probe thật Ollama trên 127.0.0.1:11434 bị từ chối kết nối, không có cấu hình model trong shell. M2 chưa nghiệm thu. Tiếp theo: build lại, chạy kiểm thử tích hợp hybrid với fixture, đo precision/recall/latency bằng model thật khi có provider, kiểm tra rebuild/restart và chạy regression M1/Google. Chưa commit M2 hoặc chuyển M3.

## Memory Core V2 — kết quả nghiệm thu M1

- Commit local M1: `f71c423`. Các hunk Ca trực và Calendar ngoài phạm vi còn ở working tree.
- Đã bắt đầu M2: tìm từ khóa có xếp hạng, lọc category/status sau ràng buộc owner, và hàm cosine/lai. Cần nối embedding provider thực, lưu index theo owner, thêm nguồn truy xuất xuyên chat và kiểm thử chất lượng/độ trễ. M2 chưa nghiệm thu.

- Backend build sạch. DeepSeek thật qua hai việc máy in, retry, chọn đúng mục, kiểm kê độc lập và restart. Mã hóa được kiểm tra trên tệp phân vùng mới.
- API migration thật: khôi phục từ ranh giới giả lập đã sao lưu nhưng chưa ghi đích, bỏ qua tệp tạm dở dang. Bản nguồn và bản sao cùng SHA-256, giữ nguyên ID, trạng thái, lịch sử qua kiểm kê giải mã. Ba lần chạy lại trả 409 và không tạo trùng. Tài khoản phụ không migrate, tìm, đọc hoặc xem lịch sử ký ức đã chuyển.
- Gmail search, Calendar read, Drive OAuth refresh thật trên Backend mới đều HTTP 200. Không thử tải lên hoặc sửa tài liệu thật.
- M2 tiếp theo: xây truy xuất lai có lọc owner trước khi chấm điểm. Reuse SQLite trong repo; chưa thấy tích hợp vector hiện có.

## Memory Core V2 — nghiệm thu M1 đang mở

- Đã bổ sung danh tính chủ workspace và tài khoản phụ chỉ có quyền API Memory. Server gán danh tính từ mật khẩu đã xác thực, không nhận owner từ request. Phân vùng tệp mã hóa, mục, lịch sử và danh sách chọn theo owner. API tra cứu theo ID, tìm, sửa theo version, hoàn thành và xóa mềm.
- Build PASS. HTTP thật trên cổng 5194: hai tài khoản độc lập, chặn đọc/tìm/sửa/xóa/hoàn thành/lịch sử xuyên owner (404), tài khoản phụ bị chặn API Google, logout và phiên hết hạn trả 401, restart vô hiệu cookie cũ và đăng nhập lại đọc được dữ liệu.
- Migration chỉ khi chủ workspace gọi POST /api/memory/migrate-legacy, sao lưu ciphertext trước khi gán owner, giữ nguyên nguồn và từ chối ghi đè kho đã có. Cần chạy E2E migration áp dụng và sao lưu, chạy lại DeepSeek/audit trên filename phân vùng mới, cập nhật fixture audit, regression Google và tách hunk Ca trực trước commit M1. Chưa chạy M2.

- Kiểm kê giải mã độc lập PASS bằng `detot-lab/tests/memory-audit`: hai ID đúng, lịch sử tạo và chuyển open → done, thời điểm có thứ tự, nguồn conversation/request đúng. Test DeepSeek thật trên bản mới PASS qua restart, kiểm tra ciphertext đạt. Parser dùng chung với Backend từ chối văn bản xác nhận giả, JSON hỏng, loại sai và đoạn nội dung không có trong tin nhắn.
- Chưa đạt phân quyền A/B trong cùng workspace: `RemoteAccess` hiện xác thực một mật khẩu chung và lưu phiên dưới dạng bool. Không có user identity đáng tin để gắn với bản ghi. Hai cookie hoặc hai Conversation ID không đại diện hai người dùng. Muốn nghiệm thu cần thêm principal thực, phân vùng kho theo principal và quy tắc gán kho cũ chỉ cho chủ đã xác minh. Giữ chưa commit M1, chưa chuyển M2.

- DeepSeek thật trên Backend cô lập cổng 5195: hai việc máy in có ID khác nhau, retry trả lại ID cũ, yêu cầu mơ hồ đưa danh sách, “việc thứ hai” chỉ hoàn thành việc phòng nhân sự, cập nhật lặp theo ID báo đã hoàn thành. Đã kiểm tra bytes là payload Data Protection; không chứa marker QA dạng rõ. Sau khi tắt và chạy lại Backend, chat mới đọc thấy đúng một việc mở và một việc hoàn thành. Build 0 lỗi, regression lịch sử frontend 2/2.
- Danh sách chọn được mã hóa, gắn ID chat và hết hạn sau 30 phút. Backend so version của ứng viên với độ dài lịch sử và kiểm tra loại/trạng thái. Lab hiện dùng một chủ workspace; chưa có kiểm thử phân quyền đa người dùng.
- Trước khi commit M1: thêm kiểm thử độc lập đọc lịch sử đã giải mã và nhánh classifier trả lời tự nhiên sai schema. Tách các hunk Ca trực đang dở khỏi commit M1. M2 chưa bắt đầu.

- Đã chuyển phân loại sang DeepSeek Chat Completions JSON mode, validate schema backend và trả trạng thái chưa lưu khi lỗi; thêm log chỉ có HTTP/parse/validation/persistence. Xác nhận lưu yêu cầu request ID, đọc lại bản ghi từ kho mã hóa và trả lại cùng ID khi retry.
- Build đạt. DeepSeek thật đã sinh hai Memory ID riêng trong pha prepare. Pha này dừng ở lệnh hoàn thành mơ hồ, hệ thống trả chưa lưu thay vì hỏi mã. Đã cập nhật schema để chấp nhận category=task cho complete. Cần build và chạy lại, kiểm tra retry, dữ liệu mã hóa, lịch sử và resume sau restart. M1 chưa commit, M2 chưa bắt đầu.

- Đã thử lại DeepSeek thật sau khi tăng deadline, chấp nhận JSON fenced, và đặt yêu cầu phân loại trong cả input lẫn instructions. Build đạt nhưng `prepare` vẫn FAIL: hai câu nhận câu trả lời chat thông thường, có lần chatbot nói đã ghi nhận dù không có ID. Chưa chứng minh bộ phân loại trả nội dung gì vì launcher hiện không hiển thị log Backend.
- Tiếp theo: thu log Backend cô lập theo loại lỗi (timeout/HTTP/JSON/schema), không in input hoặc credential. Nếu gateway không giữ hợp đồng phân loại, ưu tiên một request DeepSeek JSON chuyên dụng hoặc công cụ khai báo schema. Loại bỏ xác nhận lưu sai khi chưa ghi kho. Sau đó chạy lại prepare, restart, resume, mã hóa và lịch sử.

- Lab cổng 5196/Gateway 18896 build thành công với thư mục practice cô lập, dùng kết nối DeepSeek thật. Regression lịch sử frontend 2/2.
- `node detot-lab/tests/memory-live.mjs prepare detot-lab/.local/memory-v2-live-checkpoint.json` chưa đạt: HTTP thành công nhưng một hoặc cả hai câu lưu không trả mã. Chưa xác định nguyên nhân. Đã tăng thời hạn phân loại từ 8 lên 22 giây; cần build lại và kiểm thử, rồi chẩn đoán JSON/timeout nếu còn lỗi mà không in nội dung riêng tư. Dùng pha `resume` sau restart cùng cổng khi `prepare` đạt.
- Chưa xác minh mã hóa và lịch sử của lượt DeepSeek, chưa commit M1. M2/M3 vẫn chờ M1. Không stage thay đổi Ca trực, Calendar hoặc lockfile pnpm sinh từ môi trường thử.

## M1 — Ca trực giả lập Pro (đang làm)

- HEAD trước thay đổi: `a570149`. M1 chưa commit.
- Tái sử dụng `Practice.cs`, API chat, nút Ca trực giả lập và kho trạng thái mã hóa hiện có. Ca trực trả dữ kiện viết sẵn, không chạy lệnh thật.
- Đã thêm ba fixture LAN, DHCP, VLAN, gợi ý ba cấp lưu theo phiên và tuần tự hóa lượt xử lý ca trong một tiến trình.
- Còn thiếu: ca Windows riêng và Active Directory rõ ràng, phân vai nhất quán, kiểm tra bằng chứng bắt buộc, chấm điểm theo bốn tiêu chí, bàn giao tùy chọn bằng tiếng Anh, chuyển về chat thật và kiểm thử các luồng đó. Kiểm tra lại liệu câu hỏi phủ định hoặc lệnh sai có bị nhận nhầm.
- Bằng chứng: `git diff --check` đạt tại checkpoint. Build bị chặn trong shell hiện tại: không tìm thấy `dotnet` trên PATH, `C:\Program Files\dotnet\dotnet.exe` hoặc `detot-lab\.local\dotnet\dotnet.exe`. Chưa kiểm thử M1 qua UI/API, chưa nghiệm thu.
- Bước tiếp theo: tìm SDK hiện có trên máy nhà, dựng build tách khỏi bản đang chạy, hoàn thiện các khoảng trống trên và kiểm chứng nhiều ca qua API/UI, bao gồm restart và lệnh không hỗ trợ. Chỉ commit M1 và chuyển M2 sau khi đạt nghiệm thu.
- Giữ dữ liệu, state và cấu hình máy công ty. Không đụng BIOS, không push. Thư mục `.pnpm-store/` đã có từ trước.

### Checkpoint tiếp theo

- SDK 10.0.404 đã cài tại `detot-lab/.local/dotnet`. Đặt `DOTNET_CLI_HOME` trong `detot-lab/.local` trước khi build trong sandbox. Build Backend vào `detot-lab/.local/m1-check/build2` đạt, 0 warning và 0 error.
- Đã bổ sung Windows và Active Directory, bằng chứng trực tiếp cho năm ca mới, bàn giao tiếng Việt/Anh hoặc bỏ qua, và báo cáo dữ kiện/gợi ý/chẩn đoán. Đã sửa lỗi 502 khi đọc hồ sơ lịch sử được lưu dưới dạng JSON lồng trong chuỗi, có tương thích dữ liệu cũ.
- API thật trên Backend cô lập cổng 5198 đạt 3/3 (`detot-lab/tests/practice-api.test.mjs`): DHCP nhiều lượt và lệnh sai, phân lập hai ca đồng thời đang mở, chặn ghi cross-origin. Regression lịch sử frontend đạt 2/2. Chạy Backend và HTTP client trong cùng lệnh có quyền loopback. Không cần xác thực Google.
- Còn thiếu chấm điểm an toàn/hiệu quả dựa trên thao tác người dùng, phân vai rõ, chuyển về chat thật, kiểm thử restart và UI. Chưa nghiệm thu hoặc commit M1. Tiếp tục các phần này trước M2. Dùng thư mục build mới nếu DLL bị khóa bởi phiên kiểm tra cổng 5197.

### Checkpoint M1 tiếp theo

- Đã gắn vai nhân viên, quản trị viên và thiết bị cho dữ kiện có sẵn. Báo cáo hiển thị bốn tiêu chí, gồm số lần bị chặn thao tác rủi ro và các số liệu điều tra. Nút chuyển sang chat thường mở cuộc trò chuyện mới và giữ ca cũ để quay lại.
- Backend build3 và frontend build đạt. Regression API cổng 5198 đạt 3/3 sau thay đổi.
- Tiếp theo: kiểm thử qua trình duyệt, restart giữa ca, kiểm thử chặn thao tác rủi ro và hoàn thiện chuyển ngữ cảnh từ yêu cầu tự nhiên. Các ca cũ vẫn cần chỉ định bằng chứng trực tiếp và kiểm tra tính nhất quán. M1 chưa nghiệm thu, chưa commit, chưa chuyển M2.

### M1 — Phục hồi theo trạng thái

- Ca LAN đã có luồng chẩn đoán, cắm lại cáp giả lập, ping xác minh rồi bàn giao. Ping trước khi sửa vẫn thất bại; báo cáo chỉ xác nhận phục hồi sau ping thành công.
- Build4 đạt. API thật trên Backend cô lập đạt 4/4, gồm ca LAN kiểm tra đúng thứ tự. Cần build và chạy lại sau chỉnh sửa nhỏ để truy vấn xác minh lặp lại trả cùng kết quả.
- Bước tiếp theo: mở rộng hành động và xác minh sang các ca còn lại, kiểm thử restart giữa lúc sửa và xác minh, triển khai workspace ca trực (danh sách, terminal, dữ kiện, timeline) và chuyển ngữ cảnh tự nhiên. Chưa commit, chưa nghiệm thu M1; M2–M4 chưa bắt đầu.

### M1 — Kiểm thử phục hồi và restart

- Cả 11 ca hiện có cấu hình sửa lỗi, xác minh trước/sau và bằng chứng trực tiếp bắt buộc. Dữ kiện lịch sử được ghi nhãn sau khi sửa.
- Build5 đạt 0 lỗi và 0 cảnh báo. Cổng 5198 trên Backend cô lập: 4 kiểm thử API và 2 kiểm thử lịch sử frontend đạt. Restart thật tiến trình thử nghiệm sau khi cắm lại cáp giữ được ca LAN, ping xác nhận phục hồi và lần ping sau vẫn đạt 4/4.
- M1 còn cần UI workspace, chuyển chat từ câu tự nhiên, và kiểm thử đầy đủ 11 ca qua API. Chưa nghiệm thu hoặc commit. M2–M4 chưa bắt đầu.
# Memory Core V2 — M1 đang triển khai

- M1 hiện dùng kho mã hóa hiện có của Backend để lưu nhiều bản ghi có ID, loại, nguồn hội thoại, thời điểm, trạng thái, mức tin cậy và lịch sử. Mức 1 biểu thị nội dung do người dùng yêu cầu lưu, không xác nhận nội dung đúng ngoài thực tế.
- DeepSeek phân loại lưu, truy xuất, hoàn thành. Backend kiểm tra đoạn trích và loại bản ghi. Hoàn thành yêu cầu ID nằm trong lời người dùng để tránh sửa nhầm. Kho cũ một mục được nhập khi dùng lần đầu, giữ nguyên tệp cũ.
- Đường Memory Core/Dreaming trong repo hiện có cho backup. Chưa có bằng chứng truy xuất runtime. M2–M4 chưa bắt đầu.
- Tiếp theo: chạy Backend mới với Gateway thật, kiểm thử hai mục máy in, cập nhật đúng ID và restart; bổ sung kiểm thử cho trường hợp mơ hồ. Chưa nghiệm thu và chưa commit. Giữ nguyên thay đổi Ca trực và Calendar ngoài phạm vi.

## M3 — Reflective Memory & Dreaming (ngày 15/11/2026)

- M3 đã triển khai nền worker owner-scoped, BGE-M3 candidate retrieval, DeepSeek JSON assessment với nhãn `unverified-inference`, encrypted queue/checkpoint/retry, chống đề xuất trùng, stale-version protection và review approve/edit/reject có lịch sử. UI có bằng chứng, confidence, trạng thái tiến độ và thao tác duyệt.
- Nghiệm thu trên Backend cô lập: `dotnet build` 0 warning/0 error; frontend production build và 2/2 test; cloud storage/Google regression 24/24; MemoryAudit ranking/parser PASS.
- E2E thật: Ollama BGE-M3 và DeepSeek thật sinh đề xuất duplicate/conflict/stale; state và queue sống sót restart; checkpoint tiếp tục batch sau restart; loại trừ chuyển trạng thái hợp lệ `open→done`; duyệt cạnh tranh chỉ cập nhật một lần; sửa memory làm đề xuất cũ stale/409; lịch sử được ghi; dữ liệu encrypted; A không truy cập được Memory/queue/vector của B. Kiểm tra Gmail readonly, Calendar read, Drive token refresh + readonly listing trên token clone cô lập PASS. Không gửi email, không sửa dữ liệu cloud; token refresh chỉ trong bản sao thử nghiệm.
- M3 nghiệm thu PASS trên môi trường cô lập. Commit local đang chờ tách biệt các sửa đổi có sẵn ngoài M3; không push.
- **Bước tiếp theo: M4 Agentic Memory** — rà API Memory task và Google Calendar; liên kết việc với sự kiện/nhắc lịch có idempotency, owner checks, xác nhận rõ khi thiếu thời gian/đối tượng, cập nhật completion và ghi audit. Không tự tạo/xóa sự kiện thật; chỉ mock/local test cho mutation nếu không có consent cụ thể.
- Không sửa DB/state thật, không đổi cấu hình công ty, không đưa secrets vào Git/log.
