/* ============================================================================
 *  SiteMatic 콘솔 — 경보음 (WebAudio 합성)
 *
 *  ⚠ 오디오 파일(mp3/wav)을 쓰지 않고 WebAudio 로 합성한다.
 *     · 폐쇄망 배포에 바이너리 자산이 하나 늘지 않는다
 *     · 코덱/자동재생 정책 차이로 "현장에서만 소리가 안 나는" 문제를 피한다
 *     · 등급별 패턴을 코드로 정의하므로 튜닝이 쉽다
 * ========================================================================== */
(function () {
    'use strict';

    let ctx = null;
    let muted = false;
    let lastPlayedAt = 0;

    function audio() {
        if (!ctx) {
            const AC = window.AudioContext || window.webkitAudioContext;
            if (!AC) return null;
            ctx = new AC();
        }
        // WebView2 는 사용자 제스처 전까지 suspended 일 수 있다.
        if (ctx.state === 'suspended') { try { ctx.resume(); } catch (e) { /* 무시 */ } }
        return ctx;
    }

    /** 단일 톤. freq(Hz), dur(초), delay(초), gain(0~1) */
    function tone(freq, dur, delay, gain) {
        const ac = audio();
        if (!ac) return;
        const t0 = ac.currentTime + (delay || 0);
        const osc = ac.createOscillator();
        const amp = ac.createGain();

        osc.type = 'square';               // 현장 소음 속에서 사인파보다 잘 들린다
        osc.frequency.setValueAtTime(freq, t0);

        // 클릭 노이즈 방지용 짧은 어택/릴리스
        amp.gain.setValueAtTime(0.0001, t0);
        amp.gain.exponentialRampToValueAtTime(gain || 0.18, t0 + 0.012);
        amp.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

        osc.connect(amp).connect(ac.destination);
        osc.start(t0);
        osc.stop(t0 + dur + 0.02);
    }

    const PATTERNS = {
        // 위험: 높은 두 음을 3회 반복 (약 1.0초)
        danger: function () {
            for (let i = 0; i < 3; i++) {
                tone(1180, 0.11, i * 0.32, 0.22);
                tone(880, 0.11, i * 0.32 + 0.15, 0.22);
            }
        },
        // 주의: 중간 음 2회
        warn: function () {
            tone(760, 0.13, 0.00, 0.15);
            tone(760, 0.13, 0.26, 0.15);
        },
        // 정보: 짧은 1회
        info: function () { tone(560, 0.10, 0, 0.10); },
        // 연결 복구
        online: function () { tone(660, 0.09, 0, 0.10); tone(990, 0.11, 0.10, 0.10); },
        // 연결 끊김
        offline: function () { tone(520, 0.10, 0, 0.10); tone(360, 0.16, 0.11, 0.10); }
    };

    window.siteMaticAlarm = {
        /** 등급별 경보음 재생. minGapMs 이내 중복 호출은 무시(경보 폭주 시 소리 겹침 방지). */
        play: function (level, minGapMs) {
            if (muted) return false;
            const gap = (typeof minGapMs === 'number') ? minGapMs : 1200;
            const now = Date.now();
            if (now - lastPlayedAt < gap) return false;
            lastPlayedAt = now;

            const p = PATTERNS[level] || PATTERNS.info;
            try { p(); return true; } catch (e) { return false; }
        },

        setMuted: function (v) { muted = !!v; return muted; },
        isMuted: function () { return muted; },

        /** 사용자 제스처 시점에 호출해 오디오 컨텍스트를 깨운다(자동재생 정책 대응). */
        unlock: function () {
            const ac = audio();
            if (!ac) return false;
            try { tone(440, 0.02, 0, 0.0005); } catch (e) { /* 무시 */ }
            return ac.state === 'running';
        }
    };

    // 첫 클릭/키 입력에서 자동으로 한 번 언락한다.
    function unlockOnce() {
        window.siteMaticAlarm.unlock();
        window.removeEventListener('pointerdown', unlockOnce);
        window.removeEventListener('keydown', unlockOnce);
    }
    window.addEventListener('pointerdown', unlockOnce);
    window.addEventListener('keydown', unlockOnce);
})();

