using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Interactive;

/// <summary>v16.26.0 분기 선택지 — 버튼 라벨 + 이동할 노드.</summary>
public sealed class ChoiceOption
{
    public string Label { get; set; } = "";
    public string TargetNodeId { get; set; } = "";
}

/// <summary>v16.26.0 분기 트리 노드 — 영상 1개 + 선택지(0개=엔딩 · 1개=자동 진행 · 2~4개=시청자 선택).</summary>
public sealed class ChoiceNode
{
    public string Id { get; set; } = "n_" + Guid.NewGuid().ToString("N")[..6];
    public string Title { get; set; } = "";
    public string? VideoPath { get; set; }
    /// <summary>선택 화면 질문(예: "어느 길로 갈까요?").</summary>
    public string Prompt { get; set; } = "";
    public List<ChoiceOption> Options { get; set; } = new();
    public double CountdownSeconds { get; set; } = 6;
    /// <summary>무응답/동률 시 진행할 선택지 인덱스.</summary>
    public int DefaultOption { get; set; }
    public bool IsEnding => Options.Count == 0;
    public bool IsChoicePoint => Options.Count >= 2;
}

public enum ChoiceSessionState : byte { Idle, Voting, Resolved }

public sealed record BranchGraphIssue(string NodeId, string Message, bool Fatal);

public sealed record ChoiceResolution(string NodeId, int Option, string? NextNodeId, int TotalVotes, bool ByDefault);

public sealed record InteractivePackageResult(bool Ok, string? Folder, int Clips, bool LinearRendered, string Message);

/// <summary>
/// [PromptFlowStudio] v16.26.0 🔀 시청자 선택 분기형 비디오 오케스트레이터 — A/B(최대 4지) 결정 트리 스토리보드 · 실시간 투표 · 카운트다운 오버레이 · 인터랙티브 패키지.
///   ▸ 결정 트리: 노드(영상) + 선택지 → 다음 노드. 검증(<see cref="ValidateGraph"/>) — 시작 노드 · 중복/끊긴 링크 · 선택지 상한 · 도달 불가 · 엔딩 부재.
///   ▸ 라이브 투표: <see cref="BeginChoice"/> → <see cref="Vote"/>(Interlocked 카운터 · 무할당 핫패스) / 채팅 "A"·"1"·"!B" 파싱(<see cref="ParseVote"/> · 시청자당 1표)
///     → 마감 타이머(250ms 폴링 · 예외 격리) 자동 <see cref="Resolve"/>(최다 득표 · 동률/무응답은 기본 선택지) · <see cref="InteractiveConversionAnalytics"/> 실시간 기록.
///   ▸ FFmpeg: 선택 구간 카운트다운 오버레이(drawbox + drawtext <c>%{eif}</c>) · 마지막 프레임 홀드(tpad clone)로 투표 대기 심리스 연장 · 페이드 트랜지션
///     · v16.26.0 쇼핑 핫스팟 drawbox 번인 → 기본 경로 선형 영상(비인터랙티브 플랫폼용 · concat 무재인코딩).
///   ▸ 1-Click 패키지: 노드 클립 + graph.json(Utf8JsonWriter) + player.html(선택 버튼 · 카운트다운 · 핫스팟 팝업 · 이벤트 로그 JSON 저장 → 분석기 가져오기 호환).
///     ffmpeg 부재/렌더 실패 시 [부분 구제] — 클립·플레이어는 보존하고 선형 영상만 생략.
/// </summary>
public sealed class InteractiveChoiceBranchingService : IDisposable
{
    public const int MaxNodes = 64;
    public const int MaxOptions = InteractiveConversionAnalytics.MaxOptions;
    public const double MinCountdown = 3;
    public const double MaxCountdown = 20;
    public const double FadeSeconds = 0.25;
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<string?> _ffmpeg;
    private readonly InteractiveConversionAnalytics? _analytics;
    private readonly ShoppableVideoOverlayEngine? _shop;
    private readonly Broadcast.LiveChatAiReactionEngine? _chat;
    private readonly object _gate = new();
    private readonly List<ChoiceNode> _nodes = new();
    private readonly int[] _votes = new int[MaxOptions];
    private readonly HashSet<string> _voters = new(StringComparer.Ordinal);
    private Timer? _timer;
    private string? _startId;
    private string _title = "인터랙티브 스토리";
    private ChoiceNode? _current;
    private int _state, _optionCount, _busy, _resolvedCount;
    private long _deadline, _lastRaise;
    private bool _disposed;

    public InteractiveChoiceBranchingService(IVideoMergeService merge, InteractiveConversionAnalytics analytics, ShoppableVideoOverlayEngine shop, Broadcast.LiveChatAiReactionEngine chat)
        : this(() => merge.ResolvedFfmpegPath, analytics, shop)
    {
        _chat = chat;
        chat.OnMessage += OnChatMessage;
    }

    public InteractiveChoiceBranchingService(Func<string?> ffmpeg, InteractiveConversionAnalytics? analytics, ShoppableVideoOverlayEngine? shop)
    {
        _ffmpeg = ffmpeg;
        _analytics = analytics;
        _shop = shop;
    }

