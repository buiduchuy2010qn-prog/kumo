(function () {
  "use strict";

  const $ = (s) => document.querySelector(s);
  const $$ = (s) => Array.from(document.querySelectorAll(s));

  // Generate 3 realistic album / MV cover artworks on HTML5 Canvas (zero fake emoji boxes!)
  function makeCoverArt(kind, w, h) {
    const c = document.createElement("canvas");
    c.width = w; c.height = h;
    const g = c.getContext("2d");

    if (kind === 0) {
      // "Tìm em" — sunlit Mediterranean courtyard & coastal arch
      const bg = g.createLinearGradient(0, 0, w, h);
      bg.addColorStop(0, "#164e63");
      bg.addColorStop(0.55, "#0e7490");
      bg.addColorStop(1, "#0f172a");
      g.fillStyle = bg; g.fillRect(0, 0, w, h);

      // Warm limestone wall & sunlit arch
      g.fillStyle = "#e7dfd3";
      g.fillRect(w * 0.08, h * 0.08, w * 0.84, h * 0.84);
      g.fillStyle = "#cbbba8";
      g.fillRect(w * 0.56, h * 0.08, w * 0.36, h * 0.84);

      // Arch doorway opening to turquoise sea
      g.save();
      g.beginPath();
      g.arc(w * 0.42, h * 0.46, w * 0.16, Math.PI, 0, false);
      g.lineTo(w * 0.58, h * 0.86);
      g.lineTo(w * 0.26, h * 0.86);
      g.closePath();
      g.clip();
      const sea = g.createLinearGradient(0, h * 0.25, 0, h * 0.86);
      sea.addColorStop(0, "#7dd3fc");
      sea.addColorStop(0.55, "#0284c7");
      sea.addColorStop(1, "#334155");
      g.fillStyle = sea; g.fillRect(w * 0.24, h * 0.25, w * 0.36, h * 0.65);
      // Couple silhouette in doorway
      g.fillStyle = "#1e293b";
      g.beginPath(); g.arc(w * 0.41, h * 0.56, w * 0.032, 0, Math.PI * 2); g.fill();
      g.fillRect(w * 0.385, h * 0.59, w * 0.055, h * 0.22);
      g.restore();

      // Warm bougainvillea accents
      g.fillStyle = "#be185d";
      for (let i = 0; i < 9; i++) {
        g.beginPath();
        g.arc(w * (0.68 + (i % 3) * 0.06), h * (0.58 + Math.floor(i / 3) * 0.08), w * 0.022, 0, Math.PI * 2);
        g.fill();
      }
    } else if (kind === 1) {
      // "Vết thương" — twilight ocean horizon & two silhouettes
      const sky = g.createLinearGradient(0, 0, 0, h);
      sky.addColorStop(0, "#1e3a5f");
      sky.addColorStop(0.52, "#64748b");
      sky.addColorStop(0.75, "#d97706");
      sky.addColorStop(1, "#0f172a");
      g.fillStyle = sky; g.fillRect(0, 0, w, h);

      g.fillStyle = "rgba(15, 23, 42, 0.88)";
      g.beginPath(); g.arc(w * 0.36, h * 0.52, w * 0.075, 0, Math.PI * 2); g.fill();
      g.fillRect(w * 0.27, h * 0.60, w * 0.19, h * 0.40);
      g.fillStyle = "rgba(30, 41, 59, 0.82)";
      g.beginPath(); g.arc(w * 0.64, h * 0.56, w * 0.068, 0, Math.PI * 2); g.fill();
      g.fillRect(w * 0.56, h * 0.63, w * 0.17, h * 0.37);
    } else {
      // "Đám mây nhỏ" — crisp twilight sky & glowing cloud
      const sky = g.createLinearGradient(0, 0, w, h);
      sky.addColorStop(0, "#0c4a6e");
      sky.addColorStop(0.6, "#0284c7");
      sky.addColorStop(1, "#38bdf8");
      g.fillStyle = sky; g.fillRect(0, 0, w, h);

      g.fillStyle = "rgba(255,255,255,0.94)";
      const circles = [[0.38, 0.56, 0.13], [0.50, 0.47, 0.17], [0.63, 0.56, 0.12], [0.50, 0.59, 0.14]];
      circles.forEach(([cx, cy, r]) => {
        g.beginPath(); g.arc(w * cx, h * cy, w * r, 0, Math.PI * 2); g.fill();
      });
    }
    return c.toDataURL("image/png");
  }

  const TRACKS = [
    {
      title: "Hngle - Tìm em ft. Bảo Anh",
      ytTitle: "Hngle - Tìm em ft. Bảo Anh | Official Music Video",
      artist: "HNGLE / hưng lê",
      views: "86 Tr lượt xem",
      url: "https://www.youtube.com/watch?v=gJAbDSse5WM",
      eqCol: "#38bdf8",
      dur: 271,
      thumb: makeCoverArt(0, 160, 160),
      banner: makeCoverArt(0, 640, 360),
      lyrics: [
        { t: 0,  text: "Ngồi ngẩn ngơ anh hát vu vơ bên hiên chiều" },
        { t: 5,  text: "Có đôi lần khẽ cười vì anh nhớ em" },
        { t: 10, text: "Từ giọng nói như rót mật ngọt vào tai anh" },
        { t: 15, text: "Nhiều khi anh cứ ngỡ là nắng đang về đây" }
      ]
    },
    {
      title: "fishy - VẾT THƯƠNG",
      ytTitle: "fishy - VẾT THƯƠNG - OFFICIAL MUSIC VIDEO",
      artist: "fishy",
      views: "14 Tr lượt xem",
      url: "https://www.youtube.com/watch?v=vet-thuong-mv",
      eqCol: "#f59e0b",
      dur: 258,
      thumb: makeCoverArt(1, 160, 160),
      banner: makeCoverArt(1, 640, 360),
      lyrics: [
        { t: 0,  text: "Em đâu biết đâu, em đâu biết đâu lòng anh" },
        { t: 5,  text: "Vì vết thương chỉ là bên ngoài mà thôi" },
        { t: 10, text: "Nỗi đau mới thêm dài theo từng chiều mưa" },
        { t: 15, text: "Quá khứ kia em đã từng chôn giấu thật sâu" }
      ]
    },
    {
      title: "Đám Mây Nhỏ (Kumo Acoustic)",
      ytTitle: "Đám Mây Nhỏ — Chill Lo-Fi Session for Focus",
      artist: "Kumo Studio",
      views: "1,2 Tr lượt xem",
      url: "https://www.youtube.com/watch?v=kumo-lofi-chill",
      eqCol: "#34d399",
      dur: 196,
      thumb: makeCoverArt(2, 160, 160),
      banner: makeCoverArt(2, 640, 360),
      lyrics: [
        { t: 0,  text: "Một đám mây đen khẽ đậu ngay cạnh trên cùng" },
        { t: 5,  text: "Thu nhỏ vẫn hiện trọn vẹn từng lời bài hát" },
        { t: 10, text: "Tự động thu mảnh khi cửa sổ chạm thanh tab" },
        { t: 15, text: "Giữ nhịp làm việc êm ái suốt cả ngày dài" }
      ]
    }
  ];

  const state = {
    lang: "vi",
    modeKey: "normal",
    expanded: false,
    pinnedCollapsed: true,
    autoMinimal: false,
    mascotOn: false,
    page: 0,
    trackIdx: 0,
    playing: false, // Normal idle bar on initial load!
    lyricsSplit: false,
    posSec: 35,
    muted: false,
    volume: 68,
    brightness: 80,
    sysMetric: 0,
    pomoSec: 1500,
    pomoRun: false,
    waterMl: 1500,
    waterGoal: 2000,
    shelf: ["BaoCao_Q4.pdf", "Slide_Demo.pptx"],
    todos: [
      { text: "Kiểm tra lời nhạc không cắt chữ", done: true },
      { text: "Thử thanh tải tệp Chrome & Store", done: false }
    ],
    osdTimer: null,
    dlInterval: null
  };
  const PAGE_TITLES = [
    ["Now ", "Playing", "#38bdf8"],
    ["Focus ", "Timer", "#fb7185"],
    ["Water ", "Reminder", "#38bdf8"],
    ["File ", "Shelf", "#a78bfa"],
    ["Quick ", "To-Do", "#34d399"],
    ["System ", "Monitor", "#38bdf8"]
  ];

  async function syncRelease() {
    try {
      const r = await fetch("https://api.github.com/repos/buiduchuy2010qn-prog/kumo/releases/latest");
      if (!r.ok) return;
      const d = await r.json();
      const tag = d.tag_name || "v1.1.20";
      const exe = (d.assets || []).find(a => a.name && a.name.toLowerCase().endsWith(".exe"));
      const url = exe ? exe.browser_download_url : "https://github.com/buiduchuy2010qn-prog/kumo/releases/latest";
      $$("[data-dl-link]").forEach(el => { el.href = url; });
      $$("[data-ver-tag]").forEach(el => { el.textContent = tag; });
      if (exe) {
        $$("[data-exe-name]").forEach(el => { el.textContent = exe.name; });
        $$("[data-exe-size]").forEach(el => { el.textContent = Math.round(exe.size / 1024) + " KB"; });
      }
    } catch (_) {}
  }

  function fmt(sec) {
    return String(Math.floor(sec / 60)).padStart(2, "0") + ":" + String(Math.floor(sec % 60)).padStart(2, "0");
  }

  function curLyric() {
    const tr = TRACKS[state.trackIdx];
    const loop = state.posSec % 20;
    let idx = 0;
    for (let i = 0; i < tr.lyrics.length; i++) {
      if (tr.lyrics[i].t <= loop) idx = i;
    }
    return { idx, list: tr.lyrics, text: tr.lyrics[idx].text };
  }

  function renderPlaylist() {
    const box = $("#ytPlaylist");
    if (!box) return;
    box.innerHTML = TRACKS.map((t, i) => `
      <div class="yt-item ${i === state.trackIdx ? "active" : ""}" data-sel-track="${i}">
        <img src="${t.banner}" alt="${t.title}" />
        <div class="yt-item-meta">
          <strong>${t.title}</strong>
          <span>${t.artist} · ${fmt(t.dur)}</span>
        </div>
      </div>
    `).join("");
  }

  function renderAll() {
    const notch = $("#kumoNotch");
    if (!notch) return;
    const tr = TRACKS[state.trackIdx];
    const now = new Date();
    const hhmm = String(now.getHours()).padStart(2, "0") + ":" + String(now.getMinutes()).padStart(2, "0");

    // Taskbar clock
    $("#tbTime").textContent = hhmm;
    $("#tbDate").textContent = `${String(now.getDate()).padStart(2,"0")}/${String(now.getMonth()+1).padStart(2,"0")}/${now.getFullYear()}`;

    // Mode toolbar active pill
    const mapBtn = { normal: "#modeNormal", colMusic: "#modeColMusic", colLyrics: "#modeColLyrics", expLyrics: "#modeExpLyrics", expSys: "#modeExpSys" };
    Object.keys(mapBtn).forEach(k => { const b = $(mapBtn[k]); if (b) b.classList.toggle("active", state.modeKey === k); });

    // Notch shell classes & dynamic width (ZERO cut-off!)
    const inOsd = notch.classList.contains("state-osd");
    notch.classList.remove("state-micro", "state-expanded", "wide-lyrics", "collapsed-lyrics");
    notch.style.width = "";

    const showColLyrics = !state.expanded && !state.autoMinimal && state.lyricsSplit && state.playing;
    if (state.expanded) {
      notch.classList.add("state-expanded");
      if (state.page === 0 && state.lyricsSplit) notch.classList.add("wide-lyrics");
    } else if (state.autoMinimal && !inOsd) {
      notch.classList.add("state-micro");
    } else if (!inOsd && showColLyrics) {
      const ltxt = curLyric().text;
      notch.classList.add("collapsed-lyrics"); const dynW = Math.min(500, Math.max(296, Math.round(138 + ltxt.length * 7.6)));
      notch.style.width = dynW + "px";
    }

    $("#knMascot").classList.toggle("show", !state.expanded && !state.autoMinimal && state.mascotOn && !showColLyrics && !inOsd);

    // Collapsed left slot: Real Album Cover Image vs Normal Battery Ring!
    const hasActiveArt = state.playing || showColLyrics;
    $("#knLeftSlot").classList.toggle("has-art", hasActiveArt);
    $("#knMiniCover").src = tr.thumb;
    $("#musCoverImg").src = tr.thumb;

    const eqCol = tr.eqCol;
    $$(".kn-eq").forEach(eq => {
      eq.style.setProperty("--eq-col", eqCol);
      eq.classList.toggle("idle", !state.playing && !showColLyrics);
    });

    // Collapsed center text (full uncut lyric or clock)
    const vp = $("#knColViewport");
    const ct = $("#knColText"); const cc = $("#knColClock"); if (cc) cc.textContent = hhmm;
    if (showColLyrics) {
      ct.textContent = curLyric().text; ct.classList.add("karaoke-sweep");
      requestAnimationFrame(() => {
        const overflow = ct.scrollWidth - vp.clientWidth;
        if (overflow > 3) {
          vp.classList.add("scrolling");
          vp.style.setProperty("--pan-x", -(overflow + 8) + "px");
        } else {
          vp.classList.remove("scrolling");
        }
      });
    } else {
      vp.classList.remove("scrolling");
      ct.classList.remove("karaoke-sweep"); ct.textContent = hhmm;
    }

    // Expanded Header & Pages
    const [p1, p2, pCol] = PAGE_TITLES[state.page];
    $("#knTitle1").textContent = p1;
    $("#knTitle2").textContent = p2;
    $("#knTitle2").style.color = pCol;
    const months = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
    $("#knDateHdr").textContent = `${String(now.getDate()).padStart(2,"0")} ${months[now.getMonth()]}`;
    $("#knTimeHdr").textContent = hhmm;

    $$(".kn-page").forEach((el, i) => el.classList.toggle("active", i === state.page));
    $$("#knDots i").forEach((el, i) => el.classList.toggle("on", i === state.page));

    // Page 0: Music + Uncut 4-Line Lyrics
    $("#musTitle").textContent = tr.title;
    $("#musArtist").textContent = tr.artist + " · YouTube";
    $("#musCur").textContent = fmt(state.posSec);
    $("#musRem").textContent = "-" + fmt(Math.max(0, tr.dur - state.posSec));
    $("#musFill").style.width = Math.min(100, (state.posSec / tr.dur) * 100).toFixed(1) + "%";
    $("#btnPlay").textContent = state.playing ? "⏸" : "▶";
    $("#btnLyrics").classList.toggle("lyr-on", state.lyricsSplit);
    $("#btnMute").textContent = state.muted ? "🔇" : "🔊";

    const li = curLyric();
    $("#musLyricsPane").innerHTML = li.list.map((item, idx) =>
      `<div class="lyr-line ${idx === li.idx ? "current karaoke-sweep" : (idx < li.idx ? "past" : "")}">${item.text}</div>`
    ).join("");

    // Sync Windows 11 Chrome YouTube Window
    $("#wcTabTitle").textContent = tr.title + " - YouTube";
    $("#wcUrlInput").value = tr.url;
    $("#ytVideoImg").src = tr.banner;
    $("#ytMainTitle").textContent = tr.ytTitle;
    $("#ytMainChan").textContent = `${tr.artist} · ${tr.views}`;
    $("#ytTimeBadge").textContent = `${fmt(state.posSec)} / ${fmt(tr.dur)}`;
    $("#ytPlayBadge").textContent = state.playing
      ? (state.lang === "vi" ? "▶ Đang phát trên YouTube · Đồng bộ Kumo" : "▶ Playing on YouTube · Synced")
      : (state.lang === "vi" ? "⏸ Đang tạm dừng (Bấm để phát)" : "⏸ Paused (Click to play)");
    $$("[data-pick-track]").forEach(b => b.classList.toggle("active", Number(b.dataset.pickTrack) === state.trackIdx));
    renderPlaylist();

    // Pomodoro / Water / Shelf / Todo / System
    $("#pomoTime").textContent = fmt(state.pomoSec);
    $("#btnPomoRun").textContent = state.pomoRun ? "⏸ Pause" : "▶ Start";
    const wPct = Math.min(100, Math.round((state.waterMl / state.waterGoal) * 100));
    $("#waterReadout").textContent = `${(state.waterMl/1000).toFixed(1)} / ${(state.waterGoal/1000).toFixed(1)} L`;
    $("#waterBarFill").style.width = wPct + "%";
    $("#shelfDrop").innerHTML = state.shelf.map(f => `<span style="background:rgba(255,255,255,0.14);padding:4px 8px;border-radius:6px;">📄 ${f}</span>`).join("");
    $("#todoHero").textContent = `${state.todos.filter(t => t.done).length}/${state.todos.length}`;
    $("#todoList").innerHTML = state.todos.map((t, i) =>
      `<div data-tog-todo="${i}" style="cursor:pointer;background:rgba(255,255,255,0.1);padding:3px 8px;border-radius:5px;${t.done ? "text-decoration:line-through;opacity:0.65;" : ""}">${t.done ? "✓ " : "○ "}${t.text}</div>`
    ).join("");

    const cpu = 10 + (state.posSec % 6);
    if (state.sysMetric === 1) {
      $("#sysBig").textContent = "1%"; $("#sysUnit").textContent = "SSD · Active";
      $("#sysSub").textContent = "Read 84 KB/s · Write 18 KB/s";
    } else if (state.sysMetric === 2) {
      $("#sysBig").textContent = "14.2"; $("#sysUnit").textContent = "Mbps · Wi-Fi";
      $("#sysSub").textContent = "↓ 14.2 Mbps · ↑ 2.6 Mbps";
    } else {
      $("#sysBig").textContent = cpu + "%"; $("#sysUnit").textContent = "CPU · 4.32 GHz";
      $("#sysSub").textContent = "RAM 38% · 6.1/16.0 GB";
    }
    $$(".m-row").forEach(r => r.classList.toggle("sel", Number(r.dataset.m) === state.sysMetric));
  }

  function showOsd(icoTxt, icoCol, lblTxt, lblCol, fillBg, pct, valTxt, durMs) {
    const notch = $("#kumoNotch");
    const osd = $("#knOsd");
    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);
    $("#osdIco").textContent = icoTxt; $("#osdIco").style.color = icoCol;
    $("#osdLbl").textContent = lblTxt; $("#osdLbl").style.color = lblCol;
    $("#osdFill").style.background = fillBg; $("#osdFill").style.width = pct + "%";
    $("#osdVal").textContent = valTxt;

    if (state.expanded) {
      osd.classList.add("banner-on"); notch.classList.remove("state-osd");
    } else {
      osd.classList.remove("banner-on"); notch.style.width = ""; notch.classList.add("state-osd");
    }
    state.osdTimer = setTimeout(() => {
      osd.classList.remove("banner-on"); notch.classList.remove("state-osd"); renderAll();
    }, durMs || 2200);
  }

  function simulateDownload(isStore) {
    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);
    state.expanded = false;
    state.pinnedCollapsed = true;
    renderAll();

    let pct = 12;
    const step = () => {
      if (pct >= 100) {
        clearInterval(state.dlInterval);
        showOsd("✓", "#4ade80",
          (state.lang === "vi" ? "Tải xong · " : "Saved · ") + (isStore ? "Spotify Music" : "KumoSetup..13.exe"),
          "#4ade80", "linear-gradient(90deg,#10b981,#4ade80)", 100, isStore ? "100%" : "210 KB", 2600);
        return;
      }
      if (isStore) {
        showOsd("🛍", "#38bdf8", "Store · Spotify Music", "#fff", "linear-gradient(90deg,#0ea5e9,#67e8f9)", pct, pct + "%", 5000);
      } else {
        showOsd("⬇", "#22d3ee", (state.lang === "vi" ? "Đang tải · " : "DL · ") + "KumoSetup..13.exe", "#fff", "linear-gradient(90deg,#0ea5e9,#bae6fd)", pct, "21.4 MB/s", 5000);
      }
      pct = Math.min(100, pct + 22);
    };
    step();
    state.dlInterval = setInterval(step, 360);
  }

  function setPreset(key) {
    state.modeKey = key;
    state.autoMinimal = false;
    $("#winChrome").classList.remove("maximized");
    if (key === "normal") {
      state.expanded = false; state.pinnedCollapsed = true; state.playing = false; state.lyricsSplit = false;
    } else if (key === "colMusic") {
      state.expanded = false; state.pinnedCollapsed = true; state.playing = true; state.lyricsSplit = false;
    } else if (key === "colLyrics") {
      state.expanded = false; state.pinnedCollapsed = true; state.playing = true; state.lyricsSplit = true;
    } else if (key === "expLyrics") {
      state.expanded = true; state.pinnedCollapsed = false; state.playing = true; state.lyricsSplit = true; state.page = 0;
    } else if (key === "expSys") {
      state.expanded = true; state.pinnedCollapsed = false; state.page = 5;
    }
    renderAll();
  }

  function init() {
    $("#modeNormal").addEventListener("click", () => setPreset("normal"));
    $("#modeColMusic").addEventListener("click", () => setPreset("colMusic"));
    $("#modeColLyrics").addEventListener("click", () => setPreset("colLyrics"));
    $("#modeExpLyrics").addEventListener("click", () => setPreset("expLyrics"));
    $("#modeExpSys").addEventListener("click", () => setPreset("expSys"));

    $("#trigDlBrowser").addEventListener("click", () => simulateDownload(false));
    $("#wcDlBtn").addEventListener("click", () => simulateDownload(false));
    $("#trigDlStore").addEventListener("click", () => simulateDownload(true));
    $("#tbStore").addEventListener("click", () => simulateDownload(true));

    $("#trigVol").addEventListener("click", () => {
      state.muted = false; state.volume = Math.min(100, state.volume + 8);
      showOsd("🔊", "#fff", (state.lang === "vi" ? "Âm lượng ▲" : "Volume ▲"), "#fff", "#ffffff", state.volume, state.volume + "%");
    });
    $("#tbVolTray").addEventListener("click", () => $("#trigVol").click());
    $("#trigBri").addEventListener("click", () => {
      state.brightness = state.brightness >= 100 ? 50 : state.brightness + 25;
      showOsd("☀", "#fbbf24", (state.lang === "vi" ? "Độ sáng ▲" : "Brightness ▲"), "#fff", "linear-gradient(90deg,#f59e0b,#fde047)", state.brightness, state.brightness + "%");
    });

    const toggleSet = () => $("#kumoSettingsWin").classList.toggle("open");
    $("#trigSettings").addEventListener("click", toggleSet);
    $("#knOpenSettings").addEventListener("click", (e) => { e.stopPropagation(); toggleSet(); });
    $("#tbKumo").addEventListener("click", toggleSet);
    $("#deskKumo").addEventListener("click", toggleSet);
    $("#kswClose").addEventListener("click", () => $("#kumoSettingsWin").classList.remove("open"));
    $$(".ksw-tog").forEach(t => t.addEventListener("click", () => t.classList.toggle("off")));

    $("#trigWall").addEventListener("click", () => {
      const sc = $("#win11Screen");
      sc.dataset.wall = sc.dataset.wall === "dark" ? "light" : "dark";
    });

    // Notch hover / click
    const notch = $("#kumoNotch");
    notch.addEventListener("click", () => {
      if (!state.expanded) { state.expanded = true; state.pinnedCollapsed = false; renderAll(); }
    });
    $("#win11Screen").addEventListener("click", (e) => {
      if (e.target.closest("#kumoNotch") || e.target.closest("#kumoSettingsWin")) return;
      if (state.expanded) { state.expanded = false; renderAll(); }
    });

    // Notch internal controls
    $("#btnPrevPage").addEventListener("click", (e) => { e.stopPropagation(); state.page = (state.page + 5) % 6; renderAll(); });
    $("#btnNextPage").addEventListener("click", (e) => { e.stopPropagation(); state.page = (state.page + 1) % 6; renderAll(); });
    $$("#knDots i").forEach((d, i) => d.addEventListener("click", (e) => { e.stopPropagation(); state.page = i; renderAll(); }));

    $("#btnPlay").addEventListener("click", (e) => { e.stopPropagation(); state.playing = !state.playing; renderAll(); });
    $("#btnPrevTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + 2) % 3; state.posSec = 0; renderAll(); });
    $("#btnNextTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + 1) % 3; state.posSec = 0; renderAll(); });
    $("#btnLyrics").addEventListener("click", (e) => { e.stopPropagation(); state.lyricsSplit = !state.lyricsSplit; renderAll(); });
    $("#btnMute").addEventListener("click", (e) => {
      e.stopPropagation(); state.muted = !state.muted;
      showOsd(state.muted ? "🔇" : "🔊", state.muted ? "#f87171" : "#fff", state.muted ? "Đã tắt tiếng" : "Âm lượng", state.muted ? "#f87171" : "#fff", state.muted ? "#f87171" : "#fff", state.muted ? 0 : state.volume, state.muted ? "Tắt" : state.volume + "%");
      renderAll();
    });
    $("#musSeekBar").addEventListener("click", (e) => {
      e.stopPropagation();
      const r = e.currentTarget.getBoundingClientRect();
      state.posSec = Math.floor(Math.max(0, Math.min(1, (e.clientX - r.left) / r.width)) * TRACKS[state.trackIdx].dur);
      renderAll();
    });

    // Windows 11 Chrome Window caption buttons
    const wc = $("#winChrome");
    $("#wcMax").addEventListener("click", () => {
      const mx = !wc.classList.contains("maximized");
      wc.classList.remove("minimized");
      wc.classList.toggle("maximized", mx);
      state.autoMinimal = mx;
      if (mx) state.expanded = false;
      renderAll();
    });
    $("#wcMin").addEventListener("click", () => { wc.classList.add("minimized"); state.autoMinimal = false; renderAll(); });
    $("#wcClose").addEventListener("click", () => { wc.classList.add("minimized"); state.autoMinimal = false; renderAll(); });
    $("#tbChrome").addEventListener("click", () => { wc.classList.remove("minimized"); renderAll(); });

    // YouTube Search & Playlist inside Windows Chrome
    const doSearch = () => {
      const q = ($("#ytSearchInput").value || "").toLowerCase();
      if (q.includes("vết") || q.includes("vet") || q.includes("fishy")) state.trackIdx = 1;
      else if (q.includes("mây") || q.includes("may") || q.includes("kumo")) state.trackIdx = 2;
      else state.trackIdx = 0;
      state.playing = true; state.posSec = 2;
      if (state.modeKey === "normal") state.modeKey = "colLyrics", state.lyricsSplit = true;
      renderAll();
    };
    $("#ytSearchBtn").addEventListener("click", doSearch);
    $("#ytSearchInput").addEventListener("keydown", (e) => { if (e.key === "Enter") doSearch(); });
    $$("[data-pick-track]").forEach(b => b.addEventListener("click", () => {
      state.trackIdx = Number(b.dataset.pickTrack); state.playing = true; state.posSec = 5;
      if (state.modeKey === "normal") { state.modeKey = "colLyrics"; state.lyricsSplit = true; }
      renderAll();
    }));
    $("#ytPlaylist").addEventListener("click", (e) => {
      const it = e.target.closest("[data-sel-track]");
      if (it) { state.trackIdx = Number(it.dataset.selTrack); state.playing = true; state.posSec = 5; renderAll(); }
    });
    $("#ytVideoBox").addEventListener("click", () => {
      state.playing = !state.playing;
      if (state.playing && state.modeKey === "normal") state.modeKey = "colMusic";
      renderAll();
    });

    // Other page buttons
    $("#btnPomoRun").addEventListener("click", (e) => { e.stopPropagation(); state.pomoRun = !state.pomoRun; renderAll(); });
    $("#btnPomoMinus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec = Math.max(60, state.pomoSec - 300); renderAll(); });
    $("#btnPomoPlus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec += 300; renderAll(); });
    $("#btnWaterAdd").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl += 250; renderAll(); });
    $("#btnWaterSub").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl = Math.max(0, state.waterMl - 250); renderAll(); });
    $("#btnShelfAdd").addEventListener("click", (e) => { e.stopPropagation(); if (state.shelf.length < 4) state.shelf.push("TaiLieu_" + (state.shelf.length + 1) + ".zip"); renderAll(); });
    $("#btnTodoAdd").addEventListener("click", (e) => { e.stopPropagation(); state.todos.unshift({ text: "Việc mới #" + (state.todos.length + 1), done: false }); renderAll(); });
    $("#todoList").addEventListener("click", (e) => {
      e.stopPropagation();
      const row = e.target.closest("[data-tog-todo]");
      if (row) { const i = Number(row.dataset.togTodo); state.todos[i].done = !state.todos[i].done; renderAll(); }
    });
    $$(".m-row").forEach(r => r.addEventListener("click", (e) => { e.stopPropagation(); state.sysMetric = Number(r.dataset.m); renderAll(); }));
    $("#btnMascotToggle").addEventListener("click", (e) => { e.stopPropagation(); state.mascotOn = !state.mascotOn; state.expanded = false; state.playing = false; renderAll(); });

    // VI / EN switch
    $$("[data-lang-btn]").forEach(btn => btn.addEventListener("click", () => {
      state.lang = btn.dataset.langBtn;
      $$("[data-lang-btn]").forEach(b => b.classList.toggle("active", b.dataset.langBtn === state.lang));
      $$("[data-vi]").forEach(el => { el.textContent = state.lang === "vi" ? el.dataset.vi : (el.dataset.en || el.dataset.vi); });
      renderAll();
    }));

    let lastTickMs = performance.now();
    let subSec = 0;

    function tickKaraoke(nowMs) {
      const dt = Math.min(0.1, (nowMs - lastTickMs) / 1000);
      lastTickMs = nowMs;
      if (state.playing) {
        subSec += dt;
        if (subSec >= 1.0) {
          const step = Math.floor(subSec);
          subSec -= step;
          state.posSec = (state.posSec + step) % TRACKS[state.trackIdx].dur;
          renderAll();
        }
      }
      const exactLoop = ((state.posSec + (state.playing ? subSec : 0.4)) % 20);
      const tr = TRACKS[state.trackIdx];
      let idx = 0;
      for (let i = 0; i < tr.lyrics.length; i++) {
        if (tr.lyrics[i].t <= exactLoop) idx = i;
      }
      const t0 = tr.lyrics[idx].t;
      const t1 = (idx + 1 < tr.lyrics.length) ? tr.lyrics[idx + 1].t : 20;
      const frac = Math.max(0, Math.min(1, (exactLoop - t0) / Math.max(0.5, (t1 - t0) * 0.88)));
      const kp = (frac * 100).toFixed(1) + "%";
      const ct = $("#knColText"); const cc = $("#knColClock"); if (cc) cc.textContent = hhmm;
      if (ct && ct.classList.contains("karaoke-sweep")) ct.style.setProperty("--kp", kp);
      const curEl = $(".lyr-line.current");
      if (curEl) curEl.style.setProperty("--kp", kp);
      requestAnimationFrame(tickKaraoke);
    }
    requestAnimationFrame(tickKaraoke);

    setInterval(() => {
      if (state.pomoRun && state.pomoSec > 0) { state.pomoSec--; renderAll(); }
    }, 1000);
  }

  document.addEventListener("DOMContentLoaded", () => {
    init();
    renderAll();
    syncRelease();
  });
})();