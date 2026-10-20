using System.Buffers.Text;

namespace PromptFlowStudio.Services.OpenUsd;

/// <summary>
/// [PromptFlow Studio v16.72.0] USDA(텍스트 OpenUSD) 무할당 파서 — UTF-8 바이트 스팬 직접 토큰화(<see cref="Utf8Parser"/> 수치) → <see cref="UsdLayer"/>.
/// 지원: def/over/class 중첩 프림 · 프림/레이어 메타데이터(괄호 블록 건너뜀) · xformOp:translate(+ .timeSamples) · xformOp:scale · primvars:displayColor ·
/// radius · size · visibility · points · physics:mass · physics:rigidBodyEnabled · inputs:intensity. 그 외 속성/관계(rel)는 균형 건너뜀.
/// </summary>
public static class UsdaReader
{
    private const int MaxDepth = 32;

    public static bool Parse(ReadOnlySpan<byte> src, UsdLayer dst)
    {
        dst.Clear();
        var lx = new Lexer(src);
        if (!src.StartsWith("#usda"u8)) return Fail(dst, UsdError.BadHeader, 0);
        lx.SkipLine();
        Span<int> stack = stackalloc int[MaxDepth];
        var depth = 0;
        var current = -1;
        Span<char> name = stackalloc char[128];
        if (lx.PeekPunct('(') && !lx.SkipBalanced()) return Fail(dst, UsdError.Syntax, lx.Pos);
        while (true)
        {
            var tok = lx.Next(out var text);
            if (tok == Tok.Eof) return depth == 0 ? Done(dst) : Fail(dst, UsdError.Syntax, lx.Pos);
            if (tok == Tok.Punct && text[0] == (byte)'}')
            {
                if (depth == 0) return Fail(dst, UsdError.Syntax, lx.Pos);
                current = stack[--depth];
                continue;
            }
            if (tok != Tok.Ident) return Fail(dst, UsdError.Syntax, lx.Pos);
            UsdSpec spec;
            if (text.SequenceEqual("def"u8)) spec = UsdSpec.Def;
            else if (text.SequenceEqual("over"u8)) spec = UsdSpec.Over;
            else if (text.SequenceEqual("class"u8)) spec = UsdSpec.Class;
            else
            {
                if (current < 0) return Fail(dst, UsdError.Syntax, lx.Pos);
                if (!ParseProperty(ref lx, text, dst, current)) return Fail(dst, dst.LastError == UsdError.Capacity ? UsdError.Capacity : UsdError.Syntax, lx.Pos);
                continue;
            }
            // 프림 헤더: spec [Type] "name" [(meta)] {
            var type = UsdPrimType.None;
            tok = lx.Next(out text);
            if (tok == Tok.Ident) { type = TypeOf(text); tok = lx.Next(out text); }
            if (tok != Tok.String || text.IsEmpty) return Fail(dst, UsdError.Syntax, lx.Pos);
            if (text.Length > name.Length) return Fail(dst, UsdError.Capacity, lx.Pos);
            for (var k = 0; k < text.Length; k++) name[k] = (char)text[k];
            var prim = dst.AddPrim(current, name[..text.Length], spec, type);
            if (prim < 0) return Fail(dst, UsdError.Capacity, lx.Pos);
            if (lx.PeekPunct('(') && !lx.SkipBalanced()) return Fail(dst, UsdError.Syntax, lx.Pos);
            if (lx.Next(out text) != Tok.Punct || text[0] != (byte)'{' || depth >= MaxDepth) return Fail(dst, UsdError.Syntax, lx.Pos);
            stack[depth++] = current;
            current = prim;
        }
    }