/* ============================================================================
 *  siteMaticStore — localStorage 얇은 래퍼
 *
 *  ⚠ try/catch 로 감싸는 이유: 프라이빗 모드, 사이트 데이터 차단, WebView2 의
 *    일부 구성에서 localStorage 접근 자체가 예외를 던진다. 그때 화면 전체가
 *    죽으면 안 되므로 조용히 null 을 돌려주고 호출부가 빈 상태로 동작하게 한다.
 *
 *  ⚠ 이 저장소는 이 PC 에만 남는다. 서버로 동기화되지 않고 감사 로그에도
 *    남지 않는다. 호출부가 그 사실을 화면에 표시해야 한다.
 * ========================================================================== */
(function () {
    window.siteMaticStore = {
        get: function (k) {
            try { return window.localStorage.getItem(k); }
            catch (e) { return null; }
        },
        set: function (k, v) {
            try { window.localStorage.setItem(k, v); return true; }
            catch (e) { return false; }   // 용량 초과·차단 — 호출부는 계속 동작한다
        },
        remove: function (k) {
            try { window.localStorage.removeItem(k); } catch (e) { }
        },
        available: function () {
            try {
                var t = '__sm_probe__';
                window.localStorage.setItem(t, '1');
                window.localStorage.removeItem(t);
                return true;
            } catch (e) { return false; }
        }
    };
})();

/* ============================================================================
 *  siteMaticClip — 클립보드 복사
 *
 *  ⚠ navigator.clipboard 는 보안 컨텍스트(https 또는 localhost)에서만 동작한다.
 *    WebView2 의 일부 호스팅 구성에서는 없을 수 있어 execCommand 폴백을 둔다.
 *    폐쇄망 현장에서 "복사가 안 된다"는 문의를 미리 막는다.
 *
 *  ⚠ 성공/실패를 반드시 돌려준다. 조용히 실패하면 사용자는 복사된 줄 알고
 *    붙여넣기를 시도하다 예전 내용을 붙여 넣는다.
 * ========================================================================== */
(function () {
    function fallback(text) {
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            // 화면 밖으로 보내되 display:none 은 쓰지 않는다 — 선택이 안 된다.
            ta.setAttribute('readonly', '');
            ta.style.position = 'fixed';
            ta.style.top = '-1000px';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            var ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return ok;
        } catch (e) { return false; }
    }

    window.siteMaticClip = {
        copy: async function (text) {
            if (typeof text !== 'string' || text.length === 0) return false;
            if (navigator.clipboard && window.isSecureContext) {
                try { await navigator.clipboard.writeText(text); return true; }
                catch (e) { /* 폴백으로 내려간다 */ }
            }
            return fallback(text);
        }
    };
})();

/* ============================================================================
 *  siteMaticHelp — 전역 도움말 단축키 + 포커스 트랩
 *
 *  ⚠ 왜 Blazor 의 @onkeydown 이 아니라 document 리스너인가
 *    Blazor 의 키 핸들러는 그 요소에 포커스가 있어야 발동한다. 사용자가
 *    빈 여백을 클릭하면 포커스가 <body> 로 가고, 그 순간 F1 이 먹지 않는다.
 *    "언제든 열린다" 를 지키려면 document 레벨이어야 한다.
 *
 *  ⚠ F1 에 preventDefault 를 걸되 그 외 키는 절대 건드리지 않는다.
 *    Blazor 의 @onkeydown:preventDefault 는 조건부로 못 걸어서, 그걸 쓰면
 *    입력창의 Tab·한글 조합까지 막힌다.
 *
 *  ⚠ '?' 는 글쇠다. 입력창에 타이핑 중이면 무시해야 한다 —
 *    안 그러면 검색어에 물음표를 못 넣는다.
 * ========================================================================== */
