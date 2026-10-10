# OpenBullet2 Hotmail Plugin

Plugin OpenBullet2 với sáu block trong nhóm `Hotmail` của RuriLib: đổi token, kiểm tra đăng nhập, liệt kê thư, xem chi tiết thư, tải tệp đính kèm và xoá thư — đọc/ghi tương tác với hộp thư Hotmail/Outlook qua REST API hoặc Microsoft Graph.

## Yêu cầu

- .NET 10 SDK.
- OpenBullet2 2.0.1, bản build .NET 10 có `libraries/RuriLib.dll` trong thư mục cài đặt.
- Lệnh `zip` nếu muốn đóng gói để upload.

Phạm vi tương thích là bản host .NET 10 nêu trên; các bản khác chưa được kiểm chứng.

## Build

Chạy tại thư mục repository, thay `/path/to/openbullet2` bằng thư mục cài đặt host:

```bash
dotnet build PluginTemplate.csproj -c Release -p:Ob2Dir="/path/to/openbullet2"
```

Hoặc dùng biến môi trường:

```bash
export OB2_DIR="/path/to/openbullet2"
dotnet build PluginTemplate.csproj -c Release
```

`-p:Ob2Dir` được ưu tiên hơn `OB2_DIR`. Kết quả: `bin/Release/net10.0/PluginTemplate.dll`.

Dự án chỉ tham chiếu `libraries/RuriLib.dll` của host với `Private=false`. Chỉ phân phối `PluginTemplate.dll`, không chép hay đóng gói `RuriLib.dll` hoặc các DLL của host.

## Cài đặt

Chép `bin/Release/net10.0/PluginTemplate.dll` vào `<UserDataFolder>/Plugins`, với `UserDataFolder` là đường dẫn gốc dữ liệu đang cấu hình trong OpenBullet2 (`UserData/Plugins` chỉ là đường dẫn mặc định), rồi khởi động lại host. Khi thay DLL của plugin đã được nạp, cũng phải khởi động lại.

Có thể tạo ZIP để upload tại mục **Plugins** bằng một lệnh:

```bash
mkdir -p dist && zip -j dist/PluginTemplate.zip bin/Release/net10.0/PluginTemplate.dll
```

ZIP chứa `PluginTemplate.dll` ngay ở gốc archive, không cần manifest. Upload `dist/PluginTemplate.zip`; nếu thay plugin đang được nạp, khởi động lại host.

## Các block trong nhóm Hotmail

Cả sáu block nằm trong namespace `RuriLib.Blocks.Hotmail`, nhóm `Hotmail` khi chọn block. Mọi request của block (kể cả đổi token) đi qua proxy của bot theo pattern `data.UseProxy ? data.Proxy : null`; toàn bộ request mail API trong một lần gọi block dùng chung timeout 30 s, bước đổi token có timeout 30 s riêng. Response không phải 2xx làm block ném `HttpRequestException` nêu flavor/operation/status — host chuyển thành run status để config phân nhánh. Token cache nằm trong tiến trình, khoá theo (flavor, clientId, refreshToken); nhiều block trong cùng một run dùng chung access token, không đổi lại mỗi lượt.

### Dòng input

Tất cả các block đọc cùng một dòng input đúng **4 trường** không rỗng, phân cách bằng `:`:

```
email:password:refreshToken:clientId
```

Dòng này không được ghi ra log vì chứa mật khẩu và refresh token.

### Tham số `api`

Mọi block có tham số `api` chọn API flavor:

- `Rest` (mặc định): `outlook.office.com/api/v2.0`.
- `Graph`: `graph.microsoft.com/v1.0`.

So khớp không phân biệt hoa thường; giá trị khác ném lỗi. Không tự dò đường, không fallback giữa hai flavor.

### Tham số `message` (tham chiếu thư)

Blocks Get Message Details, Download Attachment và Delete Message nhận tham số `message`, nhận một trong hai dạng:

- ID thư thô.
- JSON row nguyên văn do List Messages trả về (parse `id` không phân biệt hoa thường; row của Rest có `Id`, của Graph có `id`).

### Tham số `folder`

List Messages có tham số `folder` (mặc định `Inbox`), nhận:

- ID folder thô (phân biệt hoa thường, nguyên văn).
- Tên folder quen thuộc dạng TitleCase: `Inbox`, `DeletedItems`, `Archive`, `Drafts`, `SentItems`, `JunkEmail`, `RecoverableItems*`.
- `all` — quy ước của plugin, ánh xạ sang `AllItems`: mọi thư trong toàn bộ hộp thư, không phải một folder.

### Các block

#### Get Token (`HotmailGetToken`)

Tham số: `api`. Trả về access token (string): đổi refresh token trong dòng input lấy access token rồi cache lại.

#### Check Login (`HotmailCheckLogin`)