    /// <summary>그래프/투표 변경 방송(투표는 250ms 스로틀).</summary>
    public event Action? OnChange;
    /// <summary>분기 확정 알림(라이브 송출 측 다음 노드 전환용).</summary>
    public event Action<ChoiceResolution>? OnResolved;

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public string? LastAction { get; private set; }
    public ChoiceResolution? LastResolution { get; private set; }
    public int ResolvedCount => Volatile.Read(ref _resolvedCount);
    public ChoiceSessionState State => (ChoiceSessionState)Volatile.Read(ref _state);
    public ChoiceNode? CurrentNode => Volatile.Read(ref _current);
    public string Title { get => _title; set => _title = string.IsNullOrWhiteSpace(value) ? "인터랙티브 스토리" : value.Trim(); }
    public string? StartNodeId { get { lock (_gate) return _startId; } set { lock (_gate) _startId = value; Raise(true); } }
    public IReadOnlyList<ChoiceNode> Nodes { get { lock (_gate) return _nodes.ToArray(); } }
    public int NodeCount { get { lock (_gate) return _nodes.Count; } }

    /// <summary>현재 투표 잔여 시간(초 · 비투표 0).</summary>
    public double RemainingSeconds => State == ChoiceSessionState.Voting ? Math.Max(0, (Interlocked.Read(ref _deadline) - Environment.TickCount64) / 1000.0) : 0;

    /// <summary>옵션별 득표(무할당 · 범위 밖 0).</summary>
    public int VotesOf(int option) => (uint)option < MaxOptions ? Volatile.Read(ref _votes[option]) : 0;

    public int TotalVotes { get { var s = 0; for (var i = 0; i < MaxOptions; i++) s += Volatile.Read(ref _votes[i]); return s; } }

    // ══════════════════════ 그래프 편집 ══════════════════════

    public bool AddNode(ChoiceNode node)
    {
        if (node is null) return false;
        lock (_gate)
        {
            if (_nodes.Count >= MaxNodes) return false;
            foreach (var n in _nodes) if (n.Id == node.Id) return false;
            _nodes.Add(node);
            _startId ??= node.Id;
        }
        LastAction = "➕ 노드 추가 · " + node.Title;
        Raise(true);
        return true;
    }

    /// <summary>노드 삭제 + 해당 노드로 향하는 선택지 제거(기본 선택지 인덱스 보정).</summary>
    public void RemoveNode(string id)
    {
        lock (_gate)
        {
            _nodes.RemoveAll(n => n.Id == id);
            foreach (var n in _nodes)
            {
                n.Options.RemoveAll(o => o.TargetNodeId == id);
                if (n.DefaultOption >= n.Options.Count) n.DefaultOption = 0;
            }
            if (_startId == id) _startId = _nodes.Count > 0 ? _nodes[0].Id : null;
        }
        Raise(true);
    }

    /// <summary>선택지 추가(상한 4 · 대상 노드 존재 필수).</summary>
    public bool LinkOption(string fromId, string label, string targetId)
    {
        lock (_gate)
        {
            var from = Find(fromId);
            if (from is null || Find(targetId) is null || from.Options.Count >= MaxOptions) return false;
            from.Options.Add(new ChoiceOption { Label = string.IsNullOrWhiteSpace(label) ? OptionLetter(from.Options.Count) : label.Trim(), TargetNodeId = targetId });
        }
        Raise(true);
        return true;
    }

    public void ClearGraph()
    {
        CancelVoting();
        lock (_gate) { _nodes.Clear(); _startId = null; }
        LastAction = "그래프 초기화";
        Raise(true);
    }

    /// <summary>
    /// 스토리보드 컷 → 선형 노드 체인 시드(완성 영상이 있는 컷만 · 각 노드는 다음 컷으로 자동 진행). 반환: 생성 노드 수.
    /// 이후 에디터에서 선택지를 추가해 분기를 만든다.
    /// </summary>
    public int SeedFromProject(StoryboardProject project)
    {
        if (project is null) return 0;
        var created = new List<ChoiceNode>();
        var i = 0;
        foreach (var s in project.Scenes)
        {
            i++;
            if (string.IsNullOrWhiteSpace(s.LocalPath) || !File.Exists(s.LocalPath)) continue;
            if (created.Count >= MaxNodes) break;
            created.Add(new ChoiceNode { Id = "n_" + s.Id, Title = "컷 " + i.ToString(CultureInfo.InvariantCulture), VideoPath = s.LocalPath });
        }
        for (var k = 0; k + 1 < created.Count; k++)
            created[k].Options.Add(new ChoiceOption { Label = "계속", TargetNodeId = created[k + 1].Id });
        CancelVoting();
        lock (_gate)
        {
            _nodes.Clear();
            _nodes.AddRange(created);
            _startId = created.Count > 0 ? created[0].Id : null;
        }
        Title = project.Title;
        LastAction = "🎬 스토리보드 컷 " + created.Count.ToString(CultureInfo.InvariantCulture) + "개 시드";
        Raise(true);
        return created.Count;
    }

    /// <summary>편집 후 UI 갱신 알림.</summary>
    public void Touch() => Raise(true);

    private ChoiceNode? Find(string? id)
    {
        if (id is null) return null;
        foreach (var n in _nodes) if (n.Id == id) return n;
        return null;
    }

    public ChoiceNode? NodeOf(string? id) { lock (_gate) return Find(id); }

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    public static string OptionLetter(int i) => i switch { 0 => "A", 1 => "B", 2 => "C", 3 => "D", _ => "?" };