(function () {
    'use strict';

    let ref = null;
    let onKey = null;
    let trapEl = null;
    let onTrapKey = null;
    let restoreTo = null;

    function isTextEntry(el) {
        if (!el) return false;
        if (el.isContentEditable) return true;
        const t = (el.tagName || '').toUpperCase();
        if (t === 'TEXTAREA' || t === 'SELECT') return true;
        if (t !== 'INPUT') return false;
        // 체크박스·버튼형 input 은 글쇠를 먹지 않으므로 단축키를 허용한다
        const type = (el.type || 'text').toLowerCase();
        return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color'].includes(type);
    }

    const FOCUSABLE = [
        'a[href]', 'button:not([disabled])', 'input:not([disabled])',
        'select:not([disabled])', 'textarea:not([disabled])', '[tabindex]:not([tabindex="-1"])'
    ].join(',');

    function focusable(root) {
        return Array.prototype.filter.call(
            root.querySelectorAll(FOCUSABLE),
            el => el.offsetParent !== null || el === document.activeElement);
    }

    window.siteMaticHelp = {
        /** 전역 단축키 등록. 중복 호출해도 리스너는 하나만 남는다. */
        register: function (dotNetRef) {
            window.siteMaticHelp.unregister();
            ref = dotNetRef;
            onKey = function (e) {
                if (e.altKey || e.ctrlKey || e.metaKey) return;

                if (e.key === 'F1') {
                    e.preventDefault();          // F1 만 기본 동작을 막는다
                    ref && ref.invokeMethodAsync('OnHotkey', 'F1');
                    return;
                }
                if (e.key === '?' && !isTextEntry(document.activeElement)) {
                    e.preventDefault();
                    ref && ref.invokeMethodAsync('OnHotkey', '?');
                }
            };
            document.addEventListener('keydown', onKey, true);
            return true;
        },

        unregister: function () {
            if (onKey) document.removeEventListener('keydown', onKey, true);
            onKey = null; ref = null;
            return true;
        },

        /**
         * 포커스 트랩. 드로어가 열린 동안 Tab 이 밖으로 새지 않게 한다.
         * ⚠ 트랩만 걸고 끝내면 안 된다. 닫을 때 원래 자리로 포커스를 되돌리지
         *   않으면 스크린리더 사용자는 문서 맨 처음으로 튕겨 나간다.
         */
        trap: function (id) {
            const el = document.getElementById(id);
            if (!el) return false;
            window.siteMaticHelp.release(false);

            restoreTo = document.activeElement;
            trapEl = el;

            onTrapKey = function (e) {
                if (e.key !== 'Tab') return;
                const items = focusable(el);
                if (items.length === 0) { e.preventDefault(); return; }
                const first = items[0], last = items[items.length - 1];
                if (e.shiftKey && document.activeElement === first) {
                    e.preventDefault(); last.focus();
                } else if (!e.shiftKey && document.activeElement === last) {
                    e.preventDefault(); first.focus();
                }
            };
            el.addEventListener('keydown', onTrapKey);

            // 첫 포커스는 닫기 버튼이 아니라 검색창에 준다 —
            // 도움말을 여는 사람의 다음 행동은 대개 '찾기' 다.
            const target = el.querySelector('[data-help-autofocus]') || focusable(el)[0];
            if (target) target.focus();
            return true;
        },

        release: function (restore) {
            if (trapEl && onTrapKey) trapEl.removeEventListener('keydown', onTrapKey);
            trapEl = null; onTrapKey = null;
            if (restore !== false && restoreTo && document.contains(restoreTo)) {
                try { restoreTo.focus(); } catch (e) { /* 무시 */ }
            }
            restoreTo = null;
            return true;
        }
    };
})();

/* ============================================================================
 *  siteMaticUi — 전역 단축키(Ctrl+K · /) · 테마 적용 · 컨텍스트 메뉴 위치
 *
 *  ⚠ Ctrl+K 를 Blazor 의 @onkeydown 으로 잡지 않는 이유는 도움말(F1)과 같다.
 *    포커스가 shell 밖에 있으면 발동하지 않아 "어디서든" 을 지키지 못한다.
 *
 *  ⚠ '/' 는 글쇠다. 입력창에 타이핑 중이면 무시해야 한다 —
 *    안 그러면 RTSP 경로에 슬래시를 못 넣는다.
 * ========================================================================== */
