/* ============================================================================
   [PromptFlow Studio] Web Audio 실시간 VU 분석기 — LiveVuMeter / CutVuMeter 물리 반응 구동
   ----------------------------------------------------------------------------
   ▸ bind(meterId, mediaId): 미디어 요소의 play 시 분석 시작 / pause·ended 시 중지 — <b>재생 중인 요소만</b> AnalyserNode 가동(컷별 다수 미터 효율).
   ▸ createMediaElementSource 는 요소당 1회(WeakMap 캐시) · source→destination 연결로 오디오 유지.
   ▸ rAF 로 .vu-seg 높이/투명도를 직접 구동(.NET 왕복 없음) + 클리핑(≥0.96) 시 data-clip 표기.
   ▸ 실패(미지원/자동재생 정책/요소 없음)는 모두 graceful — 미터는 CSS 폴백 애니메이션 유지.
   ============================================================================ */
window.pfAudio = (function () {
    var running = {};             // meterId -> { analyser, raf }
    var bound = {};               // meterId -> { media, onplay, onstop }
    var srcCache = new WeakMap(); // mediaEl -> { ctx, src }

    function ensureSource(media) {
        var cached = srcCache.get(media);
        if (cached) return cached;
        var AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) return null;
        var ctx = new AC();
        var src = ctx.createMediaElementSource(media);
        src.connect(ctx.destination);
        var rec = { ctx: ctx, src: src };
        srcCache.set(media, rec);
        return rec;
    }

    function startAnalysis(meterId, media) {
        try {
            stopAnalysis(meterId);
            var meter = document.getElementById(meterId);
            if (!meter || !media) return false;
            var segs = meter.querySelectorAll('.vu-seg');
            if (!segs.length) return false;
            var s = ensureSource(media);
            if (!s) return false;
            if (s.ctx.state === 'suspended') { s.ctx.resume().catch(function () { }); }

            var analyser = s.ctx.createAnalyser();
            analyser.fftSize = 64;
            analyser.smoothingTimeConstant = 0.78;
            s.src.connect(analyser);
            var data = new Uint8Array(analyser.frequencyBinCount), n = segs.length;
            meter.setAttribute('data-js', '1');
            var st = { analyser: analyser, raf: 0 };
            running[meterId] = st;

            (function tick() {
                analyser.getByteFrequencyData(data);
                var peak = 0;
                for (var i = 0; i < n; i++) {
                    var v = data[Math.min(data.length - 1, Math.floor(Math.pow(i / n, 1.35) * data.length))] / 255;
                    if (v > peak) peak = v;
                    segs[i].style.height = Math.max(12, v * 100).toFixed(1) + '%';
                    segs[i].style.opacity = (0.32 + v * 0.68).toFixed(2);
                }
                if (peak >= 0.96) meter.setAttribute('data-clip', '1'); else meter.removeAttribute('data-clip');
                st.raf = requestAnimationFrame(tick);
            })();
            return true;
        } catch (e) { return false; }
    }

    function stopAnalysis(meterId) {
        var st = running[meterId];
        if (st) {
            if (st.raf) cancelAnimationFrame(st.raf);
            try { st.analyser.disconnect(); } catch (e) { }
            delete running[meterId];
        }
        var m = document.getElementById(meterId);
        if (m) { m.removeAttribute('data-js'); m.removeAttribute('data-clip'); }
    }

    // 재생 시에만 분석하도록 미디어 이벤트에 바인딩(다수 컷 미터 효율)
    function bind(meterId, mediaId) {
        try {
            unbind(meterId);
            var media = document.getElementById(mediaId);
            if (!media) return false;
            var onplay = function () { startAnalysis(meterId, media); };
            var onstop = function () { stopAnalysis(meterId); };
            media.addEventListener('play', onplay);
            media.addEventListener('pause', onstop);
            media.addEventListener('ended', onstop);
            bound[meterId] = { media: media, onplay: onplay, onstop: onstop };
            if (!media.paused && !media.ended) startAnalysis(meterId, media);  // 이미 재생 중이면 즉시
            return true;
        } catch (e) { return false; }
    }

    function unbind(meterId) {
        try {
            stopAnalysis(meterId);
            var b = bound[meterId];
            if (b && b.media) {
                b.media.removeEventListener('play', b.onplay);
                b.media.removeEventListener('pause', b.onstop);
                b.media.removeEventListener('ended', b.onstop);
            }
            delete bound[meterId];
        } catch (e) { }
        return true;
    }

    // 하위호환 별칭(v1.6.0 start/stop → bind/unbind)
    return { bind: bind, unbind: unbind, start: bind, stop: unbind };
})();
