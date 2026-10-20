using System.Globalization;
using PromptFlowStudio.Services.Atmosphere;

namespace PromptFlowStudio.Services.Continuity;

public enum BibleConflictKind : byte
{
    CharacterHair, CharacterScar, CharacterHandedness, CharacterWardrobe, CharacterGlasses, DeadCharacter,
    WeatherForbidden, TemperatureRange, EraProp, TimelineReversed, ContinuousGap, WeatherJump, GroundWetness, SunMismatch, NightSun,
}

/// <summary>설정 충돌(값 형식): 종류 · 심각도 · 대상 id · 관측값 · 기대값 · 씬.</summary>
public readonly record struct BibleConflict(BibleConflictKind Kind, float Severity, int Subject, float Observed, float Expected, int Scene);

/// <summary>씬 메타데이터(관측 · 정수 id = 매처 Intern).</summary>
public sealed class SceneMeta
{
    public const int MaxCast = 4, MaxProps = 16;
    public int Scene, Day, Location = -1, Era = -1;
    public float Hour, Latitude = 37.5f;
    public bool ContinuousWithPrevious, Flashback, CostumeChange;
    public WeatherScenario Weather = WeatherScenario.Clear;
    public float SnowFraction, GroundWetness, ObservedSunElevation = float.NaN;
    public int CastCount, PropCount;
    public readonly int[] Cast = new int[MaxCast], Hair = new int[MaxCast], Scar = new int[MaxCast], Hand = new int[MaxCast], Wardrobe = new int[MaxCast];
    public readonly bool[] Glasses = new bool[MaxCast];
    public readonly int[] Props = new int[MaxProps];

    public void CopyFrom(SceneMeta s)
    {
        Scene = s.Scene; Day = s.Day; Location = s.Location; Era = s.Era; Hour = s.Hour; Latitude = s.Latitude;
        ContinuousWithPrevious = s.ContinuousWithPrevious; Flashback = s.Flashback; CostumeChange = s.CostumeChange;
        Weather = s.Weather; SnowFraction = s.SnowFraction; GroundWetness = s.GroundWetness; ObservedSunElevation = s.ObservedSunElevation;
        CastCount = s.CastCount; PropCount = s.PropCount;
        s.Cast.AsSpan().CopyTo(Cast); s.Hair.AsSpan().CopyTo(Hair); s.Scar.AsSpan().CopyTo(Scar); s.Hand.AsSpan().CopyTo(Hand);
        s.Wardrobe.AsSpan().CopyTo(Wardrobe); s.Glasses.AsSpan().CopyTo(Glasses); s.Props.AsSpan().CopyTo(Props);
    }

    /// <summary>v16.84 기상/습윤 상태 결선(관측 날씨 · 지표 습윤 · 실제 태양 고도).</summary>
    public void BindEnvironment(GenerativeEnvironmentStudioRunner env)
    {
        Weather = env.Weather.State; SnowFraction = env.Weather.SnowFraction; GroundWetness = env.Wet.MeanWetness; ObservedSunElevation = env.SunElevation;
    }
}

