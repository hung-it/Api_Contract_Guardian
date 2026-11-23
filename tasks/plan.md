# Roadmap triển khai API Contract Guardian

## Mục tiêu và điểm xuất phát

API Contract Guardian là công cụ .NET kiểm tra tương thích ngược giữa hai bản OpenAPI. Khi một Merge Request trong FlowForge thay đổi contract, Guardian áp dụng policy của nhánh đích, ghi rõ lý do trong report và quyết định trạng thái của GitLab job. Artifact của cùng lần chạy có thể được gửi lên API để tra cứu lịch sử.

- Kế hoạch bắt đầu từ tuần 1 của Guardian, với giả định một người làm 12–15 giờ/tuần theo roadmap tổng thể. Dự trù 4 tuần cho MVP, tuần 5 cho tích hợp FlowForge, và tuần 6–9 cho history, dashboard và release. Các mốc tính theo kết quả kiểm chứng.
- Khi lập kế hoạch, repo Guardian local đã có `origin` và `main` chưa có commit. Máy có .NET SDK 10.0.201. GitLab runner, registry và khả năng chạy Docker cần xác nhận trong tuần 1.
- Snapshot FlowForge local kiểm tra được ở `05d7e7c`. Route thực tế là `GET /api/v1/runs/{run_id}`, dùng `RunOutput` có `run_id: UUID`. Lần thử export tại chỗ chưa chạy được vì virtualenv trỏ tới Python không khả dụng. Tuần 1 cần lấy schema thật từ môi trường FlowForge hoạt động và chốt commit dùng làm baseline.
- Hai tài liệu nguồn thống nhất về offline gate. Bản Word đưa history và dashboard vào MVP tổng thể. Bản kỹ thuật đặt chúng ở P1 và xây sau CLI. Kế hoạch này dùng phân kỳ P0/P1 của bản kỹ thuật, đưa demo FlowForge từ tuần 8–9 của phần Guardian lên tuần 5. Dashboard read-only là mục tiêu P1 trong lịch. Exception approvals đi sau release chính nếu cần thêm thời gian. AWS là P2 sau khi có release và demo tái lập.

## Phạm vi và quyết định triển khai

| Hạng mục | Quyết định |
|---|---|
| P0 | CLI `validate`, `compare`, `version`; oasdiff đã pin; rule mapping; policy YAML; JSON/Markdown; GitLab MR job; fixtures và đóng gói CLI |
| Tích hợp | FlowForge xuất OpenAPI từ source của từng SHA, dùng Guardian image theo digest và lưu artifacts của MR |
| P1 | Publish report độc lập; ASP.NET Core, EF Core/Npgsql, PostgreSQL; project isolation; history API và React viewer; token revoke và audit cơ bản |
| Sau release chính | Exception có phê duyệt và expiry, MR comments, nhiều spec/repo, hỗ trợ dialect mở rộng, AWS |

**Stack khởi đầu** gồm `Guardian.Core`, `Guardian.Cli` và xUnit. Để adapter oasdiff ở biên CLI, giữ Core chỉ chứa dữ liệu và logic quyết định. Bổ sung dự án web API và persistence khi làm lát cắt publish/history. .NET 10 là LTS theo [support policy của Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy).

