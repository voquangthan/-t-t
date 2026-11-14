# Đệ Tốt AI Lab

Frontend và ASP.NET Core cùng origin tại `http://localhost:5183/`. Backend gọi OpenResponses của Gateway tại `127.0.0.1:18793`. Đây là lựa chọn cho lab một người dùng để kiểm tra client-side function tools qua hai lượt. Gateway có bearer token ngẫu nhiên theo lần chạy. Bearer HTTP của OpenClaw có quyền operator đầy đủ, nên token chỉ ở backend và Gateway, với cổng loopback riêng cho lab. Backend tự khai báo và chỉ thực thi `detot_lab_fixture`, trả mã cố định `DETOT-LAB-003`. Frontend hiển thị text, không render HTML từ model.

Nút **Ca trực giả lập** chạy một chế độ riêng, không gọi Gateway hay tool. Chế độ này mở một tình huống IT hư cấu (Helpdesk/máy in, Windows DNS, SQL connection pool, API token, Docker env hoặc tài khoản dịch vụ), giữ trạng thái theo cuộc trò chuyện và chỉ trả log/kết quả dựng sẵn. Thu thập ít nhất hai dữ kiện rồi nêu nguyên nhân để nhận phản hồi; nói “kết thúc ca” để xem lời giải. Không có lệnh, sửa hệ thống, database hay thao tác Git nào được thực thi. Mã `DETOT-LAB-003` chỉ thuộc bài thử function tool ở chế độ chat thường.

Sau khi kết thúc ca, hồ sơ tự học chỉ lưu số tổng hợp theo chủ đề (ca hoàn tất, chẩn đoán, lượt kiểm tra, bàn giao và kết thúc sớm), được mã hóa cục bộ. Đệ Tốt ưu tiên chủ đề chưa luyện hoặc có điểm yếu; sau các ca đạt yêu cầu, tăng số dữ kiện cần kiểm tra từ 2 lên 3. Hồ sơ không lưu nội dung chat, log hoặc mã nguồn; mục **Ghi chú học tập** vẫn dành cho ghi chú do người dùng tự nhập.

## Google Drive Cloud Storage

Trang **Cloud Storage** dùng Google Drive API qua OAuth riêng cho Drive, với scope `drive.file` và `openid email`. Backend gợi ý tài khoản `voquangthan1234` bằng `login_hint`; Google vẫn yêu cầu người dùng đăng nhập và tự cấp quyền. Kết nối Drive là riêng với danh sách Gmail/Calendar. Token, phiên upload và lịch sử Drive được bảo vệ bằng ASP.NET Data Protection + DPAPI trong `.local/google/`; thư mục này bị Git ignore.

Drive chỉ được dùng trong cây `DETOT-AI/` do ứng dụng tạo và đánh dấu. File/thư mục cũ, kể cả cùng tên, không được nhận làm của ứng dụng. Cây con gồm `datasets/{vietnamese,programming,it-support,agent-training}`, `models`, `memory-backups`, `training-results` và `documents`. Upload có version, nguồn, license, SHA-256, phiên resumable và chống ghi đè cùng phiên bản. Dataset cần giấy phép cụ thể; không có chức năng tự tìm hay tải dataset từ nguồn ngoài. Thao tác xóa/retention chỉ chuyển artifact thuộc cây `DETOT-AI` vào Google Drive Trash. **Ngắt kết nối** xóa token Drive cục bộ và không thu hồi grant OAuth dùng chung để tránh làm gián đoạn Gmail/Calendar; người dùng có thể thu hồi grant Google trong cài đặt tài khoản Google.

Để bật OAuth, tạo Google OAuth Web client có callback URI chính xác cho Gmail/Calendar và `/api/drive/callback` của origin đang chạy, sau đó import cấu hình cục bộ:

```powershell
./Start-Lab.ps1 -ImportGoogleOAuth C:\path\to\oauth-client.json
```

Khởi động lại lab và bấm **Kết nối tài khoản voquangthan1234** trong Cloud Storage. Client secret được lưu cục bộ bằng DPAPI; không đưa file OAuth vào Git hoặc chat. `DETOT_GOOGLE_DRIVE_LOGIN_HINT` cho phép thay đổi gợi ý tài khoản.

