/* ============================================================================
   [PromptFlow Studio · Shared] HudCanvas 렌더러 — ES 모듈(import "./_pfs/hud-canvas.js")
   ----------------------------------------------------------------------------
   ▸ .NET 은 변경된 프레임(Uint8Array 0..255)만 push — 그리기는 전부 여기 rAF 1회(dirty 시에만).
   ▸ 프레임당 할당 0: 데이터/피크 버퍼·그라디언트는 init/resize 때만 생성, 경로는 beginPath/lineTo 재사용.
   ▸ 화면 밖(IntersectionObserver)·숨김 탭은 rAF 미예약 → GPU/CPU 0. DPR 대응 ResizeObserver.
   ▸ 색은 CSS 토큰(--hud-accent / --hud-accent-2 / --hud-grid)에서 읽음 → 테마는 CSS 한 곳.
   ============================================================================ */
const MODE_BARS = 0, MODE_WAVE = 1, MODE_SPARK = 2;
const PEAK_FALL = 0.012;
const views = new Map();
let nextId = 1;

function readTokens(st) {
    const cs = getComputedStyle(st.canvas);
    st.c1 = cs.getPropertyValue('--hud-accent').trim() || '#34D399';
    st.c2 = cs.getPropertyValue('--hud-accent-2').trim() || '#38BDF8';
    st.cg = cs.getPropertyValue('--hud-grid').trim() || 'rgba(148,163,184,.18)';
}

function resize(st) {
    const r = st.canvas.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.max(1, Math.round(r.width * dpr)), h = Math.max(1, Math.round(r.height * dpr));
    if (w === st.canvas.width && h === st.canvas.height) return;
    st.canvas.width = w; st.canvas.height = h; st.dpr = dpr;
    readTokens(st);
    st.grad = st.ctx.createLinearGradient(0, h, 0, 0);
    st.grad.addColorStop(0, st.c1);
    st.grad.addColorStop(1, st.c2);
    st.dirty = true;
    schedule(st);
}

function schedule(st) {
    if (st.raf || !st.visible || document.hidden) return;
    st.raf = requestAnimationFrame(st.frame);
}

function drawGrid(st, w, h) {
    const ctx = st.ctx;
    ctx.strokeStyle = st.cg; ctx.lineWidth = 1;
    ctx.beginPath();
    for (let k = 1; k < 4; k++) { const y = Math.round(h * k / 4) + 0.5; ctx.moveTo(0, y); ctx.lineTo(w, y); }
    ctx.stroke();
}

function drawBars(st, w, h) {
    const ctx = st.ctx, d = st.data, pk = st.peak, n = d.length;
    const bw = w / n, gap = bw > 3 ? st.dpr : 0;
    let decaying = false;
    ctx.fillStyle = st.grad;
    for (let i = 0; i < n; i++) {
        const v = d[i] / 255, bh = v * h;
        ctx.fillRect(i * bw, h - bh, bw - gap, bh);
        if (v >= pk[i]) pk[i] = v; else { pk[i] = Math.max(v, pk[i] - PEAK_FALL); decaying = true; }
    }
    ctx.fillStyle = st.c2;
    for (let i = 0; i < n; i++) ctx.fillRect(i * bw, h - pk[i] * h - st.dpr, bw - gap, st.dpr * 1.5);
    return decaying;
}

function drawLine(st, w, h, centered) {
    const ctx = st.ctx, d = st.data, n = d.length, step = w / (n - 1);
    ctx.beginPath();
    for (let i = 0; i < n; i++) {
        const y = centered ? h * (1 - d[i] / 255) : h - (d[i] / 255) * (h - st.dpr) - st.dpr * 0.5;
        if (i === 0) ctx.moveTo(0, y); else ctx.lineTo(i * step, y);
    }
    ctx.lineWidth = 1.5 * st.dpr; ctx.lineJoin = 'round';
    ctx.strokeStyle = st.c1; ctx.shadowColor = st.c1; ctx.shadowBlur = 6 * st.dpr;
    ctx.stroke();
    ctx.shadowBlur = 0;
    if (!centered) {   // Sparkline 면 채움
        ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
        ctx.globalAlpha = 0.18; ctx.fillStyle = st.grad; ctx.fill(); ctx.globalAlpha = 1;
    }
}

function makeFrame(st) {
    return function () {
        st.raf = 0;
        if (!st.dirty) return;
        st.dirty = false;
        const w = st.canvas.width, h = st.canvas.height;
        st.ctx.clearRect(0, 0, w, h);
        drawGrid(st, w, h);
        let again = false;
        if (st.mode === MODE_BARS) again = drawBars(st, w, h);
        else drawLine(st, w, h, st.mode === MODE_WAVE);
        if (again) { st.dirty = true; schedule(st); }   // 피크 홀드 낙하 애니메이션
    };
}

export function init(canvas, capacity, mode) {
    if (!canvas || !canvas.getContext) return 0;
    const ctx = canvas.getContext('2d', { alpha: true, desynchronized: true });
    if (!ctx) return 0;
    const st = {
        canvas, ctx, mode: mode | 0, data: new Uint8Array(capacity), peak: new Float32Array(capacity),
        raf: 0, dirty: true, visible: true, dpr: 1, grad: null, c1: '', c2: '', cg: ''
    };
    if (st.mode === MODE_WAVE) st.data.fill(128);
    st.frame = makeFrame(st);
    st.ro = new ResizeObserver(function () { resize(st); });
    st.ro.observe(canvas);
    st.io = new IntersectionObserver(function (es) {
        st.visible = es[es.length - 1].isIntersecting;
        if (st.visible) { st.dirty = true; schedule(st); }
    });
    st.io.observe(canvas);
    resize(st);
    const id = nextId++;
    views.set(id, st);
    return id;
}

export function push(id, bytes) {
    const st = views.get(id);
    if (!st || !bytes) return;
    st.data.set(bytes.length > st.data.length ? bytes.subarray(0, st.data.length) : bytes);
    st.dirty = true;
    schedule(st);
}

export function setMode(id, mode) {
    const st = views.get(id);
    if (!st) return;
    st.mode = mode | 0; st.peak.fill(0); st.dirty = true;
    schedule(st);
}

export function retheme(id) {
    const st = views.get(id);
    if (!st) return;
    st.canvas.width = 0;   // resize 가 토큰/그라디언트 재생성
    resize(st);
}

export function dispose(id) {
    const st = views.get(id);
    if (!st) return;
    if (st.raf) cancelAnimationFrame(st.raf);
    st.ro.disconnect(); st.io.disconnect();
    views.delete(id);
}
