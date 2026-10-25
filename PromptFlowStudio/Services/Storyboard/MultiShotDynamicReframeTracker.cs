namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.1] 샷별 9:16 크롭 키프레임 1 개 — 활성 구간 [StartMs, EndMs) · 프레이밍 중심(원본 ‰) · 크롭 박스(원본 px).</summary>
public struct ShotCropKeyframe
{
    public int ShotNumber;
    public int StartMs, EndMs;
    public int CenterXPermille, CenterYPermille;
    public int CropX, CropY, CropWidth, CropHeight;

    public readonly int DurationMs => EndMs - StartMs;
}

/// <summary>
/// [PromptFlow Studio v18.2.1] 🎯 다중 샷 다이내믹 9:16 크롭 키프레임 트래커 — 스토리보드 전체 샷의 크롭 트랙을 시작 시각 오름차순 고정 배열로 보관하고
///   임의 재생 시각 T 의 활성 샷을 이진 탐색(0 B)으로 조회한다. 첫 샷 이전 T 는 첫 샷 · 마지막 샷 이후 T 는 마지막 샷으로 클램프.
///   트랙은 <see cref="ReadOnlyMemory{T}"/> 로 공유(0-카피) — 정적 조회 메서드는 공유 슬라이스 위에서 그대로 동작한다.
/// </summary>
public sealed class MultiShotDynamicReframeTracker
{
    public const int MaxShots = StoryboardKeyframeSequencer.MaxShots;

    private readonly ShotCropKeyframe[] _track = new ShotCropKeyframe[MaxShots];

    public int Count { get; private set; }
    public int Revision { get; private set; }
    public ReadOnlySpan<ShotCropKeyframe> Track => _track.AsSpan(0, Count);
    public ReadOnlyMemory<ShotCropKeyframe> TrackMemory => _track.AsMemory(0, Count);

    /// <summary>시퀀서 키프레임(시작 시각 정렬됨) → 크롭 트랙 제자리 갱신(0 B). 반환 = 샷 수.</summary>
    public int Load(ReadOnlySpan<StoryboardKeyframe> keys)
    {
        var n = Math.Min(keys.Length, MaxShots);
        for (var i = 0; i < n; i++)
        {
            ref readonly var k = ref keys[i];
            ref var c = ref _track[i];
            c.ShotNumber = k.ShotNumber;
            c.StartMs = k.StartMs;
            c.EndMs = k.EndMs;
            c.CenterXPermille = k.CenterXPermille;
            c.CenterYPermille = k.CenterYPermille;
            c.CropX = k.CropX;
            c.CropY = k.CropY;
            c.CropWidth = k.CropWidth;
            c.CropHeight = k.CropHeight;
        }
        Count = n;
        Revision++;
        return n;
    }

    public int IndexAt(int ms) => IndexAt(Track, ms);

    public bool TryQuery(int ms, out ShotCropKeyframe key) => TryQuery(Track, ms, out key);

    /// <summary>시각 <paramref name="ms"/> 의 활성 샷 인덱스(StartMs ≤ ms 인 마지막 샷 · 클램프) — 빈 트랙은 -1.</summary>
    public static int IndexAt(ReadOnlySpan<ShotCropKeyframe> track, int ms)
    {
        if (track.IsEmpty) return -1;
        int lo = 0, hi = track.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (track[mid].StartMs <= ms) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public static bool TryQuery(ReadOnlySpan<ShotCropKeyframe> track, int ms, out ShotCropKeyframe key)
    {
        var i = IndexAt(track, ms);
        key = i < 0 ? default : track[i];
        return i >= 0;
    }
}
