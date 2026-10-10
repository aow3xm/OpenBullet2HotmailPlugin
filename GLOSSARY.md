# OpenBullet2 Hotmail Plugin

Plugin OpenBullet2 đọc thư, tải tệp đính kèm và xoá thư trên tài khoản Microsoft consumer bằng refresh token có sẵn trong dòng input. Bộ block gồm sáu block trong namespace `RuriLib.Blocks.Hotmail`: `HotmailGetToken` ("Get Token"), `HotmailCheckLogin` ("Check Login"), `HotmailListMessages` ("List Messages"), `HotmailGetMessageDetails` ("Get Message Details"), `HotmailDownloadAttachment` ("Download Attachment") và `HotmailDeleteMessage` ("Delete Message") — tất cả đã hiện thực; các thuật ngữ dưới đây định nghĩa theo hành vi của chúng.

## Language

**Tài khoản Hotmail**:
Tài khoản Microsoft cá nhân (hotmail.com, outlook.com, live.com, msn.com), xác thực qua tenant `consumers`.
_Avoid_: tài khoản Outlook (lẫn với tài khoản Microsoft 365 của tổ chức)

**Dòng input** (Input line):
Dòng dữ liệu bot đang xử lý, gồm đúng 4 field theo thứ tự: email, email password, refresh token, client id.
_Avoid_: dòng dữ liệu, credentials line

**Refresh token**:
Grant dài hạn do Microsoft cấp cho cặp (user, client id), dùng để lấy access token mới mà không cần đăng nhập lại.
_Avoid_: token, mã làm mới

**Access token**:
Bearer token ngắn hạn cho đúng một resource (Graph hoặc outlook.office.com), hết hạn theo `expires_in`.
_Avoid_: auth token, session

**API flavor**:
Resource mà plugin gọi: `Rest` (outlook.office.com/api/v2.0) hoặc `Graph` (graph.microsoft.com/v1.0). Một lần chạy chỉ dùng một flavor, chọn bằng tham số `api`.
_Avoid_: backend, chế độ API

**Hộp thư** (Folder):
Ngăn chứa thư trong mailbox, định danh bằng folder id thô hoặc well-known folder name. Tài liệu REST v2.0 chỉ ghi nhận 4 well-known folder name (`Inbox`, `Drafts`, `SentItems`, `DeletedItems`); `junkemail` chỉ có trong tài liệu Graph v1.0. Dùng hoa/thường tùy ý (URL lẫn `DestinationId`) là hành vi đo được, tài liệu không ghi nhận. Định danh bằng DisplayName hoạt động thực tế nhưng Microsoft không ghi nhận.
_Avoid_: mailbox (mailbox là cả tài khoản), thư mục

**Well-known folder name**:
Tên hộp thư không phụ thuộc ngôn ngữ do Microsoft quy định, dùng trực tiếp trong URL với casing TitleCase như tài liệu (`Inbox`, `DeletedItems`, `AllItems`); `all` là quy ước riêng của plugin, nghĩa là mọi hộp thư (giá trị `AllItems` của tài liệu REST v2.0 — mọi thư trong toàn bộ mailbox — không phải một hộp thư).
_Avoid_: tên chuẩn, alias

**Thư** (Message):
Một email trong hộp thư, định danh bằng `id` (chuỗi opaque, đổi sau khi thư bị di chuyển).
_Avoid_: mail, email item

**Tham chiếu thư** (Message ref):
Chuỗi nhận bởi các block thao tác thư: hoặc `id` thô, hoặc nguyên JSON row do block liệt kê thư (`HotmailListMessages`, hiển thị "List Messages") trả về.
_Avoid_: message param, id

**Tệp đính kèm** (Attachment):
Tệp gắn với thư, thuộc một trong ba loại: `fileAttachment` (bytes thô — Graph ưu tiên giải mã `contentBytes` base64 trong detail, chỉ tải qua `/$value` khi thiếu field này, Rest v2.0 qua `ContentBytes` base64 vì tài liệu v2.0 không ghi nhận `/$value`), `itemAttachment` (MIME của thư/contact/event được đính kèm — tải qua `/$value`, được tài liệu ghi nhận trên Graph v1.0), `referenceAttachment` (link cloud, không tải được nội dung — Graph trả HTTP 405 cho `/$value`).
_Avoid_: attachment file, tệp gửi kèm

**Xoá mềm** (Trash):
Chuyển thư vào Deleted Items, còn khôi phục được; là hành vi mặc định của block xoá thư (`HotmailDeleteMessage`, hiển thị "Delete Message"), vì lần đo 2026-10-10 trên REST v2.0 cho thấy `DELETE` xoá hẳn thư nháp, không rơi vào Deleted Items.
_Avoid_: xoá tạm, delete

**Xoá hẳn** (Permanent delete):
Xoá thư không khôi phục được. Hai đường: `DELETE /me/messages/{id}` trực tiếp trên REST v2.0 (lần đo 2026-10-10 cho thấy xoá hẳn, chưa có tài liệu xác nhận), hoặc trên Graph dùng `POST /me/messages/{id}/permanentDelete` (Microsoft ghi nhận riêng, thư vào purges); trên Graph tài liệu không nói gì về hành vi của `DELETE`. Ngoài ra block xoá thư có cờ `Permanent = true`: trên Rest — move vào Deleted Items rồi `DELETE` theo id mới (hai request); trên Graph — `POST /me/messages/{id}/permanentDelete` (một request, thư vào purges).
_Avoid_: xoá vĩnh viễn, hard delete

**Token cache**:
Bản đồ trong tiến trình host, khoá theo `(flavor, client id, refresh token)`, giữ access token tới sát hạn để nhiều block trong cùng một lần chạy không phải đổi token lại.
_Avoid_: token store, session cache

**Bot proxy**:
Proxy mà config gán cho bot (`data.Proxy` + `data.UseProxy`); host không tự chặn `HttpClient` tùy ý, nên plugin gọi HTTP qua RuriLib với mẫu `data.UseProxy ? data.Proxy : null` của host — khi đó mọi request, kể cả đổi token, đều đi qua proxy khi bot có proxy.
_Avoid_: proxy của plugin, network mode

**Quy ước host** (Host conventions):
Các ràng buộc mã plugin phải tuân theo để host nạp được: block đặt trong namespace `RuriLib.Blocks.<tên plugin>`, tên method là ID block (trừ khi `[Block]` đặt `id` riêng), `name` là tên hiển thị, `[BlockCategory]` nhãn/mô tả cập nhật cho plugin, và tham chiếu duy nhất `RuriLib.dll` với `<Private>false</Private>`.
_Avoid_: host rules, quy tắc host