    private static UsdPrimType TypeOf(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("Xform"u8) ? UsdPrimType.Xform :
        t.SequenceEqual("Scope"u8) ? UsdPrimType.Scope :
        t.SequenceEqual("Sphere"u8) ? UsdPrimType.Sphere :
        t.SequenceEqual("Cube"u8) ? UsdPrimType.Cube :
        t.SequenceEqual("Points"u8) ? UsdPrimType.Points :
        t.SequenceEqual("DistantLight"u8) ? UsdPrimType.DistantLight : UsdPrimType.None;

    /// <summary>속성 1개: [uniform|custom] type name [= value] [(meta)] · rel name [= target].</summary>
    private static bool ParseProperty(ref Lexer lx, ReadOnlySpan<byte> first, UsdLayer dst, int prim)
    {
        var name = first;
        while (true)
        {
            if (lx.AtLineEnd()) return true;
            if (lx.PeekPunct('=')) break;
            if (lx.PeekPunct('(')) return lx.SkipBalanced();
            if (lx.PeekPunct('}')) return true;
            var tok = lx.Next(out var t);
            if (tok != Tok.Ident && tok != Tok.Path) return false;
            name = t;
        }
        lx.Next(out _); // '='
        bool ok;
        float x, y, z;
        if (name.SequenceEqual("xformOp:translate"u8)) { ok = ReadVec3(ref lx, out x, out y, out z); if (ok) dst.SetTranslate(prim, x, y, z); }
        else if (name.SequenceEqual("xformOp:scale"u8)) { ok = ReadVec3(ref lx, out x, out y, out z); if (ok) dst.SetScale(prim, x, y, z); }
        else if (name.SequenceEqual("primvars:displayColor"u8) || name.SequenceEqual("inputs:color"u8)) ok = ReadColor(ref lx, dst, prim);
        else if (name.SequenceEqual("radius"u8)) { ok = ReadNumber(ref lx, out x); if (ok) dst.SetRadius(prim, x); }
        else if (name.SequenceEqual("size"u8)) { ok = ReadNumber(ref lx, out x); if (ok) dst.SetSize(prim, x); }
        else if (name.SequenceEqual("physics:mass"u8)) { ok = ReadNumber(ref lx, out x); if (ok) dst.SetMass(prim, x); }
        else if (name.SequenceEqual("inputs:intensity"u8)) { ok = ReadNumber(ref lx, out x); if (ok) dst.SetIntensity(prim, x); }
        else if (name.SequenceEqual("physics:rigidBodyEnabled"u8))
        {
            var tok = lx.Next(out var t);
            ok = tok is Tok.Ident or Tok.Number;
            if (ok) dst.SetRigidBody(prim, t.SequenceEqual("true"u8) || t.SequenceEqual("1"u8));
        }
        else if (name.SequenceEqual("visibility"u8))
        {
            ok = lx.Next(out var t) == Tok.String;
            if (ok) dst.SetVisibility(prim, !t.SequenceEqual("invisible"u8));
        }
        else if (name.SequenceEqual("points"u8)) ok = ReadPoints(ref lx, dst, prim);
        else if (name.SequenceEqual("xformOp:translate.timeSamples"u8)) ok = ReadSamples(ref lx, dst, prim);
        else ok = lx.SkipValue();
        if (!ok) return false;
        return !lx.PeekPunct('(') || lx.SkipBalanced();
    }

    private static bool ReadNumber(ref Lexer lx, out float v)
    {
        v = 0;
        return lx.Next(out var t) == Tok.Number && Utf8Parser.TryParse(t, out v, out _);
    }

    private static bool ReadVec3(ref Lexer lx, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (lx.Next(out var t) != Tok.Punct || t[0] != (byte)'(') return false;
        if (!ReadNumber(ref lx, out x)) return false;
        if (lx.PeekPunct(')')) { lx.Next(out _); y = z = x; return true; }   // 스칼라 균일 스케일
        return lx.Comma() && ReadNumber(ref lx, out y) && lx.Comma() && ReadNumber(ref lx, out z) && lx.Close(')');
    }

    private static bool ReadColor(ref Lexer lx, UsdLayer dst, int prim)
    {
        float r, g, b;
        if (lx.PeekPunct('['))
        {
            lx.Next(out _);
            if (lx.PeekPunct(']')) { lx.Next(out _); return true; }
            if (!ReadVec3(ref lx, out r, out g, out b)) return false;
            while (!lx.PeekPunct(']'))
            {
                if (lx.PeekPunct(',')) { lx.Next(out _); continue; }
                if (!lx.SkipValue()) return false;
            }
            lx.Next(out _);
        }
        else if (!ReadVec3(ref lx, out r, out g, out b)) return false;
        dst.SetColor(prim, r, g, b);
        return true;
    }

    private static bool ReadPoints(ref Lexer lx, UsdLayer dst, int prim)
    {
        if (lx.Next(out var t) != Tok.Punct || t[0] != (byte)'[') return false;
        dst.BeginPoints(prim);
        while (true)
        {
            if (lx.PeekPunct(']')) { lx.Next(out _); return true; }
            if (!ReadVec3(ref lx, out var x, out var y, out var z)) return false;
            if (!dst.AppendPoint(prim, x, y, z)) { dst.LastError = UsdError.Capacity; return false; }
            if (lx.PeekPunct(',')) lx.Next(out _);
        }
    }

    private static bool ReadSamples(ref Lexer lx, UsdLayer dst, int prim)
    {
        if (lx.Next(out var t) != Tok.Punct || t[0] != (byte)'{') return false;
        dst.BeginSamples(prim);
        while (true)
        {
            if (lx.PeekPunct('}')) { lx.Next(out _); return true; }
            if (!ReadNumber(ref lx, out var time) || !lx.Close(':')) return false;
            if (!ReadVec3(ref lx, out var x, out var y, out var z)) return false;
            if (!dst.AddTranslateSample(prim, time, x, y, z)) { dst.LastError = UsdError.Capacity; return false; }
            if (lx.PeekPunct(',')) lx.Next(out _);
        }
    }

    private static bool Fail(UsdLayer dst, UsdError e, int at)
    {
        dst.LastError = e; dst.ErrorOffset = at;
        return false;
    }

    private static bool Done(UsdLayer dst) { dst.LastError = UsdError.None; return true; }

    internal enum Tok : byte { Eof, Ident, String, Number, Punct, Path }

    /// <summary>바이트 토크나이저(주석 # · 문자열 "…"/'…' · 에셋 @…@ · 경로 &lt;…&gt; · 식별자 [A-Za-z_][\w:.]*([])? · 수치).</summary>
    internal ref struct Lexer
    {
        private readonly ReadOnlySpan<byte> _s;
        public int Pos;

        public Lexer(ReadOnlySpan<byte> s) { _s = s; Pos = 0; }

        public void SkipLine() { while (Pos < _s.Length && _s[Pos] != (byte)'\n') Pos++; }

        private void SkipWs()
        {
            while (Pos < _s.Length)
            {
                var c = _s[Pos];
                if (c == (byte)'#') { SkipLine(); continue; }
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') { Pos++; continue; }
                break;
            }
        }

        public bool AtLineEnd()
        {
            var p = Pos;
            while (p < _s.Length && _s[p] is (byte)' ' or (byte)'\t' or (byte)'\r') p++;
            return p >= _s.Length || _s[p] is (byte)'\n' or (byte)'#';
        }

        public bool PeekPunct(char c)
        {
            SkipWs();
            return Pos < _s.Length && _s[Pos] == (byte)c;
        }

        public bool Comma() => Next(out var t) == Tok.Punct && t[0] == (byte)',';
        public bool Close(char c) => Next(out var t) == Tok.Punct && t[0] == (byte)c;

        public Tok Next(out ReadOnlySpan<byte> text)
        {
            SkipWs();
            text = default;
            if (Pos >= _s.Length) return Tok.Eof;
            var c = _s[Pos];
            var start = Pos;
            if (c is (byte)'"' or (byte)'\'')
            {
                var triple = Pos + 2 < _s.Length && _s[Pos + 1] == c && _s[Pos + 2] == c;
                if (triple)
                {
                    Pos += 3;
                    var end = _s[Pos..].IndexOf(c == (byte)'"' ? "\"\"\""u8 : "'''"u8);
                    if (end < 0) { Pos = _s.Length; return Tok.Eof; }
                    text = _s.Slice(Pos, end); Pos += end + 3;
                    return Tok.String;
                }
                Pos++;
                while (Pos < _s.Length && _s[Pos] != c) Pos += _s[Pos] == (byte)'\\' ? 2 : 1;
                if (Pos >= _s.Length) return Tok.Eof;
                text = _s.Slice(start + 1, Pos - start - 1); Pos++;
                return Tok.String;
            }
            if (c is (byte)'@' or (byte)'<')
            {
                var close = c == (byte)'@' ? (byte)'@' : (byte)'>';
                Pos++;
                while (Pos < _s.Length && _s[Pos] != close) Pos++;
                if (Pos >= _s.Length) return Tok.Eof;
                text = _s.Slice(start + 1, Pos - start - 1); Pos++;
                return Tok.Path;
            }
            if (c is >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'.')
            {
                Pos++;
                while (Pos < _s.Length && _s[Pos] is >= (byte)'0' and <= (byte)'9' or (byte)'.' or (byte)'e' or (byte)'E' or (byte)'-' or (byte)'+') Pos++;
                text = _s[start..Pos];
                return Tok.Number;
            }
            if (c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or (byte)'_')
            {
                Pos++;
                while (Pos < _s.Length && _s[Pos] is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)':' or (byte)'.') Pos++;
                if (Pos + 1 < _s.Length && _s[Pos] == (byte)'[' && _s[Pos + 1] == (byte)']') Pos += 2;
                text = _s[start..Pos];
                if (text.SequenceEqual("inf"u8) || text.SequenceEqual("nan"u8)) return Tok.Number;
                return Tok.Ident;
            }
            Pos++;
            text = _s.Slice(start, 1);
            return Tok.Punct;
        }

        /// <summary>현재 위치의 여는 괄호부터 짝이 맞는 닫는 괄호까지 건너뜀(문자열 안 괄호 무시).</summary>
        public bool SkipBalanced()
        {
            var depth = 0;
            while (true)
            {
                var tok = Next(out var t);
                if (tok == Tok.Eof) return false;
                if (tok != Tok.Punct) { if (depth == 0) return true; continue; }
                var c = t[0];
                if (c is (byte)'(' or (byte)'[' or (byte)'{') depth++;
                else if (c is (byte)')' or (byte)']' or (byte)'}') { depth--; if (depth < 0) return false; }
                if (depth == 0) return true;
            }
        }

        /// <summary>값 1개 건너뜀(스칼라 · 문자열 · 경로 · 괄호 그룹).</summary>
        public bool SkipValue()
        {
            SkipWs();
            if (Pos >= _s.Length) return false;
            var c = _s[Pos];
            if (c is (byte)'(' or (byte)'[' or (byte)'{') return SkipBalanced();
            var tok = Next(out _);
            return tok is Tok.Ident or Tok.Number or Tok.String or Tok.Path;
        }
    }
}
