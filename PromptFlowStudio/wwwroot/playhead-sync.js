// [PromptFlow Studio] v2.3.0 텐션 타임라인 실시간 플레이헤드 동기화 + 노드 클릭 SFX 시연(Web Audio).
//   - pfPlayhead.bind(graphId, videoId): 최종 영상 currentTime 을 추적해 그래프 위 수직 네온 빔을 구동,
//     빔이 지나는 컷 노드를 펄스(.lit) · SFX/덕킹 노드 반응. (Blazor JSInterop 에서 호출)
//   - pfPlayhead.playSfx(type): 노드 클릭 시 타입별 합성음(오실레이터)으로 SFX 미리듣기.
(function () {
    var bound = {};

    function bind(graphId, videoId) {
        unbind(graphId);
        var graph = document.getElementById(graphId);
        var video = document.getElementById(videoId);
        if (!graph || !video) return false;

        var beam = graph.querySelector('.ctg-playhead');
        var nodes = graph.querySelectorAll('.ctg-node');
        var raf = 0;

        function tick() {
            var d = video.duration;
            if (d && isFinite(d) && d > 0 && !video.paused && !video.ended) {
                var p = Math.max(0, Math.min(1, video.currentTime / d));
                if (beam) { beam.style.left = (p * 100) + '%'; beam.style.opacity = '1'; }
                for (var i = 0; i < nodes.length; i++) {
                    var nx = parseFloat(nodes[i].style.left) || 0;   // % (그래프 좌표와 동일 스케일)
                    if (Math.abs(nx - p * 100) < 3.5) nodes[i].classList.add('lit');
                    else nodes[i].classList.remove('lit');
                }
            } else if (beam && video.ended) {
                beam.style.opacity = '0';
                for (var j = 0; j < nodes.length; j++) nodes[j].classList.remove('lit');
            }
            raf = requestAnimationFrame(tick);
        }
        raf = requestAnimationFrame(tick);
        bound[graphId] = { raf: raf };
        return true;
    }

    function unbind(graphId) {
        var b = bound[graphId];
        if (b) { try { cancelAnimationFrame(b.raf); } catch (e) { } delete bound[graphId]; }
        var g = document.getElementById(graphId);
        if (g) {
            var bm = g.querySelector('.ctg-playhead'); if (bm) bm.style.opacity = '0';
            var ns = g.querySelectorAll('.ctg-node.lit'); for (var i = 0; i < ns.length; i++) ns[i].classList.remove('lit');
        }
    }

    // ── 타입별 합성 SFX(Web Audio) — 파일 불필요, 몰입용 미리듣기 ──
    var ac = null;
    var TONES = {
        ding: [880, 'sine', .18], pop: [520, 'square', .08], boing: [220, 'sawtooth', .2], sparkle: [1320, 'triangle', .16],
        crash: [120, 'sawtooth', .26], giggle: [660, 'sine', .13], whoosh: [300, 'sine', .22], squeak: [1500, 'square', .1],
        chomp: [180, 'square', .12], drumroll: [160, 'triangle', .3], suspense: [200, 'sine', .3], pluck: [740, 'triangle', .1],
        cute: [990, 'sine', .12], kiss: [600, 'sine', .1], clap: [400, 'square', .08], splash: [280, 'sine', .18]
    };

    function playSfx(type) {
        try {
            ac = ac || new (window.AudioContext || window.webkitAudioContext)();
            if (ac.state === 'suspended') ac.resume();
            var m = TONES[type] || [520, 'sine', .15];
            var o = ac.createOscillator(), g = ac.createGain();
            o.type = m[1]; o.frequency.value = m[0];
            o.connect(g); g.connect(ac.destination);
            var t = ac.currentTime;
            g.gain.setValueAtTime(0.0001, t);
            g.gain.exponentialRampToValueAtTime(0.28, t + 0.012);
            g.gain.exponentialRampToValueAtTime(0.0001, t + m[2]);
            o.start(t); o.stop(t + m[2] + 0.03);
            return true;
        } catch (e) { return false; }
    }

    window.pfPlayhead = { bind: bind, unbind: unbind, playSfx: playSfx };
})();
