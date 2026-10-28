(function () {
  "use strict";

  const TRACKS = [
    {
      title: "Mình Là Người Yêu Nhau",
      artist: "Indie Acoustic · YouTube Music",
      artGrad: "linear-gradient(135deg, #f43f5e, #6366f1)",
      dur: 218,
      lyrics: [
        { t: 0,  text: "♪ Dạo bước dưới hiên mây chiều dịu êm" },
        { t: 5,  text: "Giai điệu ngân vang ngay trên đỉnh màn hình" },
        { t: 10, text: "Thu nhỏ tai thỏ vẫn hiện từng câu hát" },
        { t: 15, text: "Chẳng vướng khung tab, chẳng làm chói đôi mắt" },
        { t: 20, text: "Mình cùng lắng nghe từng nhịp thở bình yên" }
      ]
    },
    {
      title: "Đám Mây Nhỏ (Kumo Lo-Fi)",
      artist: "Kumo Studio · Spotify",
      artGrad: "linear-gradient(135deg, #0ea5e9, #10b981)",
      dur: 184,
      lyrics: [
        { t: 0,  text: "♪ Một đám mây đen khẽ đậu cạnh trên cùng" },
        { t: 5,  text: "Giữ trọn lời ca và nhịp tim hệ thống" },
        { t: 10, text: "Tăng nhẹ âm lượng — thanh trắng tinh hiện lên" },
        { t: 15, text: "Tập trung làm việc qua từng phiên Pomodoro" }
      ]
    },
    {
      title: "Hoàng Hôn Thành Phố",
      artist: "Chill Vibes · SoundCloud",
      artGrad: "linear-gradient(135deg, #f59e0b, #ec4899)",
      dur: 205,
      lyrics: [
        { t: 0,  text: "♪ Ánh chiều buông nhẹ qua khung cửa sổ" },
        { t: 5,  text: "Nhấp ngụm nước mát cho ngày dài tỉnh táo" },
        { t: 10, text: "Kéo thả tài liệu vào ngăn kệ tạm thời" },
        { t: 15, text: "Mọi thứ gọn gàng trong một cú lướt chuột" }
      ]
    }
  ];

  const state = {
    lang: "vi",
    expanded: true,
    pinnedCollapsed: false,
    autoMinimal: false,
    mascotOn: false,
    page: 5, // Start on Kumo's signature System Monitor!
    trackIdx: 0,
    playing: true,
    posSec: 6,
    lyricsSplit: true,
    muted: false,
    volume: 64,
    brightness: 80,
    batPct: 78,
    batCharging: true,
    sysMetric: 0, // 0=CPU, 1=SSD, 2=WiFi, 3=GPU
    pomoSec: 25 * 60,
    pomoRun: false,
    pomoMode: 0,
    waterMl: 1500,
    waterGoal: 2000,
    shelf: ["BaoCao_Q4.pdf", "Kumo_UI.fig"],
    todos: [
      { text: "Hoàn thiện giao diện sáng dịu mắt", done: true },
      { text: "Tối ưu độ nét ClearType 125% DPI", done: true },
      { text: "Thử tính năng lời nhạc khi thu nhỏ", done: false }
    ],
    osdTimer: null
  };

  const $ = (sel) => document.querySelector(sel);
  const $$ = (sel) => Array.from(document.querySelectorAll(sel));

  // ===== 1. Fetch Live Latest Release from GitHub API =====
  async function syncLatestRelease() {
    try {
      const r = await fetch("https://api.github.com/repos/buiduchuy2010qn-prog/kumo/releases/latest");
      if (!r.ok) return;
      const data = await r.json();
      const tag = data.tag_name || "v1.1.13";
      const exeAsset = (data.assets || []).find(a => a.name && a.name.toLowerCase().endsWith(".exe"));
      const dlUrl = exeAsset ? exeAsset.browser_download_url : `https://github.com/buiduchuy2010qn-prog/kumo/releases/latest`;
      const exeName = exeAsset ? exeAsset.name : `KumoSetup-${tag}.exe`;
      const sizeKb = exeAsset ? Math.round(exeAsset.size / 1024) + " KB" : "~203 KB";

      $$("[data-dl-link]").forEach(el => { el.href = dlUrl; });
      $$("[data-ver-tag]").forEach(el => { el.textContent = tag; });
      $$("[data-exe-name]").forEach(el => { el.textContent = exeName; });
      $$("[data-exe-size]").forEach(el => { el.textContent = sizeKb; });
    } catch (_) {}
  }

  // ===== 2. Render Notch State =====
  const PAGE_TITLES = [
    ["Now ", "Playing", "#38bdf8"],
    ["Focus ", "Timer", "#fb7185"],
    ["Water ", "Reminder", "#38bdf8"],
    ["File ", "Shelf", "#a78bfa"],
    ["Quick ", "To-Do", "#34d399"],
    ["System ", "Monitor", "#19b8ff"]
  ];

  function currentLyricLine() {
    const tr = TRACKS[state.trackIdx];
    const loopT = state.posSec % 20;
    let idx = 0;
    for (let i = 0; i < tr.lyrics.length; i++) {
      if (tr.lyrics[i].t <= loopT) idx = i;
    }
    return { idx, list: tr.lyrics, text: tr.lyrics[idx].text };
  }

  function renderNotch() {
    const notch = $("#kumoNotch");
    if (!notch) return;

    notch.classList.remove("state-micro", "state-lyrics-pill", "state-expanded", "wide-lyrics");
    const mascotEl = $("#knMascotPill");
    if (mascotEl) mascotEl.classList.toggle("show", !state.expanded && !state.autoMinimal && state.mascotOn && !state.lyricsSplit);

    if (state.expanded) {
      notch.classList.add("state-expanded");
      if (state.page === 0 && state.lyricsSplit) notch.classList.add("wide-lyrics");
    } else if (state.autoMinimal) {
      notch.classList.add("state-micro");
    } else if (state.lyricsSplit && state.playing) {
      notch.classList.add("state-lyrics-pill");
    }

    // Collapsed center text (Clock vs Live Synced Lyrics!)
    const colText = $("#knColCenter");
    const miniArt = $("#knMiniArt");
    const eqs = $$(".kn-eq");
    const tr = TRACKS[state.trackIdx];
    if (miniArt) miniArt.style.background = tr.artGrad;
    eqs.forEach(eq => eq.classList.toggle("paused", !state.playing));

    if (colText) {
      if (state.lyricsSplit && state.playing) {
        colText.textContent = currentLyricLine().text;
      } else {
        const now = new Date();
        colText.textContent = String(now.getHours()).padStart(2, "0") + ":" + String(now.getMinutes()).padStart(2, "0");
      }
    }

    // Header
    const [p1, p2, pCol] = PAGE_TITLES[state.page];
    $("#knTitle1").textContent = p1;
    const t2 = $("#knTitle2");
    t2.textContent = p2;
    t2.style.color = pCol;

    const batEl = $("#knBatHdr");
    if (state.batCharging) {
      batEl.textContent = `⚡ ${state.batPct}% · ` + (state.lang === "vi" ? "Đang sạc" : "Charging");
      batEl.style.color = "#4ade80";
    } else if (state.batPct <= 20) {
      batEl.textContent = `! ${state.batPct}% · ` + (state.lang === "vi" ? "Pin yếu" : "Low Bat");
      batEl.style.color = "#fb7185";
    } else {
      batEl.textContent = `● ${state.batPct}% · ` + (state.lang === "vi" ? "Pin bth" : "Normal");
      batEl.style.color = "#e2e8f0";
    }

    const now = new Date();
    const months = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
    $("#knDateHdr").textContent = `${String(now.getDate()).padStart(2,"0")} ${months[now.getMonth()]}`;
    $("#knTimeHdr").textContent = `${String(now.getHours()).padStart(2,"0")}:${String(now.getMinutes()).padStart(2,"0")}`;

    // Pages active & dots
    $$(".kn-page").forEach((el, idx) => el.classList.toggle("active", idx === state.page));
    $$("#knDots i").forEach((el, idx) => el.classList.toggle("on", idx === state.page));
    $$("[data-goto-page]").forEach(btn => btn.classList.toggle("active", Number(btn.dataset.gotoPage) === state.page && state.expanded));

    // Page 0: Music + Lyrics
    $("#musArt").style.background = tr.artGrad;
    $("#musTitle").textContent = tr.title;
    $("#musArtist").textContent = tr.artist;
    const pct = Math.min(100, (state.posSec / tr.dur) * 100);
    $("#musFill").style.width = pct.toFixed(1) + "%";
    const fmt = (s) => `${String(Math.floor(s/60)).padStart(2,"0")}:${String(Math.floor(s%60)).padStart(2,"0")}`;
    $("#musCur").textContent = fmt(state.posSec);
    $("#musRem").textContent = "-" + fmt(Math.max(0, tr.dur - state.posSec));
    $("#btnPlay").textContent = state.playing ? "⏸" : "▶";
    $("#btnLyrics").classList.toggle("lyr-on", state.lyricsSplit);
    $("#btnMute").textContent = (state.muted || state.volume === 0) ? "🔇" : "🔊";

    const lyrInfo = currentLyricLine();
    const pane = $("#musLyricsPane");
    if (pane) {
      pane.innerHTML = lyrInfo.list.slice(0, 4).map((item, i) =>
        `<div class="lyr-line ${i === (lyrInfo.idx % 4) ? "current" : ""}">${item.text}</div>`
      ).join("");
    }
    // Page 1: Pomodoro
    $("#pomoTime").textContent = fmt(state.pomoSec);
    $("#btnPomoRun").textContent = state.pomoRun ? "⏸ Pause" : "▶ Start";

    // Page 2: Water
    const wL = (state.waterMl / 1000).toFixed(1);
    const gL = (state.waterGoal / 1000).toFixed(1);
    $("#waterReadout").textContent = `${wL} / ${gL} L`;
    const wPct = Math.min(100, Math.round((state.waterMl / state.waterGoal) * 100));
    $("#waterPct").textContent = `${wPct}% mục tiêu hôm nay`;
    $("#waterBarFill").style.width = `${wPct}%`;
    const todayBar = $("#waterTodayBar");
    if (todayBar) todayBar.style.height = `${Math.max(12, Math.min(52, Math.round(wPct * 0.5)))}px`;

    // Page 3: Shelf
    const shelfBox = $("#shelfDrop");
    if (shelfBox) {
      if (state.shelf.length === 0) {
        shelfBox.innerHTML = `<span style="color:#cbd5e1;font-size:12px;">${state.lang === "vi" ? "Kệ trống — bấm + Thêm tệp mẫu" : "Shelf empty — click + Sample file"}</span>`;
      } else {
        shelfBox.innerHTML = state.shelf.map((f, idx) =>
          `<span class="file-chip">📄 ${f} <b data-del-shelf="${idx}" style="cursor:pointer;color:#fda4af;">×</b></span>`
        ).join("");
      }
    }

    // Page 4: Todo
    const doneCnt = state.todos.filter(t => t.done).length;
    $("#todoHero").textContent = `${doneCnt}/${state.todos.length}`;
    const todoBox = $("#todoList");
    if (todoBox) {
      todoBox.innerHTML = state.todos.slice(0, 3).map((t, idx) =>
        `<div class="todo-item ${t.done ? "done" : ""}" data-toggle-todo="${idx}">
          <span>${t.done ? "✓ " : "○ "}${t.text}</span>
          <b data-del-todo="${idx}" style="color:#fda4af;padding-left:6px;">×</b>
        </div>`
      ).join("");
    }

    // Page 5: System Monitor (Kumo Signature!)
    const cpu = 9 + (state.posSec % 7);
    const ssdKb = 48 + (state.posSec % 5) * 19;
    const netDown = (12.4 + (state.posSec % 4) * 1.3).toFixed(1);
    const gpu = 2 + (state.posSec % 4);
    $("#mValSsd").textContent = `1% · ${ssdKb} KB/s`;
    $("#mValNet").textContent = `↓${netDown} ↑2.4 Mbps`;
    $("#mValGpu").textContent = `${gpu}% · 3D`;
    $$(".m-row").forEach(r => r.classList.toggle("sel", Number(r.dataset.m) === state.sysMetric));

    if (state.sysMetric === 1) {
      $("#sysBig").textContent = "1%";
      $("#sysUnit").textContent = "SSD · Active";
      $("#sysSub").textContent = `R: ${ssdKb} KB/s · W: 24 KB/s`;
      $("#sysBarFill").style.width = "10%";
    } else if (state.sysMetric === 2) {
      $("#sysBig").textContent = netDown;
      $("#sysUnit").textContent = "Mbps · Wi-Fi";
      $("#sysSub").textContent = `↓ ${netDown} Mbps · ↑ 2.4 Mbps`;
      $("#sysBarFill").style.width = "34%";
    } else if (state.sysMetric === 3) {
      $("#sysBig").textContent = `${gpu}%`;
      $("#sysUnit").textContent = "GPU 0 · 3D";
      $("#sysSub").textContent = "Intel Iris Xe / RTX Graphics";
      $("#sysBarFill").style.width = `${Math.max(8, gpu * 3)}%`;
    } else {
      $("#sysBig").textContent = `${cpu}%`;
      $("#sysUnit").textContent = "CPU · 4.33 GHz";
      $("#sysSub").textContent = "RAM 38% · 6.1/16.0 GB";
      $("#sysBarFill").style.width = `${Math.max(12, cpu)}%`;
    }
    $("#btnMascotToggle").textContent = state.mascotOn ? "^ ^ Mascot: ON" : "^ ^ Mascot";
  }

  // ===== 3. Trigger Live Hardware OSD HUD =====
  function triggerOsd(kind, delta) {
    const notch = $("#kumoNotch");
    const osd = $("#knOsd");
    const ico = $("#osdIco");
    const lbl = $("#osdLbl");
    const fill = $("#osdFill");
    const val = $("#osdVal");

    if (kind === "vol") {
      if (delta === 0) {
        state.muted = !state.muted;
      } else {
        state.muted = false;
        state.volume = Math.max(0, Math.min(100, state.volume + delta));
      }
      if (state.muted || state.volume === 0) {
        ico.textContent = "🔇";
        ico.style.color = "#f87171";
        lbl.textContent = state.lang === "vi" ? "Đã tắt tiếng" : "Muted";
        lbl.style.color = "#f87171";
        fill.style.background = "#f87171";
        fill.style.width = "0%";
        val.textContent = state.lang === "vi" ? "Tắt" : "Mute";
      } else {
        // PURE WHITE VOLUME OSD (#FFFFFF) per v1.1.13!
        const arrow = delta > 0 ? " ▲" : (delta < 0 ? " ▼" : "");
        ico.textContent = "🔊";
        ico.style.color = "#ffffff";
        lbl.textContent = (state.lang === "vi" ? "Âm lượng" : "Volume") + arrow;
        lbl.style.color = "#ffffff";
        fill.style.background = "#ffffff";
        fill.style.width = state.volume + "%";
        val.textContent = state.volume + "%";
      }
    } else if (kind === "bri") {
      state.brightness = state.brightness >= 100 ? 40 : Math.min(100, state.brightness + 20);
      ico.textContent = "☀️";
      ico.style.color = "#fbbf24";
      lbl.textContent = (state.lang === "vi" ? "Độ sáng ▲" : "Brightness ▲");
      lbl.style.color = "#ffffff";
      fill.style.background = "linear-gradient(90deg, #f59e0b, #fde047)";
      fill.style.width = state.brightness + "%";
      val.textContent = state.brightness + "%";
    } else if (kind === "bat_chg") {
      state.batCharging = true;
      state.batPct = 82;
      ico.textContent = "⚡";
      ico.style.color = "#4ade80";
      lbl.textContent = state.lang === "vi" ? "Đang sạc pin ⚡" : "Charging ⚡";
      lbl.style.color = "#4ade80";
      fill.style.background = "linear-gradient(90deg, #22c55e, #4ade80)";
      fill.style.width = "82%";
      val.textContent = "82%";
    } else if (kind === "bat_low") {
      state.batCharging = false;
      state.batPct = 15;
      ico.textContent = "🪫";
      ico.style.color = "#fb7185";
      lbl.textContent = state.lang === "vi" ? "Pin yếu!" : "Low Battery!";
      lbl.style.color = "#fb7185";
      fill.style.background = "linear-gradient(90deg, #ef4444, #fb7185)";
      fill.style.width = "15%";
      val.textContent = "15%";
    }

    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);
    if (state.expanded) {
      osd.classList.add("banner-on");
      notch.classList.remove("state-osd");
    } else {
      osd.classList.remove("banner-on");
      notch.classList.remove("state-micro", "state-lyrics-pill");
      notch.classList.add("state-osd");
    }
    renderNotch();

    state.osdTimer = setTimeout(() => {
      osd.classList.remove("banner-on");
      notch.classList.remove("state-osd");
      renderNotch();
    }, 2200);
  }

  function triggerDownloadSim(isStore) {
    const notch = $("#kumoNotch");
    const osd = $("#knOsd");
    const ico = $("#osdIco");
    const lbl = $("#osdLbl");
    const fill = $("#osdFill");
    const val = $("#osdVal");

    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);
    let pct = 8;

    const applyFrame = (done) => {
      if (done) {
        ico.textContent = "\u2713";
        ico.style.color = "#4ade80";
        lbl.textContent = (state.lang === "vi" ? "Tải xong \u00B7 " : "Saved \u00B7 ") + (isStore ? "Spotify Music" : "KumoSetup..13.exe");
        lbl.style.color = "#4ade80";
        fill.style.background = "linear-gradient(90deg, #10b981, #4ade80)";
        fill.style.width = "100%";
        val.textContent = isStore ? "100%" : "210 KB";
      } else if (isStore) {
        ico.textContent = "\uD83D\uDECD";
        ico.style.color = "#38bdf8";
        lbl.textContent = "Store \u00B7 Spotify Music";
        lbl.style.color = "#ffffff";
        fill.style.background = "linear-gradient(90deg, #0ea5e9, #67e8f9)";
        fill.style.width = pct + "%";
        val.textContent = pct + "%";
      } else {
        ico.textContent = "\u2B07";
        ico.style.color = "#22d3ee";
        lbl.textContent = (state.lang === "vi" ? "Đang tải \u00B7 " : "DL \u00B7 ") + "KumoSetup..13.exe";
        lbl.style.color = "#ffffff";
        fill.style.background = "linear-gradient(90deg, #0ea5e9, #bae6fd)";
        fill.style.width = pct + "%";
        val.textContent = "18.4 MB/s";
      }

      if (state.expanded) {
        osd.classList.add("banner-on");
        notch.classList.remove("state-osd");
      } else {
        osd.classList.remove("banner-on");
        notch.classList.remove("state-micro", "state-lyrics-pill");
        notch.classList.add("state-osd");
      }
    };

    state.expanded = false;
    state.pinnedCollapsed = true;
    renderNotch();
    applyFrame(false);

    state.dlInterval = setInterval(() => {
      pct = Math.min(100, pct + 19);
      if (pct >= 100) {
        clearInterval(state.dlInterval);
        applyFrame(true);
        state.osdTimer = setTimeout(() => {
          osd.classList.remove("banner-on");
          notch.classList.remove("state-osd");
          renderNotch();
        }, 2600);
      } else {
        applyFrame(false);
      }
    }, 340);
  }

  // ===== 4. Wire Interactive Controls =====
  function initInteractions() {
    const notch = $("#kumoNotch");
    notch.addEventListener("mouseenter", () => {
      if (!state.pinnedCollapsed) { state.expanded = true; renderNotch(); }
    });
    notch.addEventListener("click", (e) => {
      if (!state.expanded) {
        state.pinnedCollapsed = false;
        state.expanded = true;
        renderNotch();
      }
    });
    $("#desktopViewport").addEventListener("click", (e) => {
      if (e.target.closest("#kumoNotch") || e.target.closest("#simSettingsModal")) return;
      state.expanded = false;
      renderNotch();
    });

    $("#btnPrevPage").addEventListener("click", (e) => { e.stopPropagation(); state.page = (state.page + 5) % 6; renderNotch(); });
    $("#btnNextPage").addEventListener("click", (e) => { e.stopPropagation(); state.page = (state.page + 1) % 6; renderNotch(); });
    $$("#knDots i").forEach((dot, idx) => dot.addEventListener("click", (e) => { e.stopPropagation(); state.page = idx; renderNotch(); }));

    $$("[data-goto-page]").forEach(btn => btn.addEventListener("click", () => {
      state.pinnedCollapsed = false;
      state.autoMinimal = false;
      $("#simBrowser").classList.remove("maximized");
      state.expanded = true;
      state.page = Number(btn.dataset.gotoPage);
      renderNotch();
    }));

    $("#btnCollapsePill").addEventListener("click", () => {
      state.expanded = false;
      state.autoMinimal = false;
      state.pinnedCollapsed = true;
      $("#simBrowser").classList.remove("maximized");
      renderNotch();
    });

    $("#btnLyricsCollapsedDemo").addEventListener("click", () => {
      state.lyricsSplit = true;
      state.playing = true;
      state.autoMinimal = false;
      state.expanded = false;
      state.pinnedCollapsed = true;
      $("#simBrowser").classList.remove("maximized");
      renderNotch();
    });

    // Music buttons
    $("#btnPlay").addEventListener("click", (e) => { e.stopPropagation(); state.playing = !state.playing; renderNotch(); });
    $("#btnPrevTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + TRACKS.length - 1) % TRACKS.length; state.posSec = 0; renderNotch(); });
    $("#btnNextTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + 1) % TRACKS.length; state.posSec = 0; renderNotch(); });
    $("#btnLyrics").addEventListener("click", (e) => { e.stopPropagation(); state.lyricsSplit = !state.lyricsSplit; renderNotch(); });
    $("#btnMute").addEventListener("click", (e) => { e.stopPropagation(); triggerOsd("vol", 0); });
    $("#musSeekBar").addEventListener("click", (e) => {
      e.stopPropagation();
      const r = e.currentTarget.getBoundingClientRect();
      const f = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
      state.posSec = Math.floor(f * TRACKS[state.trackIdx].dur);
      renderNotch();
    });

    // System Monitor cards
    $("#sysHeroBox").addEventListener("click", () => { state.sysMetric = 0; renderNotch(); });
    $$(".m-row").forEach(r => r.addEventListener("click", (e) => { e.stopPropagation(); state.sysMetric = Number(r.dataset.m); renderNotch(); }));
    $("#btnMascotToggle").addEventListener("click", (e) => {
      e.stopPropagation();
      state.mascotOn = !state.mascotOn;
      if (state.mascotOn) { state.lyricsSplit = false; state.expanded = false; state.pinnedCollapsed = true; }
      renderNotch();
    });

    // Pomodoro
    $("#btnPomoRun").addEventListener("click", (e) => { e.stopPropagation(); state.pomoRun = !state.pomoRun; renderNotch(); });
    $("#btnPomoMinus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec = Math.max(60, state.pomoSec - 300); renderNotch(); });
    $("#btnPomoPlus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec = Math.min(5400, state.pomoSec + 300); renderNotch(); });
    $("#btnPomoReset").addEventListener("click", (e) => { e.stopPropagation(); state.pomoRun = false; state.pomoSec = 1500; renderNotch(); });

    // Water
    $("#btnWaterAdd").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl += 250; renderNotch(); });
    $("#btnWaterSub").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl = Math.max(0, state.waterMl - 250); renderNotch(); });

    // Shelf
    $("#btnShelfAdd").addEventListener("click", (e) => {
      e.stopPropagation();
      const samples = ["Slide_ThuyetTrinh.pptx", "Anh_Chup_ManHinh.png", "GhiChu_Hop.docx", "Source_Patch.zip"];
      const pick = samples[state.shelf.length % samples.length];
      if (state.shelf.length < 4) state.shelf.push(pick);
      renderNotch();
    });
    $("#btnShelfClear").addEventListener("click", (e) => { e.stopPropagation(); state.shelf = []; renderNotch(); });
    $("#shelfDrop").addEventListener("click", (e) => {
      const idx = e.target.dataset.delShelf;
      if (idx !== undefined) { e.stopPropagation(); state.shelf.splice(Number(idx), 1); renderNotch(); }
    });

    // Todo
    $("#btnTodoAdd").addEventListener("click", (e) => {
      e.stopPropagation();
      const samples = ["Kiểm tra bản cài đè v1.1.13", "Uống đủ 2 lít nước hôm nay", "Đẩy code lên GitHub Actions"];
      state.todos.unshift({ text: samples[state.todos.length % samples.length], done: false });
      renderNotch();
    });
    $("#todoList").addEventListener("click", (e) => {
      e.stopPropagation();
      if (e.target.dataset.delTodo !== undefined) {
        state.todos.splice(Number(e.target.dataset.delTodo), 1);
      } else {
        const row = e.target.closest("[data-toggle-todo]");
        if (row) {
          const i = Number(row.dataset.toggleTodo);
          state.todos[i].done = !state.todos[i].done;
        }
      }
      renderNotch();
    });

    // Trigger Deck buttons
    $("#trigVolUp").addEventListener("click", () => triggerOsd("vol", +8));
    $("#trigVolDown").addEventListener("click", () => triggerOsd("vol", -8));
    $("#trigMute").addEventListener("click", () => triggerOsd("vol", 0));
    $("#trigBri").addEventListener("click", () => triggerOsd("bri", +20));
    $("#trigBatChg").addEventListener("click", () => triggerOsd("bat_chg", 0));
    $("#trigBatLow").addEventListener("click", () => triggerOsd("bat_low", 0));
    const btnDlFile = $("#trigDlFile");
    if (btnDlFile) btnDlFile.addEventListener("click", () => triggerDownloadSim(false));
    const btnDlStore = $("#trigDlStore");
    if (btnDlStore) btnDlStore.addEventListener("click", () => triggerDownloadSim(true));

    $("#trigAutoMin").addEventListener("click", () => {
      state.autoMinimal = !state.autoMinimal;
      if (state.autoMinimal) { state.expanded = false; state.pinnedCollapsed = true; }
      $("#simBrowser").classList.toggle("maximized", state.autoMinimal);
      renderNotch();
    });

    const toggleSettings = () => $("#simSettingsModal").classList.toggle("open");
    $("#trigSettings").addEventListener("click", toggleSettings);
    $("#knOpenSettings").addEventListener("click", (e) => { e.stopPropagation(); toggleSettings(); });
    $("#ssmClose").addEventListener("click", () => $("#simSettingsModal").classList.remove("open"));
    $("#tbKumoIco").addEventListener("click", toggleSettings);
    $$(".ssm-toggle").forEach(t => t.addEventListener("click", () => t.classList.toggle("off")));

    // Wallpaper switcher
    $$("[data-set-wall]").forEach(btn => btn.addEventListener("click", () => {
      $$("[data-set-wall]").forEach(b => b.classList.remove("active"));
      btn.classList.add("active");
      $("#desktopViewport").dataset.wall = btn.dataset.setWall;
    }));

    // VI / EN Language Switcher
    $$("[data-lang-btn]").forEach(btn => btn.addEventListener("click", () => {
      state.lang = btn.dataset.langBtn;
      $$("[data-lang-btn]").forEach(b => b.classList.toggle("active", b.dataset.langBtn === state.lang));
      $$("[data-vi]").forEach(el => {
        el.textContent = state.lang === "vi" ? el.dataset.vi : (el.dataset.en || el.dataset.vi);
      });
      renderNotch();
    }));

    setInterval(() => {
      if (state.playing) {
        state.posSec = (state.posSec + 1) % TRACKS[state.trackIdx].dur;
      }
      if (state.pomoRun && state.pomoSec > 0) {
        state.pomoSec--;
      }
      renderNotch();
    }, 1000);
  }

  document.addEventListener("DOMContentLoaded", () => {
    initInteractions();
    renderNotch();
    syncLatestRelease();
  });
})();