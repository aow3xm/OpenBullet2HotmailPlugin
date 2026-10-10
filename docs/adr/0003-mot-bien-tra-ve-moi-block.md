# Mỗi block chỉ trả về một biến; giá trị phức tạp đóng gói trong List/Dictionary

Status: proposed (quy ước thiết kế — ví dụ block dưới đây chưa hiện thực trong code)

Host map kiểu trả về của block thành đúng một biến LoliCode, và chỉ chấp nhận các kiểu số nguyên (`int`/`long` → Int), số thực (`double`/`float` → Float), bool, `string` (→ String), byte[], `List<string>`, `Dictionary<string,string>`; `void` và `Task` không map được kiểu nào (block không trả biến), kèm biến thể `Task<T>` theo từng kiểu trên (map `_variableTypes` của `RuriLib.Helpers.Blocks.DescriptorsRepository`; sinh câu gán trong `AutoBlockInstance.CreateExecutionStatements`) — đã kiểm chứng với mã nguồn host openbullet/OpenBullet2. Cú pháp nhiều output `=> VAR @a, @b` không tạo hai biến: nó tạo một biến tên `ab`.

Vì vậy: block danh sách sẽ trả `List<string>`, mỗi phần tử là một JSON row; block chi tiết và login sẽ trả `Dictionary<string,string>` với khoá là tên field; block tải tệp đính kèm sẽ trả `byte[]` (nội dung nhị phân của `fileAttachment`/`itemAttachment`); block xoá sẽ trả một `string`: trên đường có move (xoá mềm mặc định, và xoá hẳn với `Permanent = true` trên Rest) là id mới của thư sau khi move (lý do xem ADR-0004); trên đường xoá hẳn của Graph — `permanentDelete`, không move — là id đã dùng để gọi request (id cũ, không đổi). Config đọc bằng indexer C# mà host sinh ra: `<mails[0]>`, `<mails.Count>`, `<msg["bodyText"]>` (đã kiểm chứng bằng transpile + compile với chính host).

Bỏ phương án tách mỗi field thành một block riêng: số block sẽ nổ theo số field, và config phải gọi nhiều lần cho cùng một request. Giữ nguyên quy ước này khi thêm block mới.
