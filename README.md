<div align="center">
  <img src="assets/logo.png" width="128" alt="Kumo Logo" />
  <h1>☁️ Kumo</h1>
  <p><b>Tai thỏ sống động thông minh (Dynamic Island) cho Windows 10 & 11</b><br/><i>Official Native Living Notch & Productivity Suite for Windows</i></p>
  <p>
    <a href="https://github.com/buiduchuy2010qn-prog/kumo/releases/latest/download/KumoSetup.exe">
      <img src="https://img.shields.io/github/v/release/buiduchuy2010qn-prog/kumo?style=for-the-badge&logo=windows&label=T%E1%BA%A3i_KumoSetup.exe&color=0ea5e9" alt="Download KumoSetup.exe" />
    </a>
    <a href="https://github.com/buiduchuy2010qn-prog/kumo/actions/workflows/build.yml">
      <img src="https://img.shields.io/github/actions/workflow/status/buiduchuy2010qn-prog/kumo/build.yml?branch=main&style=for-the-badge&logo=githubactions&label=Cloud_Build" alt="GitHub Actions Cloud Build" />
    </a>
    <img src="https://img.shields.io/badge/Windows_10_%2F_11-64--bit_Native-6366f1?style=for-the-badge" alt="Windows 10/11 x64" />
  </p>
</div>

---

## 📖 Giới thiệu (Overview)

**Kumo** biến cạnh trên màn hình máy tính Windows thành một **chiếc tai thỏ sống động (Living Notch / Dynamic Island)** mượt mà, siêu nhẹ (`~180 KB`, `~0% CPU` khi nghỉ, không dùng Electron/WebView nặng nề). Toàn bộ bộ cài đặt `KumoSetup.exe` được **biên dịch và đóng gói tự động 100% trên đám mây qua GitHub Actions**.

### ✨ Tính năng chính
- 🎵 **Nhạc & Ảnh bìa thật (WinRT SMTC + LRCLIB)**: Tự động đọc trạng thái phát nhạc thực tế, ảnh bìa Album / Thumbnail YouTube từ Spotify, Chrome, Edge, Firefox, Cốc Cốc... kèm quầng sáng màu nền chuyển sắc, thanh tua nhạc trực tiếp và lời bài hát đồng bộ từng câu.
- ☁️ **Linh vật Kumo dễ thương**: Đôi mắt Kumo tự nổi nhẹ nhàng khi bạn rời máy, ngủ gật sau 20 giây và vẫy tay chào mừng khi bạn quay trở lại.
- ➖ **Tự thu mảnh thông minh (Smart Auto-Minimal)**: Tự động thu gọn về vạch mảnh `44×4 dp` khi mở nhiều tab trình duyệt chạm giữa màn hình để không bao giờ che nút đóng tab.
- ⏱ **Đa nhiệm 6 trang tương tác ngay trên tai thỏ**:
  1. **Music & Synced Lyrics**: Tua giây trực tiếp trên thanh tiến trình, nhấp câu hát để nhảy đoạn, nhấp bìa nhạc để mở cửa sổ trình phát.
  2. **Pomodoro Timer**: Chỉnh nhanh thời lượng `− / +`, vòng đếm ngược trực quan, chuyển chế độ `Focus / Short / Long`.
  3. **Water Reminder**: Công tắc nhắc nước, cộng/trừ nhanh lượng nước, chỉnh mục tiêu & cỡ cốc ngay trên tai thỏ kèm biểu đồ cột theo giờ.
  4. **File Shelf**: Kệ lưu trữ tệp tạm thời kéo-thả 2 chiều, nhấp để mở tệp hoặc kéo sang ứng dụng khác.
  5. **TodoList**: Danh sách việc cần làm hỗ trợ cuộn con lăn chuột và thêm nhanh ngay dưới tai thỏ.
  6. **System Monitor**: Theo dõi thời gian thực `CPU`, `RAM`, `SSD`, `Wi-Fi` và `GPU 0` (nhấp từng mục để xem chi tiết).
- ⚙️ **Bảng Quản Lý Cài Đặt & Trình Cài Đặt 4 Bước (`KumoSetup.exe`)**: Song ngữ **Tiếng Việt / English**, tích hợp Start Menu, Windows Search, khởi chạy cùng Windows và gỡ cài đặt sạch sẽ qua `Uninstall.exe`.

