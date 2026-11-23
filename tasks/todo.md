# Checklist triển khai API Contract Guardian

Xem [roadmap và các quyết định](plan.md). Mỗi mục có kết quả cần lưu hoặc cách kiểm chứng. Tên file và lệnh Guardian bên dưới là đích triển khai.

## Tuần 1 — Chạy thử engine và dựng CLI

- [x] **T01 · Buổi 1, 2 giờ · Xác nhận môi trường.** Kiểm tra URL `origin`, quyền và GitLab runner/registry. Xác nhận .NET 10 SDK và Docker trên máy dev. Lưu phiên bản, kết quả `dotnet --info`, `docker info` và log job CI tối thiểu.
- [x] **T02 · Buổi 2, 2–3 giờ · Tạo ba contract mẫu.** Thêm `examples/base.yaml`, `compatible.yaml` thêm endpoint và `breaking.yaml` đổi response field string sang integer. Cài engine từ release có checksum. Lưu version/checksum và thử `oasdiff validate`, `oasdiff changelog --format json` cho cả hai cặp. Ghi rule ID và dữ liệu JSON thực.
- [x] **T03 · Buổi 3, 2–3 giờ · Chốt dialect FlowForge.** Chọn SHA FlowForge sẽ làm baseline. Trên môi trường Python có deps theo lockfile, lấy schema qua `create_app().openapi()`. Kiểm tra version, internal refs và hai lần export giống nhau. Chạy probe no-change và response type change trên bản sao fixture, lưu kết quả và chốt support matrix.
- [x] **T04 · Buổi 4, 3 giờ · Tạo solution tối thiểu.** Thêm `src/Guardian.Core`, `src/Guardian.Cli`, `tests/Guardian.Core.Tests` và README tiếng Anh. Chạy `dotnet build`, `dotnet test`, `guardian --help` và `guardian version`.
- [x] **T05 · Buổi 5, 3 giờ · Kiểm tra checkout sạch trong CI.** Thêm restore/build/test job và ghi cách chạy local. Khi remote đã sẵn sàng, chạy CI và lưu link job của đúng commit.

Xem [kết quả kiểm chứng tuần 1](../docs/week-1-verification.md).

## Tuần 2 — Từ hai spec đến findings

- [x] **T06 · Sau T02–T04 · Validate input.** Đọc YAML/JSON, kiểm tra dialect cho phép, giới hạn input và chỉ cho internal fragment references trong tài liệu. YAML/JSON hợp lệ được chấp nhận. Malformed, unsupported, missing path và external ref trả chẩn đoán phù hợp.
- [x] **T07 · Sau T06 · Gọi engine đã pin.** Dùng subprocess argument list, thu stdout/stderr có giới hạn và timeout. Test với engine hợp lệ, lỗi, hết hạn và output hỏng. Test ở path chứa khoảng trắng trên Windows và trong Linux runner.
- [x] **T08 · Sau T07 · Chuẩn hóa findings.** Map rule IDs thật sang Guardian IDs, method/path, schema location và severity. Tính fingerprint từ identity schema ổn định và sort cố định. Test fingerprint ổn định khi mô tả hoặc thứ tự YAML thay đổi. Mapping cho từng rule phải dựa trên output thật của engine.
- [x] **T09 · Sau T08 · Chạy golden fixtures đầu tiên.** Có 12–15 cases gồm spec hợp lệ JSON/YAML, unchanged, thêm/xóa endpoint, required request field, request enum thu hẹp, response type/required field, docs-only, internal ref, malformed/unsupported và engine failure. Assert finding/rule/path khi có, thêm CLI process smoke.

**Checkpoint** chạy được validator và adapter từ CLI, tách rõ bất tương thích, spec lỗi và engine lỗi.

## Tuần 3 — Policy và GitLab MR

