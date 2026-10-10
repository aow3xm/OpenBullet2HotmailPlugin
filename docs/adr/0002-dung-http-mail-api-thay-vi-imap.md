# Dùng HTTP mail API (Graph hoặc Outlook REST v2.0) thay vì IMAP

Status: proposed (thiết kế — chưa hiện thực trong code)

Ràng buộc đo được, không phải sở thích:

- Host có sẵn bộ block IMAP của RuriLib nhưng `ImapLogin` (lớp `Methods`, namespace `RuriLib.Blocks.Requests.Imap`, đã kiểm chứng với mã nguồn host openbullet/OpenBullet2) gọi `AuthenticationMechanisms.Remove("XOAUTH2")`, tức chỉ đăng nhập bằng password, mà Microsoft đã tắt basic auth cho tài khoản consumer.
- IMAP trên Outlook.com mặc định bị tắt và chỉ bật được bằng thao tác trong phần cài đặt tài khoản, plugin chạy headless không làm được.
- Outlook REST v2.0 vẫn phục vụ tài khoản consumer (kiểm chứng live 2026-10-10: `/me`, `/me/mailfolders`, `/me/messages`, chi tiết thư, `$value`, thao tác xoá đều trả 200/201/204) dù Microsoft đã công bố khai tử REST v2.0 ngày 17/11/2020 và ghi nhận endpoint sẽ bị tắt hoàn toàn vào tháng 3/2024; Graph là đường chính thức, và một phần refresh token đã có sẵn consent Graph.
- Cả hai đường HTTP đều đi qua proxy HTTP/SOCKS mà host đã hỗ trợ sẵn, không cần thêm tầng tunnel TCP cho cổng 993.

Hệ quả: plugin không tham chiếu MailKit/MimeKit, không có phụ thuộc ngoài RuriLib.dll (đã đúng ở template hiện tại — `PluginTemplate.csproj` chỉ tham chiếu `RuriLib.dll` với `<Private>false</Private>`); toàn bộ HTTP của plugin — kể cả đổi token — gọi qua block RuriLib với mẫu `data.UseProxy ? data.Proxy : null`, không tạo `HttpClient` riêng (xem glossary "bot proxy"); nếu Outlook REST v2.0 bị tắt hoàn toàn thì đổi `api = Graph` (xem ADR-0001) — việc đổi chỉ khả thi nếu cặp (user, client id) đã được consent scope Graph, tức refresh token đó còn lấy được access token cho graph.microsoft.com (xem "một phần refresh token đã có sẵn consent Graph" ở trên; refresh token không bị gán cho một resource, nhưng consent mới là điều kiện để xin token cho resource đó).
