using System.Text;

namespace PromptFlowStudio.Services.LiveArchive;

public enum StorageTier : byte { Hot, InfrequentAccess, Glacier, DeepArchive }

/// <summary>
/// [PromptFlow Studio v17.87.0] 🧊 스토리지 비용 수명주기 티어링 세이프티 가드.
///   ▸ 로컬 자동 회수: 디스크 여유 &lt; <see cref="MinFreeBytes"/>(10 GB) 이면 <b>클라우드 업로드 완료</b> 세그먼트만 오래된 순으로 삭제 —
///     목표 여유(최소 + 히스테리시스 1 GB) 회복 시 정지. 미업로드 세그먼트는 절대 삭제 금지([부분 구제] — 위험 카운트만 증가).
///   ▸ 클라우드 티어링: 확정 후 경과일 7 일 → InfrequentAccess · 30 일 → Glacier · 90 일 → DeepArchive 태그 주입(오브젝트 태깅) +
///     버킷 수명주기 규칙(태그 필터 Transition) XML 생성 — 제공자별 스토리지 클래스 매핑(R2 = IA 상한 · MinIO = ILM 태그 위임).
///   ▸ 스캔은 사전 확보 스크래치(int[])에 후보 인덱스 기록 — 할당 0.
/// </summary>
public sealed class StorageCostLifecycleTieringGuard
{
    public const long GiB = 1L << 30, DefaultMinFree = 10 * GiB, DefaultHysteresis = 1 * GiB;
    public const int DefaultIaDays = 7, DefaultGlacierDays = 30, DefaultDeepDays = 90, ScratchCap = ZeroAllocLiveSegmentRecordingEngine.MaxSegments;
    public const string TagKey = "pfs-tier", ArchiveTagKey = "pfs-archive";
    /// <summary>GB·월 USD(AWS S3 서울 근사 · R2 표준/IA) — 비용 추정용.</summary>
    public static readonly double[] AwsUsdPerGbMonth = { 0.025, 0.0138, 0.005, 0.002 };
    public static readonly double[] R2UsdPerGbMonth = { 0.015, 0.01, 0.01, 0.01 };

    private readonly int[] _prune = new int[ScratchCap];
    private readonly int[] _retag = new int[ScratchCap];
    private readonly StorageTier[] _retagTier = new StorageTier[ScratchCap];

    public long MinFreeBytes { get; set; } = DefaultMinFree;
    public long HysteresisBytes { get; set; } = DefaultHysteresis;
    public int IaDays { get; set; } = DefaultIaDays;
    public int GlacierDays { get; set; } = DefaultGlacierDays;
    public int DeepDays { get; set; } = DefaultDeepDays;

    public long Scans { get; private set; }
    public long Pruned { get; private set; }
    public long PrunedBytes { get; private set; }
    public long Transitions { get; private set; }
    /// <summary>디스크 부족인데 회수 가능한(업로드 완료) 세그먼트가 없음 — 녹화 데이터 위험.</summary>
    public long AtRisk { get; private set; }
    public bool DiskLow { get; private set; }
    public long LastFreeBytes { get; private set; }
    public int PruneCount { get; private set; }
    public int RetagCount { get; private set; }

    public int PruneCandidate(int i) => _prune[i];
    public int RetagCandidate(int i) => _retag[i];
    public StorageTier RetagTier(int i) => _retagTier[i];

    /// <summary>경과일 → 목표 티어.</summary>
    public StorageTier TierFor(double ageDays) =>
        ageDays >= DeepDays ? StorageTier.DeepArchive : ageDays >= GlacierDays ? StorageTier.Glacier : ageDays >= IaDays ? StorageTier.InfrequentAccess : StorageTier.Hot;

    /// <summary>
    /// 1 회 스캔(무할당): 자동 회수 후보(오래된 업로드 완료본 순) · 티어 승격 후보. 실제 삭제/태깅은 호출자가 수행 후
    /// <see cref="CommitPrune"/>/<see cref="CommitTransition"/> 로 보고.
    /// </summary>
    public void Scan(ZeroAllocLiveSegmentRecordingEngine rec, long freeBytes, long nowUnixSec)
    {
        Scans++;
        LastFreeBytes = freeBytes;
        DiskLow = freeBytes < MinFreeBytes;
        PruneCount = RetagCount = 0;
        var first = rec.FirstIndex; var total = rec.SegmentCount;

        if (DiskLow)
        {
            var need = MinFreeBytes + HysteresisBytes - freeBytes;
            long reclaim = 0;
            for (var i = first; i < total && reclaim < need && PruneCount < _prune.Length; i++)   // 인덱스 오름차순 = 오래된 순
            {
                ref readonly var s = ref rec.Segment(i);
                if (s.State != ArchiveSegmentState.Uploaded) continue;
                _prune[PruneCount++] = i; reclaim += s.Bytes;
            }
            if (reclaim < need) AtRisk++;
        }

        for (var i = first; i < total && RetagCount < _retag.Length; i++)
        {
            ref readonly var s = ref rec.Segment(i);
            if (s.State is not (ArchiveSegmentState.Uploaded or ArchiveSegmentState.Pruned) || s.ObjectKey is null || s.SealedAtUnixSec <= 0) continue;
            var want = TierFor((nowUnixSec - s.SealedAtUnixSec) / 86_400.0);
            if (want <= s.Tier) continue;
            _retag[RetagCount] = i; _retagTier[RetagCount] = want; RetagCount++;
        }
    }

