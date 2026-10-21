using System.Runtime.InteropServices;
namespace PromptFlowStudio.Services.VerticalTranscode;

public enum MfEncoderKind : byte { None, Hardware, Software }

/// <summary>압축 출력 수신자(콜백 · 무할당) — 데이터는 호출 동안만 유효.</summary>
public interface IMfSampleSink
{
    void OnSample(ReadOnlySpan<byte> data, long time100ns);
}

#if WINDOWS
/// <summary>
/// [PromptFlow Studio v17.49.0] Media Foundation 원시 COM 래퍼(vtable 함수 포인터 · 외부 NuGet 0 · GC 할당 0).
/// GUID/IID/vtable 순서는 Windows SDK 10.0.26100 헤더(mfapi.h · mfobjects.h · mftransform.h · codecapi.h · wmcodecdsp.h)로 검증.
/// </summary>
internal static unsafe class Mf
{
    public const int S_OK = 0, MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72), MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61), MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5), MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    public const uint METransformNeedInput = 601, METransformHaveOutput = 602, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    public const uint MSG_FLUSH = 0, MSG_DRAIN = 1, MSG_BEGIN_STREAMING = 0x10000000, MSG_END_OF_STREAM = 0x10000002, MSG_START_OF_STREAM = 0x10000003;
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71"), MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71"), NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71"), Aac = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MT_MAJOR = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d"), MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_PAR = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), MT_INTERLACE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), MT_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036"), MT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid MT_CH = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MT_SR = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"), MT_BPS = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MT_ABPS = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), MT_ALIGN = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    public static readonly Guid MT_AAC_PAYLOAD = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), MT_AAC_PLI = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    public static readonly Guid ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207"), LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid CAT_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CLSID_H264Enc = new("6ca50344-051a-4ded-9779-a43305165e35"), CLSID_H264Dec = new("62CE7E72-4C71-4d20-B15D-452831A87D9D"), CLSID_AacEnc = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");
    public static readonly Guid IID_Transform = new("bf94c121-5b05-4e6f-8000-ba598961414d"), IID_EventGen = new("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), IID_CodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    public static readonly Guid API_MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52"), API_RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423"), API_GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1"), API_BCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    [StructLayout(LayoutKind.Sequential)] public struct OutputDataBuffer { public uint StreamId; public IntPtr Sample; public uint Status; public IntPtr Events; }
    [StructLayout(LayoutKind.Sequential)] public struct StreamInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] public struct InputStreamInfo { public long Latency; public uint Flags, Size, MaxLookahead, Alignment; }
    [StructLayout(LayoutKind.Sequential)] public struct TypeInfo { public Guid Major, Sub; }
    [StructLayout(LayoutKind.Sequential, Size = 24)] public struct Variant { public ushort Vt; public ushort R1, R2, R3; public ulong Value; }

    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(uint max, out IntPtr buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, out IntPtr activates, out uint count);
    [DllImport("ole32.dll", ExactSpelling = true)] public static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll", ExactSpelling = true)] public static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint ctx, in Guid iid, out IntPtr obj);
    [DllImport("ole32.dll", ExactSpelling = true)] public static extern void CoTaskMemFree(IntPtr p);

    private static int _started;
    private static readonly object Gate = new();

    /// <summary>COM(MTA 시도) · MFStartup 1회. 실패 시 false.</summary>
    public static bool Ensure()
    {
        try
        {
            CoInitializeEx(IntPtr.Zero, 0);
            lock (Gate)
            {
                if (_started == 0) _started = MFStartup(0x00020070, 0) >= 0 ? 1 : -1;
                return _started == 1;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { _started = -1; return false; }
    }

    private static IntPtr* V(IntPtr p) => *(IntPtr**)p;
    public static uint Release(IntPtr p) => p == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p)[2])(p);
    public static int QI(IntPtr p, in Guid iid, out IntPtr o) { fixed (Guid* g = &iid) fixed (IntPtr* r = &o) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(p)[0])(p, g, r); }
    // IMFAttributes
    public static int GetU32(IntPtr p, in Guid k, out uint v) { fixed (Guid* g = &k) fixed (uint* r = &v) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)V(p)[7])(p, g, r); }
    public static int GetU64(IntPtr p, in Guid k, out ulong v) { fixed (Guid* g = &k) fixed (ulong* r = &v) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)V(p)[8])(p, g, r); }
    public static int GetGuid(IntPtr p, in Guid k, out Guid v) { fixed (Guid* g = &k) fixed (Guid* r = &v) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)V(p)[10])(p, g, r); }
    public static int SetU32(IntPtr p, in Guid k, uint v) { fixed (Guid* g = &k) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)V(p)[21])(p, g, v); }
    public static int SetU64(IntPtr p, in Guid k, ulong v) { fixed (Guid* g = &k) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)V(p)[22])(p, g, v); }
    public static int SetGuid(IntPtr p, in Guid k, in Guid v) { fixed (Guid* g = &k) fixed (Guid* r = &v) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)V(p)[24])(p, g, r); }
    // IMFSample
    public static int SetTime(IntPtr s, long t) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)V(s)[36])(s, t);
    public static int GetTime(IntPtr s, out long t) { fixed (long* r = &t) return ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)V(s)[35])(s, r); }
    public static int SetDuration(IntPtr s, long t) => ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)V(s)[38])(s, t);
    public static int Contiguous(IntPtr s, out IntPtr b) { fixed (IntPtr* r = &b) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(s)[41])(s, r); }
    public static int AddBuffer(IntPtr s, IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)V(s)[42])(s, b);
    // IMFMediaBuffer
    public static int Lock(IntPtr b, out byte* data, out uint max, out uint cur) { fixed (byte** d = &data) fixed (uint* m = &max) fixed (uint* c = &cur) return ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)V(b)[3])(b, d, m, c); }
    public static int Unlock(IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(b)[4])(b);
    public static int SetLength(IntPtr b, uint n) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)V(b)[6])(b, n);
    // IMFTransform
    public static int InStreamInfo(IntPtr t, out InputStreamInfo i) { fixed (InputStreamInfo* r = &i) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, InputStreamInfo*, int>)V(t)[6])(t, 0, r); }
    public static int OutStreamInfo(IntPtr t, out StreamInfo i) { fixed (StreamInfo* r = &i) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, StreamInfo*, int>)V(t)[7])(t, 0, r); }
    public static int Attributes(IntPtr t, out IntPtr a) { fixed (IntPtr* r = &a) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(t)[8])(t, r); }
    public static int OutAvailable(IntPtr t, uint idx, out IntPtr type) { fixed (IntPtr* r = &type) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)V(t)[14])(t, 0, idx, r); }
    public static int SetIn(IntPtr t, IntPtr type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[15])(t, 0, type, 0);
    public static int SetOut(IntPtr t, IntPtr type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[16])(t, 0, type, 0);
    public static int OutCurrent(IntPtr t, out IntPtr type) { fixed (IntPtr* r = &type) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(t)[18])(t, 0, r); }
    public static int Message(IntPtr t, uint msg) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, int>)V(t)[23])(t, msg, 0);
    public static int ProcessInput(IntPtr t, IntPtr sample) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)V(t)[24])(t, 0, sample, 0);
    public static int ProcessOutput(IntPtr t, OutputDataBuffer* buf, out uint status) { fixed (uint* s = &status) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)V(t)[25])(t, 0, 1, buf, s); }
    // IMFActivate · IMFMediaEventGenerator · IMFMediaEvent · ICodecAPI
    public static int Activate(IntPtr a, in Guid iid, out IntPtr o) { fixed (Guid* g = &iid) fixed (IntPtr* r = &o) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)V(a)[33])(a, g, r); }
    public static int GetEvent(IntPtr gen, uint flags, out IntPtr ev) { fixed (IntPtr* r = &ev) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(gen)[3])(gen, flags, r); }
    public static int EventType(IntPtr ev, out uint t) { fixed (uint* r = &t) return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)V(ev)[33])(ev, r); }
    public static int CodecSet(IntPtr api, in Guid k, uint value)
    {
        var v = new Variant { Vt = 19, Value = value };
        fixed (Guid* g = &k) return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)V(api)[9])(api, g, &v);
    }

    public static IntPtr NewType(in Guid major, in Guid sub)
    {
        if (MFCreateMediaType(out var t) < 0) return IntPtr.Zero;
        SetGuid(t, MT_MAJOR, major); SetGuid(t, MT_SUBTYPE, sub);
        return t;
    }

    /// <summary>시스템 메모리 샘플 생성(비관리 COM 할당) — data 복사 · 시간/길이 설정.</summary>
    public static IntPtr NewSample(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, long time, long duration)
    {
        if (MFCreateSample(out var s) < 0) return IntPtr.Zero;
        var total = (uint)(a.Length + b.Length);
        if (MFCreateMemoryBuffer(total, out var buf) < 0) { Release(s); return IntPtr.Zero; }
        if (Lock(buf, out var p, out _, out _) >= 0)
        {
            a.CopyTo(new Span<byte>(p, a.Length));
            b.CopyTo(new Span<byte>(p + a.Length, b.Length));
            Unlock(buf);
        }
        SetLength(buf, total); AddBuffer(s, buf); Release(buf);
        SetTime(s, time); SetDuration(s, duration);
        return s;
    }
}

