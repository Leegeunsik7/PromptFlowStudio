using System.Text;

namespace PromptFlowStudio.Services.Ui;

// =============================================================================
//  한/영 오타 보정 검색
//
//  ━━━ 무엇을 푸는가 ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//
//  관제원이 Ctrl+K 를 누르고 "카메라" 를 치려는데 IME 가 영문이면
//  "zkapfk" 가 입력된다. 그대로 검색하면 결과가 0건이고, 사용자는
//  "이 기능 안 되네" 하고 다시 안 쓴다. 실제로 가장 흔한 실패다.
//
//  그래서 입력을 두 갈래로 본다:
//    ① 친 그대로
//    ② 한글 자판으로 옮긴 것 (zkapfk → 카메라)
//  둘 중 하나라도 맞으면 결과로 친다.
//
//  ⚠ 반대 방향(한글로 친 영문, "ㅗㅐㅡㄷ" → "home")은 하지 않는다.
//    메뉴 이름이 전부 한글이라 이득이 거의 없고, 오히려 한글 검색어를
//    영문으로 잘못 바꿔 엉뚱한 결과를 만든다.
//
//  ⚠ 초성 검색(ㅋㅁㄹ → 카메라)도 하지 않는다. 두 글자 초성이 너무 많은
//    항목에 걸려 순위가 무너진다. 필요해지면 그때 별도로 판단한다.
// =============================================================================
public static class KoreanSearch
{
    // ── 두벌식 자판 → 자모 ──────────────────────────────────────────────────
    private const string LatinLower = "qwertyuiopasdfghjklzxcvbnm";
    private const string JamoLower  = "ㅂㅈㄷㄱ쇼ㅕㅑㅐㅔㅁㄴㅇㄹㅎㅗㅓㅏㅣㅋㅌㅊㅍㅠㅜㅡ";

    /// <summary>QWERTY 키 → 자모. 대문자는 쌍자음·이중모음.</summary>
    private static readonly Dictionary<char, char> Key2Jamo = new()
    {
        ['q'] = 'ㅂ', ['w'] = 'ㅈ', ['e'] = 'ㄷ', ['r'] = 'ㄱ', ['t'] = 'ㅅ',
        ['y'] = 'ㅛ', ['u'] = 'ㅕ', ['i'] = 'ㅑ', ['o'] = 'ㅐ', ['p'] = 'ㅔ',
        ['a'] = 'ㅁ', ['s'] = 'ㄴ', ['d'] = 'ㅇ', ['f'] = 'ㄹ', ['g'] = 'ㅎ',
        ['h'] = 'ㅗ', ['j'] = 'ㅓ', ['k'] = 'ㅏ', ['l'] = 'ㅣ',
        ['z'] = 'ㅋ', ['x'] = 'ㅌ', ['c'] = 'ㅊ', ['v'] = 'ㅍ',
        ['b'] = 'ㅠ', ['n'] = 'ㅜ', ['m'] = 'ㅡ',
        ['Q'] = 'ㅃ', ['W'] = 'ㅉ', ['E'] = 'ㄸ', ['R'] = 'ㄲ', ['T'] = 'ㅆ',
        ['O'] = 'ㅒ', ['P'] = 'ㅖ',
    };

    // 유니코드 한글 음절 조합 순서 (U+AC00 = 가)
    private const string Cho = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string Jung = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    private const string Jong = "\0ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    /// <summary>겹모음 — 중성 두 개가 이어지면 하나로 합쳐진다.</summary>
    private static readonly Dictionary<(char, char), char> DoubleJung = new()
    {
        [('ㅗ', 'ㅏ')] = 'ㅘ', [('ㅗ', 'ㅐ')] = 'ㅙ', [('ㅗ', 'ㅣ')] = 'ㅚ',
        [('ㅜ', 'ㅓ')] = 'ㅝ', [('ㅜ', 'ㅔ')] = 'ㅞ', [('ㅜ', 'ㅣ')] = 'ㅟ',
        [('ㅡ', 'ㅣ')] = 'ㅢ',
    };

    /// <summary>겹받침.</summary>
    private static readonly Dictionary<(char, char), char> DoubleJong = new()
    {
        [('ㄱ', 'ㅅ')] = 'ㄳ', [('ㄴ', 'ㅈ')] = 'ㄵ', [('ㄴ', 'ㅎ')] = 'ㄶ',
        [('ㄹ', 'ㄱ')] = 'ㄺ', [('ㄹ', 'ㅁ')] = 'ㄻ', [('ㄹ', 'ㅂ')] = 'ㄼ',
        [('ㄹ', 'ㅅ')] = 'ㄽ', [('ㄹ', 'ㅌ')] = 'ㄾ', [('ㄹ', 'ㅍ')] = 'ㄿ',
        [('ㄹ', 'ㅎ')] = 'ㅀ', [('ㅂ', 'ㅅ')] = 'ㅄ',
    };

