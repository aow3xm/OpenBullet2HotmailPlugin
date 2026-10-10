# OpenBullet2 Hotmail Plugin

Plugin OpenBullet2 (dự kiến) đọc thư, tải tệp đính kèm và xoá thư trên tài khoản Microsoft consumer bằng refresh token có sẵn trong dòng input. Hiện repo mới là template plugin (block `Greeting`) — các thuật ngữ dưới đây định nghĩa thiết kế cho các block sẽ hiện thực, chưa có trong code.

## Language

**Tài khoản Hotmail**:
Tài khoản Microsoft cá nhân (hotmail.com, outlook.com, live.com, msn.com), xác thực qua tenant `consumers`.
_Avoid_: tài khoản Outlook (lẫn với tài khoản Microsoft 365 của tổ chức)

**Dòng input** (Input line):
Dòng dữ liệu bot đang xử lý, gồm đúng 4 field: email, email password, refresh token, client id. (Thứ tự field sẽ do block parse input quy định khi hiện thực.)
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
Tên hộp thư không phụ thuộc ngôn ngữ do Microsoft quy định, dùng trực tiếp trong URL; `all` là quy ước riêng của plugin, nghĩa là mọi hộp thư (giá trị `AllItems` của tài liệu REST v2.0 — mọi thư trong toàn bộ mailbox — không phải một hộp thư).
_Avoid_: tên chuẩn, alias

**Thư** (Message):
Một email trong hộp thư, định danh bằng `id` (chuỗi opaque, đổi sau khi thư bị di chuyển).
_Avoid_: mail, email item

**Tham chiếu thư** (Message ref):
Chuỗi nhận bởi các block thao tác thư: hoặc `id` thô, hoặc nguyên JSON row do block liệt kê thư (tên dự kiến `HotmailListMessages`, hiển thị "List Messages" — chưa hiện thực) trả về.
_Avoid_: message param, id

**Tệp đính kèm** (Attachment):
Tệp gắn với thư, thuộc một trong ba loại: `fileAttachment` (bytes thô), `itemAttachment` (MIME của thư/contact/event được đính kèm), `referenceAttachment` (link cloud, không tải được qua `$value`).
_Avoid_: attachment file, tệp gửi kèm

**Xoá mềm** (Trash):
Chuyển thư vào Deleted Items, còn khôi phục được; là hành vi mặc định của block xoá thư (tên dự kiến `HotmailDeleteMessage`, hiển thị "Delete Message" — chưa hiện thực), vì lần đo 2026-10-10 trên REST v2.0 cho thấy `DELETE` xoá hẳn thư nháp, không rơi vào Deleted Items.
_Avoid_: xoá tạm, delete

**Xoá hẳn** (Permanent delete):
Xoá thư không khôi phục được. Hai đường: `DELETE /me/messages/{id}` trực tiếp trên REST v2.0 (lần đo 2026-10-10 cho thấy xoá hẳn, chưa có tài liệu xác nhận), hoặc trên Graph dùng `POST /me/messages/{id}/permanentDelete` (Microsoft ghi nhận riêng, thư vào purges); trên Graph tài liệu không nói gì về hành vi của `DELETE`. Ngoài ra block xoá thư có cờ `Permanent = true` để chuyển thư vào Deleted Items rồi xoá luôn khỏi đó.
_Avoid_: xoá vĩnh viễn, hard delete

**Token cache** (— chưa hiện thực):
Bản đồ trong tiến trình host, khoá theo `(flavor, client id, refresh token)`, giữ access token tới sát hạn để nhiều block trong cùng một lần chạy không phải đổi token lại.
_Avoid_: token store, session cache

**Bot proxy** (— chưa hiện thực):
Proxy mà config gán cho bot (`data.Proxy` + `data.UseProxy`); host không tự chặn `HttpClient` tùy ý, nên plugin sẽ gọi HTTP qua RuriLib với mẫu `data.UseProxy ? data.Proxy : null` của host — khi đó mọi request, kể cả đổi token, đều đi qua proxy khi bot có proxy.
_Avoid_: proxy của plugin, network mode
