// [PromptFlow Studio] 🎞 Pluto 실시간 모션 프리뷰어 — FFmpeg 없이 60fps 캔버스 Ken Burns + Web Audio 동기 + 이중 자막.
//   - 입력: PlutoMotionCompiler.ExportPreviewContractJson(…) (정규화 세그먼트 · 이징 다항식 계수) + PlutoMotionPreviewModel.SubtitlesJson(…).
//   - 수학: evalInto() 는 C# PlutoMotionPreviewModel.Evaluate 와 줄 단위 동일(이징 = Horner 다항식 · 뷰포트 클램프).
//   - 클록: 재생 중 마스터는 AudioContext.currentTime(렌더 퀀텀 사이는 performance.now() 로 보간) → UI 스레드 스터터에도 A/V 드리프트 0.
//           오디오 불가(정책/장치 없음) 시 performance.now() 단독 클록으로 폴백.
//   - 무팝: 모든 소스는 전용 GainNode 경유 — 시작 6ms 페이드 인 · 정지 8ms 페이드 아웃 후 stop(DC 클릭 없음).
//   - 프레임 루프 무할당: 샘플 객체 재사용 · 자막 줄바꿈/폰트는 갱신 시 1회 계산 · 라벨 문자열은 1/100초 변할 때만 생성.
(function () {
    'use strict';

    var W = 1080, H = 1920;
    var SUB_SCALE = W / 480;                         // 논리 1080×1920 기준 자막 배율
    var KO_PX = Math.round(24 * SUB_SCALE);          // 24pt × scale
    var EN_PX = Math.round(16 * SUB_SCALE);          // 16pt × scale
    var KO_FONT = '600 ' + KO_PX + 'px "Malgun Gothic","Apple SD Gothic Neo","Noto Sans KR",sans-serif';
    var EN_FONT = 'italic 400 ' + EN_PX + 'px Georgia,"Times New Roman",serif';
    var KO_COLOR = '#FFF4D6';                        // 웜 크림
    var EN_COLOR = 'rgba(255,255,255,0.72)';         // 반투명 화이트
    var SUB_MAX_W = W * 0.86, SUB_BOTTOM = H * 0.86;
    var FADE_IN = 0.006, FADE_OUT = 0.008, STOP_PAD = 0.012, LEAD = 0.03;
    var DEFAULT_DURATION = 15;
    var SFX_CUES = [                                 // PlutoSfxDirector 기본 15초 큐 시트와 동일 오프셋/볼륨
        { kind: 0, at: 0.8, vol: 0.7 },              // 📸 셔터
        { kind: 1, at: 5.5, vol: 0.6 },              // ✨ 차임
        { kind: 2, at: 13.2, vol: 0.55 }             // 🌙 잠자리 벨
    ];

    var instances = new Map();

    function clamp(v, a, b) { return v < a ? a : (v > b ? b : v); }

    // ───────────── 🧮 궤적 평가(C# PlutoMotionPreviewModel.Evaluate 미러) ─────────────
    function evalInto(c, t, out) {
        var segs = c.segments, n = segs.length;
        var zoom = c.minZoom, fx = 0.5, fy = 0.5;
        if (n > 0) {
            if (!isFinite(t)) t = 0;
            var i = 0;
            while (i < n - 1 && t > segs[i].endSec) i++;
            var s = segs[i];
            var span = s.endSec - s.startSec;
            var u = span > 0 ? clamp((t - s.startSec) / span, 0, 1) : 1;
            var k = s.ease, e = 0;
            for (var j = k.length - 1; j >= 0; j--) e = e * u + k[j];     // Horner — S(u) = 3u² − 2u³ 등
            zoom = s.z0 + (s.z1 - s.z0) * e;
            fx = s.x0 + (s.x1 - s.x0) * e;
            fy = s.y0 + (s.y1 - s.y0) * e;
            if (s.orbitX > 0) {
                var env = Math.sin(Math.PI * u), ang = s.orbitOmega * u;
                fx += s.orbitX * env * Math.cos(ang);
                fy += s.orbitY * env * Math.sin(ang);
            }
            out.segment = i;
        } else out.segment = -1;
        zoom = Math.max(c.minZoom, Math.min(c.maxZoom, zoom));
        var view = 1 / zoom;
        out.zoom = zoom; out.fx = fx; out.fy = fy;
        out.x = Math.max(0, Math.min(1 - view, fx - view / 2));           // 레터박스/블랙 보더 차단 클램프
        out.y = Math.max(0, Math.min(1 - view, fy - view / 2));
        return out;
    }

    function parseContract(json) {
        var c = null;
        try { c = typeof json === 'string' ? JSON.parse(json) : json; } catch (e) { c = null; }
        if (!c || c.version !== 1 || !Array.isArray(c.segments)) c = { version: 1, durationSec: 0, minZoom: 1, maxZoom: 10, segments: [] };
        var ok = [];
        for (var i = 0; i < c.segments.length; i++) {
            var s = c.segments[i];
            if (s && isFinite(s.startSec) && isFinite(s.endSec) && s.endSec > s.startSec && Array.isArray(s.ease) &&
                isFinite(s.z0) && isFinite(s.z1) && isFinite(s.x0) && isFinite(s.y0) && isFinite(s.x1) && isFinite(s.y1)) {
                s.orbitX = +s.orbitX || 0; s.orbitY = +s.orbitY || 0; s.orbitOmega = +s.orbitOmega || 0;
                ok.push(s);
            }
        }
        c.segments = ok;
        var cues = [];                               // 🥁 비트 스냅 SFX 큐(계약 sfx) — 없거나 무효 → 기본 큐 시트
        if (Array.isArray(c.sfx)) {
            for (var j = 0; j < c.sfx.length; j++) {
                var f = c.sfx[j];
                if (f && (f.kind === 0 || f.kind === 1 || f.kind === 2) && isFinite(f.at) && f.at >= 0 && isFinite(f.vol)) cues.push({ kind: f.kind, at: +f.at, vol: clamp(+f.vol, 0, 1) });
            }
        }
        c.sfx = cues.length > 0 ? cues : SFX_CUES;
        c.minZoom = isFinite(c.minZoom) && c.minZoom > 0 ? c.minZoom : 1;
        c.maxZoom = isFinite(c.maxZoom) && c.maxZoom >= c.minZoom ? c.maxZoom : 10;
        return c;
    }

    // ───────────── 💬 자막(갱신 시 1회 줄바꿈 → 프레임 루프는 fillText 만) ─────────────
    function wrapLines(ctx, font, text, maxW) {
        var lines = [];
        if (!text) return lines;
        ctx.font = font;
        var words = String(text).split(/\s+/), cur = '';
        for (var i = 0; i < words.length; i++) {
            var w = words[i];
            if (!w) continue;
            var next = cur ? cur + ' ' + w : w;
            if (ctx.measureText(next).width <= maxW) { cur = next; continue; }
            if (cur) lines.push(cur);
            cur = w;
            while (ctx.measureText(cur).width > maxW && cur.length > 1) {   // 공백 없는 긴 한글 → 글자 단위 분할
                var cut = cur.length - 1;
                while (cut > 1 && ctx.measureText(cur.substring(0, cut)).width > maxW) cut--;
                lines.push(cur.substring(0, cut));
                cur = cur.substring(cut);
            }
        }
        if (cur) lines.push(cur);
        return lines;
    }

    function parseSubtitles(inst, json) {
        var d = null;
        try { d = typeof json === 'string' ? JSON.parse(json) : json; } catch (e) { d = null; }
        var acts = [];
        var src = d && Array.isArray(d.acts) ? d.acts : [];
        for (var i = 0; i < src.length; i++) {
            var a = src[i];
            if (!a || !isFinite(a.startSec) || !isFinite(a.endSec) || a.endSec <= a.startSec) continue;
            acts.push({
                startSec: +a.startSec, endSec: +a.endSec,
                ko: wrapLines(inst.ctx, KO_FONT, a.ko, SUB_MAX_W),
                en: wrapLines(inst.ctx, EN_FONT, a.en, SUB_MAX_W)
            });
        }
        inst.acts = acts;
        inst.crossfade = d && isFinite(d.crossfadeSec) && d.crossfadeSec > 0 ? +d.crossfadeSec : 0.15;
    }

    // C# PlutoMotionPreviewModel.SubtitleAlpha 미러 — 경계 중심 선형 크로스페이드(두 막 합 = 1).
    function subtitleAlpha(acts, i, t, xf) {
        var a = acts[i], h = xf / 2;
        var fin = i === 0 ? (t >= a.startSec ? 1 : 0) : clamp((t - (a.startSec - h)) / xf, 0, 1);
        var fout = i === acts.length - 1 ? (t <= a.endSec ? 1 : 0) : clamp(((a.endSec + h) - t) / xf, 0, 1);
        return fin * fout;
    }

    function drawSubtitles(inst, t) {
        var acts = inst.acts, ctx = inst.ctx;
        if (acts.length === 0) return;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'alphabetic';
        var koLh = KO_PX * 1.32, enLh = EN_PX * 1.36, gap = EN_PX * 0.5;
        for (var i = 0; i < acts.length; i++) {
            var alpha = subtitleAlpha(acts, i, t, inst.crossfade);
            if (alpha <= 0.003) continue;
            var a = acts[i];
            var blockH = a.ko.length * koLh + (a.en.length > 0 ? gap + a.en.length * enLh : 0);
            var y = SUB_BOTTOM - blockH + KO_PX;
            ctx.globalAlpha = alpha;
            ctx.font = KO_FONT;
            ctx.fillStyle = KO_COLOR;
            ctx.shadowColor = 'rgba(0,0,0,0.55)';
            ctx.shadowBlur = 5 * SUB_SCALE;
            ctx.shadowOffsetX = 0;
            ctx.shadowOffsetY = 1.5 * SUB_SCALE;
            for (var k = 0; k < a.ko.length; k++) { ctx.fillText(a.ko[k], W / 2, y); y += koLh; }
            if (a.en.length > 0) {
                y += gap - (KO_PX - EN_PX);
                ctx.font = EN_FONT;
                ctx.fillStyle = EN_COLOR;
                ctx.shadowColor = 'rgba(0,0,0,0.45)';
                ctx.shadowBlur = 3 * SUB_SCALE;
                ctx.shadowOffsetY = SUB_SCALE;
                for (var m = 0; m < a.en.length; m++) { ctx.fillText(a.en[m], W / 2, y); y += enLh; }
            }
        }
        ctx.globalAlpha = 1;
        ctx.shadowColor = 'transparent';
        ctx.shadowBlur = 0;
        ctx.shadowOffsetY = 0;
    }

    // ───────────── 🎨 렌더 ─────────────
    function draw(inst) {
        var ctx = inst.ctx;
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.globalAlpha = 1;
        ctx.fillStyle = '#0b0b12';
        ctx.fillRect(0, 0, W, H);
        if (!inst.img) return;                                            // 빈 상태 — 안내 문구는 Blazor 오버레이
        var s = evalInto(inst.contract, inst.t, inst.sample);
        var z = s.zoom;
        ctx.imageSmoothingEnabled = true;                                 // 바이리니어 스무딩
        ctx.imageSmoothingQuality = 'high';
        ctx.setTransform(z, 0, 0, z, -s.x * W * z, -s.y * H * z);         // 9:16 뷰포트 아핀(scale · translate)
        ctx.drawImage(inst.img, inst.coverX, inst.coverY, inst.coverW, inst.coverH);
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        drawSubtitles(inst, inst.t);
    }

    function syncUi(inst) {
        var cs = Math.round(inst.t * 100);
        if (cs === inst.lastCs && inst.duration === inst.lastDur) return;
        inst.lastCs = cs; inst.lastDur = inst.duration;
        if (inst.label) inst.label.textContent = (cs / 100).toFixed(2) + 's / ' + inst.duration.toFixed(2) + 's';
        if (inst.scrub && !inst.scrubbing) inst.scrub.value = (cs / 100).toFixed(2);
    }

    function invalidate(inst) {
        if (!inst.alive || inst.raf) return;
        inst.raf = requestAnimationFrame(inst.tick);
    }

    // ───────────── ⏱ 클록 ─────────────
    // 오디오 마스터: currentTime 은 렌더 퀀텀(≈2.9ms) 단위로만 전진 → 값이 바뀐 순간의 performance.now() 를 기준으로 사이를 보간.
    function clockNow(inst) {
        var p = performance.now() / 1000;
        if (inst.clockMode !== 1) return p;
        var ct = inst.ac.currentTime;
        if (ct !== inst.lastCt) { inst.lastCt = ct; inst.lastCtPerf = p; }
        var ahead = p - inst.lastCtPerf;
        return ct + (ahead > 0.05 ? 0.05 : ahead);                        // 컨텍스트 정지 시 폭주 방지
    }

    function frame(inst) {
        inst.raf = 0;
        if (!inst.alive) return;
        if (inst.playing && !inst.scrubbing) {
            var t = inst.anchorT + Math.max(0, clockNow(inst) - inst.anchorClock) * inst.rate;
            if (t >= inst.duration) { inst.t = inst.duration; pause(inst); }
            else inst.t = t;
        }
        draw(inst);
        syncUi(inst);
        if (inst.playing) inst.raf = requestAnimationFrame(inst.tick);
    }

    // ───────────── 🔊 오디오 ─────────────
    function synthSfx(ac, kind) {
        var sr = ac.sampleRate;
        var dur = kind === 0 ? 0.22 : (kind === 1 ? 1.4 : 1.8);          // 벨 13.2s + 1.8s = 15.0s
        var n = Math.floor(sr * dur);
        var buf = ac.createBuffer(1, n, sr);
        var d = buf.getChannelData(0);
        var i, t, seed = 0x2F6E2B1 + kind;
        if (kind === 0) {                                                 // 셔터 — 노이즈 클릭 2회(미러 업/다운)
            var prev = 0;
            for (i = 0; i < n; i++) {
                t = i / sr;
                seed = (seed * 1664525 + 1013904223) >>> 0;
                var noise = seed / 2147483648 - 1;
                var env = Math.exp(-t * 90) + (t > 0.085 ? 0.8 * Math.exp(-(t - 0.085) * 70) : 0);
                var hp = noise - prev; prev = noise;                      // 1차 하이패스 → DC 성분 제거
                d[i] = 0.5 * hp * env;
            }
        } else if (kind === 1) {                                          // 차임 — 오르골 3음 스태거
            var notes = [1318.51, 1567.98, 2093.0];
            for (i = 0; i < n; i++) {
                t = i / sr;
                var v = 0;
                for (var k = 0; k < 3; k++) {
                    var tt = t - k * 0.09;
                    if (tt > 0) v += Math.sin(2 * Math.PI * notes[k] * tt) * Math.exp(-tt * 4.2);
                }
                d[i] = 0.3 * v;
            }
        } else {                                                          // 잠자리 벨 — 비조화 배음
            var f0 = 392.0, ratios = [1, 2.0, 2.76, 5.4], amps = [1, 0.5, 0.3, 0.15], decs = [2.2, 3.0, 4.2, 6.0];
            for (i = 0; i < n; i++) {
                t = i / sr;
                var b = 0;
                for (var q = 0; q < 4; q++) b += amps[q] * Math.sin(2 * Math.PI * f0 * ratios[q] * t) * Math.exp(-t * decs[q]);
                d[i] = 0.32 * b;
            }
        }
        var edge = Math.min(n >> 1, Math.floor(sr * 0.004));              // 양 끝 4ms 램프 → 버퍼 경계 0 수렴
        for (i = 0; i < edge; i++) { var g = i / edge; d[i] *= g; d[n - 1 - i] *= g; }
        return buf;
    }

    function ensureAudio(inst) {
        if (inst.ac || inst.audioFailed) return inst.ac;
        var AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) { inst.audioFailed = true; return null; }
        try {
            var ac = new AC({ latencyHint: 'interactive' });
            inst.master = ac.createGain();
            inst.master.gain.value = 1;
            inst.master.connect(ac.destination);
            inst.bgmBus = ac.createGain();
            inst.bgmBus.gain.value = inst.bgmVolume;
            inst.bgmBus.connect(inst.master);
            inst.sfx = [synthSfx(ac, 0), synthSfx(ac, 1), synthSfx(ac, 2)];
            inst.ac = ac;
            if (inst.bgmUrl) loadBgm(inst);
        } catch (e) { inst.audioFailed = true; inst.ac = null; }
        return inst.ac;
    }

    function loadBgm(inst) {
        var ac = inst.ac, url = inst.bgmUrl, seq = ++inst.bgmSeq;
        if (!ac || !url) return;
        fetch(url).then(function (r) {
            if (!r.ok) throw new Error('bgm ' + r.status);
            return r.arrayBuffer();
        }).then(function (ab) {
            return ac.decodeAudioData(ab);
        }).then(function (buf) {
            if (!inst.alive || seq !== inst.bgmSeq) return;               // 더 최신 트랙 요청 존재 → 폐기
            inst.bgmBuf = buf;
            if (inst.playing && inst.clockMode === 1 && !inst.scrubbing) schedule(inst);   // 재생 유지한 채 트랙 핫스왑
        }).catch(function () {
            if (seq === inst.bgmSeq) inst.bgmBuf = null;                  // BGM 실패 → 무음 폴백(SFX/영상은 계속)
        });
    }

    function startVoice(inst, buf, dest, when, offset, rate, loop, vol) {
        var ac = inst.ac;
        var src = ac.createBufferSource(), g = ac.createGain();
        src.buffer = buf;
        src.loop = loop;
        src.playbackRate.value = rate;
        g.gain.setValueAtTime(0, when);
        g.gain.linearRampToValueAtTime(vol, when + FADE_IN);
        src.connect(g);
        g.connect(dest);
        var voice = { src: src, gain: g };
        src.onended = function () {
            try { src.disconnect(); g.disconnect(); } catch (e) { }
            var ix = inst.voices.indexOf(voice);
            if (ix >= 0) inst.voices.splice(ix, 1);
        };
        src.start(when, offset);
        inst.voices.push(voice);
    }

    function stopVoices(inst) {
        var ac = inst.ac;
        if (!ac) return;
        var now = ac.currentTime, list = inst.voices;
        for (var i = 0; i < list.length; i++) {
            var v = list[i];
            try {
                v.gain.gain.cancelScheduledValues(now);
                v.gain.gain.setValueAtTime(v.gain.gain.value, now);
                v.gain.gain.linearRampToValueAtTime(0, now + FADE_OUT);   // 8ms 페이드 아웃 후 정지 → 무팝
                v.src.stop(now + STOP_PAD);
            } catch (e) { }
        }
        inst.voices = [];
    }

    // 현재 플레이헤드 기준으로 BGM + 남은 SFX 를 오디오 타임라인에 재배치하고 클록 앵커를 갱신.
    function schedule(inst) {
        var ac = inst.ac;
        stopVoices(inst);
        var t0 = ac.currentTime + LEAD, t = inst.t, rate = inst.rate;
        inst.anchorT = t;
        inst.anchorClock = t0 + (ac.outputLatency || ac.baseLatency || 0); // 들리는 시점 기준으로 캔버스를 슬레이브
        inst.lastCt = -1;
        try {
            if (inst.bgmBuf) startVoice(inst, inst.bgmBuf, inst.bgmBus, t0, t % inst.bgmBuf.duration, rate, true, 1);
            var cues = (inst.contract && inst.contract.sfx) || SFX_CUES;
            for (var i = 0; i < cues.length; i++) {
                var cue = cues[i], buf = inst.sfx[cue.kind];
                if (cue.at >= inst.duration) continue;
                if (t <= cue.at) startVoice(inst, buf, inst.master, t0 + (cue.at - t) / rate, 0, 1, false, cue.vol);
                else if (t < cue.at + buf.duration - 0.03) startVoice(inst, buf, inst.master, t0, t - cue.at, 1, false, cue.vol);
            }
        } catch (e) { }
    }

    function anchor(inst) {
        if (inst.clockMode === 1 && inst.ac && inst.ac.state === 'running') schedule(inst);
        else {
            inst.clockMode = 0;
            inst.anchorT = inst.t;
            inst.anchorClock = performance.now() / 1000;
        }
    }

    function notify(inst) {
        if (inst.root) inst.root.setAttribute('data-pmp-playing', inst.playing ? '1' : '0');
        if (inst.dotnet) {
            try { inst.dotnet.invokeMethodAsync('OnPreviewerPlayState', inst.playing).catch(function () { }); } catch (e) { }
        }
    }

    // ───────────── ▶ 트랜스포트 ─────────────
    function play(inst) {
        if (!inst.alive || inst.playing || inst.starting || !inst.img) return;
        if (inst.t >= inst.duration - 0.001) inst.t = 0;
        var ac = ensureAudio(inst);
        var begin = function () {
            inst.starting = false;
            if (!inst.alive || inst.playing) return;
            inst.playing = true;
            inst.clockMode = ac && ac.state === 'running' ? 1 : 0;
            anchor(inst);
            notify(inst);
            invalidate(inst);
        };
        if (ac && ac.state !== 'running') {
            inst.starting = true;
            ac.resume().then(begin, begin);
        } else begin();
    }

    function pause(inst) {
        if (!inst.playing) return;
        inst.playing = false;
        stopVoices(inst);
        notify(inst);
        invalidate(inst);
    }

    function seek(inst, t) {
        inst.t = clamp(isFinite(t) ? t : 0, 0, inst.duration);
        if (inst.playing && !inst.scrubbing) anchor(inst);
        invalidate(inst);
    }

    function setRate(inst, r) {
        r = +r;
        if (!(r > 0) || !isFinite(r)) r = 1;
        inst.rate = clamp(r, 0.25, 4);
        if (inst.playing && !inst.scrubbing) anchor(inst);
    }

    function sameCues(a, b) {
        if (a === b) return true;
        if (!a || !b || a.length !== b.length) return false;
        for (var i = 0; i < a.length; i++) if (a[i].kind !== b[i].kind || a[i].at !== b[i].at || a[i].vol !== b[i].vol) return false;
        return true;
    }

    function applyContract(inst, json) {
        var prevSfx = inst.contract ? inst.contract.sfx : null;
        inst.contract = parseContract(json);
        var d = inst.contract.segments.length > 0 ? inst.contract.segments[inst.contract.segments.length - 1].endSec : DEFAULT_DURATION;
        var changed = d !== inst.duration || !sameCues(prevSfx, inst.contract.sfx);   // 🥁 비트 스냅 큐 변경도 재배치
        inst.duration = d;
        if (inst.scrub) inst.scrub.max = d.toFixed(2);
        if (inst.t > d) inst.t = d;
        if (changed && inst.playing && !inst.scrubbing) anchor(inst);      // 길이가 바뀐 경우에만 SFX 재배치(같으면 재생 무중단)
    }

    function setImage(inst, url) {
        var seq = ++inst.imgSeq;
        if (!url) { inst.img = null; pause(inst); invalidate(inst); return; }
        var img = new Image();
        img.decoding = 'async';
        img.onload = function () {
            if (!inst.alive || seq !== inst.imgSeq) return;
            var iw = img.naturalWidth, ih = img.naturalHeight;
            if (!(iw > 0 && ih > 0)) return;
            var sc = Math.max(W / iw, H / ih);                            // 커버 크롭(FFmpeg scale=increase + crop 과 동일)
            inst.coverW = iw * sc; inst.coverH = ih * sc;
            inst.coverX = (W - inst.coverW) / 2; inst.coverY = (H - inst.coverH) / 2;
            inst.img = img;
            invalidate(inst);
        };
        img.onerror = function () { if (seq === inst.imgSeq) { inst.img = null; invalidate(inst); } };
        img.src = url;
    }

    function get(root) { return root ? instances.get(root) : null; }

    window.plutoMotionPreviewer = {
        init: function (root, dotnet, contractJson, subtitlesJson, imageUrl, bgmUrl, bgmVolume) {
            if (!root) return false;
            this.dispose(root);
            var canvas = root.querySelector('canvas');
            var ctx = canvas ? canvas.getContext('2d', { alpha: false, desynchronized: true }) : null;
            if (!ctx) return false;
            var inst = {
                alive: true, root: root, canvas: canvas, ctx: ctx, dotnet: dotnet || null,
                scrub: root.querySelector('[data-pmp-scrub]'), label: root.querySelector('[data-pmp-time]'),
                contract: null, acts: [], crossfade: 0.15, duration: DEFAULT_DURATION,
                sample: { zoom: 1, fx: 0.5, fy: 0.5, x: 0, y: 0, segment: -1 },
                img: null, imgSeq: 0, coverX: 0, coverY: 0, coverW: W, coverH: H,
                t: 0, rate: 1, playing: false, starting: false, scrubbing: false, raf: 0,
                clockMode: 0, anchorT: 0, anchorClock: 0, lastCt: -1, lastCtPerf: 0, lastCs: -1, lastDur: -1,
                ac: null, audioFailed: false, master: null, bgmBus: null, sfx: null, voices: [],
                bgmUrl: bgmUrl || null, bgmBuf: null, bgmSeq: 0, bgmVolume: isFinite(bgmVolume) ? clamp(+bgmVolume, 0, 1) : 0.25,
                tick: null, onKey: null, onInput: null, onChange: null
            };
            inst.tick = function () { frame(inst); };
            inst.onKey = function (e) {
                if (e.code !== 'Space' && e.key !== ' ') return;
                var tag = e.target && e.target.tagName;
                if (tag === 'BUTTON' || tag === 'SELECT' || tag === 'TEXTAREA') return;   // 버튼은 자체 클릭으로 토글
                e.preventDefault();
                if (inst.playing) pause(inst); else play(inst);
            };
            inst.onInput = function () {                                   // 드래그 중: 음원 즉시 페이드 정지 · 플레이헤드만 이동
                if (!inst.scrubbing) { inst.scrubbing = true; stopVoices(inst); }
                inst.t = clamp(parseFloat(inst.scrub.value) || 0, 0, inst.duration);
                invalidate(inst);
            };
            inst.onChange = function () {                                  // 놓는 순간: 새 위치에서 남은 사운드 이벤트 재배치
                inst.scrubbing = false;
                seek(inst, parseFloat(inst.scrub.value) || 0);
            };
            root.addEventListener('keydown', inst.onKey);
            if (inst.scrub) {
                inst.scrub.addEventListener('input', inst.onInput);
                inst.scrub.addEventListener('change', inst.onChange);
            }
            instances.set(root, inst);
            applyContract(inst, contractJson);
            parseSubtitles(inst, subtitlesJson);
            setImage(inst, imageUrl);
            root.setAttribute('data-pmp-playing', '0');
            invalidate(inst);
            return true;
        },
        // 🔁 핫스왑 — 플레이헤드/재생 상태 유지한 채 궤적 · 자막만 교체.
        update: function (root, contractJson, subtitlesJson) {
            var inst = get(root);
            if (!inst) return false;
            if (contractJson != null) applyContract(inst, contractJson);
            if (subtitlesJson != null) parseSubtitles(inst, subtitlesJson);
            invalidate(inst);
            return true;
        },
        setImage: function (root, url) { var inst = get(root); if (!inst) return false; setImage(inst, url); return true; },
        setBgm: function (root, url, volume) {
            var inst = get(root);
            if (!inst) return false;
            if (isFinite(volume)) {
                inst.bgmVolume = clamp(+volume, 0, 1);
                if (inst.bgmBus) inst.bgmBus.gain.setTargetAtTime(inst.bgmVolume, inst.ac.currentTime, 0.02);
            }
            url = url || null;
            if (url === inst.bgmUrl) return true;
            inst.bgmUrl = url;
            if (!url) {
                inst.bgmSeq++;
                inst.bgmBuf = null;
                if (inst.playing && inst.clockMode === 1 && !inst.scrubbing) schedule(inst);
            } else if (inst.ac) loadBgm(inst);
            return true;
        },
        play: function (root) { var inst = get(root); if (inst) play(inst); },
        pause: function (root) { var inst = get(root); if (inst) pause(inst); },
        toggle: function (root) { var inst = get(root); if (!inst) return false; if (inst.playing) pause(inst); else play(inst); return true; },
        seek: function (root, t) { var inst = get(root); if (inst) seek(inst, +t); },
        setRate: function (root, r) { var inst = get(root); if (inst) setRate(inst, r); },
        state: function (root) {
            var inst = get(root);
            return inst ? { t: inst.t, playing: inst.playing, rate: inst.rate, duration: inst.duration, clock: inst.clockMode === 1 ? 'audio' : 'performance', hasImage: !!inst.img } : null;
        },
        // 🧪 패리티 점검용 — C# PlutoMotionPreviewModel.Evaluate 와 같은 값을 반환.
        evaluate: function (contractJson, t) {
            var s = evalInto(parseContract(contractJson), +t, { zoom: 1, fx: 0.5, fy: 0.5, x: 0, y: 0, segment: -1 });
            return { zoom: s.zoom, focalX: s.fx, focalY: s.fy, x: s.x, y: s.y, segment: s.segment };
        },
        subtitleAlpha: function (subtitlesJson, index, t) {
            var d = JSON.parse(subtitlesJson);
            return subtitleAlpha(d.acts, index, +t, d.crossfadeSec || 0.15);
        },
        dispose: function (root) {
            var inst = get(root);
            if (!inst) return;
            inst.alive = false;
            inst.playing = false;
            if (inst.raf) { cancelAnimationFrame(inst.raf); inst.raf = 0; }
            stopVoices(inst);
            root.removeEventListener('keydown', inst.onKey);
            if (inst.scrub) {
                inst.scrub.removeEventListener('input', inst.onInput);
                inst.scrub.removeEventListener('change', inst.onChange);
            }
            if (inst.ac) {
                var ac = inst.ac;
                setTimeout(function () { try { ac.close(); } catch (e) { } }, 40);   // 페이드 아웃 끝난 뒤 닫기
            }
            inst.dotnet = null;
            inst.img = null;
            instances.delete(root);
        }
    };
})();
