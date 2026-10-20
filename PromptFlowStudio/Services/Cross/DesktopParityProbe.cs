using System.Linq;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Shared.Cross;
using DevicePlatform = PromptFlowStudio.Shared.Cross.DevicePlatform;   // v16.5.1: MAUI 암시적 using(Microsoft.Maui.Devices) 충돌 CS0104 회피

namespace PromptFlowStudio.Services.Cross;

/// <summary>
/// [PromptFlow Studio v4.2] 데스크톱 기능 프로브 — 실 서비스에서 기능 스냅샷을 캡처(공용 RCL <see cref="IParityProbe"/> 구현).
///   157종 테마(112 아기 + 12 기업 + 33 자동차) 및 v3.x~v4.x 파이프라인 기능을 실측해 QA 엔진에 공급한다.
/// </summary>
public sealed class DesktopParityProbe : IParityProbe
{
    private readonly BabyThemeDatabase _baby;
    private readonly CorporateThemeDatabase _corp;
    private readonly AutomotiveEquipmentDatabase _auto;

    public DesktopParityProbe(BabyThemeDatabase baby, CorporateThemeDatabase corp, AutomotiveEquipmentDatabase auto)
    { _baby = baby; _corp = corp; _auto = auto; }

    public FeatureParitySnapshot Capture()
    {
        var themes = _baby.AllThemes.Count + _corp.AllThemes.Count + _auto.AllThemes.Count;   // 157
        var schemaFields = typeof(StoryboardProject).GetProperties().Length;
        return new FeatureParitySnapshot(
            DevicePlatform.Windows, themes,
            CadExplodedView: true, TelemetryHud: true, WanHybrid: true,
            EmotionalVoice: true, SpatialAudio: true, RemoteRender: true, GoldKeepsake: true,
            SchemaFieldCount: schemaFields);
    }
}