**Chọn dialect theo schema thật trong tuần 1.** Fixture khởi đầu dùng 3.0.x theo thiết kế. FastAPI mặc định sinh OpenAPI 3.1.0. Nếu bản export của FlowForge là 3.1, ưu tiên kiểm thử 3.1/3.1 trên các cấu trúc FlowForge sử dụng, pin bản oasdiff tương ứng, công bố support matrix và giữ kiểm thử 3.0 riêng nếu đủ thời gian. Các dialect hoặc cấu trúc ngoài phạm vi đã kiểm thử phải có chẩn đoán rõ. Việc đổi riêng giá trị trường `openapi` từ 3.1 xuống 3.0 không chuyển được ngữ nghĩa schema. [FastAPI OpenAPI example](https://fastapi.tiangolo.com/tutorial/first-steps/) · [OpenAPI 3.1 specification](https://spec.openapis.org/oas/v3.1.0.html) · [oasdiff 3.1 support](https://github.com/oasdiff/oasdiff/blob/main/docs/OPENAPI-31.md).

**Đường dữ liệu của gate** đi từ baseline + candidate + trusted policy qua validation, oasdiff, chuẩn hóa findings, policy evaluation và report. Ưu tiên khảo sát `oasdiff validate` và `oasdiff changelog --format json` trên engine version đã pin để kiểm tra dữ liệu cho ERR/WARN/INFO, ví dụ thêm endpoint. Kiểm tra coverage của rule bằng fixtures. Guardian tự quyết định verdict và phân biệt findings với lỗi chạy engine. [oasdiff CLI](https://github.com/oasdiff/oasdiff) · [Output formats](https://github.com/oasdiff/oasdiff/blob/main/docs/OUTPUT-FORMATS.md).

**CLI contract đề xuất** là `0` PASS, `2` policy FAIL, `3` invalid/unsupported spec hoặc thiếu baseline, `4` engine/timeout/internal error, `5` invalid/untrusted policy. Rule chưa map dưới strict policy cho `INDETERMINATE` và exit `2` khi engine vẫn trả finding hợp lệ. Output engine hỏng cho exit `4`. Khóa các quyết định này bằng CLI tests. Report v1 ghi status, diagnostics, sorted findings, fingerprint, version CLI/engine, ba hash của spec base/candidate và policy, target SHA/candidate SHA và pipeline ID khi có. Timestamp và metadata CI thay đổi theo lần chạy nằm ngoài nội dung dùng để so tính tái lập.

**GitHub là nguồn chính, GitLab chạy CI.** Guardian dùng `origin` cho GitHub và remote `gitlab` cho GitLab. Tuần 1 đưa cùng commit lên hai remote và đối chiếu full SHA với job GitLab. FlowForge thực hiện integration theo luồng GitHub → GitLab hiện được mô tả trong repo. Nếu merge trên GitHub, ghi rõ kiểm tra same-SHA thủ công hoặc cấu hình required check ở GitHub trước khi tuyên bố tự động chặn merge ở đó. [GitHub Guardian](https://github.com/hung-it/Api_Contract_Guardian) · [GitLab Guardian](https://gitlab.com/hung-it/api_contract_guardian) · [GitLab merge checks](https://docs.gitlab.com/user/project/merge_requests/auto_merge/).

**Trust boundary của CI** gồm nguồn định nghĩa job, image, runner, target SHA, baseline và policy. Dùng baseline và policy từ cùng một snapshot của protected target. Chạy lại gate nếu target đã tiến trước merge. Kiểm chứng job bắt buộc với cấu hình độc lập do maintainer kiểm soát, ví dụ custom CI/CD configuration trỏ tới file trong repo được bảo vệ. Pipeline execution policies là một hướng nếu có GitLab Ultimate. Nếu chỉ dùng job từ chính source MR, mô tả đúng mức bảo đảm trong demo. [GitLab custom configuration](https://docs.gitlab.com/ci/pipelines/settings/#specify-a-custom-cicd-configuration-file) · [Pipeline execution policies](https://docs.gitlab.com/user/application_security/policies/pipeline_execution_policies/).

## Mốc 9 tuần

| Tuần | Việc chính | Mốc nghiệm thu |
|---|---|---|
| 1 | Kiểm chứng SDK/runner, ba contract mẫu, spike oasdiff, lấy schema FlowForge thật, scaffold CLI/Core.Tests | CLI help/version và build/test chạy từ checkout sạch. Có output engine thực, support matrix nháp và dấu vết runner |
| 2 | Spec loader, validation, adapter, finding normalization và fixtures | Từ CLI phân biệt compatible, breaking, invalid input và engine error. Vượt 12–15 fixtures đầu tiên |
| 3 | Policy v1, deterministic report, release image thử và GitLab MR demo | Ba tình huống PASS, FAIL và baseline thiếu chạy trên GitLab. Retain artifacts. Gate chạy khi API chưa triển khai. Khoảng 25 fixtures |
| 4 | Giới hạn tài nguyên, pin checksum/digest, trust GitLab, reproducibility | Ít nhất 30 golden cases cho phạm vi đã công bố. 10 lần chạy cùng input cho cùng nội dung quyết định. Có release candidate P0 và bằng chứng về phạm vi cưỡng chế merge |
| 5 | Export/check-sync OpenAPI FlowForge, seed baseline, chạy Guardian trong pipeline thật | MR demo thay đổi response model làm job FAIL, commit sửa tương thích làm job PASS, report chỉ đúng field/rule và có bằng chứng cùng SHA |
| 6 | API publish có scoped token, migration và DB unique constraint | Áp migration từ DB sạch. Cùng key/payload trả cùng analysis. Concurrent retries chỉ tạo một logical record. Khác payload cùng key trả 409 |
| 7 | History/detail API, membership, React viewer, audit/token revoke, Compose | Reviewer xem một report đã publish, lọc finding và thấy provenance. Kiểm chứng cross-project isolation và token đã revoke |
| 8 | Clean-clone checks, benchmark 10/100/500+ operations, docs, reliability checks | Tái lập demo từ README. Ghi số liệu, phiên bản, workload và kết quả test có thể kiểm tra |
| 9 | Quay video, gắn link MR/artifact, release notes, CV và tag | Release Guardian `v1.0.0` có Quick Start, license, sample contracts, report mẫu và bằng chứng thật |

**Thứ tự phụ thuộc** là schema thật + engine spike → validator/adapter → policy & report → GitLab gate → FlowForge. API publish dựa trên report v1 đã ổn định. React dựa trên history API và RBAC đã được kiểm chứng. Xem [checklist triển khai](todo.md) theo tuần.

## Chuẩn nghiệm thu và điểm dừng

- **Cuối tuần 3** chạy được ít nhất một MR đỏ do breaking và một MR xanh do compatible. Nếu runner, dialect hoặc report còn lỗi, tuần tiếp theo ưu tiên hoàn thành gate.
- **Cuối tuần 4** có bộ fixture với kỳ vọng theo chiều request/response, ít nhất 30 case, chính xác provenance và behavior khi invalid/timeout/unknown. Mục tiêu coverage nhánh policy core khoảng 90%. Kết quả fixture quan trọng được kiểm tra chính xác theo rule/đường dẫn, ngoài mã thoát.
- **Cuối tuần 5** đã đủ bằng chứng cho mảng Platform/DevOps trên CV nếu MR thực tế và quick start tái lập được. Khi lịch bị ép, chuyển P1 sang sau và dùng GitLab artifacts để đọc lịch sử demo.
- **Cuối tuần 9** kiểm chứng CLI từ checkout sạch và Compose nếu release chứa web stack. Đưa version/digest đã dùng, link MR, report mẫu và video vào README. Nếu FlowForge đã có release, dẫn đúng tag/commit; không tạo lại tag chỉ để khớp tên trong roadmap cũ.

## Bằng chứng cho portfolio

1. README tiếng Anh mô tả workflow, Quick Start, sơ đồ, phạm vi OpenAPI và trust boundary. ADR ngắn cho offline-first, oasdiff adapter và trusted CI.
2. Link hai kết quả GitLab hoặc hai lần chạy của cùng MR, report JSON/Markdown, SHA của từng bản và kết quả của compatible fix. Đưa dashboard screenshot vào khi P1 sẵn sàng.
3. Benchmark cùng workload và machine config, nêu phiên bản CLI/engine, số lần warmup và số sample, median/p95 và giới hạn tài nguyên nếu đo được. Cập nhật CV theo dữ liệu thực.
4. Demo 3–5 phút từ local comparison → FlowForge MR FAIL → đọc finding → commit sửa → PASS → history nếu có.

CV sau khi kiểm chứng integration có thể dùng câu sau:

> Built an offline .NET CLI that enforces backward-compatible OpenAPI changes in FlowForge’s GitLab merge request pipelines, using versioned policies and reproducible JSON and Markdown reports.

## Tài liệu nguồn

- [Thiết kế kỹ thuật Guardian](../../API_Contract_Guardian_Technical_Design_and_Roadmap.md), xem mục 3, 8–12, 17–18 và 22–25.
- [Roadmap tổng thể 18 tuần](../../Roadmap_FlowForge_API_Contract_Guardian_AWS_Optional.docx), xem phần II và III.

## Tiến độ thực hiện

| Mốc | Trạng thái | Thay đổi và bằng chứng |
|---|---|---|
| Tuần 1 · T01–T05 | Hoàn thành | GitHub/GitLab cùng SHA `f59099a`. Build, 5 xUnit cases, 5 CLI smoke cases và probes oasdiff đều qua. [Pipeline GitLab](https://gitlab.com/hung-it/api_contract_guardian/-/pipelines/3102928132) · [Chi tiết](../docs/week-1-verification.md) |
| Tuần 2 · T06 | Hoàn thành | Commit `3df2eaf`. Đọc spec JSON/YAML local có giới hạn UTF-8, kích thước, depth và số references. Chấp nhận OpenAPI 3.0.x/3.1.x và internal JSON Pointer. Đã sửa lỗi kiểm tra URI fragment làm từ chối pointer hợp lệ. `dotnet restore --locked-mode` và `dotnet test -c Release --no-restore` qua 37 tests, gồm 32 ca loader. |
| Tuần 2 · T07 | Hoàn thành | Commit `1253d1e`. Adapter kiểm tra oasdiff 1.38.0, gọi process bằng argument list với bản snapshot riêng, giới hạn hai stream và xử lý timeout/cancel. Suite Release qua 47 tests trên Windows và container Linux. Đã thử `guardian validate` với path có khoảng trắng, spec FlowForge, file thiếu và executable sai version. |
| Tuần 2 · T08 | Hoàn thành | `guardian compare` in normalized JSON. Rule chưa map thành UNKNOWN, fingerprint Guardian v1 độc lập với message và tọa độ nguồn. `dotnet test -c Release --no-restore` qua 61 tests và format check qua. Engine thật cho exit 0 khi unchanged/compatible và exit 2 với mẫu breaking cùng bản sao FlowForge đổi `run_id`. |
