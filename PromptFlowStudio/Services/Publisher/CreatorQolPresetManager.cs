using System.Text.Json;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.StationMaster;

namespace PromptFlowStudio.Services.Publisher;

public enum CreatorPresetKind : byte { Gaming, LiveCommerce, RadioTalk, Concert, Custom }

/// <summary>v17.71.0 크리에이터 프리셋(3DGS 세팅 · 아바타 배치 · BGM · MCR 스위칭 룰 · 배포 옵션) — JSON 영속.</summary>
public sealed class CreatorPreset
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "⭐";
    public CreatorPresetKind Kind { get; set; } = CreatorPresetKind.Custom;
    public StationProgramKind Program { get; set; } = StationProgramKind.Talkshow;
    /// <summary>3DGS 세트 스플랫 수(v17.66 MCR 생성 시 반영 · 실행 중 변경은 다음 MCR 재생성부터).</summary>
    public int EnvSplats { get; set; } = 150_000;
    public int Avatars { get; set; } = 3;
    public double BgmBpm { get; set; } = 120;
    public float BgmLevel { get; set; } = 0.5f;
    public bool AutoDirect { get; set; } = true;
    public float ReelsThreshold { get; set; } = 85f;
    public bool UploadYouTube { get; set; } = true;
    public bool UploadInstagram { get; set; } = true;
    public string Privacy { get; set; } = "private";
    public double FailoverHoldMs { get; set; } = 3_000;
    public bool BuiltIn { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<CreatorPreset>), TypeInfoPropertyName = "CreatorPresetList")]
internal sealed partial class CreatorPresetJsonContext : JsonSerializerContext { }

/// <summary>v17.71.0 프리셋 적용 대상(러너 결선 · 테스트 주입) — 미결선 항목은 건너뜀.</summary>
public interface ICreatorPresetTarget
{
    void ApplyPreset(CreatorPreset p, List<string> log);
}

/// <summary>
/// [PromptFlow Studio v17.71.0] 🎛️ 크리에이터 편의성(QoL) 프리셋 매니저 &amp; 1-Click 위자드.
///   ▸ 내장 프리셋 4(🎮 게임 방송 · 🛍️ 라이브 커머스 · 📻 라디오 토크쇼 · 🎵 리듬 콘서트) + 사용자 프리셋(현재 세팅 캡처 · 이름 저장 · 삭제) → System.Text.Json 소스 생성 영속(원자적 교체 · 손상 시 내장만 복구).
///   ▸ 적용: 아바타 슬롯(v17.65) · BGM 템포/레벨(v17.53) · MCR 자동 디렉팅(v17.66) · 즉시 편성(v17.70 Mix) · 페일오버 보류 · 릴스 임계(v17.69) · 업로드 플랫폼/공개 범위(v17.71).
///   ▸ 1-Click: 프리셋 적용 → 릴스 팩토리 가동 · 자동 배포 → 방송국 24/7 송출 → 자동 업로드 활성 — 단계별 체크리스트 반환(실패 단계 [부분 구제] 후 계속).
/// </summary>
public sealed class CreatorQolPresetManager
{
    private readonly List<CreatorPreset> _user = new();
    private readonly object _gate = new();

    public CreatorQolPresetManager(string path) { Path = path; }

    public string Path { get; }
    public string? LastError { get; private set; }
    public long Saves { get; private set; }
    public long Loads { get; private set; }
    public long Applies { get; private set; }
    public string? ActiveName { get; private set; }

    public static readonly CreatorPreset[] BuiltIns =
    {
        new() { Name = "게임 방송", Icon = "🎮", Kind = CreatorPresetKind.Gaming, Program = StationProgramKind.RhythmConcert, EnvSplats = 60_000, Avatars = 2, BgmBpm = 140, BgmLevel = 0.75f, AutoDirect = false, ReelsThreshold = 80f, Privacy = "public", FailoverHoldMs = 2_000, BuiltIn = true },
        new() { Name = "라이브 커머스", Icon = "🛍️", Kind = CreatorPresetKind.LiveCommerce, Program = StationProgramKind.LiveCommerce, EnvSplats = 150_000, Avatars = 2, BgmBpm = 112, BgmLevel = 0.45f, AutoDirect = true, ReelsThreshold = 85f, Privacy = "public", FailoverHoldMs = 3_000, BuiltIn = true },
        new() { Name = "라디오 토크쇼", Icon = "📻", Kind = CreatorPresetKind.RadioTalk, Program = StationProgramKind.Talkshow, EnvSplats = 60_000, Avatars = 3, BgmBpm = 96, BgmLevel = 0.25f, AutoDirect = true, ReelsThreshold = 88f, UploadInstagram = false, FailoverHoldMs = 4_000, BuiltIn = true },
        new() { Name = "리듬 콘서트", Icon = "🎵", Kind = CreatorPresetKind.Concert, Program = StationProgramKind.RhythmConcert, EnvSplats = 300_000, Avatars = 2, BgmBpm = 128, BgmLevel = 0.9f, AutoDirect = false, ReelsThreshold = 82f, Privacy = "public", FailoverHoldMs = 2_000, BuiltIn = true },
    };