    public void CommitPrune(ref ArchiveSegment s)
    {
        Pruned++; PrunedBytes += s.Bytes;
        s.State = ArchiveSegmentState.Pruned;
    }

    public void CommitTransition(ref ArchiveSegment s, StorageTier tier)
    {
        if (tier <= s.Tier) return;
        s.Tier = tier; Transitions++;
    }

    /// <summary>제공자별 스토리지 클래스(null = 헤더 생략 · 버킷 ILM 위임).</summary>
    public static string? StorageClass(VaultProvider p, StorageTier t) => p switch
    {
        VaultProvider.AwsS3 => t switch { StorageTier.InfrequentAccess => "STANDARD_IA", StorageTier.Glacier => "GLACIER_IR", StorageTier.DeepArchive => "DEEP_ARCHIVE", _ => "STANDARD" },
        VaultProvider.CloudflareR2 => t == StorageTier.Hot ? "STANDARD" : "STANDARD_IA",
        _ => null,
    };

    public static string TierTag(StorageTier t) => t switch
    {
        StorageTier.InfrequentAccess => "infrequent-access", StorageTier.Glacier => "glacier", StorageTier.DeepArchive => "deep-archive", _ => "hot",
    };

    public static string TierName(StorageTier t) => t switch
    {
        StorageTier.InfrequentAccess => "IA", StorageTier.Glacier => "Glacier", StorageTier.DeepArchive => "DeepArchive", _ => "Hot",
    };

    /// <summary>x-amz-tagging 헤더 값(URL 쿼리 형식).</summary>
    public static string TaggingHeader(StorageTier t) => ArchiveTagKey + "=live&" + TagKey + "=" + TierTag(t);

    /// <summary>PutObjectTagging 본문.</summary>
    public static string TaggingXml(StorageTier t) =>
        "<Tagging><TagSet><Tag><Key>" + ArchiveTagKey + "</Key><Value>live</Value></Tag><Tag><Key>" + TagKey + "</Key><Value>" + TierTag(t) + "</Value></Tag></TagSet></Tagging>";

    /// <summary>버킷 수명주기 규칙 — 태그 필터별 Transition(경과일 · 스토리지 클래스). R2 는 IA 단일 규칙, MinIO 는 사전 등록 티어 이름.</summary>
    public string LifecycleXml(VaultProvider p, string prefix)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<LifecycleConfiguration>");
        Rule(sb, p, prefix, StorageTier.InfrequentAccess, IaDays);
        if (p != VaultProvider.CloudflareR2)
        {
            Rule(sb, p, prefix, StorageTier.Glacier, GlacierDays);
            Rule(sb, p, prefix, StorageTier.DeepArchive, DeepDays);
        }
        sb.Append("</LifecycleConfiguration>");
        return sb.ToString();
    }

    private static void Rule(StringBuilder sb, VaultProvider p, string prefix, StorageTier t, int days)
    {
        var cls = p == VaultProvider.MinIO ? "PFS-" + TierName(t).ToUpperInvariant() : StorageClass(p, t);
        sb.Append("<Rule><ID>pfs-").Append(TierTag(t)).Append("</ID><Filter><And><Prefix>").Append(prefix)
          .Append("</Prefix><Tag><Key>").Append(ArchiveTagKey).Append("</Key><Value>live</Value></Tag></And></Filter><Status>Enabled</Status>")
          .Append("<Transition><Days>").Append(days).Append("</Days><StorageClass>").Append(cls).Append("</StorageClass></Transition></Rule>");
    }

    /// <summary>월 예상 비용(USD) · 전량 Hot 대비 절감률.</summary>
    public static double MonthlyUsd(ZeroAllocLiveSegmentRecordingEngine rec, VaultProvider p, out double hotUsd)
    {
        var table = p == VaultProvider.CloudflareR2 ? R2UsdPerGbMonth : AwsUsdPerGbMonth;
        double usd = 0; hotUsd = 0;
        for (var i = rec.FirstIndex; i < rec.SegmentCount; i++)
        {
            ref readonly var s = ref rec.Segment(i);
            if (s.State is not (ArchiveSegmentState.Uploaded or ArchiveSegmentState.Pruned)) continue;
            var gb = s.Bytes / (double)GiB;
            usd += gb * table[(int)s.Tier]; hotUsd += gb * table[0];
        }
        return usd;
    }

    public void ResetStats()
    {
        Scans = Pruned = PrunedBytes = Transitions = AtRisk = 0;
        DiskLow = false; LastFreeBytes = 0; PruneCount = RetagCount = 0;
    }
}
