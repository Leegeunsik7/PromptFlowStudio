// [PromptFlow Studio v16.97.0] WebXR 브리지 — navigator.xr 지원 탐지 · immersive 세션 뷰어 포즈를 .NET(OnXrPose)으로 전달.
// 세션 프레임은 단색 클리어만 수행(스트림 영상 합성은 .NET 측 프리뷰). in-flight 1슬롯으로 백프레셔.
window.pfXr = (function () {
    let session = null, refSpace = null, gl = null, inflight = false, frames = 0;

    async function probe() {
        const r = { xr: !!navigator.xr, secure: !!window.isSecureContext, vr: false, ar: false };
        if (!navigator.xr) return r;
        try { r.vr = await navigator.xr.isSessionSupported('immersive-vr'); } catch (e) { }
        try { r.ar = await navigator.xr.isSessionSupported('immersive-ar'); } catch (e) { }
        return r;
    }

    async function start(dotnet, mode) {
        if (!navigator.xr) return 'navigator.xr 없음';
        if (session) return 'ok';
        try {
            session = await navigator.xr.requestSession(mode || 'immersive-vr', { optionalFeatures: ['local-floor', 'hand-tracking'] });
            const canvas = document.createElement('canvas');
            gl = canvas.getContext('webgl2', { xrCompatible: true }) || canvas.getContext('webgl', { xrCompatible: true });
            await gl.makeXRCompatible();
            session.updateRenderState({ baseLayer: new XRWebGLLayer(session, gl) });
            try { refSpace = await session.requestReferenceSpace('local-floor'); } catch (e) { refSpace = await session.requestReferenceSpace('local'); }
            session.addEventListener('end', () => { session = null; dotnet.invokeMethodAsync('OnXrEnded'); });
            const onFrame = (t, frame) => {
                if (!session) return;
                session.requestAnimationFrame(onFrame);
                const layer = session.renderState.baseLayer;
                gl.bindFramebuffer(gl.FRAMEBUFFER, layer.framebuffer);
                gl.clearColor(0.05, 0.07, 0.12, 1); gl.clear(gl.COLOR_BUFFER_BIT);
                const pose = frame.getViewerPose(refSpace);
                if (!pose || inflight) return;
                const p = pose.transform.position, o = pose.transform.orientation;
                let ipd = 0;
                if (pose.views.length >= 2) {
                    const a = pose.views[0].transform.position, b = pose.views[1].transform.position;
                    ipd = Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z) * 1000;
                }
                inflight = true; frames++;
                dotnet.invokeMethodAsync('OnXrPose', p.x, p.y, p.z, o.x, o.y, o.z, o.w, ipd).finally(() => { inflight = false; });
            };
            session.requestAnimationFrame(onFrame);
            return 'ok';
        } catch (e) {
            session = null;
            return String(e && e.message || e);
        }
    }

    async function stop() { if (session) { try { await session.end(); } catch (e) { } session = null; } }

    return { probe, start, stop, frames: () => frames };
})();