(function () {
    'use strict';

    let ref = null, onKey = null;

    function isTextEntry(el) {
        if (!el) return false;
        if (el.isContentEditable) return true;
        const t = (el.tagName || '').toUpperCase();
        if (t === 'TEXTAREA' || t === 'SELECT') return true;
        if (t !== 'INPUT') return false;
        const type = (el.type || 'text').toLowerCase();
        return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color'].includes(type);
    }

    window.siteMaticUi = {
        register: function (dotNetRef) {
            window.siteMaticUi.unregister();
            ref = dotNetRef;
            onKey = function (e) {
                // Ctrl+K / Cmd+K — 어디서든(입력창 안에서도) 연다
                if ((e.ctrlKey || e.metaKey) && !e.altKey &&
                    (e.key === 'k' || e.key === 'K')) {
                    e.preventDefault();
                    ref && ref.invokeMethodAsync('OnPaletteHotkey');
                    return;
                }
                // '/' — 타이핑 중이 아닐 때만
                if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey &&
                    !isTextEntry(document.activeElement)) {
                    e.preventDefault();
                    ref && ref.invokeMethodAsync('OnPaletteHotkey');
                }
            };
            document.addEventListener('keydown', onKey, true);
            return true;
        },

        unregister: function () {
            if (onKey) document.removeEventListener('keydown', onKey, true);
            onKey = null; ref = null;
            return true;
        },

        /**
         * 테마를 문서 루트에 적용한다.
         * ⚠ app.css 의 토큰이 :root[data-theme] 로 갈리므로 여기가 유일한 스위치다.
         *   이 함수가 없으면 환경 설정의 테마 선택이 아무 효과도 내지 않는다
         *   (실제로 그런 상태였다).
         */
        setTheme: function (v) {
            try {
                if (v === 'dark' || v === 'light') {
                    document.documentElement.setAttribute('data-theme', v);
                } else {
                    document.documentElement.removeAttribute('data-theme');
                }
                return true;
            } catch (e) { return false; }
        },

        /**
         * 문서 루트에 표시용 클래스를 켜고 끈다.
         *
         * ⚠ 테마에는 쓰지 않는다. 테마는 setTheme 의 data-theme 하나로만
         *   갈린다 — app.css 토큰이 :root[data-theme] 를 보기 때문이다.
         *   여기 클래스를 얹으면 진실이 둘이 되어 반드시 어긋난다.
         *
         * ⚠ 실패해도 예외를 던지지 않는다. 화면 옵션 때문에 앱이 죽으면 안 된다.
         */
        setRootFlag: function (name, on) {
            try {
                if (!name) return false;
                document.documentElement.classList.toggle(name, !!on);
                return true;
            } catch (e) { return false; }
        },

        focus: function (id) {
            try { const el = document.getElementById(id); if (el) { el.focus(); return true; } }
            catch (e) { }
            return false;
        },

        /**
         * 지정 id 요소로 부드럽게 스크롤한다(텐션 타임라인 → 컷 카드 연동).
         * ⚠ 실패해도 예외를 던지지 않는다(요소가 아직 없을 수 있다).
         */
        scrollToId: function (id, block) {
            try {
                const el = document.getElementById(id);
                if (el) { el.scrollIntoView({ behavior: 'smooth', block: block || 'center', inline: 'nearest' }); return true; }
            } catch (e) { }
            return false;
        },

        /**
         * 컨텍스트 메뉴가 화면 밖으로 나가지 않게 좌표를 보정한다.
         * ⚠ 메뉴 크기를 실제로 재서 계산한다. 상수로 가정하면 항목 수가
         *   달라질 때 아래쪽 메뉴가 잘린다.
         */
        clampMenu: function (id, x, y) {
            try {
                const el = document.getElementById(id);
                if (!el) return { x: x, y: y };
                const r = el.getBoundingClientRect();
                const pad = 8;
                const vw = window.innerWidth, vh = window.innerHeight;
                let nx = x, ny = y;
                if (x + r.width + pad > vw) nx = Math.max(pad, x - r.width);
                if (y + r.height + pad > vh) ny = Math.max(pad, y - r.height);
                if (ny + r.height + pad > vh) ny = Math.max(pad, vh - r.height - pad);
                return { x: Math.round(nx), y: Math.round(ny) };
            } catch (e) { return { x: x, y: y }; }
        }
    };
})();

/* ============================================================================
 *  siteMaticMedia — 증거 영상 모달 플레이어 제어 (모듈 10)
 *
 *  ⚠ <video> 요소를 직접 만지는 얇은 래퍼다. 코덱·스트리밍은 브라우저가
 *    맡고, 여기서는 재생속도·프레임 이동·재생/정지만 조작한다. 외부 미디어
 *    라이브러리를 쓰지 않는다(클린 PC 제로-디펜던시 원칙).
 *
 *  ⚠ 모든 함수는 요소가 없거나 예외가 나도 false 를 돌려줄 뿐 던지지 않는다.
 *    실제 영상 URL 이 없는 시연/미연결 상태에서도 화면이 죽으면 안 된다.
 *
 *  ⚠ 프레임 이동은 fps 를 <가정>한 시간 시크다. 컨테이너에 정확한 프레임
 *    인덱스가 없으므로 1/fps 초 단위로 currentTime 을 옮긴다 — 근사값이다.
 * ========================================================================== */
