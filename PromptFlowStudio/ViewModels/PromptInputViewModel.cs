using System;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Prompt;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 1번 워크플로우 【프롬프트 입력(단일 생성)】 뷰모델 — 좌측 입력 · 중앙 캔버스 HUD · 첫 프레임 미리보기 결선.
///   ▸ 참조 이미지 유무로 T2V ↔ I2V 자동 감지(<see cref="StatusBadge"/>).
///   ▸ 업로드 이미지의 실제 픽셀 크기를 헤더(PNG/JPEG/WebP/BMP)에서 읽어 미리보기 비율 유지(<see cref="ReferenceAspectCss"/>).
///   ▸ 입력/칩 변경마다 <see cref="PromptSynthesizerService"/> 로 재합성 → <see cref="CharacterCount"/>/800 실시간 통지.
///   ⚠ PropertyChanged 는 호출 스레드에서 발생 — Blazor 구독부는 InvokeAsync(StateHasChanged)로 마샬링한다.
/// </summary>
public sealed class PromptInputViewModel : INotifyPropertyChanged
{
    public const string T2VBadge = "WAN 2.1 · T2V READY";
    public const string I2VBadge = "WAN 2.1 · I2V READY";
    public const string DefaultSize = "1280*720";

    /// <summary>참조 이미지 상한(Wan 업로드 상한과 동일 32MB).</summary>
    public const long MaxReferenceImageBytes = 32L * 1024 * 1024;

    private readonly PromptSynthesizerService _synth;
    private readonly PromptInspirationService _inspiration;

