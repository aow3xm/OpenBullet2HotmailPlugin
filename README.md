# OpenBullet2 Hotmail Plugin

Plugin OpenBullet2 với block `Get Token` trong nhóm `Hotmail`: đổi refresh token trong dòng input (`email:password:refreshToken:clientId`) lấy access token. Các block đọc/xoá thư chưa hiện thực.

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

## Dùng trong LoliCode

```lolicode
BLOCK:HotmailGetToken
  api = "Rest"
  => VAR @accessToken
ENDBLOCK
```

Biến `accessToken` nhận access token đổi từ refresh token trong dòng input `email:password:refreshToken:clientId`. Tham số `api` chọn API flavor (`Rest` mặc định hoặc `Graph`).