- [x] **T10 · Sau T08 · Áp policy v1.** Parse `.guardian.yml` với `version`, `fail_on`, `fail_on_unknown` và rule overrides. Test strict ERR, WARN/INFO, override, invalid schema và unknown/unmapped rule.
- [ ] **T11 · Sau T09–T10 · Xuất report và exit codes.** Hoàn thiện `guardian validate` và `guardian compare`, kiểm chứng exit codes `0/2/3/4/5` và report schema v1. JSON và Markdown chứa cùng verdict. Nêu rõ input lỗi hoặc engine diagnostic nếu chưa có findings. Nâng fixture lên khoảng 25.
- [ ] **T12 · Sau T11 · Đóng gói CLI.** Build image Linux có CLI và engine đã pin. Từ image chạy cặp PASS và FAIL. Lưu executable version, image digest và report.
- [ ] **T13 · Sau T12 · Chạy detached MR gate cùng project.** Dùng job `api_contract_guard`, lấy baseline và policy cùng target SHA, ghim image digest, lưu artifacts kể cả khi job FAIL. Dùng cặp sample trong repo demo. Lưu link MR compatible, MR breaking và kết quả thiếu baseline.

**Checkpoint** GitLab job trả kết quả đúng từ CLI và có artifacts khi Guardian API chưa triển khai.

## Tuần 4 — Độ tin cậy và trust boundary

- [ ] **T14 · Sau T13 · Kiểm chứng GitLab merge settings.** Với quyền Maintainer, kiểm tra protected target và `Pipelines must succeed`. Xác minh nguồn job được quản lý độc lập nếu môi trường hỗ trợ. Dùng MR test có policy hoặc config thay đổi để xác nhận gate bảo vệ được phần nào. Ghi GitLab tier, runner, merge settings và kết quả thực.
- [ ] **T15 · Sau T07 · Kiểm tra ranh giới input.** Thêm test offline cho refs bên ngoài, path ngoài workspace, symlink, input lớn/quá sâu, timeout và output vượt giới hạn. Xác nhận engine chỉ nhận file nội bộ đã qua validation và không cần mạng khi compare, trong runner phù hợp.
- [ ] **T16 · Sau T11 và T15 · Khóa regression suite.** Chốt ít nhất 30 golden cases, gồm giới hạn dialect runtime. Lặp cùng input 10 lần để so verdict/findings/fingerprints và hash nội dung ổn định. Đo branch coverage policy core, mục tiêu khoảng 90%. Viết `docs/policy-reference.md` và `docs/threat-model.md`.
- [ ] **T17 · Sau T14–T16 · Chuẩn bị release candidate P0.** Từ clean checkout chạy Quick Start và bộ fixture trên Linux, xác nhận exit/report và phiên bản của CLI/image/engine.

**Checkpoint** release candidate CLI tái lập được, rõ khả năng cưỡng chế merge trên GitLab và sẵn sàng dùng ở FlowForge.

## Tuần 5 — FlowForge thật

- [ ] **T18 · Sau T03 và T17 · Export OpenAPI từ source.** Trong nhánh tích hợp FlowForge riêng, tạo script export JSON ổn định và job kiểm tra file OpenAPI commit khớp output mới. Xác nhận hai lần export cùng SHA giống nhau và job phát hiện contract stale khi response model đổi.
- [ ] **T19 · Sau T18 · Bootstrap baseline.** Chạy một lần đưa spec và policy chuẩn vào protected target theo quy trình review. Giữ thiếu baseline là lỗi sau bootstrap. Ghi SHA chứa baseline, policy hash và digest của Guardian.
- [ ] **T20 · Sau T19 · Chạy flow MR lỗi rồi sửa.** Candidate sinh từ source thật. Trong nhánh demo, đổi `run_id` trong response model của `GET /api/v1/runs/{run_id}` từ UUID thành integer. Nếu dùng `RunAccepted` để thực hiện thay đổi, lưu ý các endpoint dùng model này và model kế thừa cũng sẽ đổi contract. Kiểm chứng CLI báo đúng field và job FAIL. Commit sửa về tương thích cho PASS. Với mirror GitHub → GitLab, đối chiếu full SHA của source với pipeline. Lưu link MR, pipeline và cả hai report.

