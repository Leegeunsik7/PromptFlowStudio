// [PromptFlow Studio v5.5.1] HTML5 <video> 제어 헬퍼(ES 모듈 · 외부 SDK 0).
//   프리뷰 노드의 재생/일시정지/프레임 탐색(frame-accurate seek)에 사용. 모든 함수는 실패를 삼켜 앱을 죽이지 않는다.

/** 재생 토글 → 재생 중이면 true. */
export function toggle(el) {
    if (!el) return false;
    try { if (el.paused) { el.play(); return true; } el.pause(); return false; }
    catch { return false; }
}

/** 재생. */
export function play(el) { try { el && el.play && el.play(); } catch { } }

/** 일시정지. */
export function pause(el) { try { el && el.pause && el.pause(); } catch { } }

/** dt(초)만큼 프레임 탐색(일시정지 후 currentTime 이동) → 새 currentTime. 60fps 스텝 = ±1/60. */
export function step(el, dt) {
    if (!el) return 0;
    try {
        el.pause();
        const d = isFinite(el.duration) ? el.duration : 0;
        el.currentTime = Math.max(0, Math.min(d, (el.currentTime || 0) + dt));
        return el.currentTime;
    } catch { return 0; }
}

/** 정규화 위치(0~1)로 시크 → 새 currentTime. */
export function seekFrac(el, f) {
    if (!el || !isFinite(el.duration) || el.duration <= 0) return 0;
    try { el.currentTime = el.duration * Math.max(0, Math.min(1, f)); return el.currentTime; }
    catch { return 0; }
}

/** 현재 재생 위치(초). */
export function now(el) { try { return el ? (el.currentTime || 0) : 0; } catch { return 0; } }

/** 총 길이(초 · 미확정 0). */
export function duration(el) { try { return (el && isFinite(el.duration)) ? el.duration : 0; } catch { return 0; } }

/** [v5.5.2] 현재 프레임 캡처 → JPEG data URL(최대 640px 축소 · 페이로드 절감). 실패/미로드는 null. */
export function grab(el) {
    if (!el || !el.videoWidth) return null;
    try {
        const maxW = 640;
        const scale = Math.min(1, maxW / el.videoWidth);
        const w = Math.round(el.videoWidth * scale), h = Math.round(el.videoHeight * scale);
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        const ctx = c.getContext('2d');
        if (!ctx) return null;
        ctx.drawImage(el, 0, 0, w, h);
        return c.toDataURL('image/jpeg', 0.82);
    } catch { return null; }
}