/// <summary>MFT 구동기 — 동기(소프트웨어) · 비동기(하드웨어, 이벤트) 공통 출력 수확.</summary>
internal sealed unsafe class MfTransform : IDisposable
{
    private IntPtr _t, _events, _api, _outSample;
    private uint _outFlags, _outSize;
    private int _needInput;
    public MfTransform(IntPtr transform, bool async)
    {
        _t = transform; Async = async;
        if (async) Mf.QI(_t, Mf.IID_EventGen, out _events);
        Mf.QI(_t, Mf.IID_CodecApi, out _api);
    }
    public bool Async { get; }
    public IntPtr Handle => _t;
    public IntPtr CodecApi => _api;
    public int LastHr { get; private set; }
    public long Inputs { get; private set; }
    public long Outputs { get; private set; }

    public bool Begin()
    {
        Mf.OutStreamInfo(_t, out var si); _outFlags = si.Flags; _outSize = Math.Max(si.Size, 1u << 20);
        return (LastHr = Mf.Message(_t, Mf.MSG_BEGIN_STREAMING)) >= 0 && (LastHr = Mf.Message(_t, Mf.MSG_START_OF_STREAM)) >= 0;
    }

    /// <summary>비동기 이벤트 펌프 — NeedInput 크레딧 적립 · HaveOutput 수확. 동기 MFT 는 무동작.</summary>
    private void Pump(IMfSampleSink sink)
    {
        if (!Async || _events == IntPtr.Zero) return;
        for (var i = 0; i < 64; i++)
        {
            var hr = Mf.GetEvent(_events, 1, out var ev);
            if (hr < 0) break;
            Mf.EventType(ev, out var type);
            Mf.Release(ev);
            if (type == Mf.METransformNeedInput) _needInput++;
            else if (type == Mf.METransformHaveOutput) Drain(sink, 1);
        }
    }

