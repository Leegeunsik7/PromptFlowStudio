using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Audio;

/// <summary>
/// [PromptFlowStudio] 대본 → 음성 파일(WAV) 합성 계약 — 합본 오디오 트랙 믹싱 입력.
///   ▸ 실패/빈 텍스트는 예외 없이 false(Graceful). 취소는 <c>ct</c> 로 즉시 중단.
/// </summary>
public interface ITtsSynthesisService
{
    /// <summary>마지막 합성에 사용된 백엔드 표시명(Kokoro-82M · SAPI · Mock 등).</summary>
    string LastBackend { get; }

    /// <summary><paramref name="text"/> 를 <paramref name="outputPath"/> 에 WAV 로 합성. 성공 시 true.</summary>
    Task<bool> SynthesizeToFileAsync(string text, string outputPath, CancellationToken ct);
}
