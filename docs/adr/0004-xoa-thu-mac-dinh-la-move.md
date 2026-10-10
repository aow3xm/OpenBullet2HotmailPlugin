# Xoá thư mặc định là chuyển vào Deleted Items, không phải DELETE

Status: accepted (hiện thực: `HotmailDeleteMessage` — move vào Deleted Items mặc định, `permanent` theo đúng hai đường flavor như dưới, phủ bởi test)

Tài liệu Microsoft không ghi nhận rằng `DELETE /me/messages/{id}` đưa thư vào Deleted Items — Graph im lặng về điểm này, còn tài liệu REST v2.0 cảnh báo "Deleted contents might not be recoverable". Đo thực tế trên tài khoản consumer (2026-10-10, Outlook REST v2.0) khẳng định thêm: sau `DELETE` thư nháp biến mất khỏi Drafts (1 → 0), Deleted Items vẫn 0, `GET` theo id cũ trả 404, và thư không xuất hiện trong bất kỳ listing nào. Tức `DELETE` là xoá hẳn ở đây, còn `POST /me/messages/{id}/move` với `DestinationId = DeletedItems` mới là thao tác đưa vào thùng rác (đã kiểm chứng: Deleted Items 0 → 1 và thư hiện trong listing của hộp đó).

Vì vậy `HotmailDeleteMessage` (tên method là ID block; tên hiển thị sẽ là "Delete Message") mặc định gọi move (khôi phục được), và chỉ khi `Permanent = true` mới đi tiếp bước xoá. Đường xoá hẳn là move rồi `DELETE` theo **id mới** do bước move trả về, bởi Microsoft đổi id khi thư đổi hộp — tài liệu Graph v1.0 ghi nhận chính thức rằng id thư có thể đổi sau copy/move; trên REST v2.0 đây là đo thực tế 2026-10-10, không có tài liệu; dùng lại id cũ sẽ nhận 404. Khi `api = Graph`, có đường xoá hẳn một request do Microsoft ghi nhận riêng: `POST /me/messages/{id}/permanentDelete` (tài liệu trình bày ở dạng `/users/{userId}/…`; thư vào purges, hỗ trợ tài khoản consumer); đường này không tồn tại trên REST v2.0. `DestinationId` dùng giá trị well-known `DeletedItems` (casing TitleCase theo tài liệu).

Hệ quả: block xoá hẳn trên REST tốn hai request thay vì một, và mọi thao tác sau khi di chuyển phải dùng id mới.