Memory backup chỉ nhận các nguồn được cấu hình rõ ràng bằng `DETOT_MEMORY_CORE_PATH`, `DETOT_DREAMING_PATH` và `DETOT_MEMORY_RELATED_PATH`. Từng lần backup cần xác nhận trong giao diện và passphrase riêng tối thiểu 16 ký tự. Backup dùng AES-GCM theo khối; khóa dẫn xuất PBKDF2 và passphrase không được lưu hoặc tải lên Drive. Tệp có dấu hiệu chứa credential bị loại khỏi backup. Restore chỉ giải mã vào vùng tạm, xác minh manifest/SHA-256 và yêu cầu danh sách thu hồi session bên ngoài snapshot; không tự ghi đè database hoặc Memory Core. Tệp khôi phục sẵn sàng tải một lần trong 15 phút. Google Drive không thay thế database đang chạy.

Dung lượng hiển thị là tổng byte artifact trong cây `DETOT-AI` mà API cho phép liệt kê, không phải toàn bộ quota tài khoản. Chưa có OAuth consent trong môi trường triển khai thì giao diện sẽ báo chưa cấu hình/chưa kết nối, không hiển thị dữ liệu mẫu.

## File & URL Reader

Trong chat, có thể kéo thả/chọn tối đa 5 tệp hoặc gửi URL HTTPS công khai. Reader trích xuất văn bản ngay trong lab, đưa vào đúng lượt hỏi đáp rồi bỏ khỏi ngữ cảnh request; Read Mode không thêm nội dung vào Memory/RAG. Dataset Drive đã được phê duyệt dùng chung `UniversalFileReader` trước khi chạy Quality Gate. Các trích đoạn có SHA-256 và nhãn dòng, phần DOCX, hàng/sheet XLSX và slide PPTX; ZIP chỉ đọc tối đa 100 mục và 32 MiB giải nén, không ghi file ra đĩa hay chạy nội dung bên trong.

Văn bản, JSON/JSONL, XML, CSV/TSV, source code, DOCX, XLSX, PPTX, PDF có lớp văn bản, Parquet, SQLite và ZIP văn bản được trích xuất cục bộ. PDF giữ số trang; PDF scan cần OCR riêng. XLSX giữ tên phần sheet và hàng; SQLite mở read-only, chỉ đọc tối đa 20 bảng/1000 hàng, không thực thi SQL từ người dùng; Parquet giới hạn 2000 hàng. JPEG/PNG/GIF/WebP được gửi dạng ảnh qua Responses API tới model `deepseek-flash` đang khai báo Vision trong cấu hình lab; giới hạn 3 ảnh, 4 MiB mỗi ảnh. Phần văn bản/ảnh được gửi tới nhà cung cấp LLM để trả lời. URL reader chỉ lấy HTTPS, giới hạn 2 MiB, timeout và 3 lần redirect; client khóa proxy và từ chối DNS trả địa chỉ private. Trang chỉ có nội dung JavaScript, redirect ra HTTP, CAPTCHA hoặc đăng nhập không được vượt qua.

PDF scan, TIFF/BMP và DOC/XLS/PPT cũ hiện trả lỗi adapter chưa được cài, không báo đã đọc thành công. Ảnh nhúng trong Office/PDF, citation theo trang in DOCX và PDF renderer/preview chưa có. Không dùng HTML renderer bên ngoài cho nội dung chat. Đây là các giới hạn rõ ràng thay vì kết quả giả.

## Trusted Learning Sources

Trang **Nguồn học** quản lý các nguồn theo trạng thái `pending → checked → approved` hoặc `quarantined`. Chat nhận câu lệnh như “Học ASP.NET Core từ Microsoft Learn” và mở Search Discovery với chủ đề/domain tương ứng. Khi đặt `DETOT_SEARXNG_BASE_URL`, Search Discovery dùng SearXNG tự host để tìm URL ứng viên. Backend kiểm tra giấy phép và chờ người dùng phê duyệt phạm vi trước khi Worker thu thập qua Universal Reader. URL cụ thể vẫn có thể thêm trực tiếp để kiểm tra.

