# Xoá thư mặc định là chuyển vào Deleted Items, không phải DELETE

Status: proposed (thiết kế — block `HotmailDeleteMessage` chưa hiện thực trong code)

Tài liệu Microsoft không ghi nhận rằng `DELETE /me/messages/{id}` đưa thư vào Deleted Items — Graph im lặng về điểm này, còn tài liệu REST v2.0 cảnh báo "Deleted contents might not be recoverable". Đo thực tế trên tài khoản consumer (2026-10-10, Outlook REST v2.0) khẳng định thêm: sau `DELETE` thư nháp biến mất khỏi Drafts (1 → 0), Deleted Items vẫn 0, `GET` theo id cũ trả 404, và thư không xuất hiện trong bất kỳ listing nào. Tức `DELETE` là xoá hẳn ở đây, còn `POST /me/messages/{id}/move` với `DestinationId = deleteditems` mới là thao tác đưa vào thùng rác (đã kiểm chứng: Deleted Items 0 → 1 và thư hiện trong listing của hộp đó).

Vì vậy `HotmailDeleteMessage` (tên method là ID block; tên hiển thị sẽ là "Delete Message") mặc định gọi move (khôi phục được), và chỉ khi `Permanent = true` mới đi tiếp bước xoá. Đường xoá hẳn là move rồi `DELETE` theo **id mới** do bước move trả về, bởi Microsoft đổi id khi thư đổi hộp; dùng lại id cũ sẽ nhận 404. Khi `api = Graph`, có đường xoá hẳn một request do Microsoft ghi nhận riêng: `POST /me/messages/{id}/permanentDelete` (tài liệu trình bày ở dạng `/users/{userId}/…`; thư vào purges, hỗ trợ tài khoản consumer); đường này không tồn tại trên REST v2.0.

Hệ quả: block xoá hẳn trên REST tốn hai request thay vì một, và mọi thao tác sau khi di chuyển phải dùng id mới.