/// <summary>
/// [PromptFlow Studio v16.88.0] 📖 씬 코히어런스 &amp; 스토리 바이블 매처(파싱 = 제어 평면 · 진단 = 무할당 정수 비교).
///   ▸ 바이블 문법(한/영 · 줄 단위): <c>캐릭터 이름: 머리=…, 흉터=…, 주손=…, 의상=…, 안경=있음|없음, 사망=씬 N</c> ·
///     <c>장소 이름: 비=불가, 눈=불가, 기온=a..b, 위도=…</c> · <c>시대 이름: 금지=소품1, 소품2</c> · <c># 주석</c>.
///   ▸ 진단: 캐릭터 외형/소지(머리 · 흉터 · 주손 · 안경 · 의상 = 의상 교체 씬 예외) · 사망 이후 등장 · 장소 기상 금지/기온 범위 · 시대 금지 소품 ·
///     타임라인 역행(회상 예외) · 연속 씬 시간 간극 · 기상 급변 · 지표 습윤(v16.84 습윤 가드) 불일치 · NOAA 태양 고도(v16.84) vs 관측 조명 · 야간 태양.
///   ▸ 정합성 점수 0..100 · 규칙 수 · 충돌 링.
/// </summary>
public sealed class NeuralSceneCoherenceStoryBibleMatcher
{
    public const int MaxCharacters = 32, MaxLocations = 32, MaxEras = 8, MaxForbidden = 16, MaxConflicts = 64;
    private const int None = -1;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _ids = new(256, StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = new(256);
    // 캐릭터 시트
    private readonly int[] _cId = new int[MaxCharacters], _cHair = new int[MaxCharacters], _cScar = new int[MaxCharacters], _cHand = new int[MaxCharacters], _cWard = new int[MaxCharacters], _cDeath = new int[MaxCharacters];
    private readonly sbyte[] _cGlasses = new sbyte[MaxCharacters];
    private int _nChar;
    // 장소
    private readonly int[] _lId = new int[MaxLocations];
    private readonly bool[] _lNoRain = new bool[MaxLocations], _lNoSnow = new bool[MaxLocations];
    private readonly float[] _lTMin = new float[MaxLocations], _lTMax = new float[MaxLocations], _lLat = new float[MaxLocations];
    private int _nLoc;
    // 시대
    private readonly int[] _eId = new int[MaxEras], _eCount = new int[MaxEras];
    private readonly int[] _eForbidden = new int[MaxEras * MaxForbidden];
    private int _nEra;
    // 진단
    private readonly BibleConflict[] _conf = new BibleConflict[MaxConflicts];
    private int _nConf;
    private readonly SceneMeta _prev = new();
    private bool _hasPrev;

    public int RuleCount { get; private set; }
    public int ParseErrors { get; private set; }
    public float CoherenceScore { get; private set; } = 100f;
    public int ConflictCount => _nConf;
    public long Diagnoses { get; private set; }
    public double LastDiagnoseMs { get; private set; }
    public string? LastAction { get; private set; }
    public int CharacterCount => _nChar;
    public int LocationCount => _nLoc;
    public int EraCount => _nEra;
    public float SunTolerance { get; set; } = 8f;
    public BibleConflict Conflict(int i) => _conf[i];

    public static string KindName(BibleConflictKind k) => k switch
    {
        BibleConflictKind.CharacterHair => "머리색", BibleConflictKind.CharacterScar => "흉터 위치", BibleConflictKind.CharacterHandedness => "주손",
        BibleConflictKind.CharacterWardrobe => "의상 설정", BibleConflictKind.CharacterGlasses => "안경", BibleConflictKind.DeadCharacter => "사망 캐릭터 등장",
        BibleConflictKind.WeatherForbidden => "장소 기상 금지", BibleConflictKind.TemperatureRange => "기온 범위", BibleConflictKind.EraProp => "시대 금지 소품",
        BibleConflictKind.TimelineReversed => "타임라인 역행", BibleConflictKind.ContinuousGap => "연속 씬 시간 간극", BibleConflictKind.WeatherJump => "기상 급변",
        BibleConflictKind.GroundWetness => "지표 습윤 불일치", BibleConflictKind.SunMismatch => "태양 고도 불일치", _ => "야간 태양",
    };

    /// <summary>문자열 → 정수 id(제어 평면 · 최초 등록 시 할당).</summary>
    public int Intern(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return None;
        s = s.Trim();
        lock (_gate)
        {
            if (_ids.TryGetValue(s, out var id)) return id;
            id = _names.Count; _names.Add(s); _ids[s] = id;
            return id;
        }
    }

    public string NameOf(int id) => id >= 0 && id < _names.Count ? _names[id] : "—";

    public int FindCharacter(int id) { for (var i = 0; i < _nChar; i++) if (_cId[i] == id) return i; return -1; }
    public int FindLocation(int id) { for (var i = 0; i < _nLoc; i++) if (_lId[i] == id) return i; return -1; }
    public int FindEra(int id) { for (var i = 0; i < _nEra; i++) if (_eId[i] == id) return i; return -1; }
    public (int Hair, int Scar, int Hand, int Wardrobe, int Glasses, int DeathScene) Sheet(int idx) => (_cHair[idx], _cScar[idx], _cHand[idx], _cWard[idx], _cGlasses[idx], _cDeath[idx]);
    public int CharacterId(int idx) => _cId[idx];
    public int LocationId(int idx) => _lId[idx];
    public (bool NoRain, bool NoSnow, float TMin, float TMax, float Lat) LocationRule(int idx) => (_lNoRain[idx], _lNoSnow[idx], _lTMin[idx], _lTMax[idx], _lLat[idx]);

    private static bool Is(string k, string a, string b) => k.Equals(a, StringComparison.OrdinalIgnoreCase) || k.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool IsNo(string v) => v is "불가" or "금지" or "없음" or "no" or "never" or "false" or "forbidden";

    /// <summary>바이블 파싱(제어 평면) → 규칙 수. 기존 규칙 초기화.</summary>
    public int Load(string? bible)
    {
        lock (_gate)
        {
            _nChar = _nLoc = _nEra = 0; RuleCount = 0; ParseErrors = 0; _hasPrev = false;
            if (string.IsNullOrWhiteSpace(bible)) { LastAction = "⚠ 바이블 비어 있음"; return 0; }
            foreach (var raw in bible.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var colon = line.IndexOf(':');
                if (colon < 0) { ParseErrors++; continue; }
                var head = line[..colon].Trim(); var body = line[(colon + 1)..].Trim();
                var sp = head.IndexOf(' ');
                if (sp < 0) { ParseErrors++; continue; }
                var kind = head[..sp].Trim(); var name = head[(sp + 1)..].Trim();
                if (Is(kind, "캐릭터", "character")) ParseCharacter(name, body);
                else if (Is(kind, "장소", "location")) ParseLocation(name, body);
                else if (Is(kind, "시대", "era")) ParseEra(name, body);
                else ParseErrors++;
            }
            LastAction = "📖 바이블 로드 — 캐릭터 " + _nChar.ToString(CultureInfo.InvariantCulture) + " · 장소 " + _nLoc.ToString(CultureInfo.InvariantCulture) + " · 시대 " + _nEra.ToString(CultureInfo.InvariantCulture) + " · 규칙 " + RuleCount.ToString(CultureInfo.InvariantCulture);
            return RuleCount;
        }
    }

    private void ParseCharacter(string name, string body)
    {
        if (_nChar >= MaxCharacters) { ParseErrors++; return; }
        var i = _nChar++;
        _cId[i] = Intern(name); _cHair[i] = _cScar[i] = _cHand[i] = _cWard[i] = None; _cGlasses[i] = -1; _cDeath[i] = int.MaxValue;
        foreach (var kv in body.Split(','))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) { ParseErrors++; continue; }
            var k = kv[..eq].Trim(); var v = kv[(eq + 1)..].Trim();
            if (Is(k, "머리", "hair")) _cHair[i] = Intern(v);
            else if (Is(k, "흉터", "scar")) _cScar[i] = Intern(v);
            else if (Is(k, "주손", "hand")) _cHand[i] = Intern(v);
            else if (Is(k, "의상", "wardrobe")) _cWard[i] = Intern(v);
            else if (Is(k, "안경", "glasses")) _cGlasses[i] = (sbyte)(IsNo(v) ? 0 : 1);
            else if (Is(k, "사망", "death"))
            {
                var digits = v.AsSpan().TrimStart("씬 sceneSCENE".AsSpan()).Trim();
                if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc)) _cDeath[i] = sc; else { ParseErrors++; continue; }
            }
            else { ParseErrors++; continue; }
            RuleCount++;
        }
    }

    private void ParseLocation(string name, string body)
    {
        if (_nLoc >= MaxLocations) { ParseErrors++; return; }
        var i = _nLoc++;
        _lId[i] = Intern(name); _lNoRain[i] = _lNoSnow[i] = false; _lTMin[i] = -60f; _lTMax[i] = 60f; _lLat[i] = float.NaN;
        foreach (var kv in body.Split(','))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) { ParseErrors++; continue; }
            var k = kv[..eq].Trim(); var v = kv[(eq + 1)..].Trim();
            if (Is(k, "비", "rain")) _lNoRain[i] = IsNo(v);
            else if (Is(k, "눈", "snow")) _lNoSnow[i] = IsNo(v);
            else if (Is(k, "기온", "temperature"))
            {
                var dd = v.IndexOf("..", StringComparison.Ordinal);
                if (dd < 0 || !float.TryParse(v[..dd], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || !float.TryParse(v[(dd + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) { ParseErrors++; continue; }
                _lTMin[i] = MathF.Min(a, b); _lTMax[i] = MathF.Max(a, b);
            }
            else if (Is(k, "위도", "latitude"))
            {
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) { ParseErrors++; continue; }
                _lLat[i] = lat;
            }
            else { ParseErrors++; continue; }
            RuleCount++;
        }
    }

    private void ParseEra(string name, string body)
    {
        if (_nEra >= MaxEras) { ParseErrors++; return; }
        var i = _nEra++;
        _eId[i] = Intern(name); _eCount[i] = 0;
        var eq = body.IndexOf('=');
        if (eq < 0 || !Is(body[..eq].Trim(), "금지", "forbidden")) { ParseErrors++; return; }
        foreach (var item in body[(eq + 1)..].Split(','))
        {
            if (string.IsNullOrWhiteSpace(item) || _eCount[i] >= MaxForbidden) continue;
            _eForbidden[i * MaxForbidden + _eCount[i]++] = Intern(item);
            RuleCount++;
        }
    }

    private void Add(BibleConflictKind k, float sev, int subject, float obs, float exp, int scene)
    {
        if (_nConf < MaxConflicts) _conf[_nConf++] = new BibleConflict(k, sev, subject, obs, exp, scene);
    }

    /// <summary>씬 진단(무할당): 이전 진단 씬과의 전이 규칙 포함. 반환 = 충돌 수.</summary>
    public int Diagnose(SceneMeta s)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _nConf = 0;
            var sc = s.Scene;
            // ── 캐릭터 ──
            for (var k = 0; k < s.CastCount; k++)
            {
                var ci = FindCharacter(s.Cast[k]);
                if (ci < 0) continue;
                if (!s.Flashback && sc > _cDeath[ci]) Add(BibleConflictKind.DeadCharacter, 1f, s.Cast[k], sc, _cDeath[ci], sc);
                if (_cHair[ci] != None && s.Hair[k] != None && s.Hair[k] != _cHair[ci]) Add(BibleConflictKind.CharacterHair, 0.6f, s.Cast[k], s.Hair[k], _cHair[ci], sc);
                if (_cScar[ci] != None && s.Scar[k] != _cScar[ci]) Add(BibleConflictKind.CharacterScar, 0.7f, s.Cast[k], s.Scar[k], _cScar[ci], sc);
                if (_cHand[ci] != None && s.Hand[k] != None && s.Hand[k] != _cHand[ci]) Add(BibleConflictKind.CharacterHandedness, 0.5f, s.Cast[k], s.Hand[k], _cHand[ci], sc);
                if (_cWard[ci] != None && !s.CostumeChange && s.Wardrobe[k] != None && s.Wardrobe[k] != _cWard[ci]) Add(BibleConflictKind.CharacterWardrobe, 0.4f, s.Cast[k], s.Wardrobe[k], _cWard[ci], sc);
                if (_cGlasses[ci] >= 0 && (s.Glasses[k] ? 1 : 0) != _cGlasses[ci]) Add(BibleConflictKind.CharacterGlasses, 0.4f, s.Cast[k], s.Glasses[k] ? 1 : 0, _cGlasses[ci], sc);
            }
            // ── 장소 기상 ──
            var li = FindLocation(s.Location);
            var w = s.Weather;
            var lat = s.Latitude;
            if (li >= 0)
            {
                var rain = w.Precipitation * (1f - s.SnowFraction); var snow = w.Precipitation * s.SnowFraction;
                if (_lNoRain[li] && rain > 0.2f) Add(BibleConflictKind.WeatherForbidden, 0.8f, s.Location, rain, 0f, sc);
                if (_lNoSnow[li] && snow > 0.2f) Add(BibleConflictKind.WeatherForbidden, 0.8f, s.Location, snow, 0f, sc);
                if (w.Temperature < _lTMin[li] - 2f || w.Temperature > _lTMax[li] + 2f) Add(BibleConflictKind.TemperatureRange, 0.5f, s.Location, w.Temperature, w.Temperature < _lTMin[li] ? _lTMin[li] : _lTMax[li], sc);
                if (!float.IsNaN(_lLat[li])) lat = _lLat[li];
            }
            // ── 시대 소품 ──
            var ei = FindEra(s.Era);
            if (ei >= 0)
                for (var p = 0; p < s.PropCount; p++)
                    for (var f = 0; f < _eCount[ei]; f++)
                        if (_eForbidden[ei * MaxForbidden + f] == s.Props[p]) Add(BibleConflictKind.EraProp, 0.9f, s.Props[p], 1f, 0f, sc);
            // ── 태양 ──
            if (!float.IsNaN(s.ObservedSunElevation))
            {
                var doy = Math.Clamp(172 + s.Day, 1, 365);
                var (el, _) = AtmosphericScatteringLightingEngine.SolarPosition(doy, s.Hour, lat);
                var night = el < -6f;
                if (night && s.ObservedSunElevation > 2f) Add(BibleConflictKind.NightSun, 0.9f, -1, s.ObservedSunElevation, el, sc);
                else if (!night && MathF.Abs(el - s.ObservedSunElevation) > SunTolerance) Add(BibleConflictKind.SunMismatch, MathF.Min(1f, MathF.Abs(el - s.ObservedSunElevation) / 40f + 0.2f), -1, s.ObservedSunElevation, el, sc);
            }
            // ── 전이 ──
            if (_hasPrev)
            {
                var p = _prev;
                var tPrev = p.Day * 24f + p.Hour; var tCur = s.Day * 24f + s.Hour;
                if (!s.Flashback && tCur < tPrev - 1e-3f) Add(BibleConflictKind.TimelineReversed, 0.8f, -1, tCur, tPrev, sc);
                if (s.ContinuousWithPrevious)
                {
                    var gap = tCur - tPrev;
                    if (gap > 0.25f || gap < -1e-3f) Add(BibleConflictKind.ContinuousGap, 0.6f, -1, gap, 0f, sc);
                    var dp = MathF.Abs(w.Precipitation - p.Weather.Precipitation);
                    if (dp > 2f + 0.5f * p.Weather.Precipitation) Add(BibleConflictKind.WeatherJump, MathF.Min(1f, dp / 20f + 0.3f), -1, w.Precipitation, p.Weather.Precipitation, sc);
                    // 비가 오던 연속 씬 → 지표가 젖어 있어야 함(건조 1 h 이상 필요)
                    if (p.GroundWetness > 0.4f && s.GroundWetness < p.GroundWetness - 0.35f) Add(BibleConflictKind.GroundWetness, 0.5f, -1, s.GroundWetness, p.GroundWetness, sc);
                }
            }
            _prev.CopyFrom(s); _hasPrev = true;
            float sum = 0f; for (var i = 0; i < _nConf; i++) sum += _conf[i].Severity;
            CoherenceScore = 100f * MathF.Exp(-sum);
            Diagnoses++;
            LastDiagnoseMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return _nConf;
        }
    }

    public void ResetTimeline() { lock (_gate) _hasPrev = false; }

    /// <summary>바이블 충돌 → 연속성 엔진 점수 합류.</summary>
    public void MergeInto(RealtimeScriptSupervisorContinuityEngine engine, int shot)
    {
        for (var i = 0; i < _nConf; i++) engine.Merge(ViolationKind.StoryBible, _conf[i].Severity, (int)_conf[i].Kind, _conf[i].Observed, shot);
    }
}
