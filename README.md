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
| `tools/OpenDelulu.ShaderCompiler` | Biên dịch shader Vulkan (GLSL → SPIR-V) | net10.0 |

## Đồ họa

Trình xem 3D có hai backend, chọn ở góc trên bên phải ("Đồ họa: …") hoặc bằng `--backend auto|gl|vulkan`:

| Backend | Nền tảng | Cách hiển thị |
|---|---|---|
| **Vulkan** | Windows, Linux (Android: thử, tự lùi về OpenGL nếu compositor không nhận ảnh Vulkan) | Render bằng device Vulkan riêng, trao ảnh cho compositor của Avalonia qua `ICompositionGpuInterop` (Windows: texture D3D11 chia sẻ + keyed mutex; Linux: file descriptor + semaphore), không chép qua CPU |
| **OpenGL ES** | Mọi nền tảng (Windows chạy qua ANGLE → Direct3D 11) | `OpenGlControlBase` |

- **Tự động**: giao diện chạy ANGLE (Direct3D 11) như mặc định của Avalonia; khung 3D thử Vulkan trước
  (trao ảnh qua texture D3D11 chia sẻ), lỗi thì lùi về OpenGL ES.
- **Vulkan** ("Vulkan thuần"): từ lần mở app sau, cả giao diện Avalonia cũng chạy trên Vulkan
  (`Win32RenderingMode.Vulkan` / `X11RenderingMode.Vulkan`), khung 3D trao ảnh Vulkan trực tiếp, không qua D3D11.
  OpenGL không dùng được trong chế độ này.
- **OpenGL ES**: chỉ dùng `OpenGlControlBase`.

Backend nào không khởi tạo được trong 3 giây thì khung xem tự chuyển sang backend còn lại và ghi lý do.
Góc trái khung xem ghi backend, GPU và cách trao ảnh đang dùng (`D3D11 shared texture` hoặc `Vulkan NT handle`).
Thử nghiệm: `--backend auto|gl|vulkan`, `--compositor vulkan|default` (không lưu vào cài đặt).
Skinning hiện chạy trên CPU (dùng chung cho cả hai backend).
Sửa shader Vulkan trong `OpenDelulu/Rendering/Vulkan/Shaders/*.vert|frag` rồi chạy:

```
dotnet run --project tools/OpenDelulu.ShaderCompiler -- OpenDelulu/Rendering/Vulkan/Shaders
```

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
- [x] Animation `.xa`: khung xương (clip, tự chuyển clip, tốc độ), biểu cảm mặt bằng morph, chớp mắt; skinning trên CPU
- [x] Backend Vulkan (Silk.NET) bên cạnh OpenGL ES, tự lùi về OpenGL khi cần
- [x] Âm thanh: bộ trộn phần mềm (wav/ogg, resample, kênh nhạc/môi trường/hiệu ứng/giọng), xuất qua waveOut (Windows),
      PulseAudio/PipeWire (Linux), AudioTrack (Android); giọng nói kèm khẩu hình (`.vss` + âm lượng)
- [x] Đọc `.vss` (khẩu hình), `.xl` (đèn), `.kys` (tóc), `.eyes`/`.neck`, `.tty`, `.ptcl`/`.glare`, `.xm`, `.sav`, `.sdt` (kịch bản) và các bảng gameplay (100% file)

## Lộ trình

1. **Đồ họa**: skinning trên GPU, Vulkan trên Android (compositor Vulkan), bóng đổ/đổ sáng gần với game gốc.
2. **Ghép nhân vật (tiếp)**: phụ kiện (`lc*`), khăn pareo, vệt rám nắng, mắt/biểu cảm.
3. ~~**Âm thanh**~~: đã có (xem bên trên); còn bảng nhạc nền theo cảnh, âm lượng trong cài đặt.
4. **Cảnh và kịch bản**: trình chạy `.sdt` (thoại, lựa chọn, Goto/SetVar/Switch), vòng lặp game, camera, UI trong game.
5. **Lưu game**: đã đọc được cấu trúc `data/save/*.sav` (`delulu saves`); còn ý nghĩa từng trường và ghi lại.
6. **Video**: các file `.mpg` (MPEG-1), dùng thư viện giải mã đa nền tảng.

Chi tiết định dạng: [docs/formats.md](docs/formats.md).