    /// <summary>결정 트리 검증 — Fatal 은 내보내기 차단, 나머지는 경고.</summary>
    public static List<BranchGraphIssue> ValidateGraph(IReadOnlyList<ChoiceNode> nodes, string? startId)
    {
        var issues = new List<BranchGraphIssue>();
        if (nodes.Count == 0) { issues.Add(new("", "노드가 없습니다.", true)); return issues; }
        if (nodes.Count > MaxNodes) issues.Add(new("", "노드 상한(" + MaxNodes.ToString(CultureInfo.InvariantCulture) + ") 초과", true));
        var index = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (string.IsNullOrWhiteSpace(n.Id) || !IsSafeId(n.Id)) { issues.Add(new(n.Id ?? "", "노드 ID 형식 오류(영문/숫자/_- · 32자 이하)", true)); continue; }
            if (!index.TryAdd(n.Id, i)) issues.Add(new(n.Id, "중복 노드 ID", true));
        }
        if (startId is null || !index.ContainsKey(startId)) issues.Add(new(startId ?? "", "시작 노드가 지정되지 않았습니다.", true));
        var endings = 0;
        foreach (var n in nodes)
        {
            if (n.Options.Count > MaxOptions) issues.Add(new(n.Id, "선택지는 최대 4개입니다.", true));
            if (n.IsEnding) endings++;
            if (n.IsChoicePoint)
            {
                if ((uint)n.DefaultOption >= (uint)n.Options.Count) issues.Add(new(n.Id, "기본 선택지 범위 오류", true));
                if (n.CountdownSeconds < MinCountdown || n.CountdownSeconds > MaxCountdown) issues.Add(new(n.Id, "카운트다운은 3~20초입니다.", true));
            }
            foreach (var o in n.Options)
            {
                if (!index.ContainsKey(o.TargetNodeId)) issues.Add(new(n.Id, "끊긴 링크 → " + o.TargetNodeId, true));
                if (n.IsChoicePoint && string.IsNullOrWhiteSpace(o.Label)) issues.Add(new(n.Id, "선택지 라벨이 비어 있습니다.", false));
            }
            if (string.IsNullOrWhiteSpace(n.VideoPath) || !File.Exists(n.VideoPath)) issues.Add(new(n.Id, "영상 파일 없음", false));
        }
        if (endings == 0) issues.Add(new("", "엔딩 노드(선택지 0개)가 없습니다 — 무한 루프 스토리", false));
        if (startId is not null && index.ContainsKey(startId))
        {
            var depth = ComputeDepths(nodes, startId);
            foreach (var n in nodes) if (!depth.ContainsKey(n.Id)) issues.Add(new(n.Id, "시작 노드에서 도달할 수 없습니다.", false));
        }
        return issues;
    }

    public static bool HasFatal(List<BranchGraphIssue> issues)
    {
        foreach (var i in issues) if (i.Fatal) return true;
        return false;
    }

    /// <summary>시작 노드 기준 BFS 깊이(트리 시각화 열 배치 · 도달 불가 노드는 미포함).</summary>
    public static Dictionary<string, int> ComputeDepths(IReadOnlyList<ChoiceNode> nodes, string startId)
    {
        var map = new Dictionary<string, ChoiceNode>(nodes.Count, StringComparer.Ordinal);
        foreach (var n in nodes) map.TryAdd(n.Id, n);
        var depth = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        if (!map.ContainsKey(startId)) return depth;
        var q = new Queue<string>();
        depth[startId] = 0; q.Enqueue(startId);
        while (q.Count > 0)
        {
            var id = q.Dequeue();
            var d = depth[id];
            foreach (var o in map[id].Options)
                if (map.ContainsKey(o.TargetNodeId) && depth.TryAdd(o.TargetNodeId, d + 1)) q.Enqueue(o.TargetNodeId);
        }
        return depth;
    }

    /// <summary>기본 선택지를 따라가는 경로(사이클 시 첫 재방문에서 중단).</summary>
    public static List<ChoiceNode> DefaultPath(IReadOnlyList<ChoiceNode> nodes, string? startId)
    {
        var path = new List<ChoiceNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur = startId;
        while (cur is not null && seen.Add(cur))
        {
            ChoiceNode? n = null;
            foreach (var x in nodes) if (x.Id == cur) { n = x; break; }
            if (n is null) break;
            path.Add(n);
            cur = n.Options.Count == 0 ? null : n.Options[(uint)n.DefaultOption < (uint)n.Options.Count ? n.DefaultOption : 0].TargetNodeId;
        }
        return path;
    }

    /// <summary>최다 득표 선택 — 동률이면 기본 선택지(동률군에 있을 때) 아니면 낮은 인덱스, 무투표면 기본.</summary>
    public static int PickWinner(ReadOnlySpan<int> votes, int count, int defaultOption, out bool byDefault)
    {
        count = Math.Clamp(count, 0, votes.Length);
        var def = (uint)defaultOption < (uint)count ? defaultOption : 0;
        int best = -1, bestVotes = 0;
        for (var i = 0; i < count; i++)
        {
            if (votes[i] > bestVotes) { best = i; bestVotes = votes[i]; }
        }
        if (best < 0) { byDefault = true; return def; }
        byDefault = false;
        if (votes[def] == bestVotes && def != best) { byDefault = true; return def; }
        return best;
    }

    /// <summary>채팅 투표 파싱(무할당) — "A"·"b"·"1"·"!A"·"#2"·"A!" 등 → 0~3, 그 외 -1.</summary>
    public static int ParseVote(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.Length > 0 && text[0] is '!' or '#' or '/') text = text[1..].TrimStart();
        while (text.Length > 1 && text[^1] is '!' or '.' or '?' or '~') text = text[..^1];
        if (text.Length != 1) return -1;
        return text[0] switch
        {
            'A' or 'a' or '1' or 'Ａ' or 'ａ' or '１' => 0,
            'B' or 'b' or '2' or 'Ｂ' or 'ｂ' or '２' => 1,
            'C' or 'c' or '3' or 'Ｃ' or 'ｃ' or '３' => 2,
            'D' or 'd' or '4' or 'Ｄ' or 'ｄ' or '４' => 3,
            _ => -1,
        };
    }

    /// <summary>ffmpeg stderr 의 "Duration: hh:mm:ss.xx" → 초(실패 0).</summary>
    public static double ParseDuration(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return 0;
        var i = stderr.IndexOf("Duration: ", StringComparison.Ordinal);
        if (i < 0) return 0;
        var s = stderr.AsSpan(i + 10);
        var end = s.IndexOf(',');
        if (end > 0) s = s[..end];
        return TimeSpan.TryParseExact(s.Trim(), @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture, out var ts) ? ts.TotalSeconds : 0;
    }

    /// <summary>drawtext 표시 문자열 정화 — 따옴표/역슬래시/필터 구분자/제어문자 제거 · 길이 제한.</summary>
    public static string SanitizeDrawText(string? s, int max = 40)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (char.IsControl(ch) || ch is '\'' or '"' or '\\' or ':' or ',' or ';' or '[' or ']' or '%' or '`') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static bool IsSafeId(string id)
    {
        if (id.Length > 32) return false;
        foreach (var ch in id) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')) return false;
        return true;
    }

    /// <summary>
    /// 선택 구간 오버레이 필터(-vf) — 영상 끝 카운트다운 구간에 반투명 패널 + 질문 + 선택지 + 남은 초.
    /// <paramref name="duration"/> 은 원본 길이, <paramref name="hold"/> 만큼 마지막 프레임을 복제해 투표 대기 구간을 연장(0 이면 연장 없음).
    /// 선택 지점이 아니면 페이드만.
    /// </summary>
    public static string BuildChoiceOverlayFilter(ChoiceNode node, double duration, double hold, string? fontPath, int width, int height)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append("scale=").Append(width.ToString(inv)).Append(':').Append(height.ToString(inv)).Append(":force_original_aspect_ratio=decrease,pad=")
          .Append(width.ToString(inv)).Append(':').Append(height.ToString(inv)).Append(":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p");
        hold = Math.Clamp(hold, 0, MaxCountdown);
        if (hold > 0) sb.Append(",tpad=stop_mode=clone:stop_duration=").Append(hold.ToString("0.###", inv));
        var total = Math.Max(0.1, duration + hold);
        sb.Append(",fade=t=in:st=0:d=").Append(FadeSeconds.ToString("0.###", inv));
        if (node.IsChoicePoint)
        {
            var cd = Math.Clamp(node.CountdownSeconds, MinCountdown, MaxCountdown);
            var ts = Math.Max(0, total - cd);
            var te = total.ToString("0.###", inv);
            var en = ":enable='between(t," + ts.ToString("0.###", inv) + "," + te + ")'";
            var font = fontPath is null ? "" : "fontfile='" + FontResolver.EscapeDrawPath(fontPath) + "':";
            var big = Math.Max(24, height / 18).ToString(inv);
            var mid = Math.Max(18, height / 30).ToString(inv);
            var n = Math.Min(node.Options.Count, MaxOptions);
            var panelH = (int)(height * (0.12 + 0.06 * n));
            sb.Append(",drawbox=x=0:y=ih-").Append(panelH.ToString(inv)).Append(":w=iw:h=").Append(panelH.ToString(inv)).Append(":color=black@0.55:t=fill").Append(en);
            var y = height - panelH + height / 40;
            var prompt = SanitizeDrawText(string.IsNullOrWhiteSpace(node.Prompt) ? "선택하세요" : node.Prompt, 48);
            sb.Append(",drawtext=").Append(font).Append("expansion=none:text='").Append(prompt).Append("':fontsize=").Append(mid)
              .Append(":fontcolor=white:x=(w-text_w)/2:y=").Append(y.ToString(inv)).Append(en);
            for (var i = 0; i < n; i++)
            {
                y += height / 17;
                var label = OptionLetter(i) + "  " + SanitizeDrawText(node.Options[i].Label);
                sb.Append(",drawtext=").Append(font).Append("expansion=none:text='").Append(label).Append("':fontsize=").Append(mid)
                  .Append(":fontcolor=").Append(i == node.DefaultOption ? "0xFFD54F" : "white").Append(":box=1:boxcolor=0x5B5BF0@0.75:boxborderw=12:x=(w-text_w)/2:y=")
                  .Append(y.ToString(inv)).Append(en);
            }
            sb.Append(",drawtext=").Append(font).Append("text='%{eif\\:ceil(").Append(te).Append("-t)\\:d}':fontsize=").Append(big)
              .Append(":fontcolor=white:box=1:boxcolor=0xFF4F8B@0.85:boxborderw=16:x=w-text_w-40:y=40").Append(en);
        }
        var fadeOut = Math.Max(0, total - FadeSeconds);
        sb.Append(",fade=t=out:st=").Append(fadeOut.ToString("0.###", inv)).Append(":d=").Append(FadeSeconds.ToString("0.###", inv));
        return sb.ToString();
    }

    /// <summary>노드 세그먼트 렌더 인자(정규화 · 오버레이 · 쇼핑 drawbox 체인) — 원본 오디오 없으면 무음 트랙 생성, 있으면 홀드 구간 무음 패딩.</summary>
    public static List<string> BuildSegmentArgs(string input, string output, string videoFilter, double totalSeconds, bool hasAudio)
    {
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error", "-y", "-i", input };
        if (hasAudio) a.AddRange(new[] { "-filter_complex", "[0:v]" + videoFilter + "[v];[0:a]aresample=48000,apad[a]" });
        else a.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-filter_complex", "[0:v]" + videoFilter + "[v];[1:a]anull[a]" });
        a.AddRange(new[]
        {
            "-map", "[v]", "-map", "[a]", "-t", totalSeconds.ToString("0.###", inv),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", output,
        });
        return a;
    }

    // ══════════════════════ 라이브 투표(핫패스) ══════════════════════

    /// <summary>선택 지점 투표 시작(카운트다운 = 노드 설정) — 기존 투표는 취소. 선택 지점이 아니면 false.</summary>
    public bool BeginChoice(string nodeId)
    {
        ChoiceNode? n;
        lock (_gate)
        {
            if (_disposed) return false;
            n = Find(nodeId);
            if (n is null || !n.IsChoicePoint) return false;
            for (var i = 0; i < MaxOptions; i++) Volatile.Write(ref _votes[i], 0);
            _voters.Clear();
            _optionCount = Math.Min(n.Options.Count, MaxOptions);
            Volatile.Write(ref _current, n);
            var cd = Math.Clamp(n.CountdownSeconds, MinCountdown, MaxCountdown);
            Interlocked.Exchange(ref _deadline, Environment.TickCount64 + (long)(cd * 1000));
            Volatile.Write(ref _state, (int)ChoiceSessionState.Voting);
            _timer ??= new Timer(static s => ((InteractiveChoiceBranchingService)s!).OnTick(), this, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(250, 250);
        }
        _analytics?.Record(InteractiveEventKind.ChoiceShown, n.Id);
        LastAction = "🗳 투표 시작 · " + n.Title;
        Raise(true);
        return true;
    }

    /// <summary>투표 1건(무할당 · 투표 중 + 범위 내만 수락).</summary>
    public bool Vote(int option)
    {
        if (Volatile.Read(ref _state) != (int)ChoiceSessionState.Voting || (uint)option >= (uint)Volatile.Read(ref _optionCount)) return false;
        var n = Volatile.Read(ref _current);
        if (n is null) return false;
        Interlocked.Increment(ref _votes[option]);
        _analytics?.Record(InteractiveEventKind.ChoiceMade, n.Id, option);
        Raise(false);
        return true;
    }

    /// <summary>채팅 메시지 투표 — 시청자당 1표(투표 세션 내) · 파싱 실패/중복 false.</summary>
    public bool VoteFromChat(string? user, string? text)
    {
        if (text is null || Volatile.Read(ref _state) != (int)ChoiceSessionState.Voting) return false;
        var opt = ParseVote(text);
        if (opt < 0) return false;
        if (!string.IsNullOrEmpty(user))
        {
            lock (_gate)
            {
                if (_voters.Count >= 100_000 || !_voters.Add(user)) return false;
            }
        }
        return Vote(opt);
    }

    /// <summary>투표 마감 → 승자 확정(투표 중이 아니면 null). 무투표는 이탈로 기록.</summary>
    public ChoiceResolution? Resolve()
    {
        ChoiceNode? n;
        int winner, total = 0; bool byDefault;
        lock (_gate)
        {
            if (Volatile.Read(ref _state) != (int)ChoiceSessionState.Voting) return null;
            n = Volatile.Read(ref _current);
            if (n is null) return null;
            Span<int> snap = stackalloc int[MaxOptions];
            for (var i = 0; i < MaxOptions; i++) { snap[i] = Volatile.Read(ref _votes[i]); total += snap[i]; }
            winner = PickWinner(snap, _optionCount, n.DefaultOption, out byDefault);
            Volatile.Write(ref _state, (int)ChoiceSessionState.Resolved);
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (total == 0) _analytics?.Record(InteractiveEventKind.ChoiceAbandon, n.Id);
        var next = (uint)winner < (uint)n.Options.Count ? n.Options[winner].TargetNodeId : null;
        var r = new ChoiceResolution(n.Id, winner, next, total, byDefault);
        LastResolution = r;
        Interlocked.Increment(ref _resolvedCount);
        LastAction = "✅ 분기 확정 · " + OptionLetter(winner) + (byDefault ? " (기본)" : "");
        var h = OnResolved;
        if (h is not null)
            foreach (var d in h.GetInvocationList())
            {
                try { ((Action<ChoiceResolution>)d)(r); } catch (Exception ex) { Program.Log("InteractiveChoice.OnResolved", ex); }
            }
        Raise(true);
        return r;
    }

    public void CancelVoting()
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            if (Volatile.Read(ref _state) == (int)ChoiceSessionState.Voting) Volatile.Write(ref _state, (int)ChoiceSessionState.Idle);
        }
        Raise(true);
    }

    /// <summary>라이브 채팅 → 투표(투표 중일 때만 · 파싱 실패는 무시).</summary>
    private void OnChatMessage(Broadcast.LiveChatMessage m)
    {
        if (Volatile.Read(ref _state) == (int)ChoiceSessionState.Voting) VoteFromChat(m.User, m.Text);
    }

    private void OnTick()
    {
        try
        {
            if (Volatile.Read(ref _state) != (int)ChoiceSessionState.Voting) return;
            if (Environment.TickCount64 >= Interlocked.Read(ref _deadline)) Resolve();
            else Raise(false);
        }
        catch (Exception ex) { Program.Log("InteractiveChoice.Tick", ex); }
    }

    // ══════════════════════ 1-Click 인터랙티브 패키지 ══════════════════════

    /// <summary>
    /// 패키지 내보내기 — clips/ 노드 영상 복사 · graph.json · player.html · (ffmpeg 가능 시) 기본 경로 선형 영상(카운트다운 + 핫스팟 번인).
    /// 치명 검증 오류는 차단, 렌더 실패는 [부분 구제](선형 영상만 생략).
    /// </summary>
    public async Task<InteractivePackageResult> ExportPackageAsync(string rootFolder, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, 0, false, "다른 내보내기가 진행 중입니다.");
        LastAction = "📦 패키지 생성 중";
        Raise(true);
        try
        {
            ChoiceNode[] nodes; string? start;
            lock (_gate) { nodes = _nodes.ToArray(); start = _startId; }
            var issues = ValidateGraph(nodes, start);
            if (HasFatal(issues))
            {
                foreach (var i in issues) if (i.Fatal) return new(false, null, 0, false, "검증 실패: " + i.Message + (i.NodeId.Length > 0 ? " (" + i.NodeId + ")" : ""));
            }
            var folder = Path.Combine(rootFolder, "interactive_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            var clipDir = Path.Combine(folder, "clips");
            Directory.CreateDirectory(clipDir);

            var clipNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(n.VideoPath) || !File.Exists(n.VideoPath)) continue;
                var ext = Path.GetExtension(n.VideoPath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".mp4";
                var name = n.Id + ext.ToLowerInvariant();
                File.Copy(n.VideoPath, Path.Combine(clipDir, name), true);
                clipNames[n.Id] = "clips/" + name;
            }

            var tags = _shop?.Tags ?? Array.Empty<ShoppableTag>();
            var json = BuildGraphJson(Title, start!, nodes, clipNames, tags);
            await File.WriteAllTextAsync(Path.Combine(folder, "graph.json"), json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, "player.html"), BuildPlayerHtml(Title, json), new UTF8Encoding(false), ct).ConfigureAwait(false);

            var linear = false;
            var note = "";
            if (_ffmpeg() is { } ffmpeg)
            {
                LastAction = "🎞 기본 경로 선형 영상 렌더 중";
                Raise(true);
                (linear, note) = await RenderLinearAsync(ffmpeg, DefaultPath(nodes, start), tags, Path.Combine(folder, "linear_default.mp4"), ct).ConfigureAwait(false);
            }
            else note = "ffmpeg 없음 — 선형 영상 생략";

            var msg = "패키지 완료 · 클립 " + clipNames.Count.ToString(CultureInfo.InvariantCulture) + "개" + (linear ? " · 선형 영상 포함" : " · [부분 구제] " + note);
            LastAction = "📦 " + msg;
            return new(true, folder, clipNames.Count, linear, msg);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, null, 0, false, "취소됨"); }
        catch (Exception ex) { Program.Log("InteractiveChoice.Export", ex); return new(false, null, 0, false, "내보내기 실패: " + ex.Message); }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Raise(true);
        }
    }

    private static async Task<(bool Ok, string Note)> RenderLinearAsync(string ffmpeg, List<ChoiceNode> path, IReadOnlyList<ShoppableTag> tags, string output, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_interactive_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tmp);
            var font = FontResolver.FindKoreanFont(true);
            int w = 1080, h = 1920;
            var concat = new StringBuilder(256);
            concat.Append("ffconcat version 1.0\n");
            var segs = 0;
            foreach (var n in path)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(n.VideoPath) || !File.Exists(n.VideoPath)) continue;
                var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, n.VideoPath, ct).ConfigureAwait(false);
                if (probe is null || probe.Value.Duration <= 0) continue;
                if (segs == 0) { w = probe.Value.W & ~1; h = probe.Value.H & ~1; }
                var hold = n.IsChoicePoint ? Math.Clamp(n.CountdownSeconds, MinCountdown, MaxCountdown) : 0;
                var vf = BuildChoiceOverlayFilter(n, probe.Value.Duration, hold, font, w, h);
                var shop = ShoppableVideoOverlayEngine.BuildDrawboxFilter(tags, n.Id);
                if (shop != "null") vf += "," + shop;
                var seg = Path.Combine(tmp, "seg" + segs.ToString("000", CultureInfo.InvariantCulture) + ".mp4");
                var total = probe.Value.Duration + hold;
                if (!await RunAsync(ffmpeg, BuildSegmentArgs(n.VideoPath, seg, vf, total, true), ct).ConfigureAwait(false)
                    && !await RunAsync(ffmpeg, BuildSegmentArgs(n.VideoPath, seg, vf, total, false), ct).ConfigureAwait(false))
                    return (false, "세그먼트 렌더 실패(" + n.Id + ")");
                concat.Append("file '").Append(Path.GetFileName(seg)).Append("'\n");
                segs++;
            }
            if (segs == 0) return (false, "렌더할 영상이 없습니다");
            var list = Path.Combine(tmp, "list.ffconcat");
            await File.WriteAllTextAsync(list, concat.ToString(), ct).ConfigureAwait(false);
            var ok = await RunAsync(ffmpeg, new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", output }, ct).ConfigureAwait(false);
            return ok ? (true, "") : (false, "concat 실패");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("InteractiveChoice.Linear", ex); return (false, ex.Message); }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception ex) { Program.Log("InteractiveChoice.Cleanup", ex); }
        }
    }

    private static async Task<bool> RunAsync(string ffmpeg, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        if (!p.Start()) return false;
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(RenderTimeout);
        try
        {
            var err = p.StandardError.ReadToEndAsync(d.Token);
            _ = p.StandardOutput.ReadToEndAsync(d.Token);
            await p.WaitForExitAsync(d.Token).ConfigureAwait(false);
            if (p.ExitCode != 0) Program.Log("InteractiveChoice.Ffmpeg", new InvalidOperationException((await err.ConfigureAwait(false)).Trim()));
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    /// <summary>graph.json(플레이어 데이터) — Utf8JsonWriter 직렬화 · 안전 URL 태그만 포함.</summary>
    public static string BuildGraphJson(string title, string startId, IReadOnlyList<ChoiceNode> nodes, IReadOnlyDictionary<string, string> clips, IReadOnlyList<ShoppableTag> tags)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("title", title);
            w.WriteString("start", startId);
            w.WriteStartArray("nodes");
            foreach (var n in nodes)
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id);
                w.WriteString("title", n.Title);
                w.WriteString("video", clips.TryGetValue(n.Id, out var c) ? c : "");
                w.WriteString("prompt", n.Prompt);
                w.WriteNumber("countdown", Math.Clamp(n.CountdownSeconds, MinCountdown, MaxCountdown));
                w.WriteNumber("default", (uint)n.DefaultOption < (uint)n.Options.Count ? n.DefaultOption : 0);
                w.WriteStartArray("options");
                foreach (var o in n.Options)
                {
                    w.WriteStartObject();
                    w.WriteString("label", o.Label);
                    w.WriteString("target", o.TargetNodeId);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("tags");
            foreach (var t in tags)
            {
                if (ShoppableVideoOverlayEngine.Validate(t) is not null) continue;
                w.WriteStartObject();
                w.WriteString("id", t.Id);
                w.WriteString("node", t.NodeId);
                w.WriteString("name", t.ProductName);
                w.WriteString("url", t.Url);
                w.WriteString("coupon", t.CouponCode ?? "");
                w.WriteString("couponText", t.CouponText ?? "");
                w.WriteString("price", t.PriceText ?? "");
                w.WriteString("color", "#" + SponsorVideoCompositorService.Hex(t.Color));
                w.WriteNumber("start", t.StartSeconds);
                w.WriteNumber("end", t.EndSeconds);
                w.WriteStartArray("keys");
                foreach (var k in t.Keyframes)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(Math.Round(k.T, 3));
                    w.WriteNumberValue(Math.Round(k.Box.X, 4)); w.WriteNumberValue(Math.Round(k.Box.Y, 4));
                    w.WriteNumberValue(Math.Round(k.Box.W, 4)); w.WriteNumberValue(Math.Round(k.Box.H, 4));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// 독립 실행 player.html — 그래프 JSON 을 script(application/json)로 임베드(&lt;/ 이스케이프) · 표시 문자열은 textContent 로만 사용(XSS 차단)
    /// · 이벤트 로그(shown/pick/abandon/imp/click/buy)는 localStorage 보존 + JSON 저장 → <see cref="InteractiveConversionAnalytics.ImportEventLog"/> 호환.
    /// </summary>
    public static string BuildPlayerHtml(string title, string graphJson)
    {
        var safeJson = graphJson.Replace("</", "<\\/", StringComparison.Ordinal).Replace("<!--", "<\\!--", StringComparison.Ordinal);
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        return PlayerTemplate.Replace("{{TITLE}}", safeTitle, StringComparison.Ordinal).Replace("{{GRAPH}}", safeJson, StringComparison.Ordinal);
    }

    private const string PlayerTemplate = """
<!DOCTYPE html>
<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{TITLE}}</title>
<style>
body{margin:0;background:#0b0b12;color:#fff;font-family:system-ui,'Malgun Gothic',sans-serif;display:flex;flex-direction:column;align-items:center}
#stage{position:relative;max-width:100vw;max-height:88vh;aspect-ratio:9/16;background:#000}
video{width:100%;height:100%;object-fit:contain;display:block}
#choice{position:absolute;left:0;right:0;bottom:0;padding:18px;background:linear-gradient(transparent,rgba(0,0,0,.85));display:none;text-align:center}
#choice h3{margin:0 0 10px}#opts button{display:block;width:100%;margin:8px 0;padding:14px;border:0;border-radius:12px;background:#5b5bf0;color:#fff;font-size:17px;cursor:pointer}
#cd{position:absolute;top:14px;right:14px;background:#ff4f8b;border-radius:999px;padding:8px 14px;font-weight:700;display:none}
.hs{position:absolute;border:3px solid;border-radius:12px;background:rgba(255,255,255,.06);cursor:pointer}
.hs::after{content:'';position:absolute;left:50%;top:50%;width:16px;height:16px;margin:-8px;border-radius:50%;background:currentColor;animation:p 1.2s infinite}
@keyframes p{50%{transform:scale(1.5);opacity:.5}}
#pop{position:absolute;left:8%;right:8%;top:30%;background:#fff;color:#111;border-radius:16px;padding:16px;display:none;text-align:center}
#pop a{display:inline-block;margin-top:10px;padding:10px 18px;border-radius:10px;background:#ff4f8b;color:#fff;text-decoration:none}
#bar{margin:10px;display:flex;gap:8px}#bar button{padding:8px 12px;border-radius:8px;border:0;background:#22223a;color:#fff;cursor:pointer}
</style></head><body>
<div id="stage"><video id="v" playsinline></video><div id="hsl"></div><div id="cd"></div>
<div id="choice"><h3 id="q"></h3><div id="opts"></div></div>
<div id="pop"><b id="pn"></b><div id="pp"></div><div id="pc"></div><a id="pb" target="_blank" rel="noopener noreferrer">구매하기</a><div><button id="px">닫기</button></div></div></div>
<div id="bar"><button id="restart">처음부터</button><button id="save">이벤트 로그 저장</button></div>
<script type="application/json" id="graph">{{GRAPH}}</script>
<script>
(function(){
const g=JSON.parse(document.getElementById('graph').textContent);const byId={};g.nodes.forEach(n=>byId[n.id]=n);
const v=document.getElementById('v'),choice=document.getElementById('choice'),opts=document.getElementById('opts'),q=document.getElementById('q'),cd=document.getElementById('cd'),hsl=document.getElementById('hsl'),pop=document.getElementById('pop');
const KEY='pfs_interactive_events';let ev=[];try{ev=JSON.parse(localStorage.getItem(KEY)||'[]')}catch(e){ev=[]}
function log(k,id,o,val){ev.push({k:k,id:id,o:o==null?-1:o,v:val||0});if(ev.length>20000)ev.shift();try{localStorage.setItem(KEY,JSON.stringify(ev))}catch(e){}}
let cur=null,deciding=false,timer=null,seen={};
function play(id){const n=byId[id];if(!n)return;cur=n;deciding=false;seen={};choice.style.display='none';cd.style.display='none';pop.style.display='none';hsl.textContent='';if(!n.video){next(n);return}v.src=n.video;v.play().catch(()=>{})}
function next(n){const o=n.options;if(!o.length){cd.style.display='none';return}if(o.length===1){play(o[0].target);return}}
function showChoice(){if(deciding||!cur||cur.options.length<2)return;deciding=true;log('shown',cur.id);q.textContent=cur.prompt||'선택하세요';opts.textContent='';
cur.options.forEach((o,i)=>{const b=document.createElement('button');b.textContent=String.fromCharCode(65+i)+'  '+o.label;b.onclick=()=>pick(i,true);opts.appendChild(b)});
choice.style.display='block';let left=cur.countdown;cd.textContent=left;cd.style.display='block';clearInterval(timer);timer=setInterval(()=>{left--;cd.textContent=Math.max(0,left);if(left<=0)pick(cur.default,false)},1000)}
function pick(i,user){clearInterval(timer);if(!cur||!deciding)return;const n=cur;deciding=false;if(user)log('pick',n.id,i);play(n.options[i].target)}
v.addEventListener('timeupdate',()=>{if(!cur)return;const d=v.duration||0;if(cur.options.length>=2&&d>0&&v.currentTime>=Math.max(0,d-cur.countdown))showChoice();drawTags()});
v.addEventListener('ended',()=>{if(!cur)return;if(cur.options.length===1)play(cur.options[0].target);else if(cur.options.length>=2){showChoice();v.pause()}});
function lerp(keys,t){if(!keys.length)return null;if(t<=keys[0][0])return keys[0];const l=keys[keys.length-1];if(t>=l[0])return l;let lo=0,hi=keys.length-1;while(hi-lo>1){const m=(lo+hi)>>1;if(keys[m][0]<=t)lo=m;else hi=m}const a=keys[lo],b=keys[hi],u=(t-a[0])/Math.max(1e-3,b[0]-a[0]);return [t,a[1]+(b[1]-a[1])*u,a[2]+(b[2]-a[2])*u,a[3]+(b[3]-a[3])*u,a[4]+(b[4]-a[4])*u]}
function drawTags(){const t=v.currentTime;hsl.textContent='';g.tags.forEach(tag=>{if(tag.node!==cur.id||t<tag.start||t>tag.end)return;const b=lerp(tag.keys,t);if(!b)return;if(!seen[tag.id]){seen[tag.id]=1;log('imp',tag.id)}
const e=document.createElement('div');e.className='hs';e.style.left=(b[1]*100)+'%';e.style.top=(b[2]*100)+'%';e.style.width=(b[3]*100)+'%';e.style.height=(b[4]*100)+'%';e.style.borderColor=tag.color;e.style.color=tag.color;e.onclick=()=>openTag(tag);hsl.appendChild(e)})}
function openTag(tag){log('click',tag.id);v.pause();document.getElementById('pn').textContent=tag.name;document.getElementById('pp').textContent=tag.price;document.getElementById('pc').textContent=tag.coupon?(tag.couponText||'쿠폰')+' · '+tag.coupon:'';
const a=document.getElementById('pb');if(/^https:\/\//i.test(tag.url)||/^http:\/\/(localhost|127\.0\.0\.1)/i.test(tag.url)){a.href=tag.url;a.style.display='inline-block';a.onclick=()=>log('buy',tag.id)}else a.style.display='none';pop.style.display='block'}
document.getElementById('px').onclick=()=>{pop.style.display='none';v.play().catch(()=>{})};
document.getElementById('restart').onclick=()=>play(g.start);
document.getElementById('save').onclick=()=>{const b=new Blob([JSON.stringify({events:ev})],{type:'application/json'});const a=document.createElement('a');a.href=URL.createObjectURL(b);a.download='interactive_events.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000)};
window.addEventListener('pagehide',()=>{if(deciding&&cur)log('abandon',cur.id)});
play(g.start);
})();
</script></body></html>
""";

    private void Raise(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastRaise) < 250) return;
        Interlocked.Exchange(ref _lastRaise, now);
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("InteractiveChoice.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        Timer? t;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            t = _timer; _timer = null;
            Volatile.Write(ref _state, (int)ChoiceSessionState.Idle);
        }
        t?.Dispose();
        if (_chat is not null) _chat.OnMessage -= OnChatMessage;
        OnChange = null;
        OnResolved = null;
    }
}
