namespace PromptFlowStudio.Services.Editorial;

public enum EditTrackKind : byte { Video = 0, Audio }

public enum CutReason : byte { Start = 0, Speaker, Action, Beat, MaxLength, Reaction, Montage, Music }

/// <summary>타임라인 클립(프레임 단위 · 멀티캠 동기 소스이므로 SrcIn 은 소스 자체 프레임).</summary>
public struct EditClip
{
    public int Track, Source, SrcIn, RecIn, Length;
    public CutReason Reason;
    public readonly int RecOut => RecIn + Length;
}

/// <summary>
/// [PromptFlow Studio v16.79.0] ✂️ 자율 편집 멀티트랙 타임라인(사전 할당 클립 배열 · 트랙/소스 메타 · 무할당 추가).
/// 트랙 0 = V1 프로그램 컷 · 1 = V2 몽타주 · 2 = A1 BGM(관례 · 호출자 정의 가능).
/// </summary>
public sealed class EditTimeline
{
    public const int MaxClips = 4096, MaxTracks = 8, MaxSources = 16;

    private readonly EditClip[] _clips = new EditClip[MaxClips];
    private readonly string[] _trackName = new string[MaxTracks];
    private readonly EditTrackKind[] _trackKind = new EditTrackKind[MaxTracks];
    private readonly string[] _srcName = new string[MaxSources], _srcReel = new string[MaxSources];
    private int _count, _tracks, _sources;

    public EditTimeline(int fps = 30) => Fps = fps;

    public int Fps { get; set; }
    public string Title { get; set; } = "PromptFlow Autonomous Edit";
    public int Count => _count;
    public int TrackCount => _tracks;
    public int SourceCount => _sources;
    public long Version { get; private set; }
    public ref readonly EditClip Clip(int i) => ref _clips[i];
    public ReadOnlySpan<EditClip> Clips => _clips.AsSpan(0, _count);
    public string TrackName(int t) => _trackName[t];
    public EditTrackKind TrackKind(int t) => _trackKind[t];
    public string SourceName(int s) => _srcName[s];
    public string SourceReel(int s) => _srcReel[s];

    public void ResetAll() { _count = _tracks = _sources = 0; Version++; }
    public void ClearClips() { _count = 0; Version++; }

    public int AddTrack(string name, EditTrackKind kind)
    {
        if (_tracks >= MaxTracks) return -1;
        _trackName[_tracks] = name; _trackKind[_tracks] = kind;
        return _tracks++;
    }

    /// <summary>소스 등록(릴 이름 = 영숫자 8자 이내로 정규화 · CMX3600).</summary>
    public int AddSource(string name, string reel)
    {
        if (_sources >= MaxSources) return -1;
        _srcName[_sources] = name;
        Span<char> r = stackalloc char[8];
        var n = 0;
        foreach (var c in reel)
        {
            if (n == 8) break;
            if (char.IsAsciiLetterOrDigit(c) || c == '_') r[n++] = char.ToUpperInvariant(c);
        }
        _srcReel[_sources] = n == 0 ? "AX" : new string(r[..n]);
        return _sources++;
    }

    /// <summary>클립 추가(무할당). 반환 = 인덱스(-1 = 용량 초과/길이 0).</summary>
    public int Add(int track, int source, int recIn, int length, CutReason reason, int srcIn = -1)
    {
        if (_count >= MaxClips || length <= 0) return -1;
        ref var c = ref _clips[_count];
        c.Track = track; c.Source = source; c.RecIn = recIn; c.Length = length; c.SrcIn = srcIn < 0 ? recIn : srcIn; c.Reason = reason;
        Version++;
        return _count++;
    }

    /// <summary>마지막 클립 길이 연장(같은 트랙 · 연속 컷 없이 진행 중 샷).</summary>
    public void ExtendLast(int track, int recOut)
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            if (_clips[i].Track != track) continue;
            _clips[i].Length = Math.Max(1, recOut - _clips[i].RecIn);
            Version++;
            return;
        }
    }

    /// <summary>트랙 클립 전부 제거(순서 보존 압축 · 무할당).</summary>
    public void RemoveTrackClips(int track)
    {
        var w = 0;
        for (var i = 0; i < _count; i++) if (_clips[i].Track != track) _clips[w++] = _clips[i];
        _count = w; Version++;
    }

    public int TrackEnd(int track)
    {
        var e = 0;
        for (var i = 0; i < _count; i++) if (_clips[i].Track == track) e = Math.Max(e, _clips[i].RecOut);
        return e;
    }

    public int ClipsOn(int track)
    {
        var n = 0;
        for (var i = 0; i < _count; i++) if (_clips[i].Track == track) n++;
        return n;
    }
}