(function () {
    'use strict';

    function vid(id) {
        var el = document.getElementById(id);
        return (el && el.tagName === 'VIDEO') ? el : null;
    }

    window.siteMaticMedia = {
        /** 재생속도 배율 설정(0.25~4). 요소 없으면 false. */
        setRate: function (id, rate) {
            var v = vid(id);
            if (!v) return false;
            try { v.playbackRate = Math.max(0.1, Math.min(8, rate || 1)); return true; }
            catch (e) { return false; }
        },

        /** deltaSeconds 만큼 시크(프레임 이동 = ±1/fps). 경계를 넘지 않는다. */
        step: function (id, deltaSeconds) {
            var v = vid(id);
            if (!v || !isFinite(v.duration)) return false;
            try {
                if (!v.paused) v.pause();                 // 프레임 단위 이동은 정지 상태에서
                var t = (v.currentTime || 0) + (deltaSeconds || 0);
                v.currentTime = Math.max(0, Math.min(v.duration || 0, t));
                return true;
            } catch (e) { return false; }
        },

        /** 재생/정지 토글. 반환: 재생 중이면 true. */
        toggle: function (id) {
            var v = vid(id);
            if (!v) return false;
            try {
                if (v.paused) { v.play(); return true; }
                v.pause(); return false;
            } catch (e) { return false; }
        },

        /** 처음으로 되감기. */
        rewind: function (id) {
            var v = vid(id);
            if (!v) return false;
            try { v.currentTime = 0; return true; } catch (e) { return false; }
        }
    };
})();

/* ============================================================================
 *  siteMaticWebcam — 가상 CCTV(노트북 웹캠) 캡처 + 프레임차분 모션 + 오버레이
 *
 *  ⚠ 캡처는 전적으로 브라우저(getUserMedia)가 한다 — .NET/네이티브 코덱 의존 0.
 *    <video> 가 스트림을 네이티브로 표출하고, 그 위 투명 <canvas> 에 바운딩 박스와
 *    위험 예측%를 그린다. 프레임차분(모션 점수)도 여기 canvas 에서 계산한다.
 *
 *  ⚠ .NET 으로는 <분석 주기(~10fps)로만> base64 JPEG + 모션 점수를 넘긴다
 *    (PushFrame). 오버레이는 JS 가 60fps 로 부드럽게 그리므로 프레임마다 .NET 을
 *    왕복하지 않는다. 위험% 라벨만 PushFrame 응답에서 받아 갱신한다.
 *
 *  ⚠ 실패(웹캠 없음/권한 거부)해도 던지지 않는다 — dotNet.ReportError 로 넘겨
 *    화면이 "[⚠️ 웹캠 장치를 찾을 수 없음]" 카드를 정직하게 그리게 한다.
 * ========================================================================== */
