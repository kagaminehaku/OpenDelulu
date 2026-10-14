# Open Delulu

Viết lại trình chạy cho **Sexy Beach 3** (Illusion, 2006) bằng C# + Avalonia UI.
Dự án đọc trực tiếp dữ liệu từ bản game người dùng đã cài, **không cần file exe gốc** và
**không chứa hay phát tán tài nguyên của game**.

## Cấu trúc solution (`OpenDelulu.slnx`, mở bằng Visual Studio 2026)

| Project | Vai trò | Nền tảng |
|---|---|---|
| `OpenDelulu.Formats` | Đọc định dạng: `.pp`, `.lst`, `.xx`, ảnh `.tga/.bmp/.ema` | net10.0, thuần C# |
| `OpenDelulu.Engine` | Phát hiện bản cài, hệ thống file ảo, dựng model để render | net10.0, thuần C# |
| `OpenDelulu` | Giao diện Avalonia dùng chung + trình xem 3D OpenGL ES | net10.0 |
| `OpenDelulu.Desktop` | Bản Windows / Linux / macOS | net10.0 |
| `OpenDelulu.Android` | Bản Android | net10.0-android |
| `OpenDelulu.iOS` | Bản iOS (build cần máy Mac) | net10.0-ios |
| `OpenDelulu.Cli` | Công cụ dòng lệnh `delulu` để kiểm tra / giải nén | net10.0 |

Toàn bộ logic nằm trong các thư viện không phụ thuộc nền tảng, nên mang lên điện thoại
chỉ cần một project "đầu vào" mỏng cho từng nền tảng.

## Chạy

```
dotnet run --project OpenDelulu.Desktop
dotnet run --project OpenDelulu.Desktop -- --game "D:\Sexy Beach 3" --open sb3_0400.pp mp01_01_00_00_00.xx
dotnet run --project OpenDelulu.Desktop -- --game "D:\Sexy Beach 3" --character 3 --swimsuit 2
dotnet run --project OpenDelulu.Cli -- verify "D:\Sexy Beach 3"
```

Thư mục game là thư mục chứa `SexyBeach3` (và tùy chọn `SexyBeach3Plus`, `SexyBeach3PlusG`).

**Android:** chép thư mục `Sexy Beach 3` vào `Android/data/com.opendelulu.app/files/` (qua USB),
rồi bấm *Mở*. Thư mục này không cần cấp quyền truy cập bộ nhớ.

**iOS:** chép thư mục `Sexy Beach 3` vào Open Delulu bằng ứng dụng Tệp (Files) hoặc Finder.

## Đã làm được (mốc 1)

- [x] Đọc mọi archive `.pp` của cả 3 bản (52 archive, 18.412 file)
- [x] Giải mã bảng `.lst` (tìm ra khóa XOR; 278/278 file)
- [x] Đọc model `.xx`: cây frame, mesh, bone, material, texture nhúng (393/393 file)
- [x] Giải mã ảnh `.tga`, `.bmp`, `.ema`
- [x] Trình duyệt dữ liệu + xem trước bảng, ảnh, model 3D (OpenGL ES 2/3 và GL 3.3, xoay/zoom bằng chuột hoặc cảm ứng)
- [x] Build được cho Desktop, Android, iOS
- [x] Ghép nhân vật từ bảng `.lst`: 8 nhân vật, 5 kiểu tóc, 168 bộ đồ bơi (tab **Nhân vật**)

## Lộ trình

1. **Animation `.xa`**: keyframe và morph (biểu cảm khuôn mặt), skinning trên GPU.
2. **Ghép nhân vật (tiếp)**: phụ kiện (`lc*`), khăn pareo, vệt rám nắng, mắt/biểu cảm.
3. **Âm thanh**: `.ogg`/`.wav` (giọng nói, nhạc nền), lip-sync (`.vss`?).
4. **Cảnh và kịch bản**: phân tích `.sdt` và các bảng `lx/ly/lz`; vòng lặp game, camera, UI trong game.
5. **Lưu game**: định dạng `data/save/*.sav`.
6. **Video**: các file `.mpg` (MPEG-1), dùng thư viện giải mã đa nền tảng.

Chi tiết định dạng: [docs/formats.md](docs/formats.md).
