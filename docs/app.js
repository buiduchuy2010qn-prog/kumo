(function () {
  "use strict";

  const $ = (s) => document.querySelector(s);
  const $$ = (s) => Array.from(document.querySelectorAll(s));

  // Generate crisp album / MV cover artworks on HTML5 Canvas
  function makeCoverArt(kind, w, h) {
    const c = document.createElement("canvas");
    c.width = w;
    c.height = h;
    const g = c.getContext("2d");

    if (kind === 0) {
      const bg = g.createLinearGradient(0, 0, w, h);
      bg.addColorStop(0, "#164e63");
      bg.addColorStop(0.55, "#0e7490");
      bg.addColorStop(1, "#0f172a");
      g.fillStyle = bg;
      g.fillRect(0, 0, w, h);

      g.fillStyle = "#e7dfd3";
      g.fillRect(w * 0.08, h * 0.08, w * 0.84, h * 0.84);
      g.fillStyle = "#cbbba8";
      g.fillRect(w * 0.56, h * 0.08, w * 0.36, h * 0.84);

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
      g.fillStyle = sea;
      g.fillRect(w * 0.24, h * 0.25, w * 0.36, h * 0.65);

      g.fillStyle = "#1e293b";
      g.beginPath();
      g.arc(w * 0.41, h * 0.56, w * 0.032, 0, Math.PI * 2);
      g.fill();
      g.fillRect(w * 0.385, h * 0.59, w * 0.055, h * 0.22);
      g.restore();

      g.fillStyle = "#be185d";
      for (let i = 0; i < 9; i++) {
        g.beginPath();
        g.arc(w * (0.68 + (i % 3) * 0.06), h * (0.58 + Math.floor(i / 3) * 0.08), w * 0.022, 0, Math.PI * 2);
        g.fill();
      }
    } else if (kind === 1) {
      const sky = g.createLinearGradient(0, 0, 0, h);
      sky.addColorStop(0, "#1e3a5f");
      sky.addColorStop(0.52, "#64748b");
      sky.addColorStop(0.75, "#d97706");
      sky.addColorStop(1, "#0f172a");
      g.fillStyle = sky;
      g.fillRect(0, 0, w, h);

      g.fillStyle = "rgba(15, 23, 42, 0.88)";
      g.beginPath();
      g.arc(w * 0.36, h * 0.52, w * 0.075, 0, Math.PI * 2);
      g.fill();
      g.fillRect(w * 0.27, h * 0.60, w * 0.19, h * 0.40);
      g.fillStyle = "rgba(30, 41, 59, 0.82)";
      g.beginPath();
      g.arc(w * 0.64, h * 0.56, w * 0.068, 0, Math.PI * 2);
      g.fill();
      g.fillRect(w * 0.56, h * 0.63, w * 0.17, h * 0.37);
    } else {
      const sky = g.createLinearGradient(0, 0, w, h);
      sky.addColorStop(0, "#0c4a6e");
      sky.addColorStop(0.6, "#0284c7");
      sky.addColorStop(1, "#38bdf8");
      g.fillStyle = sky;
      g.fillRect(0, 0, w, h);

      g.fillStyle = "rgba(255,255,255,0.94)";
      const circles = [[0.38, 0.56, 0.13], [0.50, 0.47, 0.17], [0.63, 0.56, 0.12], [0.50, 0.59, 0.14]];
      circles.forEach(([cx, cy, r]) => {
        g.beginPath();
        g.arc(w * cx, h * cy, w * r, 0, Math.PI * 2);
        g.fill();
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
        { t: 0, text: "Ngồi ngẩn ngơ anh hát vu vơ bên hiên chiều" },
        { t: 5, text: "Có đôi lần khẽ cười vì anh nhớ em" },
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
        { t: 0, text: "Em đâu biết đâu, em đâu biết đâu lòng anh" },
        { t: 5, text: "Vì vết thương chỉ là bên ngoài mà thôi" },
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
        { t: 0, text: "Một đám mây đen khẽ đậu ngay cạnh trên cùng" },
        { t: 5, text: "Thu nhỏ vẫn hiện trọn vẹn từng lời bài hát" },
        { t: 10, text: "Tự động thu mảnh khi cửa sổ chạm thanh tab" },
        { t: 15, text: "Giữ nhịp làm việc êm ái suốt cả ngày dài" }
      ]
    }
  ];

  const state = {
    lang: "vi",
    modeKey: "normal",
    expanded: false,
    autoMinimal: false,
    mascotOn: false,
    weatherOn: true,
    desktopCpuMode: false,
    headphonesOn: false,
    micMuted: false,
    micHotkeyName: "Ctrl+Alt+M",
    capsOn: false,
    page: 0,
    trackIdx: 0,
    playing: false,
    lyricsSplit: false,
    posSec: 35,
    muted: false,
    volume: 68,
    brightness: 80,
    sysMetric: 0,
    pomoSec: 1500,
    pomoRun: false,
    qtMin: 5,
    qtSec: 300,
    qtRun: false,
    waterMl: 1500,
    waterGoal: 2000,
    calOpen: false,
    calOffset: 0,
    showSeconds: false,
    fsMode: false,
    fsPeeking: false,
    clipMode: false,
    shelf: ["BaoCao_Q4.pdf", "Slide_Demo.pptx"],
    clips: [
      "https://github.com/buiduchuy2010qn-prog/kumo",
      "irm https://buiduchuy2010qn-prog.github.io/kumo/install.ps1 | iex",
      "Open-Meteo 28°C · Quy Nhơn"
    ],
    todos: [
      { text: "Đồng bộ lời nhạc MV 60 FPS", done: true },
      { text: "Bấm ngày giờ mở Lịch Tháng", done: false }
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
      const tag = d.tag_name || "v1.1.27";
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

  function getClockStr() {
    const now = new Date();
    const hhmm = String(now.getHours()).padStart(2, "0") + ":" + String(now.getMinutes()).padStart(2, "0");
    return {
      now,
      hhmm,
      hdrTime: state.showSeconds ? `${hhmm}:${String(now.getSeconds()).padStart(2, "0")}` : hhmm
    };
  }

  function renderMiniCalendar(now, vi) {
    const grid = $("#calGrid");
    if (!grid) return;
    const viewFirst = new Date(now.getFullYear(), now.getMonth() + state.calOffset, 1);
    const y = viewFirst.getFullYear();
    const m = viewFirst.getMonth();
    const daysInMonth = new Date(y, m + 1, 0).getDate();
    const daysInPrev = new Date(y, m, 0).getDate();
    const startOffset = (viewFirst.getDay() + 6) % 7; // Mon=0..Sun=6
    const monthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    const dowVi = ["Chủ Nhật", "Thứ Hai", "Thứ Ba", "Thứ Tư", "Thứ Năm", "Thứ Sáu", "Thứ Bảy"];
    const dowEn = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    $("#calMonthLbl").textContent = vi ? `Tháng ${m + 1} · ${y}` : `${monthsEn[m]} ${y}`;
    $("#calBigDay").textContent = String(now.getDate());
    $("#calDowLbl").textContent = state.calOffset === 0
      ? (vi ? `${dowVi[now.getDay()]} · Hôm nay` : `${dowEn[now.getDay()]} · Today`)
      : (vi ? "Cuộn chuột đổi tháng" : "Scroll to flip months");

    const hdrs = vi ? ["T2", "T3", "T4", "T5", "T6", "T7", "CN"] : ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    let html = hdrs.map(h => `<span class="cal-dow-h">${h}</span>`).join("");
    for (let cell = 0; cell < 42; cell++) {
      const rel = cell - startOffset + 1;
      let dayNum = rel;
      let inCur = true;
      if (rel < 1) { dayNum = daysInPrev + rel; inCur = false; }
      else if (rel > daysInMonth) { dayNum = rel - daysInMonth; inCur = false; }
      const isToday = inCur && state.calOffset === 0 && dayNum === now.getDate();
      html += `<span class="cal-cell${inCur ? "" : " dim"}${isToday ? " today" : ""}">${dayNum}</span>`;
    }
    grid.innerHTML = html;
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
    const { now, hhmm, hdrTime } = getClockStr();
    const vi = state.lang === "vi";

    // Taskbar clock
    $("#tbTime").textContent = hhmm;
    $("#tbDate").textContent = `${String(now.getDate()).padStart(2, "0")}/${String(now.getMonth() + 1).padStart(2, "0")}/${now.getFullYear()}`;

    // Fullscreen Auto-Hide + Top-Edge Peek (v1.1.25)
    const screenEl = $("#win11Screen");
    if (screenEl) screenEl.classList.toggle("fs-mode", state.fsMode);
    const fsHidden = state.fsMode && !state.fsPeeking;
    notch.classList.toggle("state-fs-hidden", fsHidden);

    // Mode segmented control active button
    const mapBtn = {
      normal: "#modeNormal",
      colMusic: "#modeColMusic",
      colLyrics: "#modeColLyrics",
      expLyrics: "#modeExpLyrics",
      expSys: "#modeExpSys"
    };
    Object.keys(mapBtn).forEach(k => {
      const b = $(mapBtn[k]);
      if (b) {
        const on = state.modeKey === k;
        b.classList.toggle("active", on);
        b.setAttribute("aria-selected", on ? "true" : "false");
      }
    });

    // Notch state classes
    const inOsd = notch.classList.contains("state-osd");
    notch.classList.remove("state-micro", "state-expanded", "wide-lyrics", "collapsed-lyrics");
    notch.style.width = "";

    const showColLyrics = !state.expanded && !state.autoMinimal && state.lyricsSplit && state.playing;
    if (state.expanded) {
      notch.classList.add("state-expanded");
      if (!state.calOpen && state.page === 0 && state.lyricsSplit) notch.classList.add("wide-lyrics");
    } else if (state.autoMinimal && !inOsd) {
      notch.classList.add("state-micro");
    } else if (!inOsd && showColLyrics) {
      const ltxt = curLyric().text;
      notch.classList.add("collapsed-lyrics");
      const dynW = Math.min(506, Math.max(304, Math.round(142 + ltxt.length * 7.6)));
      notch.style.width = dynW + "px";
    } else if (!inOsd && (state.pomoRun || state.qtRun)) {
      notch.style.width = "196px";
    }

    $("#knMascot").classList.toggle("show", !state.expanded && !state.autoMinimal && state.mascotOn && !showColLyrics && !inOsd);

    // Collapsed left slot: Album Cover vs Laptop Battery Ring vs Desktop CPU Ring
    const hasActiveArt = state.playing || showColLyrics;
    $("#knLeftSlot").classList.toggle("has-art", hasActiveArt);
    $("#knMiniCover").src = tr.thumb;
    $("#musCoverImg").src = tr.thumb;

    const cpu = 11 + (state.posSec % 7);
    const batArc = $("#knBatArc");
    const batBolt = $("#knBatBolt");
    if (batArc && batBolt) {
      if (state.desktopCpuMode) {
        batArc.setAttribute("stroke", "#38bdf8");
        batArc.setAttribute("stroke-dasharray", `${Math.max(8, Math.round(cpu * 0.45))} 46`);
        batBolt.style.display = "none";
      } else {
        batArc.setAttribute("stroke", "#4ade80");
        batArc.setAttribute("stroke-dasharray", "39 46");
        batBolt.style.display = "block";
      }
    }

    // Equalizer & 2px Collapsed Song Progress Line (v1.1.23)
    const eqCol = tr.eqCol;
    $$(".kn-eq").forEach(eq => {
      eq.style.setProperty("--eq-col", eqCol);
      eq.classList.toggle("idle", !state.playing && !showColLyrics);
    });
    const songLine = $("#knSongLine");
    const songLineFill = $("#knSongLineFill");
    if (songLine && songLineFill) {
      songLine.classList.toggle("on", state.playing && !state.expanded && !inOsd && !state.autoMinimal);
      songLine.style.setProperty("--eq-col", eqCol);
      songLineFill.style.width = Math.min(100, (state.posSec / tr.dur) * 100).toFixed(1) + "%";
    }

    // Collapsed center text: Uncut Lyrics > Active Timer/Pomodoro > Clock
    const vp = $("#knColViewport");
    const ct = $("#knColText");
    const cc = $("#knColClock");
    if (cc) cc.textContent = hhmm;

    if (showColLyrics) {
      ct.textContent = curLyric().text;
      ct.style.color = "";
      ct.classList.add("karaoke-sweep");
      requestAnimationFrame(() => {
        const overflow = ct.scrollWidth - vp.clientWidth;
        if (overflow > 3) {
          vp.classList.add("scrolling");
          vp.style.setProperty("--pan-x", -(overflow + 8) + "px");
        } else {
          vp.classList.remove("scrolling");
        }
      });
    } else if (state.qtRun) {
      vp.classList.remove("scrolling");
      ct.classList.remove("karaoke-sweep");
      ct.style.color = "#fcd34d";
      ct.textContent = "⏱ " + fmt(state.qtSec);
    } else if (state.pomoRun) {
      vp.classList.remove("scrolling");
      ct.classList.remove("karaoke-sweep");
      ct.style.color = "#fda4af";
      ct.textContent = "🍅 " + fmt(state.pomoSec);
    } else {
      vp.classList.remove("scrolling");
      ct.classList.remove("karaoke-sweep");
      ct.style.color = "";
      ct.textContent = hhmm;
    }

    // Expanded Header: Page Title (or Mini Calendar) + Smart Power/CPU + Weather + Date/Clock
    if (state.calOpen) {
      $("#knTitle1").textContent = vi ? "Lịch " : "Mini ";
      $("#knTitle2").textContent = vi ? "Tháng" : "Calendar";
      $("#knTitle2").style.color = "#38bdf8";
    } else {
      const [p1, p2, pCol] = PAGE_TITLES[state.page];
      $("#knTitle1").textContent = p1;
      $("#knTitle2").textContent = p2;
      $("#knTitle2").style.color = pCol;
    }

    const batHdr = $("#knBatHdr");
    batHdr.classList.toggle("cpu-mode", state.desktopCpuMode);
    batHdr.textContent = state.desktopCpuMode
      ? `CPU ${cpu}% · 4.32 GHz`
      : (vi ? "86% · còn 2g15p" : "86% · 2h15m left");

    const wHdr = $("#knWeatherHdr");
    if (wHdr) wHdr.classList.toggle("hidden", !state.weatherOn);

    const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    $("#knDateHdr").textContent = `${String(now.getDate()).padStart(2, "0")} ${months[now.getMonth()]}`;
    $("#knTimeHdr").textContent = hdrTime;
    const clkBtn = $("#knClockBtn");
    if (clkBtn) clkBtn.classList.toggle("cal-open", state.calOpen);

    const activeKey = state.calOpen ? "cal" : String(state.page);
    $$(".kn-page").forEach(el => el.classList.toggle("active", el.dataset.p === activeKey));
    $$("#knDots i").forEach((el, i) => el.classList.toggle("on", i === state.page));
    if (state.calOpen) renderMiniCalendar(now, vi);

    // Page 0: Music + Lyrics + 1-Click Audio Output Switcher
    $("#musTitle").textContent = tr.title;
    $("#musArtist").textContent = tr.artist + " · YouTube";
    $("#musCur").textContent = fmt(state.posSec);
    $("#musRem").textContent = "-" + fmt(Math.max(0, tr.dur - state.posSec));
    $("#musFill").style.width = Math.min(100, (state.posSec / tr.dur) * 100).toFixed(1) + "%";
    $("#btnPlay").textContent = state.playing ? "⏸" : "▶";
    $("#btnLyrics").classList.toggle("lyr-on", state.lyricsSplit);

    const outBtn = $("#btnAudioOut");
    if (outBtn) {
      if (state.muted) {
        outBtn.textContent = vi ? "🔇 Đã tắt" : "🔇 Muted";
      } else {
        outBtn.textContent = state.headphonesOn
          ? (vi ? "🎧 Tai nghe" : "🎧 Headphone")
          : (vi ? "🔊 Loa" : "🔊 Speaker");
      }
    }

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
      ? (vi ? "▶ Đang phát trên YouTube · Đồng bộ Kumo" : "▶ Playing on YouTube · Synced")
      : (vi ? "⏸ Đang tạm dừng (Bấm để phát)" : "⏸ Paused (Click to play)");
    $$("[data-pick-track]").forEach(b => b.classList.toggle("active", Number(b.dataset.pickTrack) === state.trackIdx));
    renderPlaylist();

    // Page 1: Pomodoro & Quick Timer
    $("#pomoTime").textContent = fmt(state.pomoSec);
    $("#btnPomoRun").textContent = state.pomoRun
      ? (vi ? "⏸ Tạm dừng" : "⏸ Pause")
      : (vi ? "▶ Bắt đầu" : "▶ Start");
    $("#qtText").textContent = state.qtRun
      ? ("⏱ " + fmt(state.qtSec))
      : (vi ? `${state.qtMin} phút` : `${state.qtMin} min`);

    // Page 2: Water
    const wPct = Math.min(100, Math.round((state.waterMl / state.waterGoal) * 100));
    $("#waterReadout").textContent = `${(state.waterMl / 1000).toFixed(1)} / ${(state.waterGoal / 1000).toFixed(1)} L`;
    $("#waterBarFill").style.width = wPct + "%";

    // Page 3: File Shelf + RAM Clipboard History
    const clipBtn = $("#btnClipMode");
    const shelfLbl = $("#shelfModeLbl");
    if (clipBtn) {
      clipBtn.classList.toggle("blue", state.clipMode);
      clipBtn.textContent = `Copy · ${state.clips.length}`;
    }
    if (shelfLbl) {
      shelfLbl.textContent = state.clipMode
        ? (vi ? "Lịch sử Copy trên RAM (bấm để chép lại)" : "RAM Clipboard History (click to re-copy)")
        : (vi ? "Kéo tệp/ảnh từ trình duyệt thả vào đây" : "Drag browser files, images or URLs here");
    }
    $("#shelfDrop").innerHTML = state.clipMode
      ? state.clips.map((c, idx) => `<span class="shelf-pill" data-copy-idx="${idx}">📋 ${c}</span>`).join("")
      : state.shelf.map(f => `<span class="shelf-pill">📄 ${f}</span>`).join("");

    // Page 4: To-Do
    $("#todoHero").textContent = `${state.todos.filter(t => t.done).length}/${state.todos.length}`;
    $("#todoList").innerHTML = state.todos.map((t, i) =>
      `<div data-tog-todo="${i}" style="cursor:pointer;background:rgba(255,255,255,0.1);padding:3px 8px;border-radius:5px;${t.done ? "text-decoration:line-through;opacity:0.65;" : ""}">${t.done ? "✓ " : "○ "}${t.text}</div>`
    ).join("");

    // Page 5: System Monitor
    if (state.sysMetric === 1) {
      $("#sysBig").textContent = "1%";
      $("#sysUnit").textContent = "SSD · Active";
      $("#sysSub").textContent = "Read 84 KB/s · Write 18 KB/s";
    } else if (state.sysMetric === 2) {
      $("#sysBig").textContent = "14.2";
      $("#sysUnit").textContent = "Mbps · Wi-Fi";
      $("#sysSub").textContent = "↓ 14.2 Mbps · ↑ 2.6 Mbps";
    } else {
      $("#sysBig").textContent = cpu + "%";
      $("#sysUnit").textContent = "CPU · 4.32 GHz";
      $("#sysSub").textContent = "RAM 38% · 6.1/16.0 GB";
    }
    $("#btnPcMode").textContent = state.desktopCpuMode
      ? (vi ? "Chế độ PC (CPU)" : "Desktop CPU")
      : (vi ? "Laptop Pin" : "Laptop Battery");
    $$(".m-row").forEach(r => r.classList.toggle("sel", Number(r.dataset.m) === state.sysMetric));
  }

  // Authentic v1.1.22+ HUD: 1-row horizontal pill when collapsed, flat inline badge when expanded!
  function showHud(opts) {
    const notch = $("#kumoNotch");
    const inlineHud = $("#knInlineHud");
    const trackWrap = $("#osdTrackWrap");
    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);

    $("#osdIco").textContent = opts.ico;
    $("#osdIco").style.color = opts.icoCol || "#ffffff";
    $("#osdLbl").textContent = opts.lbl;
    $("#osdLbl").style.color = opts.lblCol || "#ffffff";
    $("#osdVal").textContent = opts.val || "";
    $("#osdVal").style.color = opts.valCol || "#ffffff";

    if (typeof opts.pct === "number") {
      trackWrap.classList.remove("hidden");
      trackWrap.setAttribute("aria-valuenow", String(Math.round(opts.pct)));
      $("#osdFill").style.background = opts.fillBg || "#ffffff";
      $("#osdFill").style.width = opts.pct + "%";
    } else {
      trackWrap.classList.add("hidden");
    }

    if (state.expanded) {
      notch.classList.remove("state-osd");
      inlineHud.textContent = `${opts.ico} ${opts.shortBadge || opts.val || opts.lbl}`;
      inlineHud.classList.add("on");
    } else {
      inlineHud.classList.remove("on");
      notch.style.width = "";
      notch.classList.add("state-osd");
    }

    state.osdTimer = setTimeout(() => {
      inlineHud.classList.remove("on");
      notch.classList.remove("state-osd");
      renderAll();
    }, opts.durMs || 2200);
  }

  function simulateDownload(isStore) {
    clearInterval(state.dlInterval);
    clearTimeout(state.osdTimer);
    let pct = 14;
    const vi = state.lang === "vi";

    const step = () => {
      if (pct >= 100) {
        clearInterval(state.dlInterval);
        showHud({
          ico: "✓",
          icoCol: "#4ade80",
          lbl: (vi ? "Tải xong · " : "Saved · ") + (isStore ? "Spotify Music" : "KumoSetup-v1.1.27.exe"),
          lblCol: "#4ade80",
          fillBg: "linear-gradient(90deg,#10b981,#4ade80)",
          pct: 100,
          val: isStore ? "100%" : "255 KB",
          valCol: "#4ade80",
          shortBadge: isStore ? "Store ✓" : "255 KB ✓",
          durMs: 2500
        });
        return;
      }
      if (isStore) {
        showHud({
          ico: "🛍",
          icoCol: "#38bdf8",
          lbl: "Store · Spotify Music",
          fillBg: "linear-gradient(90deg,#0ea5e9,#67e8f9)",
          pct,
          val: pct + "%",
          shortBadge: `Store ${pct}%`,
          durMs: 5000
        });
      } else {
        showHud({
          ico: "⬇",
          icoCol: "#22d3ee",
          lbl: (vi ? "Chrome · " : "DL · ") + "KumoSetup.exe",
          fillBg: "linear-gradient(90deg,#0ea5e9,#34d399)",
          pct,
          val: "24.8 MB/s",
          valCol: "#34d399",
          shortBadge: `${pct}% · 24.8 MB/s`,
          durMs: 5000
        });
      }
      pct = Math.min(100, pct + 22);
    };
    step();
    state.dlInterval = setInterval(step, 360);
  }

  function setPreset(key) {
    state.modeKey = key;
    state.autoMinimal = false;
    state.fsMode = false;
    state.fsPeeking = false;
    state.calOpen = false;
    $("#winChrome").classList.remove("maximized");
    $("#kumoNotch").classList.remove("state-osd");
    $("#knInlineHud").classList.remove("on");

    if (key === "normal") {
      state.expanded = false;
      state.playing = false;
      state.lyricsSplit = false;
    } else if (key === "colMusic") {
      state.expanded = false;
      state.playing = true;
      state.lyricsSplit = false;
    } else if (key === "colLyrics") {
      state.expanded = false;
      state.playing = true;
      state.lyricsSplit = true;
    } else if (key === "expLyrics") {
      state.expanded = true;
      state.playing = true;
      state.lyricsSplit = true;
      state.page = 0;
    } else if (key === "expSys") {
      state.expanded = true;
      state.page = state.expanded && state.modeKey === "expSys" ? (state.page + 1) % 6 : 5;
    }
    renderAll();
  }

  function initSpotlight() {
    const card = $("#heroSpotlight");
    if (!card) return;
    card.addEventListener("pointermove", (e) => {
      const r = card.getBoundingClientRect();
      card.style.setProperty("--x", (e.clientX - r.left).toFixed(1) + "px");
      card.style.setProperty("--y", (e.clientY - r.top).toFixed(1) + "px");
    });
    card.addEventListener("pointerleave", () => {
      card.style.setProperty("--x", "-999px");
      card.style.setProperty("--y", "-999px");
    });
  }

  function init() {
    initSpotlight();

    const bind = (sel, ev, fn) => {
      const el = $(sel);
      if (el) el.addEventListener(ev, fn);
    };

    // 1-Line PowerShell Installer Copy Button (v1.1.25)
    bind("#btnCopyPs", "click", () => {
      const cmd = "irm https://buiduchuy2010qn-prog.github.io/kumo/install.ps1 | iex";
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(cmd).catch(() => {});
      }
      const btn = $("#btnCopyPs");
      if (btn) {
        btn.classList.add("copied");
        btn.textContent = state.lang === "vi" ? "Đã chép ✓" : "Copied ✓";
        setTimeout(() => {
          btn.classList.remove("copied");
          btn.textContent = state.lang === "vi" ? "Sao chép" : "Copy";
        }, 2000);
      }
    });

    bind("#modeNormal", "click", () => setPreset("normal"));
    bind("#modeColMusic", "click", () => setPreset("colMusic"));
    bind("#modeColLyrics", "click", () => setPreset("colLyrics"));
    bind("#modeExpLyrics", "click", () => setPreset("expLyrics"));
    bind("#modeExpSys", "click", () => {
      state.fsMode = false;
      state.calOpen = false;
      if (state.expanded && state.modeKey === "expSys") {
        state.page = (state.page + 1) % 6;
        renderAll();
      } else {
        state.modeKey = "expSys";
        state.expanded = true;
        state.autoMinimal = false;
        state.page = 5;
        renderAll();
      }
    });

    // Mini Month Calendar triggers (v1.1.25)
    bind("#trigCal", "click", () => {
      state.fsMode = false;
      state.autoMinimal = false;
      state.expanded = true;
      state.calOpen = true;
      state.calOffset = 0;
      $("#kumoNotch").classList.remove("state-osd");
      renderAll();
    });
    bind("#knClockBtn", "click", (e) => {
      e.stopPropagation();
      state.calOpen = !state.calOpen;
      if (state.calOpen) state.calOffset = 0;
      renderAll();
    });
    bind("#knClockBtn", "contextmenu", (e) => {
      e.preventDefault();
      e.stopPropagation();
      state.showSeconds = !state.showSeconds;
      renderAll();
    });
    bind("#btnCalPrev", "click", (e) => { e.stopPropagation(); state.calOffset--; renderAll(); });
    bind("#btnCalNext", "click", (e) => { e.stopPropagation(); state.calOffset++; renderAll(); });
    bind("#btnCalToday", "click", (e) => { e.stopPropagation(); state.calOffset = 0; renderAll(); });
    bind("#knCalPage", "wheel", (e) => {
      e.preventDefault();
      state.calOffset += e.deltaY > 0 ? 1 : -1;
      renderAll();
    });

    // Gentle 20-20-20 Eye-Care 1-Row HUD (v1.1.25)
    bind("#trigEye", "click", () => {
      state.fsMode = false;
      state.expanded = false;
      const vi = state.lang === "vi";
      showHud({
        ico: "👁",
        icoCol: "#34d399",
        lbl: vi ? "Nghỉ mắt 20s · Nhìn xa 6m" : "20-20-20 · Look 20ft away",
        fillBg: "linear-gradient(90deg,#10b981,#34d399)",
        pct: 100,
        val: "20s",
        valCol: "#6ee7b7",
        shortBadge: "20-20-20",
        durMs: 3200
      });
    });

    // True Fullscreen Auto-Hide + Top-Edge Peek (v1.1.25)
    bind("#trigFs", "click", () => {
      state.fsMode = !state.fsMode;
      state.fsPeeking = false;
      if (state.fsMode) {
        state.expanded = false;
        state.playing = true;
        $("#winChrome").classList.remove("minimized");
        $("#kumoNotch").classList.remove("state-osd");
      }
      renderAll();
    });
    bind("#btnExitFs", "click", (e) => {
      e.stopPropagation();
      state.fsMode = false;
      state.fsPeeking = false;
      renderAll();
    });
    bind("#fsPeekSensor", "pointerenter", () => {
      if (state.fsMode) {
        state.fsPeeking = true;
        renderAll();
      }
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && state.fsMode) {
        state.fsMode = false;
        state.fsPeeking = false;
        renderAll();
      }
    });

    bind("#trigDlBrowser", "click", () => { state.fsMode = false; simulateDownload(false); });
    bind("#wcDlBtn", "click", () => simulateDownload(false));
    bind("#trigDlStore", "click", () => { state.fsMode = false; simulateDownload(true); });
    bind("#tbStore", "click", () => simulateDownload(true));

    bind("#trigVol", "click", () => {
      state.fsMode = false;
      state.muted = false;
      state.volume = state.volume >= 100 ? 44 : Math.min(100, state.volume + 8);
      showHud({
        ico: "🔊",
        lbl: state.lang === "vi" ? "Âm lượng ▲" : "Volume ▲",
        fillBg: "#ffffff",
        pct: state.volume,
        val: state.volume + "%"
      });
    });
    bind("#tbVolTray", "click", () => $("#trigVol").click());

    bind("#trigBri", "click", () => {
      state.brightness = state.brightness >= 100 ? 50 : state.brightness + 25;
      showHud({
        ico: "☀",
        icoCol: "#fbbf24",
        lbl: state.lang === "vi" ? "Độ sáng ▲" : "Brightness ▲",
        fillBg: "linear-gradient(90deg,#f59e0b,#fde047)",
        pct: state.brightness,
        val: state.brightness + "%"
      });
    });

    // Global Mic Mute Hotkey HUD (v1.1.23 / v1.1.24)
    bind("#trigMic", "click", () => {
      state.fsMode = false;
      state.micMuted = !state.micMuted;
      const vi = state.lang === "vi";
      showHud({
        ico: "🎙",
        icoCol: state.micMuted ? "#f87171" : "#34d399",
        lbl: state.micMuted
          ? (vi ? "Mic đã tắt" : "Mic Muted")
          : (vi ? "Mic đang bật" : "Mic Live"),
        lblCol: state.micMuted ? "#fca5a5" : "#ffffff",
        val: state.micHotkeyName,
        valCol: state.micMuted ? "#f87171" : "#34d399",
        shortBadge: state.micMuted ? (vi ? "Mic Tắt" : "Mic Off") : (vi ? "Mic Bật" : "Mic On")
      });
    });

    // Caps Lock 1-Row HUD (v1.1.23)
    bind("#trigCaps", "click", () => {
      state.fsMode = false;
      state.capsOn = !state.capsOn;
      const vi = state.lang === "vi";
      showHud({
        ico: "⇪",
        icoCol: state.capsOn ? "#38bdf8" : "#94a3b8",
        lbl: "Caps Lock",
        val: state.capsOn ? (vi ? "ĐANG BẬT" : "ON") : (vi ? "ĐÃ TẮT" : "OFF"),
        valCol: state.capsOn ? "#38bdf8" : "#cbd5e1",
        shortBadge: state.capsOn ? "Caps ON" : "Caps OFF"
      });
    });

    // Settings Window & Tabs
    const toggleSet = () => {
      state.fsMode = false;
      $("#kumoSettingsWin").classList.toggle("open");
      renderAll();
    };
    bind("#trigSettings", "click", toggleSet);
    bind("#knOpenSettings", "click", (e) => { e.stopPropagation(); toggleSet(); });
    bind("#tbKumo", "click", toggleSet);
    bind("#deskKumo", "click", toggleSet);
    bind("#kswClose", "click", () => $("#kumoSettingsWin").classList.remove("open"));

    $$("#kswNav button").forEach(btn => btn.addEventListener("click", () => {
      const idx = btn.dataset.stab;
      $$("#kswNav button").forEach(b => b.classList.toggle("on", b.dataset.stab === idx));
      $$("[data-spane]").forEach(p => p.classList.toggle("active", p.dataset.spane === idx));
    }));

    $$(".ksw-tog").forEach(t => t.addEventListener("click", () => {
      t.classList.toggle("off");
      if (t.id === "togWeatherCfg") {
        state.weatherOn = !t.classList.contains("off");
        renderAll();
      }
    }));

    $$("#micHkSeg button").forEach(btn => btn.addEventListener("click", () => {
      $$("#micHkSeg button").forEach(b => b.classList.remove("on"));
      btn.classList.add("on");
      state.micHotkeyName = btn.textContent.trim();
    }));

    bind("#trigWall", "click", () => {
      const sc = $("#win11Screen");
      sc.dataset.wall = sc.dataset.wall === "dark" ? "light" : "dark";
    });

    // Notch click to expand / outside click to collapse
    const notch = $("#kumoNotch");
    notch.addEventListener("click", () => {
      if (!state.expanded) {
        state.expanded = true;
        notch.classList.remove("state-osd");
        renderAll();
      }
    });
    notch.addEventListener("pointerleave", () => {
      if (state.fsMode && state.fsPeeking && !state.expanded) {
        state.fsPeeking = false;
        renderAll();
      }
    });
    $("#win11Screen").addEventListener("click", (e) => {
      if (e.target.closest("#kumoNotch") || e.target.closest("#kumoSettingsWin") || e.target.closest("#fsHintPill")) return;
      if (state.expanded) {
        state.expanded = false;
        if (state.fsMode) state.fsPeeking = false;
        renderAll();
      }
    });

    // Notch header & page navigation
    bind("#knBatHdr", "click", (e) => {
      e.stopPropagation();
      state.desktopCpuMode = !state.desktopCpuMode;
      renderAll();
    });
    bind("#btnPcMode", "click", (e) => {
      e.stopPropagation();
      state.desktopCpuMode = !state.desktopCpuMode;
      renderAll();
    });

    bind("#btnPrevPage", "click", (e) => { e.stopPropagation(); state.calOpen = false; state.page = (state.page + 5) % 6; renderAll(); });
    bind("#btnNextPage", "click", (e) => { e.stopPropagation(); state.calOpen = false; state.page = (state.page + 1) % 6; renderAll(); });
    $$("#knDots i").forEach((d, i) => d.addEventListener("click", (e) => { e.stopPropagation(); state.calOpen = false; state.page = i; renderAll(); }));

    // Music controls + 1-Click Speaker <-> Headphones Switcher
    $("#btnPlay").addEventListener("click", (e) => { e.stopPropagation(); state.playing = !state.playing; renderAll(); });
    $("#btnPrevTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + 2) % 3; state.posSec = 0; renderAll(); });
    $("#btnNextTrack").addEventListener("click", (e) => { e.stopPropagation(); state.trackIdx = (state.trackIdx + 1) % 3; state.posSec = 0; renderAll(); });
    $("#btnLyrics").addEventListener("click", (e) => { e.stopPropagation(); state.lyricsSplit = !state.lyricsSplit; renderAll(); });

    $("#btnAudioOut").addEventListener("click", (e) => {
      e.stopPropagation();
      state.muted = false;
      state.headphonesOn = !state.headphonesOn;
      const vi = state.lang === "vi";
      showHud({
        ico: state.headphonesOn ? "🎧" : "🔊",
        icoCol: "#38bdf8",
        lbl: state.headphonesOn ? (vi ? "Đã chuyển · Tai nghe" : "Output · Headphones") : (vi ? "Đã chuyển · Loa ngoài" : "Output · Speakers"),
        val: state.headphonesOn ? (vi ? "Tai nghe" : "Headphone") : (vi ? "Loa" : "Speaker")
      });
      renderAll();
    });
    $("#btnAudioOut").addEventListener("contextmenu", (e) => {
      e.preventDefault();
      e.stopPropagation();
      state.muted = !state.muted;
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

    // YouTube Search & Playlist inside Chrome
    const doSearch = () => {
      const q = ($("#ytSearchInput").value || "").toLowerCase();
      if (q.includes("vết") || q.includes("vet") || q.includes("fishy")) state.trackIdx = 1;
      else if (q.includes("mây") || q.includes("may") || q.includes("kumo")) state.trackIdx = 2;
      else state.trackIdx = 0;
      state.playing = true;
      state.posSec = 2;
      if (state.modeKey === "normal") {
        state.modeKey = "colLyrics";
        state.lyricsSplit = true;
      }
      renderAll();
    };
    $("#ytSearchBtn").addEventListener("click", doSearch);
    $("#ytSearchInput").addEventListener("keydown", (e) => { if (e.key === "Enter") doSearch(); });
    $$("[data-pick-track]").forEach(b => b.addEventListener("click", () => {
      state.trackIdx = Number(b.dataset.pickTrack);
      state.playing = true;
      state.posSec = 5;
      if (state.modeKey === "normal") {
        state.modeKey = "colLyrics";
        state.lyricsSplit = true;
      }
      renderAll();
    }));
    $("#ytPlaylist").addEventListener("click", (e) => {
      const it = e.target.closest("[data-sel-track]");
      if (it) {
        state.trackIdx = Number(it.dataset.selTrack);
        state.playing = true;
        state.posSec = 5;
        renderAll();
      }
    });
    $("#ytVideoBox").addEventListener("click", () => {
      state.playing = !state.playing;
      if (state.playing && state.modeKey === "normal") state.modeKey = "colMusic";
      renderAll();
    });

    // Page 1: Pomodoro + Quick Timer
    $("#btnPomoRun").addEventListener("click", (e) => { e.stopPropagation(); state.pomoRun = !state.pomoRun; renderAll(); });
    $("#btnPomoMinus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec = Math.max(60, state.pomoSec - 300); renderAll(); });
    $("#btnPomoPlus").addEventListener("click", (e) => { e.stopPropagation(); state.pomoSec += 300; renderAll(); });

    $("#btnQtMinus").addEventListener("click", (e) => {
      e.stopPropagation();
      state.qtMin = Math.max(1, state.qtMin - 1);
      state.qtSec = state.qtMin * 60;
      renderAll();
    });
    $("#btnQtPlus").addEventListener("click", (e) => {
      e.stopPropagation();
      state.qtMin = Math.min(180, state.qtMin + 5);
      state.qtSec = state.qtMin * 60;
      renderAll();
    });
    $("#qtText").addEventListener("click", (e) => {
      e.stopPropagation();
      state.qtRun = !state.qtRun;
      if (state.qtRun && state.qtSec <= 0) state.qtSec = state.qtMin * 60;
      renderAll();
    });

    // Page 2: Water
    $("#btnWaterAdd").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl += 250; renderAll(); });
    $("#btnWaterSub").addEventListener("click", (e) => { e.stopPropagation(); state.waterMl = Math.max(0, state.waterMl - 250); renderAll(); });

    // Page 3: File Shelf + Drag & Drop + Clipboard History
    $("#btnShelfAdd").addEventListener("click", (e) => {
      e.stopPropagation();
      state.clipMode = false;
      if (state.shelf.length < 5) state.shelf.push("TaiLieu_" + (state.shelf.length + 1) + ".zip");
      renderAll();
    });
    $("#btnClipMode").addEventListener("click", (e) => {
      e.stopPropagation();
      state.clipMode = !state.clipMode;
      renderAll();
    });
    $("#shelfDrop").addEventListener("click", (e) => {
      e.stopPropagation();
      const cp = e.target.closest("[data-copy-idx]");
      if (cp) {
        const txt = state.clips[Number(cp.dataset.copyIdx)];
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(txt).catch(() => {});
        }
        showHud({
          ico: "✓",
          icoCol: "#34d399",
          lbl: state.lang === "vi" ? "Đã chép lại vào bộ nhớ tạm" : "Copied to clipboard",
          shortBadge: state.lang === "vi" ? "Đã chép ✓" : "Copied ✓"
        });
      }
    });
    const shelfZone = $("#shelfZone");
    if (shelfZone) {
      shelfZone.addEventListener("dragover", (e) => { e.preventDefault(); });
      shelfZone.addEventListener("drop", (e) => {
        e.preventDefault();
        e.stopPropagation();
        state.clipMode = false;
        const files = e.dataTransfer && e.dataTransfer.files;
        if (files && files.length > 0) {
          state.shelf.unshift(files[0].name);
        } else {
          const uri = (e.dataTransfer && (e.dataTransfer.getData("text/uri-list") || e.dataTransfer.getData("text/plain"))) || "Link_Tu_TrinhDuyet.url";
          state.shelf.unshift(uri.slice(0, 26));
        }
        state.shelf = state.shelf.slice(0, 5);
        renderAll();
      });
    }

    // Page 4 & 5
    $("#btnTodoAdd").addEventListener("click", (e) => {
      e.stopPropagation();
      state.todos.unshift({ text: (state.lang === "vi" ? "Việc mới #" : "Task #") + (state.todos.length + 1), done: false });
      renderAll();
    });
    $("#todoList").addEventListener("click", (e) => {
      e.stopPropagation();
      const row = e.target.closest("[data-tog-todo]");
      if (row) {
        const i = Number(row.dataset.togTodo);
        state.todos[i].done = !state.todos[i].done;
        renderAll();
      }
    });
    $$(".m-row").forEach(r => r.addEventListener("click", (e) => { e.stopPropagation(); state.sysMetric = Number(r.dataset.m); renderAll(); }));
    $("#btnMascotToggle").addEventListener("click", (e) => {
      e.stopPropagation();
      state.mascotOn = !state.mascotOn;
      state.expanded = false;
      state.playing = false;
      renderAll();
    });

    // Bilingual VI / EN switch
    $$("[data-lang-btn]").forEach(btn => btn.addEventListener("click", () => {
      state.lang = btn.dataset.langBtn;
      $$("[data-lang-btn]").forEach(b => b.classList.toggle("active", b.dataset.langBtn === state.lang));
      $$("[data-vi]").forEach(el => {
        el.textContent = state.lang === "vi" ? el.dataset.vi : (el.dataset.en || el.dataset.vi);
      });
      renderAll();
    }));

    // Silky 60 FPS Karaoke Word Sweep Loop (zero undefined globals!)
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

      const ct = $("#knColText");
      if (ct && ct.classList.contains("karaoke-sweep")) ct.style.setProperty("--kp", kp);
      const curEl = $(".lyr-line.current");
      if (curEl) curEl.style.setProperty("--kp", kp);
      requestAnimationFrame(tickKaraoke);
    }
    requestAnimationFrame(tickKaraoke);

    setInterval(() => {
      let changed = false;
      if (state.pomoRun && state.pomoSec > 0) { state.pomoSec--; changed = true; }
      if (state.qtRun && state.qtSec > 0) {
        state.qtSec--;
        changed = true;
        if (state.qtSec === 0) {
          state.qtRun = false;
          showHud({
            ico: "⏰",
            icoCol: "#fbbf24",
            lbl: state.lang === "vi" ? "Hết giờ hẹn nhanh!" : "Quick Timer Done!",
            val: "00:00",
            valCol: "#fcd34d",
            durMs: 4000
          });
        }
      }
      if (changed) renderAll();
    }, 1000);
  }

  document.addEventListener("DOMContentLoaded", () => {
    init();
    const q = new URLSearchParams(window.location.search).get("view");
    if (q === "colLyrics") setPreset("colLyrics");
    else if (q === "expLyrics") setPreset("expLyrics");
    else if (q === "calendar") { state.expanded = true; state.calOpen = true; }
    else if (q === "pomo") { state.expanded = true; state.page = 1; }
    else if (q === "shelf") { state.expanded = true; state.page = 3; state.clipMode = true; }
    else if (q === "settings") { $("#kumoSettingsWin").classList.add("open"); }
    renderAll();
    if (q === "hud") simulateDownload(false);
    syncRelease();
  });
})();