(function () {
    'use strict';

    let stream = null;
    let videoEl = null;
    let overlayEl = null;
    let dotNet = null;
    let rafId = 0;
    let timer = 0;
    let running = false;

    // 작업용 캔버스(모션 계산: 저해상 / 캡처: 중해상)
    const work = document.createElement('canvas'); work.width = 160; work.height = 120;
    const cap = document.createElement('canvas'); cap.width = 480; cap.height = 360;
    const wctx = work.getContext('2d', { willReadFrequently: true });
    const cctx = cap.getContext('2d');
    let prevGray = null;

    // 최신 분석 결과(오버레이가 읽는다)
    let latest = { prob: 0, tone: 'idle', label: '정상', box: null, motion: 0 };
    let flashUntil = 0;

    const TONE_COLOR = {
        idle:    'rgba(148,163,184,.9)',
        motion:  'rgba(59,130,246,.95)',
        suspect: 'rgba(245,158,11,.98)',
        fall:    'rgba(239,68,68,1)'
    };

    function pickProp(o, a, b) { return (o && o[a] != null) ? o[a] : (o ? o[b] : undefined); }

    function computeMotion() {
        // 저해상 그레이스케일 차분 → 변화 픽셀 비율 + 바운딩 박스
        wctx.drawImage(videoEl, 0, 0, work.width, work.height);
        let img;
        try { img = wctx.getImageData(0, 0, work.width, work.height); }
        catch (e) { return { motion: 0, box: null }; }   // 보안 컨텍스트 문제 등
        const d = img.data;
        const n = work.width * work.height;
        const gray = new Uint8Array(n);
        for (let i = 0, p = 0; i < n; i++, p += 4)
            gray[i] = (d[p] * 77 + d[p + 1] * 150 + d[p + 2] * 29) >> 8;

        if (!prevGray) { prevGray = gray; return { motion: 0, box: null }; }

        const TH = 28;                 // 픽셀 변화 임계
        let changed = 0, minX = 1e9, minY = 1e9, maxX = -1, maxY = -1;
        for (let y = 0; y < work.height; y++) {
            for (let x = 0; x < work.width; x++) {
                const i = y * work.width + x;
                if (Math.abs(gray[i] - prevGray[i]) > TH) {
                    changed++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        prevGray = gray;
        const motion = changed / n;
        const box = (changed > n * 0.008 && maxX >= minX)
            ? { x: minX / work.width, y: minY / work.height,
                w: (maxX - minX) / work.width, h: (maxY - minY) / work.height }
            : null;
        return { motion: motion, box: box };
    }

    async function analyze() {
        if (!running || !videoEl || videoEl.readyState < 2) return;
        const m = computeMotion();
        latest.motion = m.motion;
        latest.box = m.box;

        let b64 = '';
        try {
            cctx.drawImage(videoEl, 0, 0, cap.width, cap.height);
            const url = cap.toDataURL('image/jpeg', 0.55);
            const c = url.indexOf(',');
            b64 = c >= 0 ? url.substring(c + 1) : '';
        } catch (e) { b64 = ''; }

        if (dotNet) {
            try {
                const r = await dotNet.invokeMethodAsync('PushFrame', b64, m.motion);
                if (r) {
                    latest.prob = pickProp(r, 'p', 'P') || 0;
                    latest.tone = pickProp(r, 'tone', 'Tone') || 'idle';
                    latest.label = pickProp(r, 'label', 'Label') || '정상';
                    if (pickProp(r, 'fall', 'Fall')) flashUntil = Date.now() + 900;
                }
            } catch (e) { /* 인터롭 실패는 조용히 무시(다음 틱에 회복) */ }
        }
    }

    function drawOverlay() {
        if (!overlayEl || !videoEl) return;
        const w = videoEl.clientWidth || overlayEl.width;
        const h = videoEl.clientHeight || overlayEl.height;
        if (overlayEl.width !== w) overlayEl.width = w;
        if (overlayEl.height !== h) overlayEl.height = h;
        const g = overlayEl.getContext('2d');
        g.clearRect(0, 0, w, h);

        if (running) {
            const color = TONE_COLOR[latest.tone] || TONE_COLOR.idle;

            // 바운딩 박스
            if (latest.box) {
                const b = latest.box;
                g.lineWidth = 3; g.strokeStyle = color;
                g.strokeRect(b.x * w, b.y * h, b.w * w, b.h * h);
                g.fillStyle = color;
                const tag = latest.label + ' ' + Math.round(latest.prob * 100) + '%';
                g.font = '600 13px system-ui, sans-serif';
                const tw = g.measureText(tag).width + 12;
                const ty = Math.max(0, b.y * h - 22);
                g.fillRect(b.x * w, ty, tw, 20);
                g.fillStyle = '#0b1220';
                g.fillText(tag, b.x * w + 6, ty + 14);
            }

            // 상단 HUD(위험 예측 막대)
            g.fillStyle = 'rgba(11,18,32,.55)';
            g.fillRect(10, 10, 180, 46);
            g.fillStyle = '#cbd5e1';
            g.font = '600 11px system-ui, sans-serif';
            g.fillText('CAM-VIRTUAL-01 · 가상 Vision', 18, 26);
            g.fillStyle = 'rgba(148,163,184,.35)';
            g.fillRect(18, 36, 150, 8);
            g.fillStyle = color;
            g.fillRect(18, 36, 150 * Math.max(0, Math.min(1, latest.prob)), 8);

            // 쓰러짐 확정 플래시
            if (Date.now() < flashUntil) {
                g.strokeStyle = 'rgba(239,68,68,.95)'; g.lineWidth = 8;
                g.strokeRect(4, 4, w - 8, h - 8);
            }
        }
        rafId = requestAnimationFrame(drawOverlay);
    }

    window.siteMaticWebcam = {
        /** 사용 가능한 비디오 입력 장치 목록. 권한 부여 전에는 label 이 빈 값일 수 있다. */
        listDevices: async function () {
            try {
                const list = await navigator.mediaDevices.enumerateDevices();
                return list.filter(d => d.kind === 'videoinput')
                           .map(d => ({ id: d.deviceId, label: d.label || '카메라' }));
            } catch (e) { return []; }
        },

        /** 웹캠 세션 시작. 성공 true / 실패 false(+dotNet.ReportError). */
        start: async function (videoId, overlayId, dotNetRef, deviceId) {
            window.siteMaticWebcam.stop();
            videoEl = document.getElementById(videoId);
            overlayEl = document.getElementById(overlayId);
            dotNet = dotNetRef;
            if (!videoEl) return false;

            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                if (dotNet) dotNet.invokeMethodAsync('ReportError', '이 환경은 웹캠 API(getUserMedia)를 지원하지 않습니다.');
                return false;
            }

            const constraints = {
                audio: false,
                video: deviceId ? { deviceId: { exact: deviceId } } : { width: { ideal: 640 }, height: { ideal: 480 } }
            };
            try {
                stream = await navigator.mediaDevices.getUserMedia(constraints);
            } catch (e) {
                const msg = (e && e.name === 'NotFoundError')
                    ? '웹캠 장치를 찾을 수 없습니다 (서버형 VM 등 카메라 미장착).'
                    : (e && e.name === 'NotAllowedError')
                        ? '웹캠 접근이 거부되었습니다.'
                        : ('웹캠을 열 수 없습니다: ' + (e && e.message ? e.message : e));
                if (dotNet) dotNet.invokeMethodAsync('ReportError', msg);
                return false;
            }

            videoEl.srcObject = stream;
            videoEl.muted = true;
            try { await videoEl.play(); } catch (e) { /* 자동재생 정책 — 무시 */ }

            prevGray = null;
            latest = { prob: 0, tone: 'idle', label: '정상', box: null, motion: 0 };
            running = true;
            timer = window.setInterval(analyze, 100);   // 분석 ~10fps
            rafId = requestAnimationFrame(drawOverlay);   // 오버레이 60fps
            return true;
        },

        /** 세션 종료 — 트랙 해제 + 루프 정지 + 오버레이 지움. */
        stop: function () {
            running = false;
            if (timer) { clearInterval(timer); timer = 0; }
            if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
            try {
                if (stream) stream.getTracks().forEach(t => t.stop());
            } catch (e) { /* 무시 */ }
            stream = null;
            if (videoEl) { try { videoEl.srcObject = null; } catch (e) { } }
            if (overlayEl) {
                try { overlayEl.getContext('2d').clearRect(0, 0, overlayEl.width, overlayEl.height); } catch (e) { }
            }
            prevGray = null;
            return true;
        },

        isRunning: function () { return running; },

        /** 수동 스냅샷 시 화면 플래시(시각 피드백). */
        flash: function () { flashUntil = Date.now() + 350; return true; }
    };
})();

/* ============================================================================
 *  v15.4.1 라이브 비디오 캔버스 — MMF 파이프 RGBA32 프레임을 <canvas> 에 그린다.
 *  .NET byte[] 는 interop 에서 Uint8Array 로 넘어온다. ImageData 는 해상도가 바뀔 때만 새로 만든다.
 * ========================================================================== */
(function () {
    'use strict';
    const cache = new Map();   // id → { ctx, img }

    window.pfsLiveCanvas = {
        draw: function (id, w, h, bytes) {
            const el = document.getElementById(id);
            if (!el || !bytes || bytes.length < w * h * 4) return false;
            let c = cache.get(id);
            if (!c || c.el !== el || c.img.width !== w || c.img.height !== h) {
                if (el.width !== w) el.width = w;
                if (el.height !== h) el.height = h;
                const ctx = el.getContext('2d');
                if (!ctx) return false;
                c = { el: el, ctx: ctx, img: ctx.createImageData(w, h) };
                cache.set(id, c);
            }
            c.img.data.set(bytes.subarray ? bytes.subarray(0, w * h * 4) : bytes);
            c.ctx.putImageData(c.img, 0, 0);
            return true;
        },
        release: function (id) { cache.delete(id); return true; }
    };
})();

/* ============================================================================
 *  v16.36.0 디지털 트윈 & 모캡 — 웹캠(getUserMedia) → 다운스케일 RGBA → .NET PushRgba(in-flight 1슬롯 · 백프레셔).
 *  스냅샷(트윈 복원용)은 동일 캔버스에서 동기 추출. 캡처는 전적으로 브라우저 — 네이티브 코덱 의존 0.
 * ========================================================================== */
(function () {
    'use strict';
    let stream = null, videoEl = null, dotNet = null, timer = 0, busy = false, canvas = null, W = 96, H = 72;

    function grab(w, h) {
        if (!videoEl || videoEl.readyState < 2) return null;
        if (!canvas) canvas = document.createElement('canvas');
        if (canvas.width !== w) canvas.width = w;
        if (canvas.height !== h) canvas.height = h;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) return null;
        ctx.drawImage(videoEl, 0, 0, w, h);
        return new Uint8Array(ctx.getImageData(0, 0, w, h).data.buffer);
    }

    window.pfsMocap = {
        start: async function (videoId, dotNetRef, w, h, fps) {
            window.pfsMocap.stop();
            videoEl = document.getElementById(videoId);
            dotNet = dotNetRef; W = w || 96; H = h || 72;
            if (!videoEl) return false;
            if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
                if (dotNet) dotNet.invokeMethodAsync('ReportError', '이 환경은 웹캠 API(getUserMedia)를 지원하지 않습니다.');
                return false;
            }
            try {
                stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { width: { ideal: 640 }, height: { ideal: 480 } } });
            } catch (e) {
                const msg = (e && e.name === 'NotFoundError') ? '웹캠 장치를 찾을 수 없습니다.'
                    : (e && e.name === 'NotAllowedError') ? '웹캠 접근이 거부되었습니다.' : ('웹캠을 열 수 없습니다: ' + (e && e.message ? e.message : e));
                if (dotNet) dotNet.invokeMethodAsync('ReportError', msg);
                return false;
            }
            videoEl.srcObject = stream;
            videoEl.muted = true;
            try { await videoEl.play(); } catch (e) { /* 자동재생 정책 — 무시 */ }
            timer = window.setInterval(async function () {
                if (busy || !dotNet) return;
                const px = grab(W, H);
                if (!px) return;
                busy = true;
                try { await dotNet.invokeMethodAsync('PushRgba', px, W, H); } catch (e) { /* 컴포넌트 해제 — 무시 */ } finally { busy = false; }
            }, Math.max(16, Math.round(1000 / (fps || 30))));
            return true;
        },

        snapshot: function (w, h) { return grab(w || 128, h || 128); },

        stop: function () {
            if (timer) { clearInterval(timer); timer = 0; }
            try { if (stream) stream.getTracks().forEach(t => t.stop()); } catch (e) { /* 무시 */ }
            stream = null;
            if (videoEl) { try { videoEl.srcObject = null; } catch (e) { } }
            videoEl = null; dotNet = null; busy = false;
            return true;
        }
    };
})();

/* ============================================================================
 *  v17.19.1 — 헤더 스튜디오 툴바: 세로 휠 → 가로 스크롤
 *  ⚠ 넘칠 때만 가로채고(scrollWidth > clientWidth), 가로 휠/트랙패드 입력은 그대로 둔다.
 * ========================================================================== */
(function () {
    'use strict';
    // UI 감사: document 전역 non-passive 휠 리스너는 모든 휠 스크롤을 메인 스레드(Blazor 렌더) 대기로 묶어
    // 모달/본문 휠이 멈칫·정지하던 원인. 툴바 요소에만 지연 부착(passive pointerover 로 감지 · 재렌더 시 재부착).
    function onWheel(e) {
        const t = e.currentTarget;
        if (t.scrollWidth <= t.clientWidth || Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return;
        t.scrollLeft += e.deltaY;
        e.preventDefault();
    }
    document.addEventListener('pointerover', function (e) {
        const t = e.target && e.target.closest ? e.target.closest('.topbar-tools') : null;
        if (!t || t.__pfWheel) return;
        t.__pfWheel = true;
        t.addEventListener('wheel', onWheel, { passive: false });
    }, { passive: true });
})();