Quyền truy cập được lưu riêng với giấy phép; connector hiện chỉ đọc. Backend tự nhận diện license từ metadata GitHub, metadata Hugging Face dataset card và metadata/license URL của Website. License do người dùng nhập không được xem là bằng chứng. Chỉ giấy phép trong allowlist tự động (CC0, CC-BY/CC-BY-SA, MIT, Apache-2.0, BSD, PSF/Python và MPL) mới được xử lý tự động; thiếu bằng chứng hoặc license ngoài chính sách sẽ bị cách ly, không có nút xác nhận thủ công. Website giới hạn ở đúng URL đã khai báo, GitHub chỉ đọc README của một repository, Drive chỉ đọc một thư mục dataset con trong `DETOT-AI/datasets` và kiểm tra metadata license từng artifact.

Worker chạy nền mỗi 15 phút, đồng bộ nguồn tối đa sau 6 giờ hoặc theo yêu cầu thủ công; lỗi có backoff tăng dần tới 6 giờ, retry thủ công và trạng thái đang chạy được đưa lại hàng đợi sau restart. Drive manifest gồm ID, checksum, kích thước, phiên bản và thời gian sửa; manifest không đổi sẽ bỏ qua tải file và trích xuất. Mỗi đoạn lưu citation, license, phương thức/URL bằng chứng license, version nguồn hoặc checksum artifact và SHA-256 của nội dung. Quality Gate lọc prompt-injection bằng luật, bỏ hash trùng, dùng Jaccard để đánh dấu đoạn gần giống và cách ly mâu thuẫn dấu phủ định; đây là heuristic cục bộ, không phải xác minh tính đúng bằng LLM. Kết quả không đạt được cách ly cùng lý do. Đây là cập nhật chỉ mục RAG, không tự huấn luyện hoặc thay đổi trọng số model.

Danh sách nguồn và chỉ mục cục bộ được mã hóa bằng Data Protection; snapshot có kiểm tra checksum được đồng bộ vào `DETOT-AI/documents` để máy khác nạp lại. Thu hồi nguồn gỡ các đoạn khỏi retrieval cục bộ ngay, ghi snapshot thay thế rồi xóa snapshot cũ; nếu Drive lỗi, cờ mirror pending được giữ bền vững để worker thử lại và dashboard báo trạng thái. File dataset gốc vẫn được giữ nguyên. Vì người dùng phê duyệt việc thu thập, nội dung trích xuất của nguồn được lưu trong snapshot Drive.

## Môi trường

Git 2.56.1 đã có trên máy. Node.js 24.22.3, .NET SDK 10.0.404 và OpenClaw 2026.11.3 được đặt trong `.local/`. OpenClaw chạy trực tiếp trên Windows vì trình cài WSL trả mã 1603. Binding fs-safe x64 đã nạp thành công sau khi bổ sung DLL runtime có chữ ký Microsoft riêng trong lab.

## Chạy thử

Trong Windows PowerShell 5.1 hoặc PowerShell 7, mở thư mục `detot-lab`. Cài frontend và bản Gateway đã khóa trước lần chạy đầu tiên:

```powershell
npm.cmd --prefix frontend ci
npm.cmd --prefix tests/runtime-candidate ci
```

