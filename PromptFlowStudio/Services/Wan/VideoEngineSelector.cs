using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Engines;   // EngineConfigService

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlowStudio] 비디오 생성 엔진 선택기 — 등록된 모든 <see cref="IVideoGenerationEngine"/> 중 설정(EngineConfigService.VideoEngineKey)에 따라 활성 엔진을 고른다.
///   ▸ 선택 키의 엔진이 없으면 첫 등록 엔진(Wan)으로 폴백. JobManager 는 <see cref="Active"/> 로 생성한다.
///   ▸ 플러그인 추가는 <see cref="IVideoGenerationEngine"/> 구현 + DI 등록만으로 끝(추상화 레이어).
/// </summary>
public sealed class VideoEngineSelector
{
    private readonly IReadOnlyList<IVideoGenerationEngine> _engines;
    private readonly EngineConfigService _cfg;

    public VideoEngineSelector(IEnumerable<IVideoGenerationEngine> engines, EngineConfigService cfg)
    {
        _engines = engines.ToList();
        _cfg = cfg;
    }

    /// <summary>등록된 모든 엔진(UI 선택 목록용).</summary>
    public IReadOnlyList<IVideoGenerationEngine> All => _engines;

    /// <summary>현재 선택된 활성 엔진(없으면 첫 엔진 폴백).</summary>
    public IVideoGenerationEngine Active
        => _engines.FirstOrDefault(e => e.Key == _cfg.VideoEngineKey) ?? _engines.First();
}