    public int Count { get { lock (_gate) return BuiltIns.Length + _user.Count; } }
    public CreatorPreset At(int i) { lock (_gate) return i < BuiltIns.Length ? BuiltIns[i] : _user[i - BuiltIns.Length]; }
    public CreatorPreset? Find(string name) { lock (_gate) { foreach (var b in BuiltIns) if (b.Name == name) return b; foreach (var u in _user) if (u.Name == name) return u; } return null; }

    // ══════════════════════ 영속(비핫패스) ══════════════════════
    public bool Load()
    {
        try
        {
            if (!File.Exists(Path)) return false;
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var list = JsonSerializer.Deserialize(fs, CreatorPresetJsonContext.Default.CreatorPresetList);
            lock (_gate)
            {
                _user.Clear();
                if (list is not null) foreach (var p in list) if (!string.IsNullOrWhiteSpace(p.Name) && !p.BuiltIn && Validate(p)) _user.Add(p);
            }
            Loads++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = "프리셋 파일 손상/접근 불가 — 내장 프리셋만 사용: " + ex.Message;
            PromptFlowStudio.Program.Log("CreatorPreset.Load", ex);
            lock (_gate) _user.Clear();
            return false;
        }
    }

    public bool Save()
    {
        try
        {
            List<CreatorPreset> copy; lock (_gate) copy = new List<CreatorPreset>(_user);
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(fs, copy, CreatorPresetJsonContext.Default.CreatorPresetList);
            File.Move(tmp, Path, true);
            Saves++;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "프리셋 저장 실패: " + ex.Message;
            PromptFlowStudio.Program.Log("CreatorPreset.Save", ex);
            return false;
        }
    }

    /// <summary>값 범위 검증(외부 편집 JSON 방어) — 범위 밖은 클램프 · 이름 필수.</summary>
    public static bool Validate(CreatorPreset p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 40) return false;
        p.EnvSplats = Math.Clamp(p.EnvSplats, 10_000, 1_000_000);
        p.Avatars = Math.Clamp(p.Avatars, 2, 3);
        p.BgmBpm = Math.Clamp(p.BgmBpm, 60, 200);
        p.BgmLevel = Math.Clamp(p.BgmLevel, 0f, 1f);
        p.ReelsThreshold = Math.Clamp(p.ReelsThreshold, 50f, 99f);
        p.FailoverHoldMs = Math.Clamp(p.FailoverHoldMs, 250, 60_000);
        if (p.Privacy is not ("private" or "unlisted" or "public")) p.Privacy = "private";
        if (!Enum.IsDefined(p.Program)) p.Program = StationProgramKind.Talkshow;
        return true;
    }

    /// <summary>사용자 프리셋 저장(같은 이름 덮어쓰기 · 내장 이름 거부).</summary>
    public bool Upsert(CreatorPreset p)
    {
        if (!Validate(p)) { LastError = "프리셋 이름이 비었거나 40자 초과"; return false; }
        foreach (var b in BuiltIns) if (b.Name == p.Name) { LastError = "내장 프리셋 이름은 사용할 수 없습니다"; return false; }
        p.BuiltIn = false;
        lock (_gate)
        {
            var i = _user.FindIndex(u => u.Name == p.Name);
            if (i >= 0) _user[i] = p; else _user.Add(p);
        }
        return Save();
    }

    public bool Remove(string name)
    {
        bool hit; lock (_gate) hit = _user.RemoveAll(u => u.Name == name) > 0;
        return hit && Save();
    }

    /// <summary>프리셋 복제(사용자 편집 시작점).</summary>
    public static CreatorPreset Clone(CreatorPreset s, string name) => new()
    {
        Name = name, Icon = s.Icon, Kind = CreatorPresetKind.Custom, Program = s.Program, EnvSplats = s.EnvSplats, Avatars = s.Avatars, BgmBpm = s.BgmBpm, BgmLevel = s.BgmLevel,
        AutoDirect = s.AutoDirect, ReelsThreshold = s.ReelsThreshold, UploadYouTube = s.UploadYouTube, UploadInstagram = s.UploadInstagram, Privacy = s.Privacy, FailoverHoldMs = s.FailoverHoldMs,
    };

    /// <summary>프리셋 적용 — 대상별 결선 · 로그 반환.</summary>
    public List<string> Apply(CreatorPreset p, ICreatorPresetTarget target)
    {
        var log = new List<string>(8);
        Validate(p);
        try { target.ApplyPreset(p, log); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            log.Add("⚠ 적용 일부 실패: " + ex.Message);
            PromptFlowStudio.Program.Log("CreatorPreset.Apply", ex);
        }
        ActiveName = p.Name; Applies++;
        return log;
    }
}