    public PromptInputViewModel(PromptSynthesizerService synth, PromptInspirationService? inspiration = null)
    {
        _synth = synth ?? throw new ArgumentNullException(nameof(synth));
        _inspiration = inspiration ?? new PromptInspirationService();
        CustomPresets = new ObservableCollection<UserCustomPreset>(_inspiration.LoadCustomPresets());
        _result = _synth.Synthesize(BuildContext());
        ApplySize(DefaultSize);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── 입력(합성 컨텍스트) ──
    private string? _userInput, _cameraMotion, _graphicStyle, _subjectIdentity, _negativeInput;
    private bool _isIdentityLocked, _autoBabyModifier;

    public string? UserInput { get => _userInput; set { if (Set(ref _userInput, value)) Resynthesize(); } }
    public string? CameraMotion { get => _cameraMotion; set { if (Set(ref _cameraMotion, value)) Resynthesize(); } }
    public string? GraphicStyle { get => _graphicStyle; set { if (Set(ref _graphicStyle, value)) Resynthesize(); } }
    public string? SubjectIdentity { get => _subjectIdentity; set { if (Set(ref _subjectIdentity, value)) Resynthesize(); } }
    public bool IsIdentityLocked { get => _isIdentityLocked; set { if (Set(ref _isIdentityLocked, value)) Resynthesize(); } }
    public string? NegativeInput { get => _negativeInput; set { if (Set(ref _negativeInput, value)) Resynthesize(); } }
    public bool AutoBabyModifier { get => _autoBabyModifier; set { if (Set(ref _autoBabyModifier, value)) Resynthesize(); } }

    // ── 합성 결과 ──
    private SynthesizedResult _result;
    public SynthesizedResult Result => _result;
    public int CharacterCount => _result.CharacterCount;
    public bool IsOverBudget => _result.IsOverBudget;
    public int MaxCharacterLimit => PromptSynthesizerService.MaxCharacterLimit;

    // ── 💡 스토리 레시피 & 인스피레이션 모달 ──
    private bool _isInspirationModalOpen;
    public bool IsInspirationModalOpen { get => _isInspirationModalOpen; set => Set(ref _isInspirationModalOpen, value); }
    public PromptInspirationService Inspiration => _inspiration;

    /// <summary>레시피 <see cref="StoryRecipe.FullPrompt"/> → <see cref="UserInput"/> 대입(재합성) 후 모달 닫기.</summary>
    public void ApplyRecipe(StoryRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        UserInput = recipe.FullPrompt;
        IsInspirationModalOpen = false;
    }

    /// <summary>칩 조합 합성 → <see cref="UserInput"/> 대입 후 모달 닫기. 전부 빈 칩이면 false(상태 불변).</summary>
    public bool BuildAndApplyChips(string? subject, string? env, string? motion, string? light)
    {
        var text = _inspiration.BuildFromChips(subject, env, motion, light);
        if (text.Length == 0) return false;
        UserInput = text;
        IsInspirationModalOpen = false;
        return true;
    }

    // ── ⭐ 내 커스텀 프리셋 ──
    public ObservableCollection<UserCustomPreset> CustomPresets { get; }

    /// <summary>현재 <see cref="UserInput"/>·<see cref="CameraMotion"/> 을 프리셋으로 저장. 빈 제목/입력 또는 IO 실패 시 false.</summary>
    public bool SaveCurrentAsPreset(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(_userInput)) return false;
        try { _inspiration.SaveCustomPreset(title, _userInput, _cameraMotion ?? ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        ReloadCustomPresets();
        return true;
    }

    /// <summary>프리셋 삭제 — 없거나 IO 실패면 false.</summary>
    public bool DeletePreset(Guid id)
    {
        bool removed;
        try { removed = _inspiration.DeleteCustomPreset(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        if (removed) ReloadCustomPresets();
        return removed;
    }

    /// <summary>프리셋 → <see cref="UserInput"/>/<see cref="CameraMotion"/> 대입 후 모달 닫기.</summary>
    public void ApplyCustomPreset(UserCustomPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        UserInput = preset.Prompt;
        if (!string.IsNullOrWhiteSpace(preset.CameraMotion)) CameraMotion = preset.CameraMotion;
        IsInspirationModalOpen = false;
    }

    private void ReloadCustomPresets()
    {
        CustomPresets.Clear();
        foreach (var p in _inspiration.LoadCustomPresets()) CustomPresets.Add(p);
    }

    // ── 참조 이미지 / 모드 ──
    public byte[]? ReferenceImageBytes { get; private set; }
    public int ReferenceImageWidth { get; private set; }
    public int ReferenceImageHeight { get; private set; }
    public bool IsI2V => ReferenceImageBytes is { Length: > 0 };
    public string StatusBadge => IsI2V ? I2VBadge : T2VBadge;

    /// <summary>미리보기용 CSS aspect-ratio("w / h") — 크기를 못 읽었으면 null(기본 16:9 유지).</summary>
    public string? ReferenceAspectCss { get; private set; }

    // ── 출력 해상도 / 캔버스 HUD ──
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public string HudAspectCss { get; private set; } = "16 / 9";
    public string HudStatusText { get; private set; } = "";

    /// <summary>이미지 드래그&드롭/선택 스트림 수신 → 바이트 보관 + 크기 판독 → I2V 전환. 상한 초과/빈 스트림이면 false(상태 불변).</summary>
    public async Task<bool> HandleImageDroppedAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxReferenceImageBytes) return false;
            ms.Write(buf, 0, n);
        }
        if (ms.Length == 0) return false;

        var bytes = ms.ToArray();
        ReferenceImageBytes = bytes;
        if (TryReadImageSize(bytes, out var w, out var h))
        {
            ReferenceImageWidth = w; ReferenceImageHeight = h;
            ReferenceAspectCss = w.ToString(CultureInfo.InvariantCulture) + " / " + h.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ReferenceImageWidth = 0; ReferenceImageHeight = 0; ReferenceAspectCss = null;
        }
        RaiseImageChanged();
        return true;
    }

    /// <summary>참조 이미지 제거 → T2V 복귀.</summary>
    public void RemoveReferenceImage()
    {
        if (ReferenceImageBytes is null) return;
        ReferenceImageBytes = null;
        ReferenceImageWidth = 0; ReferenceImageHeight = 0; ReferenceAspectCss = null;
        RaiseImageChanged();
    }

    /// <summary>출력 해상도("1280*720" · "1280x720") 반영 → HUD 가이드 박스 비율/상태 텍스트 갱신. 파싱 실패 시 1280x720.</summary>
    public void SetOutputSize(string? size)
    {
        if (ApplySize(size))
        {
            OnPropertyChanged(nameof(OutputWidth));
            OnPropertyChanged(nameof(OutputHeight));
            OnPropertyChanged(nameof(HudAspectCss));
            OnPropertyChanged(nameof(HudStatusText));
        }
    }

    private bool ApplySize(string? size)
    {
        if (!TryParseSize(size, out var w, out var h)) { w = 1280; h = 720; }
        if (w == OutputWidth && h == OutputHeight) return false;
        OutputWidth = w; OutputHeight = h;
        var g = Gcd(w, h);
        var iw = w.ToString(CultureInfo.InvariantCulture);
        var ih = h.ToString(CultureInfo.InvariantCulture);
        HudAspectCss = iw + " / " + ih;
        HudStatusText = iw + "x" + ih + " (" + (w / g).ToString(CultureInfo.InvariantCulture) + ":" + (h / g).ToString(CultureInfo.InvariantCulture) + ") HUD READY";
        return true;
    }

    public static bool TryParseSize(string? size, out int width, out int height)
    {
        width = 0; height = 0;
        if (string.IsNullOrWhiteSpace(size)) return false;
        var s = size.AsSpan().Trim();
        var i = s.IndexOfAny('*', 'x', 'X');
        return i > 0
            && int.TryParse(s[..i], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(s[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width > 0 && height > 0;
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    /// <summary>PNG/JPEG/WebP/BMP 헤더에서 픽셀 크기를 판독(디코딩 없이 · 순수 BCL).</summary>
    public static bool TryReadImageSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0; height = 0;
        // PNG: 8B 시그니처 + IHDR(폭/높이 BE)
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
            return width > 0 && height > 0;
        }
        // BMP: BITMAPINFOHEADER 폭/높이 LE(높이 음수 = top-down)
        if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4));
            height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4)));
            return width > 0 && height > 0;
        }
        // WebP: RIFF....WEBP + VP8 / VP8L / VP8X
        if (d.Length >= 30 && d[0] == (byte)'R' && d[1] == (byte)'I' && d[8] == (byte)'W' && d[9] == (byte)'E')
        {
            var fourcc = d.Slice(12, 4);
            if (fourcc.SequenceEqual("VP8 "u8))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF;
            }
            else if (fourcc.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
            }
            else if (fourcc.SequenceEqual("VP8X"u8))
            {
                width = (d[24] | d[25] << 8 | d[26] << 16) + 1;
                height = (d[27] | d[28] << 8 | d[29] << 16) + 1;
            }
            return width > 0 && height > 0;
        }
        // JPEG: SOFn 마커 스캔(DHT/JPG/DAC 제외)
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                var len = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2));
                    return width > 0 && height > 0;
                }
                if (len < 2) return false;
                i += 2 + len;
            }
        }
        return false;
    }

    private PromptContext BuildContext() =>
        new(_userInput, _cameraMotion, _graphicStyle, _subjectIdentity, _isIdentityLocked, _negativeInput, _autoBabyModifier);

    private void Resynthesize()
    {
        var prev = _result;
        _result = _synth.Synthesize(BuildContext());
        OnPropertyChanged(nameof(Result));
        if (prev.CharacterCount != _result.CharacterCount) OnPropertyChanged(nameof(CharacterCount));
        if (prev.IsOverBudget != _result.IsOverBudget) OnPropertyChanged(nameof(IsOverBudget));
    }

    private void RaiseImageChanged()
    {
        OnPropertyChanged(nameof(ReferenceImageBytes));
        OnPropertyChanged(nameof(ReferenceImageWidth));
        OnPropertyChanged(nameof(ReferenceImageHeight));
        OnPropertyChanged(nameof(ReferenceAspectCss));
        OnPropertyChanged(nameof(IsI2V));
        OnPropertyChanged(nameof(StatusBadge));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name)
    {
        var h = PropertyChanged;
        if (h is null) return;
        try { h(this, new PropertyChangedEventArgs(name)); }
        catch (Exception ex) { Program.Log("PromptInputViewModel.PropertyChanged", ex); }   // 구독자 예외 격리
    }
}
