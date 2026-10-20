// [PromptFlow Studio] v2.9.0 음성 감독 브릿지 — 브라우저 내장 Web Speech API(인식) + Web Audio API(실시간 스펙트럼).
// 외부 라이브러리 0. 인식된 텍스트만 .NET(VoiceCloudStudioDeck.OnVoiceText)으로 전달하고, 스펙트럼은 JS가 직접 DOM 바에 렌더.
window.pfVoice = {
    _rec: null, _audio: null, _raf: 0, _dotnet: null,

    // 마이크 시작: 스펙트럼(barsId 컨테이너의 자식 바 높이) + 한국어 음성 인식. 반환: 어느 하나라도 활성화되면 true.
    async start(dotnetRef, barsId) {
        this._dotnet = dotnetRef;

        // ── 실시간 스펙트럼(AnalyserNode) ──
        try {
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            const Ctx = window.AudioContext || window.webkitAudioContext;
            const ctx = new Ctx();
            const src = ctx.createMediaStreamSource(stream);
            const an = ctx.createAnalyser();
            an.fftSize = 64;
            src.connect(an);
            this._audio = { ctx, stream, an };
            const bars = document.getElementById(barsId);
            const data = new Uint8Array(an.frequencyBinCount);
            const draw = () => {
                an.getByteFrequencyData(data);
                if (bars) {
                    for (let i = 0; i < bars.children.length; i++) {
                        const v = data[i * 2] || 0;
                        bars.children[i].style.height = (8 + (v / 255) * 100) + '%';
                    }
                }
                this._raf = requestAnimationFrame(draw);
            };
            draw();
        } catch (e) { /* 마이크 권한/미지원 → 스펙트럼 없이 진행 */ }

        // ── 음성 인식(SpeechRecognition · 브라우저 내장) ──
        try {
            const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
            if (SR) {
                const r = new SR();
                r.lang = 'ko-KR'; r.continuous = true; r.interimResults = false;
                r.onresult = (e) => {
                    const t = e.results[e.results.length - 1][0].transcript;
                    if (this._dotnet && t) this._dotnet.invokeMethodAsync('OnVoiceText', t.trim());
                };
                r.onend = () => { if (this._rec) { try { r.start(); } catch (e) { } } };   // 자동 재시작(연속)
                this._rec = r; r.start();
            }
        } catch (e) { /* 인식 미지원 → 텍스트 입력 폴백 사용 */ }

        return !!(this._audio || this._rec);
    },

    stop() {
        if (this._rec) { const r = this._rec; this._rec = null; try { r.stop(); } catch (e) { } }
        if (this._raf) { cancelAnimationFrame(this._raf); this._raf = 0; }
        if (this._audio) {
            try { this._audio.stream.getTracks().forEach(t => t.stop()); this._audio.ctx.close(); } catch (e) { }
            this._audio = null;
        }
    },

    // 음성 피드백 효과음 재생(VoiceFeedbackSynthesizer 가 만든 WAV Data URI).
    play(dataUri) { try { const a = new Audio(dataUri); a.play(); } catch (e) { } }
};