Để chạy SearXNG trên cùng máy, dùng Docker với Linux containers. Cấu hình mẫu chỉ mở cổng trên loopback và bật JSON. Tạo secret riêng cho instance, sau đó khởi chạy trong PowerShell cùng thư mục `detot-lab`. [Tài liệu SearXNG](https://docs.searxng.org/admin/installation-docker.html)

```powershell
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$secretBytes = New-Object byte[] 32
try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
$env:SEARXNG_SECRET = [Convert]::ToBase64String($secretBytes)
docker compose -f searxng/compose.yaml up -d
$env:DETOT_SEARCH_PROVIDER = 'searxng'
$env:DETOT_SEARXNG_BASE_URL = 'http://127.0.0.1:8888'
./Start-Lab.ps1
```

Với SearXNG đã tự host trên máy khác, đặt `DETOT_SEARXNG_BASE_URL` thành base URL HTTPS của instance và bật `json` trong `search.formats`. Có thể bỏ qua `DETOT_SEARCH_PROVIDER` khi không có cấu hình provider cũ. Backend gọi `POST /search` qua form với `format=json`. SearXNG chuyển truy vấn cho các công cụ tìm kiếm được bật trên instance. [Search API](https://docs.searxng.org/dev/search_api.html)

Sau khi mở workspace, vào **Nguồn học → Tìm nguồn**. Tìm một chủ đề, kiểm tra và phê duyệt URL phù hợp, sau đó đồng bộ. Hỏi lại chủ đề trong Chat để kiểm tra câu trả lời dẫn đúng URL của nguồn.

Gateway dùng `deepseek-flash`. Nếu cần lưu khóa DeepSeek cục bộ bằng DPAPI, chạy `./Start-Lab.ps1 -ConfigureDeepSeek`. Launcher cũng chấp nhận `DEEPSEEK_API_KEY` của phiên hoặc hỏi bằng prompt ẩn. Script dùng state Gateway trong `.state/`, có thể chứa hội thoại thử.

Sau khi đổi cấu hình provider, dừng lab bằng Ctrl+C trong terminal cũ rồi chạy lại launcher để Gateway nhận key của phiên mới.

Mở [trang lab](http://localhost:5183/) và gửi câu đã điền. Đạt khi thấy “Đã gọi công cụ giả lập” và câu trả lời có `DETOT-LAB-003`. Ctrl+C tại terminal chạy script để dừng.

Ghim OpenClaw `2026.11.3` cho mục đích thử nghiệm trên loopback. Cấu hình tắt Control UI, memory search, cron, heartbeat, Tool Search, Code Mode và tool truy cập máy. ASP.NET Core từ chối Origin ngoài địa chỉ lab và không chuyển tiếp URL hoặc tool tùy ý. Bản Windows dùng chung ranh giới tài khoản Windows của người chạy. Khi WSL2 sẵn sàng, có thể chuyển Gateway sang môi trường tách riêng sau khi kiểm tra lại loopback và quyền mount.

## Kiểm thử

- Chạy `dotnet build --no-restore` để build ASP.NET Core.
- Chạy `dotnet run --project tests/cloud-storage/CloudStorageChecks.csproj` để kiểm tra Dataset Manager, backup mã hóa và vòng đời Trusted Learning bằng storage provider giả lập; không gọi Google Drive thật.
- Chạy `npm test` và `npm run build` trong `frontend/`.
- Edge headless gửi câu mẫu qua ASP.NET Core, Gateway OpenClaw thật và provider giả lập trên loopback. Provider chỉ nhận `detot_lab_fixture`. Frontend hiển thị `DETOT-LAB-003`.
- Backend từ chối origin khác và tên tool ngoài allowlist.
- Kiểm tra Drive thật qua `/api/drive/status` tại `http://localhost:5183` khi Backend đang chạy. Đạt khi `enabled`, `connected` và `available` đều là `true`. Nếu chưa kết nối, cấp quyền tại Cloud Storage trong trình duyệt.
- DeepSeek thật còn chờ key nhập ở terminal. WSL2 còn chờ cài đặt bằng quyền quản trị.

### Kết quả nghiệm thu trước khi chuyển sang SearXNG, 14/11/2026

| Bước | Kết quả | Bằng chứng |
| --- | --- | --- |
| Backend và bản build frontend | PASS | Backend `/health` trả `ready`, build thành công, kiểm thử frontend 2/2. |
| Gateway OpenClaw | PASS với provider giả lập | `/readyz` HTTP 200, từ chối bearer sai, gọi tool và tiếp tục lượt qua HTTP 200. |
| Google Drive thật | PASS kết nối và liệt kê | `enabled`, `connected`, `available` đều `true`. API liệt kê 1 artifact `trusted-learning` đã có. |
| Search thật | BLOCKED | Backend báo `configured=false`. Cần `DETOT_SEARCH_PROVIDER=microsoft-web` và `DETOT_SEARCH_API_KEY`. |
| DeepSeek thật | BLOCKED | Thiếu `.local/deepseek-key.dpapi` và `DEEPSEEK_API_KEY` trong phiên kiểm tra. |
| Reader, Quality Gate, RAG và đồng bộ | PASS kiểm thử cục bộ | 20/20 từ thư mục `detot-lab`. Có kiểm tra citation và bằng chứng giấy phép, trùng lặp, mâu thuẫn, prompt injection, retry khi mất Drive, checkpoint qua restart, bỏ qua manifest không đổi và thu hồi nguồn. |
| E2E Search → Reader → Quality Gate → Drive → RAG → DeepSeek | BLOCKED | Chưa xác nhận ghi snapshot mới lên Drive hoặc citation trong câu trả lời DeepSeek với API thật. |