---

## 📸 Hình ảnh giao diện (Screenshots)

<div align="center">
  <img src="assets/pill_playing.png" alt="Kumo Collapsed Pill" />
  <br/><br/>
  <img src="assets/music_playing.png" width="48%" alt="Music & Album Art" />
  <img src="assets/pomodoro.png" width="48%" alt="Pomodoro Timer" />
  <br/>
  <img src="assets/water.png" width="48%" alt="Water Reminder" />
  <img src="assets/perf.png" width="48%" alt="System Monitor" />
  <br/>
  <img src="assets/todo.png" width="48%" alt="TodoList" />
  <br/><br/>
  <img src="assets/setup_welcome.png" width="48%" alt="Kumo Official Setup Wizard" />
  <img src="assets/settings.png" width="48%" alt="Kumo Settings Manager" />
</div>

---

## 🚀 Tải về & Cài đặt (Installation)

1. Nhấn tải **[KumoSetup.exe (Bản chính thức mới nhất)](https://github.com/buiduchuy2010qn-prog/kumo/releases/latest/download/KumoSetup.exe)**.
2. Mở `KumoSetup.exe` → Chọn **Cài đặt chính thức (Install)** (không yêu cầu quyền Admin).
3. Tìm và mở **Kumo** hoặc **Kumo Settings** bất kỳ lúc nào từ **Start Menu** / **Windows Search**.

---

## ☁️ Biên dịch tự động hoàn toàn trên GitHub Actions (Cloud CI/CD)

Dự án được cấu hình sẵn workflow [`.github/workflows/build.yml`](.github/workflows/build.yml):
- Mỗi khi bạn sửa code trực tiếp trên web GitHub (`main`), máy chủ `windows-latest` của GitHub sẽ tự động tạo icon, biên dịch `Kumo.exe` + `KumoSetup.exe` và kiểm thử giao diện.
- Vào tab **Actions → Build & Release Kumo Setup → Run workflow** (nhập tag ví dụ `v1.0.0` hoặc `v1.0.1`) để GitHub tự động build và đính kèm `KumoSetup.exe` + `SHA256SUMS.txt` thẳng vào mục **Releases** mà không cần build trên máy cá nhân.

---

## 🔄 Phiên bản động & Cài đè trực tiếp (Dynamic Versioning & In-Place Overwrite)

- **Phiên bản không cố định (Rolling / Custom Versioning)**:
  - Mỗi lần cập nhật lên nhánh `main`, GitHub Actions tự động tăng phiên bản động `v1.1.<số_build>` (ví dụ `v1.1.2`, `v1.1.3`...) và phát hành ngay lên **Releases (Latest)**.
  - Bạn cũng có thể vào **Actions → Build & Release Kumo Setup → Run workflow** và nhập **bất kỳ số phiên bản tuỳ ý** nào (ví dụ `1.2.5`, `2.0.0-beta`, `2026.11`) hoặc đẩy Git Tag `v*` để hệ thống tự đóng dấu phiên bản đó vào toàn bộ `Kumo.exe` và `KumoSetup.exe`.
- **Cài đè trực tiếp không mất dữ liệu (Seamless Overwrite)**:
  - **Cách 1 (Từ bộ cài `KumoSetup.exe`)**: Chạy `KumoSetup.exe` mới đè thẳng lên máy đã cài Kumo. Trình cài đặt tự nhận diện phiên bản cũ (`Đang cài v... → Cài đè lên v...`), tự đóng tiến trình đang chạy, thay thế tệp thực thi an toàn (kể cả khi Windows còn giữ handle tệp cũ) và **giữ nguyên 100% cấu hình, danh sách việc cần làm & dữ liệu người dùng**.
  - **Cách 2 (1 chạm ngay trong ứng dụng)**: Mở **Kumo Settings → Giới thiệu (About)** → nhấn **Kiểm tra bản mới trên GitHub** → nhấn **Tải & Cài đè ngay** để Kumo tự tải bản mới nhất, cài đè ngầm (`--silent --relaunch`) và tự khởi động lại sang phiên bản mới.