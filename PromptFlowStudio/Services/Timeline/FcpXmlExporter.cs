using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PromptFlowStudio.Services.Timeline;

/// <summary>
/// [PromptFlow Studio v7.3.0] 표준 NLE 내보내기 엔진(순수 BCL · <see cref="System.Xml.Linq"/> / <see cref="System.Text.Json"/> · 외부 라이브러리 0).
///   ▸ <see cref="ToFcpXml"/>: 타임라인을 Apple FCPXML 1.9 문서로 변환 — Premiere Pro / Final Cut Pro / DaVinci Resolve 임포트·편집 가능.
///   ▸ <see cref="ToOtioJson"/>: OpenTimelineIO(OTIO) JSON 트리로 변환(교차 NLE 호환 보너스).
///   ▸ 초→프레임 환산(기본 30fps)으로 offset/duration 을 rational time("N/30s")으로 매핑. 미디어는 식별자/플레이스홀더 경로(오프라인 편집).
/// </summary>
public sealed class FcpXmlExporter
{
    /// <summary>타임라인 → FCPXML 1.9 문서 문자열(선언 + DOCTYPE 포함).</summary>
    public string ToFcpXml(MultiTrackTimelineState state, int fps = 30, string projectTitle = "PromptFlow Timeline")
    {
        fps = Math.Clamp(fps, 24, 60);
        var videos = state.ClipsFor(TimelineTrackKind.Video).OrderBy(c => c.StartSec).ToList();
        var vocals = state.ClipsFor(TimelineTrackKind.Vocal).OrderBy(c => c.StartSec).ToList();
        var prompts = state.ClipsFor(TimelineTrackKind.Prompt);
        var gyros = state.ClipsFor(TimelineTrackKind.Gyro);
        var total = state.TotalDurationSec;

        var resources = new XElement("resources",
            new XElement("format",
                new XAttribute("id", "r1"),
                new XAttribute("name", $"FFVideoFormat1080p{fps}"),
                new XAttribute("frameDuration", $"1/{fps}s"),
                new XAttribute("width", "1080"),
                new XAttribute("height", "1920")));

        var spine = new XElement("spine");

        for (var i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            var aid = $"a{i + 1}";
            resources.Add(new XElement("asset",
                new XAttribute("id", aid),
                new XAttribute("name", Safe(v.Label)),
                new XAttribute("start", "0s"),
                new XAttribute("duration", Rat(v.DurationSec, fps)),
                new XAttribute("hasVideo", "1"),
                new XAttribute("hasAudio", "0"),
                new XAttribute("format", "r1"),
                new XElement("media-rep",
                    new XAttribute("kind", "original-media"),
                    new XAttribute("src", $"file:///PromptFlow/clip_{i + 1:00}.mov"))));

            var clip = new XElement("asset-clip",
                new XAttribute("ref", aid),
                new XAttribute("offset", Rat(v.StartSec, fps)),
                new XAttribute("name", Safe(v.Label)),
                new XAttribute("duration", Rat(v.DurationSec, fps)),
                new XAttribute("format", "r1"));

            // 프롬프트/자이로 메타를 FCPXML <note> 로 임베드(편집 앱에서 클립 노트로 표시).
            var noteParts = prompts.Where(p => TimelineRenderCompiler.Overlaps(p, v)).Select(p => p.Label)
                .Concat(gyros.Where(g => TimelineRenderCompiler.Overlaps(g, v)).Select(g => "cam:" + g.Label))
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var note = string.Join(" · ", noteParts);
            if (!string.IsNullOrWhiteSpace(note)) clip.Add(new XElement("note", note));

            // 겹치는 보컬 → 연결 오디오(lane -1).
            foreach (var (av, avi) in vocals.Where(a => TimelineRenderCompiler.Overlaps(a, v)).Select((a, k) => (a, k)))
            {
                var auid = $"au{i + 1}_{avi + 1}";
                resources.Add(new XElement("asset",
                    new XAttribute("id", auid),
                    new XAttribute("name", Safe(av.Label)),
                    new XAttribute("start", "0s"),
                    new XAttribute("duration", Rat(av.DurationSec, fps)),
                    new XAttribute("hasVideo", "0"),
                    new XAttribute("hasAudio", "1"),
                    new XAttribute("audioSources", "1"),
                    new XElement("media-rep",
                        new XAttribute("kind", "original-media"),
                        new XAttribute("src", $"file:///PromptFlow/vocal_{i + 1:00}_{avi + 1}.wav"))));
                clip.Add(new XElement("asset-clip",
                    new XAttribute("ref", auid),
                    new XAttribute("lane", "-1"),
                    new XAttribute("offset", Rat(av.StartSec, fps)),
                    new XAttribute("name", Safe(av.Label)),
                    new XAttribute("duration", Rat(av.DurationSec, fps)),
                    new XAttribute("audioRole", "dialogue")));
            }

            spine.Add(clip);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("fcpxml", null, null, null),
            new XElement("fcpxml",
                new XAttribute("version", "1.9"),
                resources,
                new XElement("library",
                    new XElement("event",
                        new XAttribute("name", "PromptFlow Studio"),
                        new XElement("project",
                            new XAttribute("name", Safe(projectTitle)),
                            new XElement("sequence",
                                new XAttribute("format", "r1"),
                                new XAttribute("duration", Rat(total, fps)),
                                new XAttribute("tcStart", "0s"),
                                new XAttribute("tcFormat", "NDF"),
                                spine))))));

        var sb = new StringBuilder();
        using (var w = System.Xml.XmlWriter.Create(sb, new System.Xml.XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = false }))
            doc.Save(w);
        return sb.ToString();
    }

    /// <summary>타임라인 → OpenTimelineIO(OTIO) JSON(교차 NLE 호환 · 4트랙 모두 보존).</summary>
    public string ToOtioJson(MultiTrackTimelineState state, int fps = 30, string projectTitle = "PromptFlow Timeline")
    {
        fps = Math.Clamp(fps, 24, 60);
        var tracks = new List<JsonNode?>();
        foreach (var kind in new[] { TimelineTrackKind.Video, TimelineTrackKind.Vocal, TimelineTrackKind.Gyro, TimelineTrackKind.Prompt })
        {
            var clips = state.ClipsFor(kind).OrderBy(c => c.StartSec).ToList();
            var items = new List<JsonNode?>();
            var cursor = 0.0;
            foreach (var c in clips)
            {
                if (c.StartSec > cursor)   // 간격 → gap
                    items.Add(new JsonObject { ["OTIO_SCHEMA"] = "Gap.1", ["source_range"] = Range(c.StartSec - cursor, fps) });
                items.Add(new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Clip.1",
                    ["name"] = c.Label,
                    ["source_range"] = Range(c.DurationSec, fps),
                    ["metadata"] = new JsonObject { ["promptflow"] = new JsonObject { ["track"] = kind.ToString(), ["accent"] = c.Accent } },
                });
                cursor = c.EndSec;
            }
            tracks.Add(new JsonObject { ["OTIO_SCHEMA"] = "Track.1", ["name"] = kind.ToString(), ["kind"] = kind == TimelineTrackKind.Vocal ? "Audio" : "Video", ["children"] = new JsonArray(items.ToArray()) });
        }

        var timeline = new JsonObject
        {
            ["OTIO_SCHEMA"] = "Timeline.1",
            ["name"] = projectTitle,
            ["tracks"] = new JsonObject { ["OTIO_SCHEMA"] = "Stack.1", ["name"] = "tracks", ["children"] = new JsonArray(tracks.ToArray()) },
        };
        return timeline.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ── 헬퍼 ──
    private static JsonObject Range(double durSec, int fps) => new()
    {
        ["OTIO_SCHEMA"] = "TimeRange.1",
        ["start_time"] = new JsonObject { ["OTIO_SCHEMA"] = "RationalTime.1", ["rate"] = (double)fps, ["value"] = 0.0 },
        ["duration"] = new JsonObject { ["OTIO_SCHEMA"] = "RationalTime.1", ["rate"] = (double)fps, ["value"] = Math.Round(durSec * fps) },
    };

    /// <summary>초 → FCPXML rational time("N/{fps}s").</summary>
    private static string Rat(double sec, int fps) => ((long)Math.Round(sec * fps)).ToString(CultureInfo.InvariantCulture) + "/" + fps + "s";

    private static string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? "clip" : s.Trim();
}