    /// <summary>
    /// 영문 자판 입력을 한글로 옮긴다. 한글이 섞여 있으면 그대로 통과시킨다.
    ///
    /// <para>
    /// ⚠ 완벽한 IME 를 만들려는 게 아니다. 검색어를 한 번 더 시도해 보는
    ///   용도이므로, 애매하면 원문을 그대로 두는 쪽을 택한다.
    ///   틀린 변환으로 엉뚱한 결과를 보여 주는 것이 0건보다 나쁘다.
    /// </para>
    /// </summary>
    public static string LatinToHangul(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "";

        var sb = new StringBuilder(input.Length);
        // 조합 중인 음절
        int cho = -1, jung = -1, jong = 0;

        void Flush()
        {
            if (cho < 0) return;
            if (jung < 0)
            {
                // 자음만 남았다 — 낱자로 내보낸다
                sb.Append(Cho[cho]);
            }
            else
            {
                sb.Append((char)(0xAC00 + (cho * 21 + jung) * 28 + jong));
            }
            cho = -1; jung = -1; jong = 0;
        }

        foreach (var ch in input)
        {
            if (!Key2Jamo.TryGetValue(ch, out var j))
            {
                Flush();
                sb.Append(ch);          // 숫자·공백·한글 등은 그대로
                continue;
            }

            var isVowel = Jung.IndexOf(j) >= 0;

            if (isVowel)
            {
                var v = Jung.IndexOf(j);

                if (cho < 0)
                {
                    // 초성 없이 모음 — 낱자 모음으로 내보낸다
                    Flush();
                    sb.Append(j);
                    continue;
                }

                if (jung < 0) { jung = v; continue; }

                // 겹모음 시도
                if (jong == 0 && DoubleJung.TryGetValue((Jung[jung], j), out var dbl))
                {
                    jung = Jung.IndexOf(dbl);
                    continue;
                }

                // 받침이 있었다면 그 받침이 다음 글자의 초성으로 넘어간다 (연음)
                if (jong != 0)
                {
                    var moved = Jong[jong];
                    // 겹받침이면 뒤 자음만 넘긴다
                    var pair = DoubleJong.FirstOrDefault(kv => kv.Value == moved);
                    if (pair.Value != '\0')
                    {
                        jong = Jong.IndexOf(pair.Key.Item1);
                        moved = pair.Key.Item2;
                    }
                    else
                    {
                        jong = 0;
                    }
                    Flush();
                    cho = Cho.IndexOf(moved);
                    jung = v;
                    continue;
                }

                Flush();
                sb.Append(j);           // 초성이 될 수 없는 상황
                continue;
            }

            // 자음
            if (cho < 0) { cho = Cho.IndexOf(j); if (cho < 0) { sb.Append(j); } continue; }
            if (jung < 0)
            {
                // 초성이 연달아 왔다 — 앞 글자를 끝내고 새로 시작
                Flush();
                cho = Cho.IndexOf(j);
                continue;
            }

            var jo = Jong.IndexOf(j);
            if (jo <= 0)
            {
                // 받침이 될 수 없는 자음(ㄸㅃㅉ)
                Flush();
                cho = Cho.IndexOf(j);
                continue;
            }

            if (jong == 0) { jong = jo; continue; }

            if (DoubleJong.TryGetValue((Jong[jong], j), out var dj))
            {
                jong = Jong.IndexOf(dj);
                continue;
            }

            Flush();
            cho = Cho.IndexOf(j);
        }

        Flush();
        return sb.ToString();
    }

    /// <summary>
    /// 검색 점수. 0 이면 불일치.
    ///
    /// <para>
    /// 앞에서 맞을수록, 짧은 대상일수록 높다 — "카메라" 를 치면
    /// '카메라 관리' 가 '가상 시뮬레이터(카메라 없이…)' 보다 위에 와야 한다.
    /// </para>
    /// </summary>
    public static int Score(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return 1;
        if (string.IsNullOrEmpty(haystack)) return 0;

        var h = haystack.ToLowerInvariant();
        var n = needle.Trim().ToLowerInvariant();

        var idx = h.IndexOf(n, StringComparison.Ordinal);
        if (idx < 0) return 0;

        var score = 1000;
        score -= idx * 10;                       // 앞에서 맞을수록 좋다
        score -= Math.Min(h.Length, 200);        // 짧은 대상이 더 정확한 답이다
        if (idx == 0) score += 300;              // 접두 일치는 크게 우대
        return Math.Max(score, 1);
    }

    /// <summary>
    /// 원문과 한글 변환본 양쪽으로 점수를 내고 높은 쪽을 쓴다.
    /// ⚠ 변환본 점수는 살짝 깎는다. 제대로 친 검색어가 항상 우선이어야 한다.
    /// </summary>
    public static int SmartScore(string haystack, string needle)
    {
        var direct = Score(haystack, needle);

        var converted = LatinToHangul(needle);
        if (string.Equals(converted, needle, StringComparison.Ordinal)) return direct;

        var alt = Score(haystack, converted);
        if (alt > 0) alt = Math.Max(1, alt - 120);

        return Math.Max(direct, alt);
    }
}