Tham số: `api`. Trả về `Dictionary<string,string>` với các key đúng bằng các trường dòng input: `email`, `password`, `refreshToken`, `clientId`. Gọi đúng **một** request mail API thật để kiểm tra: Rest `GET /me`; Graph `GET /me/messages?$top=1` (scope token của Graph là `Mail.ReadWrite`, endpoint `/me` cần `User.Read` — đây là hành vi được ghi nhận).

#### List Messages (`HotmailListMessages`)

Tham số: `api`, `folder`. Trả về `List<string>`: mỗi thư là một JSON row thô để block sau đọc bất kỳ field nào. Config dùng `<mails[0]>`, `<mails.Count>`, indexer để trích field.

#### Get Message Details (`HotmailGetMessageDetails`)

Tham số: `api`, `message`. Trả về `Dictionary<string,string>`: các field top-level dạng chuỗi đúng casing của flavor (Rest PascalCase ví dụ `Subject`, Graph camelCase ví dụ `subject`); số/bool chuyển thành chuỗi; object/mảng/null bị bỏ qua. Ngoài ra có key `bodyText`: lấy `uniqueBody.content` của Graph khi có, nếu không thì `body.content`. Đọc qua indexer của host: `<msg["bodyText"]>`, `<msg["subject"]>`.

#### Download Attachment (`HotmailDownloadAttachment`)

Tham số: `api`, `message`, `attachment` (ID tệp đính kèm thô hoặc JSON row chứa `id`). Trả về nội dung tệp dạng `byte[]`. Đường dẫn tải theo loại tệp và flavor:

- `fileAttachment`: Graph ưu tiên giải mã field `contentBytes` base64 trong detail, chỉ tải nội dung thô qua `/{id}/$value` khi thiếu field này; Rest v2.0 đọc field `ContentBytes` trong detail (base64, trần khoảng 4 MB; tài liệu v2.0 không ghi nhận `/$value`).
- `itemAttachment`: MIME của thư/tệp đính kèm sự kiện kèm theo, tải qua `/$value` (Graph v1.0 có ghi nhận).
- `referenceAttachment`: chỉ là liên kết cloud, không tải được nội dung (Graph trả HTTP 405 cho `/$value`); block ném lỗi rõ ràng thay vì trả về dữ liệu rác.

#### Delete Message (`HotmailDeleteMessage`)

Tham số: `api`, `message`, `permanent` (bool, mặc định `false`). Mặc định (`permanent = false`): `POST /me/messages/{id}/move` với `DestinationId = DeletedItems` — xoá mềm, khôi phục được, **không** gửi request `DELETE`.

`permanent = true`:

- Rest: move trước rồi `DELETE /me/messages/{newId}` bằng **id mới** trả về từ move (id cũ bị 404; Microsoft đổi id khi thư đổi folder — ADR-0004); hai request.
- Graph: một request `POST /me/messages/{id}/permanentDelete` (thư rơi vào purges).

`DELETE` trực tiếp không bao giờ được dùng làm xoá mềm (đo ngày 2026-10-10: trên Rest v2.0 nó xoá vĩnh viễn). Giá trị trả về: id mới sau move trên mọi đường đi có move; đường Graph `permanentDelete` không move nên trả về id cũ dùng trong request.

## Dùng trong LoliCode

Ví dụ này nối cả sáu block: đăng nhập, lấy token, liệt kê Inbox, xem chi tiết thư đầu tiên, tải tệp đính kèm đầu tiên, rồi xoá thư đó.

```lolicode
BLOCK:HotmailCheckLogin
  api = "Rest"
  => VAR @account
ENDBLOCK

BLOCK:HotmailGetToken
  api = "Rest"
  => VAR @token
ENDBLOCK

BLOCK:HotmailListMessages
  api = "Rest"
  folder = "Inbox"
  => VAR @mails
ENDBLOCK

BLOCK:HotmailGetMessageDetails
  api = "Rest"
  message = "<mails[0]>"
  => VAR @msg
ENDBLOCK

BLOCK:HotmailDownloadAttachment
  api = "Rest"
  message = "<mails[0]>"
  attachment = "<attachmentId>"
  => VAR @attachmentBytes
ENDBLOCK

BLOCK:HotmailDeleteMessage
  api = "Rest"
  message = "<mails[0]>"
  permanent = False
  => VAR @deletedId
ENDBLOCK

LOG "Subject: <msg[\"Subject\"]>"
LOG "Body: <msg[\"bodyText\"]>"
LOG "Token: OK"
LOG "Mail count: <mails.Count>"
```

Đổi `api = "Graph"` ở các block nếu dùng Graph (lưu ý casing key của `msg` đổi theo flavor: `Subject` ở Rest, `subject` ở Graph). Dòng input của bot phải là `email:password:refreshToken:clientId`; `<attachmentId>` thay bằng ID tệp đính kèm thật hoặc JSON row chứa `id`.