    /// <summary>입력 1개 제출(동기 = 즉시 · 비동기 = 크레딧 대기 최대 waitMs). 출력은 sink 로 전달.</summary>
    public bool Feed(IntPtr sample, IMfSampleSink sink, int waitMs = 200)
    {
        if (Async)
        {
            var deadline = Environment.TickCount64 + waitMs;
            Pump(sink);
            while (_needInput == 0 && Environment.TickCount64 < deadline) { Thread.Sleep(1); Pump(sink); }
            if (_needInput == 0) { LastHr = Mf.MF_E_NOTACCEPTING; return false; }
            _needInput--;
            var ah = LastHr = Mf.ProcessInput(_t, sample);
            Pump(sink);
            if (ah >= 0) Inputs++;
            return ah >= 0;
        }
        // v17.74: 수락 여부 = ProcessInput 결과(후속 Drain 의 NEED_MORE_INPUT 은 정상 — 이전엔 LastHr 덮어써 false 오보)
        var hr = Mf.ProcessInput(_t, sample);
        if (hr == Mf.MF_E_NOTACCEPTING) { Drain(sink, 64); hr = Mf.ProcessInput(_t, sample); }
        LastHr = hr;
        Drain(sink, 64);
        if (hr >= 0) Inputs++;
        return hr >= 0;
    }

