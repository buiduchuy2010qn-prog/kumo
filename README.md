# Kumo — Tai thỏ nhỏ gọn cho Windows 10 & 11

**Trang chủ & Mô phỏng trực tiếp:** [https://buiduchuy2010qn-prog.github.io/kumo/](https://buiduchuy2010qn-prog.github.io/kumo/)  
**Tải bản cài đặt mới nhất (`KumoSetup-v*.exe`):** [GitHub Releases](https://github.com/buiduchuy2010qn-prog/kumo/releases/latest)

### Cài đặt / Cập nhật nhanh 1 dòng lệnh PowerShell
```powershell
irm https://buiduchuy2010qn-prog.github.io/kumo/install.ps1 | iex
```
*(Dự phòng trực tiếp qua GitHub Raw nếu nhà mạng chặn DNS `github.io`: `irm https://raw.githubusercontent.com/buiduchuy2010qn-prog/kumo/main/docs/install.ps1 | iex`)*

---

Kumo biến cạnh trên màn hình Windows 10 & Windows 11 (x64) thành một thanh tai thỏ đen nguyên bản sắc nét (`UpdateLayeredWindow` + `PerMonitorV2` DPI), đi kèm bảng quản lý nền sáng dịu mắt.

## Tính năng chính
- **Karaoke 60 FPS & Sóng nhạc WASAPI thực**: Đồng bộ lời bài hát từng chữ từ LRCLIB cho Spotify, YouTube, trình duyệt cả khi thu gọn lẫn mở rộng; bấm `🔊` chuyển nhanh **Loa ngoài ↔ Tai nghe**.
- **Bấm giờ mở Lịch Tháng & Thời tiết Open-Meteo**: Nhấp vào ngày giờ trên thanh tiêu đề để mở ngay **Lịch Tháng** gọn trong tai thỏ (cuộn chuột đổi tháng, chuột phải bật/tắt giây), kèm nhiệt độ thời gian thực.
- **Tự né Tab `4.5px` & Fullscreen chuẩn + Hé mép trên**: Tự thu mảnh khi cửa sổ chạm mép trên; tự ẩn 100% xuyên chuột khi xem YouTube `F`/`F11`, UWP, phim hoặc chơi game toàn màn hình nhưng vẫn hé mở khi đưa chuột sát mép trên `2px`.
- **HUD 1 hàng ngang & Nhắc sức khỏe thông minh (`Smart DND`)**: Tải tệp Chrome/IDM/Store (`MB/s`), Âm lượng, Độ sáng, phím tắt Tắt Mic toàn cục, Caps/Num Lock, nhắc uống nước và nghỉ mắt **20-20-20** đều nằm trên 1 trục ngang — tự hoãn nhắc khi đang họp Camera/Mic, Fullscreen hoặc tập trung Pomodoro.
- **Đa màn hình theo con trỏ chuột (`Follow Cursor`)**: Chọn màn hình cố định hoặc tự động đi theo màn hình đang thao tác với tỷ lệ DPI riêng từng màn hình.
- **Kệ tệp Kéo-Thả & Lịch sử Copy RAM**: Kéo tệp/ảnh trực tiếp từ trình duyệt vào tai thỏ; lưu 12 mục vừa copy chỉ trên RAM (tự bỏ qua trình quản lý mật khẩu).
- **Siêu nhẹ ~255 KB · Cài đè 1 chạm**: Thuần C# / Win32 không cần quyền Admin, không chạy nền trình duyệt; cài bản mới đè thẳng giữ nguyên 100% cài đặt.

## Giới thiệu & Nguồn cảm hứng (Credits)
- **Nguồn cảm hứng gốc — Nusa**: Kumo được lấy cảm hứng từ dự án **[Nusa — a living notch for Windows](https://github.com/rama2555/nusa)** của tác giả **[@rama2555](https://github.com/rama2555)** ([rama2555.github.io/nusa](https://rama2555.github.io/nusa/)).
- **Phát triển phiên bản Kumo**: **[@buiduchuy2010qn-prog](https://github.com/buiduchuy2010qn-prog)** — kho mã nguồn tại [github.com/buiduchuy2010qn-prog/kumo](https://github.com/buiduchuy2010qn-prog/kumo).
- **Dữ liệu mở**: Lời bài hát đồng bộ từ **[LRCLIB](https://lrclib.net)** · Thời tiết từ **[Open-Meteo](https://open-meteo.com)**.
