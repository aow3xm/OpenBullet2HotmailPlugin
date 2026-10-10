# Chọn API flavor bằng tham số, không tự dò và không fallback

Status: accepted (hiện thực: tham số `api` Rest/Graph trên mọi block, giá trị khác ném lỗi — không tự dò, không fallback)

Refresh token chỉ lấy được access token cho resource mà cặp (user, client id) đã được cấp quyền. Đo thực tế ngày 2026-10-10 với refresh token Thunderbird: `https://outlook.office.com/Mail.ReadWrite` trả về access token dùng được với REST v2.0, còn `https://graph.microsoft.com/Mail.Read` bị từ chối (AADSTS70000 — `invalid_grant`; trên tài khoản MSA mã này cũng hiện khi scope chưa được consent — đo thực tế, tài liệu Microsoft gắn trường hợp chưa consent với AADSTS65001); ngược lại, Thunderbird công bố hỗ trợ Graph từ 09/2026 nhưng chỉ cho Microsoft 365 — với tài khoản consumer nó vẫn xin scope outlook.office.com (khớp với đo phía trên), nên không có căn cứ cho rằng refresh token consumer mới chỉ có Graph. Vì vậy plugin nhận tham số `api` (`Rest` hoặc `Graph`): mỗi lần chạy chỉ dùng đúng một flavor: không tự dò, không fallback sang flavor còn lại.

Đã cân nhắc và bỏ: thang tự dò Graph trước rồi REST sau. Nó làm hành vi mỗi lần chạy phụ thuộc consent của token, khiến lỗi khó đoán và biến một lần chạy thành hai lần gọi token endpoint. Đổi flavor khi cần chỉ là sửa một tham số trong config.

Hệ quả: người viết config phải chọn đúng flavor cho loại refresh token mình có; thông báo lỗi khi access token thiếu scope phải nêu rõ flavor và scope đã yêu cầu.