    public void Finish(IMfSampleSink sink)
    {
        Mf.Message(_t, Mf.MSG_END_OF_STREAM);
        Mf.Message(_t, Mf.MSG_DRAIN);
        if (Async) { var deadline = Environment.TickCount64 + 1000; while (Environment.TickCount64 < deadline) { Pump(sink); Thread.Sleep(1); } }
        else Drain(sink, 4096);
    }

    /// <summary>ProcessOutput 반복 — 스트림 변경 시 첫 가용 출력 형식(디코더는 NV12)으로 재설정.</summary>
    public int Drain(IMfSampleSink sink, int max, Guid? wantSub = null)
    {
        var got = 0;
        for (var k = 0; k < max; k++)
        {
            var provides = (_outFlags & (Mf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | Mf.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            if (!provides && _outSample == IntPtr.Zero)
            {
                Mf.MFCreateSample(out _outSample);
                Mf.MFCreateMemoryBuffer(_outSize, out var b); Mf.AddBuffer(_outSample, b); Mf.Release(b);
            }
            Mf.OutputDataBuffer odb = default;
            odb.Sample = provides ? IntPtr.Zero : _outSample;
            if (!provides && Mf.Contiguous(_outSample, out var ob) >= 0) { Mf.SetLength(ob, 0); Mf.Release(ob); }
            var hr = Mf.ProcessOutput(_t, &odb, out _);
            if (odb.Events != IntPtr.Zero) Mf.Release(odb.Events);
            if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (!ResetOutput(wantSub ?? Mf.NV12)) return got;
                continue;
            }
            if (hr < 0) { LastHr = hr; break; }
            var s = odb.Sample;
            if (s != IntPtr.Zero && Mf.Contiguous(s, out var buf) >= 0)
            {
                Mf.GetTime(s, out var t);
                if (Mf.Lock(buf, out var p, out _, out var len) >= 0)
                {
                    try { sink.OnSample(new ReadOnlySpan<byte>(p, (int)len), t); }
                    finally { Mf.Unlock(buf); }
                }
                Mf.Release(buf);
                got++; Outputs++;
            }
            if (provides && s != IntPtr.Zero) Mf.Release(s);
            if (Async) break;
        }
        return got;
    }

    private bool ResetOutput(Guid sub)
    {
        for (uint i = 0; i < 32; i++)
        {
            if (Mf.OutAvailable(_t, i, out var type) < 0) return false;
            Mf.GetGuid(type, Mf.MT_SUBTYPE, out var g);
            var ok = g == sub && Mf.SetOut(_t, type) >= 0;
            Mf.Release(type);
            if (ok)
            {
                Mf.OutStreamInfo(_t, out var si); _outFlags = si.Flags;
                if (si.Size > _outSize) { _outSize = si.Size; Mf.Release(_outSample); _outSample = IntPtr.Zero; }
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        Mf.Release(_outSample); _outSample = IntPtr.Zero;
        Mf.Release(_events); _events = IntPtr.Zero;
        Mf.Release(_api); _api = IntPtr.Zero;
        Mf.Release(_t); _t = IntPtr.Zero;
    }
}

/// <summary>H.264 인코더 — 하드웨어 비동기 MFT 우선, 실패 시 Microsoft 소프트웨어 MFT(CPU) 폴백.</summary>
public sealed unsafe class MfH264Encoder : IDisposable
{
    private MfTransform? _x;
    public MfEncoderKind Kind { get; private set; }
    public string? FailReason { get; private set; }
    public string? HardwareName { get; private set; }
    public int BitrateKbps { get; private set; }
    public long Inputs => _x?.Inputs ?? 0;
    public long Outputs => _x?.Outputs ?? 0;

    public static MfH264Encoder? TryCreate(int w, int h, int fps, int kbps, bool preferHardware)
    {
        if (!OperatingSystem.IsWindows() || !Mf.Ensure()) return null;
        var e = new MfH264Encoder();
        if (preferHardware && e.TryHardware(w, h, fps, kbps)) return e;
        if (e.TrySoftware(w, h, fps, kbps)) return e;
        e.Dispose();
        return null;
    }

    private bool TryHardware(int w, int h, int fps, int kbps)
    {
        var inInfo = new Mf.TypeInfo { Major = Mf.MediaTypeVideo, Sub = Mf.NV12 };
        var outInfo = new Mf.TypeInfo { Major = Mf.MediaTypeVideo, Sub = Mf.H264 };
        if (Mf.MFTEnumEx(Mf.CAT_VIDEO_ENCODER, 0x4 | 0x40, &inInfo, &outInfo, out var arr, out var count) < 0 || count == 0) { FailReason = "하드웨어 H.264 MFT 없음"; return false; }
        var acts = (IntPtr*)arr;
        try
        {
            for (var i = 0; i < count; i++)
            {
                if (Mf.Activate(acts[i], Mf.IID_Transform, out var t) < 0) continue;
                if (Mf.Attributes(t, out var attrs) >= 0) { Mf.SetU32(attrs, Mf.ASYNC_UNLOCK, 1); Mf.SetU32(attrs, Mf.LOW_LATENCY, 1); Mf.Release(attrs); }
                var x = new MfTransform(t, async: true);
                if (Configure(x, w, h, fps, kbps) && x.Begin()) { _x = x; Kind = MfEncoderKind.Hardware; HardwareName = "HW MFT #" + i.ToString(System.Globalization.CultureInfo.InvariantCulture); return true; }
                FailReason = "HW MFT 구성 실패 0x" + x.LastHr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                x.Dispose();
            }
        }
        finally { for (var i = 0; i < count; i++) Mf.Release(acts[i]); Mf.CoTaskMemFree(arr); }
        return false;
    }

    private bool TrySoftware(int w, int h, int fps, int kbps)
    {
        if (Mf.CoCreateInstance(Mf.CLSID_H264Enc, IntPtr.Zero, 1, Mf.IID_Transform, out var t) < 0) { FailReason = "MS H.264 MFT 생성 실패"; return false; }
        if (Mf.Attributes(t, out var attrs) >= 0) { Mf.SetU32(attrs, Mf.LOW_LATENCY, 1); Mf.Release(attrs); }
        var x = new MfTransform(t, async: false);
        if (Configure(x, w, h, fps, kbps) && x.Begin()) { _x = x; Kind = MfEncoderKind.Software; return true; }
        FailReason = "MS H.264 MFT 구성 실패 0x" + x.LastHr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        x.Dispose();
        return false;
    }

    private bool Configure(MfTransform x, int w, int h, int fps, int kbps)
    {
        var size = (ulong)w << 32 | (uint)h; var rate = (ulong)fps << 32 | 1u;
        if (x.CodecApi != IntPtr.Zero)
        {
            Mf.CodecSet(x.CodecApi, Mf.API_RateControl, 0);
            Mf.CodecSet(x.CodecApi, Mf.API_GopSize, (uint)fps);
            Mf.CodecSet(x.CodecApi, Mf.API_BCount, 0);
        }
        var o = Mf.NewType(Mf.MediaTypeVideo, Mf.H264);
        Mf.SetU64(o, Mf.MT_FRAME_SIZE, size); Mf.SetU64(o, Mf.MT_FRAME_RATE, rate); Mf.SetU64(o, Mf.MT_PAR, 1UL << 32 | 1);
        Mf.SetU32(o, Mf.MT_INTERLACE, 2); Mf.SetU32(o, Mf.MT_AVG_BITRATE, (uint)kbps * 1000); Mf.SetU32(o, Mf.MT_PROFILE, 66);
        var hr = Mf.SetOut(x.Handle, o); Mf.Release(o);
        if (hr < 0) return false;
        var i = Mf.NewType(Mf.MediaTypeVideo, Mf.NV12);
        Mf.SetU64(i, Mf.MT_FRAME_SIZE, size); Mf.SetU64(i, Mf.MT_FRAME_RATE, rate); Mf.SetU64(i, Mf.MT_PAR, 1UL << 32 | 1); Mf.SetU32(i, Mf.MT_INTERLACE, 2);
        hr = Mf.SetIn(x.Handle, i); Mf.Release(i);
        BitrateKbps = kbps;
        return hr >= 0;
    }

    /// <summary>평균 비트레이트 동적 변경(ICodecAPI) — 미지원이면 false.</summary>
    public bool SetBitrate(int kbps)
    {
        if (_x is null || _x.CodecApi == IntPtr.Zero) return false;
        if (Mf.CodecSet(_x.CodecApi, Mf.API_MeanBitRate, (uint)kbps * 1000) < 0) return false;
        BitrateKbps = kbps;
        return true;
    }

    public bool Encode(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, long time100ns, long dur100ns, IMfSampleSink sink)
    {
        if (_x is null) return false;
        var s = Mf.NewSample(y, uv, time100ns, dur100ns);
        if (s == IntPtr.Zero) return false;
        try { return _x.Feed(s, sink); }
        finally { Mf.Release(s); }
    }

    public void Finish(IMfSampleSink sink) => _x?.Finish(sink);
    public void Dispose() { _x?.Dispose(); _x = null; }
}

/// <summary>AAC-LC 인코더(Microsoft AAC MFT · 128 kbps · 48 kHz 스테레오 · raw 페이로드).</summary>
public sealed class MfAacEncoder : IDisposable
{
    private MfTransform? _x;
    public static MfAacEncoder? TryCreate()
    {
        if (!OperatingSystem.IsWindows() || !Mf.Ensure()) return null;
        if (Mf.CoCreateInstance(Mf.CLSID_AacEnc, IntPtr.Zero, 1, Mf.IID_Transform, out var t) < 0) return null;
        var x = new MfTransform(t, async: false);
        var i = Mf.NewType(Mf.MediaTypeAudio, Mf.Pcm);
        Mf.SetU32(i, Mf.MT_BPS, 16); Mf.SetU32(i, Mf.MT_SR, 48000); Mf.SetU32(i, Mf.MT_CH, 2); Mf.SetU32(i, Mf.MT_ALIGN, 4); Mf.SetU32(i, Mf.MT_ABPS, 192000);
        var o = Mf.NewType(Mf.MediaTypeAudio, Mf.Aac);
        Mf.SetU32(o, Mf.MT_BPS, 16); Mf.SetU32(o, Mf.MT_SR, 48000); Mf.SetU32(o, Mf.MT_CH, 2); Mf.SetU32(o, Mf.MT_ABPS, 16000);
        Mf.SetU32(o, Mf.MT_AAC_PAYLOAD, 0); Mf.SetU32(o, Mf.MT_AAC_PLI, 0x29);
        var ok = Mf.SetIn(t, i) >= 0 && Mf.SetOut(t, o) >= 0 || Mf.SetOut(t, o) >= 0 && Mf.SetIn(t, i) >= 0;
        Mf.Release(i); Mf.Release(o);
        if (!ok || !x.Begin()) { x.Dispose(); return null; }
        return new MfAacEncoder { _x = x };
    }

    public bool Encode(ReadOnlySpan<byte> pcm16, long time100ns, IMfSampleSink sink)
    {
        if (_x is null) return false;
        var s = Mf.NewSample(pcm16, default, time100ns, pcm16.Length / 4 * 10_000_000L / 48000);
        if (s == IntPtr.Zero) return false;
        try { return _x.Feed(s, sink); }
        finally { Mf.Release(s); }
    }

    public void Finish(IMfSampleSink sink) => _x?.Finish(sink);
    public void Dispose() { _x?.Dispose(); _x = null; }
}

/// <summary>H.264 → NV12 디코더(Microsoft MFT) — 인코딩 결과 루프백 검증 · 프리뷰 전용.</summary>
public sealed class MfH264Decoder : IDisposable
{
    private MfTransform? _x;
    public static MfH264Decoder? TryCreate(int w, int h, int fps)
    {
        if (!OperatingSystem.IsWindows() || !Mf.Ensure()) return null;
        if (Mf.CoCreateInstance(Mf.CLSID_H264Dec, IntPtr.Zero, 1, Mf.IID_Transform, out var t) < 0) return null;
        // v17.74 저지연 모드 — 입력 1 개당 출력 1 개(DPB 지연 배출 억제 · 실패 무시)
        if (Mf.Attributes(t, out var attrs) >= 0) { Mf.SetU32(attrs, Mf.LOW_LATENCY, 1); Mf.Release(attrs); }
        var x = new MfTransform(t, async: false);
        var i = Mf.NewType(Mf.MediaTypeVideo, Mf.H264);
        Mf.SetU64(i, Mf.MT_FRAME_SIZE, (ulong)w << 32 | (uint)h); Mf.SetU64(i, Mf.MT_FRAME_RATE, (ulong)fps << 32 | 1u); Mf.SetU32(i, Mf.MT_INTERLACE, 2);
        var ok = Mf.SetIn(t, i) >= 0;
        Mf.Release(i);
        if (ok)
        {
            ok = false;
            for (uint k = 0; k < 16 && !ok; k++)
            {
                if (Mf.OutAvailable(t, k, out var o) < 0) break;
                Mf.GetGuid(o, Mf.MT_SUBTYPE, out var g);
                ok = g == Mf.NV12 && Mf.SetOut(t, o) >= 0;
                Mf.Release(o);
            }
        }
        if (!ok || !x.Begin()) { x.Dispose(); return null; }
        return new MfH264Decoder { _x = x };
    }

    /// <summary>현재 출력 프레임 크기(디코더 패딩 포함) · 보폭.</summary>
    public bool OutputSize(out int w, out int h, out int stride)
    {
        w = h = stride = 0;
        if (_x is null || Mf.OutCurrent(_x.Handle, out var t) < 0) return false;
        Mf.GetU64(t, Mf.MT_FRAME_SIZE, out var sz);
        stride = Mf.GetU32(t, Mf.MT_STRIDE, out var st) >= 0 ? (int)st : (int)(sz >> 32);
        Mf.Release(t);
        w = (int)(sz >> 32); h = (int)(sz & 0xFFFFFFFF);
        return true;
    }

    public bool Decode(ReadOnlySpan<byte> annexB, long time100ns, IMfSampleSink sink)
    {
        if (_x is null) return false;
        var s = Mf.NewSample(annexB, default, time100ns, 0);
        if (s == IntPtr.Zero) return false;
        try { return _x.Feed(s, sink); }
        finally { Mf.Release(s); }
    }

    public void Finish(IMfSampleSink sink) => _x?.Finish(sink);
    public void Dispose() { _x?.Dispose(); _x = null; }
}
#else
public sealed class MfH264Encoder : IDisposable
{
    public MfEncoderKind Kind => MfEncoderKind.None;
    public string? FailReason => "Media Foundation 미지원 플랫폼";
    public string? HardwareName => null;
    public int BitrateKbps => 0;
    public long Inputs => 0;
    public long Outputs => 0;
    public static MfH264Encoder? TryCreate(int w, int h, int fps, int kbps, bool preferHardware) => null;
    public bool SetBitrate(int kbps) => false;
    public bool Encode(ReadOnlySpan<byte> y, ReadOnlySpan<byte> uv, long time100ns, long dur100ns, IMfSampleSink sink) => false;
    public void Finish(IMfSampleSink sink) { }
    public void Dispose() { }
}
public sealed class MfAacEncoder : IDisposable
{
    public static MfAacEncoder? TryCreate() => null;
    public bool Encode(ReadOnlySpan<byte> pcm16, long time100ns, IMfSampleSink sink) => false;
    public void Finish(IMfSampleSink sink) { }
    public void Dispose() { }
}
public sealed class MfH264Decoder : IDisposable
{
    public static MfH264Decoder? TryCreate(int w, int h, int fps) => null;
    public bool OutputSize(out int w, out int h, out int stride) { w = h = stride = 0; return false; }
    public bool Decode(ReadOnlySpan<byte> annexB, long time100ns, IMfSampleSink sink) => false;
    public void Finish(IMfSampleSink sink) { }
    public void Dispose() { }
}
#endif