**Checkpoint** có bằng chứng kỹ thuật cho CV từ FlowForge, gồm cùng workflow CI và report có provenance.

## Tuần 6 — Publish và persistence

- [ ] **T21 · Sau T11 và T20 · Ghi một report vào PostgreSQL.** Thêm ASP.NET Core API, EF Core/Npgsql và migration tối thiểu cho project, token, analysis, findings. Tạo `POST /api/v1/projects/{id}/ci-reports`, validate report v1, lưu decision/provenance do CLI cung cấp. Smoke từ DB sạch và từ report thật của T20.
- [ ] **T22 · Sau T21 · Scoped token và publish idempotent.** Lưu token hash, giới hạn project/scope. Định danh logical analysis theo project và CI/source identity với các hash, version liên quan. Giữ trường bất định ngoài phép so payload của retry. Test concurrent duplicate tạo đúng một record, payload xung đột trả 409 và token ngoài scope bị từ chối.
- [ ] **T23 · Sau T22 · Publish từ artifact.** Thêm `guardian publish` và job publish độc lập dùng credentials phù hợp ở luồng trusted. Test API unavailable, retry hợp lệ và publish report FAIL. Kiểm tra job gate vẫn giữ verdict và artifacts.

## Tuần 7 — Reviewer tra cứu report

- [ ] **T24 · Sau T21–T22 · History và detail API.** Có danh sách theo project, phân trang cơ bản, detail/finding query, xác thực và membership. Integration tests xác nhận đọc đúng report, chặn đọc chéo project và áp dụng index với truy vấn chính.
- [ ] **T25 · Sau T24 · React viewer.** Danh sách analyses → detail → lọc severity/rule → xem diff và provenance. Kiểm thử trên browser với report từ T20, gồm loading, empty và unauthorized.
- [ ] **T26 · Sau T22 và T25 · Audit và vận hành local.** Token revoke, audit event tối thiểu, health endpoint và Compose cho API/DB/web. Chạy từ clean DB, publish mẫu, truy cập viewer và kiểm thử token sau revoke. Chuyển exception approvals sang backlog sau release nếu lịch đang chật.

## Tuần 8–9 — Release và portfolio

- [ ] **T27 · Tuần 8 · Kiểm chứng release từ đầu.** Chạy unit/golden/CLI tests, CI smoke và dependency checks. Nếu có P1, chạy API integration, RBAC, idempotency và Compose từ clean checkout. Lưu bằng chứng trên đúng release SHA.
- [ ] **T28 · Tuần 8 · Đo benchmark và hoàn thiện docs.** Chạy theo cùng harness ở 10/100/500+ operations, có warmup và đủ sample cho median/p95. Ghi OS/CPU/RAM, SDK/engine, workload và lần chạy. Hoàn thiện Quick Start, support matrix, architecture và hướng khắc phục khi CI fail.
- [ ] **T29 · Tuần 9 · Đóng gói showcase.** Quay 3–5 phút demo từ local CLI đến FlowForge MR đỏ/xanh, thêm history nếu đã xong. Kiểm tra link MR/artifact, sample report công khai phù hợp, release notes và CV bullet. Tag `v1.0.0` khi các mốc đã công bố được kiểm chứng.

## Backlog sau release

- [ ] Exception workflow có scope theo project/fingerprint/policy version, expiry, tách người yêu cầu và người duyệt. Chỉ tích hợp exceptions vào gate khi có nguồn tin cậy và hành vi tái lập được khi offline.
- [ ] Mở rộng OpenAPI support matrix, multi-spec và GitLab MR comments khi có use case cùng fixtures.
- [ ] AWS lab khi hai project có release, demo Compose ổn định và có lý do triển khai.
