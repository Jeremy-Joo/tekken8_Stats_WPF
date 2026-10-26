namespace TekkenStats.Core;

/// <summary>집계 로직(파이썬 build_* 포팅). 모두 LINQ 기반.</summary>
public static class Aggregations
{
    public const int WeakMinGames = 5;
    public const double WeakMaxWr = 50.0;
    public const int H2hMinGames = 2;

    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;
    private static readonly Dictionary<string, int> TypeOrder = new()
    {
        ["Ranked"] = 0, ["Quick"] = 1, ["Player"] = 2, ["Group"] = 3,
    };

    private static double Wr(int w, int total) => total > 0 ? Math.Round(w * 100.0 / total, 2) : 0.0;
    private static double Pct(int n, int total) => total > 0 ? Math.Round(n * 100.0 / total, 2) : 0.0;
    private static double Avg(int n, int games) => games > 0 ? Math.Round((double)n / games, 2) : 0.0;

    // ── Total: 캐릭터별 + ALL ──
    public static Table BuildTotal(IEnumerable<MatchRecord> df)
    {
        var t = new Table("my_char", "Total", "W", "L", "WinRate(%)");
        var groups = df.GroupBy(r => r.MyChar)
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { C = g.Key, W = w, L = l, Total = w + l }; })
            .OrderByDescending(x => x.Total).ThenByDescending(x => Wr(x.W, x.Total)).ThenBy(x => x.C, OIC)
            .ToList();
        foreach (var x in groups) t.Add(x.C, x.Total, x.W, x.L, Wr(x.W, x.Total));
        int aw = groups.Sum(x => x.W), al = groups.Sum(x => x.L), at = aw + al;
        t.Add("ALL", at, aw, al, Wr(aw, at));
        return t;
    }

    // ── 상대 캐릭터 피벗 (games / winrate 정렬) ──
    public static Table BuildPivot(IEnumerable<MatchRecord> df, string sortKey = "games")
    {
        var rows = df.GroupBy(r => r.OppChar)
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { Opp = g.Key, W = w, L = l, Games = w + l }; });
        rows = sortKey == "winrate"
            ? rows.OrderByDescending(x => Wr(x.W, x.Games)).ThenByDescending(x => x.Games).ThenBy(x => x.Opp, OIC)
            : rows.OrderByDescending(x => x.Games).ThenByDescending(x => Wr(x.W, x.Games)).ThenBy(x => x.Opp, OIC);
        var t = new Table("opp_char", "Games", "W", "L", "WinRate(%)");
        foreach (var x in rows) t.Add(x.Opp, x.Games, x.W, x.L, Wr(x.W, x.Games));
        return t;
    }

    // ── 약점 매치업 (표본 충분 + 승률 낮은 상대) ──
    public static Table BuildWeak(IEnumerable<MatchRecord> df, int minG = WeakMinGames, double maxWr = WeakMaxWr)
    {
        var rows = df.GroupBy(r => r.OppChar)
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { Opp = g.Key, W = w, L = l, Games = w + l }; })
            .Where(x => x.Games >= minG && Wr(x.W, x.Games) < maxWr)
            .OrderBy(x => Wr(x.W, x.Games)).ThenByDescending(x => x.Games);
        var t = new Table("opp_char", "Games", "W", "L", "WinRate(%)");
        foreach (var x in rows) t.Add(x.Opp, x.Games, x.W, x.L, Wr(x.W, x.Games));
        return t;
    }

    // ── 강점 매치업 (약점의 대칭) ──
    // 경계(정확히 50%)는 강점 쪽에 넣는다 — 5경기 이상인 매치업이 약점/강점 중
    // 정확히 한쪽에만 나타나게 하려는 것이다.
    public static Table BuildStrong(IEnumerable<MatchRecord> df, int minG = WeakMinGames,
        double minWr = WeakMaxWr)
    {
        var rows = df.GroupBy(r => r.OppChar)
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { Opp = g.Key, W = w, L = l, Games = w + l }; })
            .Where(x => x.Games >= minG && Wr(x.W, x.Games) >= minWr)
            .OrderByDescending(x => Wr(x.W, x.Games)).ThenByDescending(x => x.Games);
        var t = new Table("opp_char", "Games", "W", "L", "WinRate(%)");
        foreach (var x in rows) t.Add(x.Opp, x.Games, x.W, x.L, Wr(x.W, x.Games));
        return t;
    }

    // ── 라운드 통계 (캐릭터별 + ALL) ──
    public static Table BuildRound(IEnumerable<MatchRecord> df)
    {
        var list = df as IReadOnlyList<MatchRecord> ?? df.ToList();
        var t = new Table("my_char", "Games", "RoundsWon", "RoundsLost", "RoundWR(%)",
            "AvgRoundsWon", "AvgRoundsLost", "CloseGames", "Close(%)", "CloseWins", "CloseWin(%)",
            "CloseLosses", "CloseLoss(%)", "Shutouts_Dealt", "ShutoutWin(%)", "Shutouts_Received", "ShutoutLoss(%)");

        var perChar = list.GroupBy(r => r.MyChar)
            .Select(g => new { C = g.Key, Row = RoundRow(g, g.Key), Games = g.Count() })
            .OrderByDescending(x => x.Games).ToList();
        foreach (var x in perChar) t.Rows.Add(x.Row);
        t.Rows.Add(RoundRow(list, "ALL"));
        return t;
    }

    private static object?[] RoundRow(IEnumerable<MatchRecord> sub, string label)
    {
        var s = sub as IReadOnlyList<MatchRecord> ?? sub.ToList();
        int games = s.Count;
        int rw = s.Sum(r => r.MyRounds), rl = s.Sum(r => r.OppRounds), rtot = rw + rl;
        int close = s.Count(r => Math.Abs(r.MyRounds - r.OppRounds) == 1);
        int closeW = s.Count(r => Math.Abs(r.MyRounds - r.OppRounds) == 1 && r.Result == "W");
        int closeL = s.Count(r => Math.Abs(r.MyRounds - r.OppRounds) == 1 && r.Result == "L");
        int sd = s.Count(r => r.OppRounds == 0);
        int sr = s.Count(r => r.MyRounds == 0);
        return new object?[]
        {
            label, games, rw, rl, Wr(rw, rtot), Avg(rw, games), Avg(rl, games),
            close, Pct(close, games), closeW, Pct(closeW, games), closeL, Pct(closeL, games),
            sd, Pct(sd, games), sr, Pct(sr, games),
        };
    }

    // ── h2h: 식별코드(opp_polaris)+상대캐릭터 기준 ──
    public static Table BuildH2h(IEnumerable<MatchRecord> df, int minG = H2hMinGames)
    {
        var rows = df.GroupBy(r => (r.OppPolaris, r.OppChar))
            .Select(g =>
            {
                int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L");
                string name = g.Where(r => !string.IsNullOrEmpty(r.OppName))
                    .GroupBy(r => r.OppName).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "(unknown)";
                return new { Name = name, Pol = g.Key.OppPolaris, Opp = g.Key.OppChar, W = w, L = l, Games = w + l, Last = g.Max(r => r.Dt) };
            })
            .Where(x => x.Games >= minG)
            .OrderByDescending(x => x.Games).ThenByDescending(x => Wr(x.W, x.Games)).ThenBy(x => x.Name, OIC);
        var t = new Table("opp_name", "opp_polaris", "opp_char", "Games", "W", "L", "WinRate(%)", "LastPlayed");
        foreach (var x in rows)
            t.Add(x.Name, x.Pol, x.Opp, x.Games, x.W, x.L, Wr(x.W, x.Games), x.Last.ToString("yyyy-MM-dd HH:mm:ss"));
        return t;
    }

    // ── 일별 집계 (날짜 × 캐릭터) ──
    public static Table BuildDaily(IEnumerable<MatchRecord> df)
    {
        var rows = df.GroupBy(r => (Date: r.Dt.Date, r.MyChar))
            .Select(g =>
            {
                var ord = g.OrderBy(r => r.Dt).ToList();
                int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L");
                return new { g.Key.Date, g.Key.MyChar, Games = w + l, W = w, L = l, Delta = g.Sum(r => r.MyDelta), End = ord[^1].MyRating };
            })
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Games).ThenBy(x => x.MyChar, OIC);
        var t = new Table("Date", "my_char", "Games", "W", "L", "WinRate(%)", "RatingDelta", "EndRating");
        foreach (var x in rows)
            t.Add(x.Date.ToString("yyyy-MM-dd"), x.MyChar, x.Games, x.W, x.L, Wr(x.W, x.Games), x.Delta, x.End);
        return t;
    }

    // ── 캐릭터 × 종류 (종류 순 정렬) ──
    public static Table BuildByType(IEnumerable<MatchRecord> df)
    {
        var rows = df.GroupBy(r => (r.MyChar, r.BattleType))
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { g.Key.MyChar, Type = g.Key.BattleType, W = w, L = l, Games = w + l }; })
            .OrderBy(x => TypeOrder.GetValueOrDefault(x.Type, 99)).ThenByDescending(x => x.Games);
        var t = new Table("my_char", "battleType", "Games", "W", "L", "WinRate(%)");
        foreach (var x in rows) t.Add(x.MyChar, x.Type, x.Games, x.W, x.L, Wr(x.W, x.Games));
        return t;
    }

    // ── power_trend (와이드: 캐릭터별 레이팅 컬럼) ──
    public static (Table table, List<string> chars) BuildPowerTrend(IEnumerable<MatchRecord> df)
    {
        var ordered = df.OrderBy(r => r.Dt).ToList();
        var chars = ordered.Select(r => r.MyChar).Distinct().OrderBy(c => c, OIC).ToList();
        var cols = new List<string> { "dt", "my_rating", "my_char", "result" };
        cols.AddRange(chars);
        var t = new Table(cols.ToArray());
        foreach (var r in ordered)
        {
            var row = new object?[cols.Count];
            row[0] = r.Dt.ToString("yyyy-MM-dd HH:mm:ss");
            row[1] = r.MyRating;
            row[2] = r.MyChar;
            row[3] = r.Result;
            for (int i = 0; i < chars.Count; i++)
                row[4 + i] = chars[i] == r.MyChar ? r.MyRating : (object?)null;
            t.Rows.Add(row);
        }
        return (t, chars);
    }

    // ── 캐릭터별 빌더 결과를 my_char 컬럼 붙여 1개로 합침 ──
    public static Table ConsolidatePerChar(IReadOnlyList<MatchRecord> df, List<string> chars,
        Func<IEnumerable<MatchRecord>, Table> builder)
    {
        Table? outT = null;
        foreach (var ch in chars)
        {
            var sub = df.Where(r => r.MyChar == ch).ToList();
            var d = builder(sub);
            if (d.Count == 0) continue;
            outT ??= new Table(new[] { "my_char" }.Concat(d.Columns).ToArray());
            foreach (var row in d.Rows)
                outT.Add(new object?[] { ch }.Concat(row).ToArray());
        }
        outT ??= new Table("my_char");
        int gi = outT.Columns.IndexOf("Games");
        outT.Rows.Sort((a, b) =>
        {
            int c = OIC.Compare(a[0]?.ToString(), b[0]?.ToString());
            if (c != 0) return c;
            if (gi >= 0) return Convert.ToInt32(b[gi]).CompareTo(Convert.ToInt32(a[gi]));
            return 0;
        });
        return outT;
    }

    // ── 종류별/시즌별 요약 (플레이 횟수 + 승률) ──
    public static Table SummaryBy(IEnumerable<MatchRecord> df, Func<MatchRecord, string> key, string keyName,
        Func<string, int>? order = null)
    {
        var rows = df.GroupBy(key)
            .Select(g => { int w = g.Count(r => r.Result == "W"), l = g.Count(r => r.Result == "L"); return new { Key = g.Key, W = w, L = l, Games = w + l }; });
        rows = order != null ? rows.OrderBy(x => order(x.Key)) : rows.OrderByDescending(x => x.Games);
        var t = new Table(keyName, "Games", "W", "L", "WinRate(%)");
        foreach (var x in rows) t.Add(x.Key, x.Games, x.W, x.L, Wr(x.W, x.Games));
        return t;
    }

    /// <summary>시즌 × 매치 종류별 경기 수. 없는 조합은 0, 마지막 행은 종류별 전체 합계.</summary>
    public static Table BuildSeasonTypeCounts(IEnumerable<MatchRecord> df)
    {
        var records = df.ToList();
        var types = TypeOrder.OrderBy(x => x.Value).Select(x => x.Key)
            .Concat(records.Select(r => r.BattleType).Distinct()
                .Where(t => !TypeOrder.ContainsKey(t)).OrderBy(t => t, OIC)).ToList();
        var table = new Table(new[] { "Season" }.Concat(types).Append("Games").ToArray());
        var seasons = records.GroupBy(r => r.Season)
            .OrderByDescending(g => g.Key.StartsWith("S", StringComparison.Ordinal)
                && int.TryParse(g.Key.AsSpan(1), out int n) ? n : 0)
            .ThenBy(g => g.Key, OIC);

        void AddRow(string season, IEnumerable<MatchRecord> matches)
        {
            var counts = matches.GroupBy(r => r.BattleType).ToDictionary(g => g.Key, g => g.Count());
            table.Add(new object?[] { season }
                .Concat(types.Select(t => (object?)counts.GetValueOrDefault(t)))
                .Append(counts.Values.Sum()).ToArray());
        }

        foreach (var season in seasons) AddRow(season.Key, season);
        AddRow("ALL", records);
        return table;
    }

    /* ══════════════════════════════════════════════════════════════════
       아래 다섯은 web_game_tekken_stats_wavu(lib/tekken/aggregations.ts)에서
       옮겨온 것이다. 계산식을 바꾸지 말 것 — 웹과 같은 숫자가 나와야 한다.
       이미 수집·정규화까지 해놓고 안 쓰던 필드(MyRank/OppRating/OppDelta)를 살린 것이라
       추가 수집은 없다.
       ══════════════════════════════════════════════════════════════════ */

    /// <summary>직전 경기와 이만큼 이상 비면 새 세션으로 끊는다(캐릭터 무관, 시간 기준).</summary>
    public const int SessionGapMinutes = 120;

    /// <summary>경기 <b>직전</b> 레이팅 = 경기 후 값 - 변동. 상대와의 실력차는 이 값으로 봐야 맞다.</summary>
    private static int RatingBefore(MatchRecord r) => r.MyRating - r.MyDelta;
    private static int OppRatingBefore(MatchRecord r) => r.OppRating - r.OppDelta;

    /// <summary>시간 간격으로 세션을 끊는다(오래된 → 최신 순).</summary>
    private static List<List<MatchRecord>> SplitSessions(IEnumerable<MatchRecord> df, int gapMinutes)
    {
        var ordered = df.OrderBy(r => r.Dt).ToList();
        var sessions = new List<List<MatchRecord>>();
        if (ordered.Count == 0) return sessions;

        var cur = new List<MatchRecord> { ordered[0] };
        for (int i = 1; i < ordered.Count; i++)
        {
            if ((ordered[i].Dt - ordered[i - 1].Dt).TotalMinutes > gapMinutes)
            {
                sessions.Add(cur);
                cur = new List<MatchRecord>();
            }
            cur.Add(ordered[i]);
        }
        sessions.Add(cur);
        return sessions;
    }

    // ── 세션 집계 (세션 × 캐릭터, 최신 세션 우선) ──
    public static Table BuildSessions(IEnumerable<MatchRecord> df, int gapMinutes = SessionGapMinutes)
    {
        var t = new Table("Session", "Start", "End", "my_char", "Games", "W", "L",
            "WinRate(%)", "RatingDelta", "EndRating");

        var rows = new List<(string Label, string Start, string End, string Char,
            int W, int L, int Delta, int EndRating)>();

        foreach (var sess in SplitSessions(df, gapMinutes))
        {
            string label = sess[0].Dt.ToString("yyyy-MM-dd HH:mm");
            string end = sess[^1].Dt.ToString("yyyy-MM-dd HH:mm");
            foreach (var g in sess.GroupBy(r => r.MyChar))
            {
                var sub = g.ToList();
                rows.Add((label, label, end, g.Key,
                    sub.Count(r => r.Result == "W"), sub.Count(r => r.Result == "L"),
                    sub.Sum(r => r.MyDelta), sub[^1].MyRating));
            }
        }

        foreach (var x in rows
                     .OrderByDescending(x => x.Label, StringComparer.Ordinal)
                     .ThenByDescending(x => x.W + x.L)
                     .ThenBy(x => x.Char, OIC))
        {
            int games = x.W + x.L;
            t.Add(x.Label, x.Start, x.End, x.Char, games, x.W, x.L, Wr(x.W, games), x.Delta, x.EndRating);
        }
        return t;
    }

    // 레이팅차 구간 — 상대가 나보다 얼마나 위/아래였나. 경계는 표시 순서를 겸한다.
    private static readonly (string Label, double Lo, double Hi)[] DiffBands =
    {
        ("-300 이하 (내가 훨씬 위)", double.NegativeInfinity, -300),
        ("-300 ~ -150", -300, -150),
        ("-150 ~ -50", -150, -50),
        ("-50 ~ +50 (비슷)", -50, 50),
        ("+50 ~ +150", 50, 150),
        ("+150 ~ +300", 150, 300),
        ("+300 이상 (상대가 훨씬 위)", 300, double.PositiveInfinity),
    };

    /// <summary>
    /// 상대 레이팅대별 성적.
    /// 전체 승률 하나만 보면 55%가 '만만한 상대만 이겨서'인지 '강자도 잡아서'인지 알 수 없다.
    /// 레이팅차로 갈라 보면 그게 드러난다.
    /// </summary>
    public static Table BuildVsRating(IEnumerable<MatchRecord> df)
    {
        var t = new Table("RatingGap", "Games", "W", "L", "WinRate(%)", "AvgRatingDelta", "Share(%)");
        var agg = DiffBands.Select(b => new { b.Label, b.Lo, b.Hi, V = new int[3] }).ToList();  // [W, L, Delta]
        int counted = 0;

        foreach (var r in df)
        {
            int my = RatingBefore(r), op = OppRatingBefore(r);
            // 레이팅이 아직 안 붙은 경기(둘 중 하나가 0)는 실력차를 말할 수 없다 — 뺀다.
            if (my <= 0 || op <= 0) continue;
            double diff = op - my;
            var g = agg.FirstOrDefault(x => diff >= x.Lo && diff < x.Hi);
            if (g == null) continue;
            if (r.Result == "W") g.V[0]++; else g.V[1]++;
            g.V[2] += r.MyDelta;
            counted++;
        }

        foreach (var x in agg)
        {
            int games = x.V[0] + x.V[1];
            if (games == 0) continue;   // 빈 구간은 행을 만들지 않는다
            t.Add(x.Label, games, x.V[0], x.V[1], Wr(x.V[0], games), Avg(x.V[2], games), Pct(games, counted));
        }
        return t;
    }

    /// <summary>
    /// 승단 이력 — 단이 바뀐 시점만 한 줄씩. 최신 우선.
    ///
    /// 단별 누적 통계가 아니라 <b>사건 기록</b>이다. "언제 올라갔고 언제 떨어졌나,
    /// 그 직전 단에서 얼마나 버텼나"가 레이팅 숫자보다 체감에 가깝다.
    /// 단 이름은 붙이지 않는다 — wavu 가 노출하지 않아 숫자 그대로다.
    /// 오르내림은 숫자 크기로 판정하므로 이름을 몰라도 방향은 정확하다.
    /// </summary>
    public static Table BuildRankHistory(IEnumerable<MatchRecord> df)
    {
        var t = new Table("dt", "From", "To", "Change", "my_char", "my_rating",
            "PrevGames", "PrevWinRate(%)");
        var ordered = df.Where(r => r.MyRank != null).OrderBy(r => r.Dt).ToList();
        if (ordered.Count == 0) return t;

        var events = new List<(DateTime Dt, int From, int To, string Char, int Rating, int Games, int W)>();
        int cur = ordered[0].MyRank!.Value;
        int segW = 0, segL = 0;

        foreach (var r in ordered)
        {
            if (r.MyRank!.Value != cur)
            {
                // 직전 단에서의 성적(segW/segL)은 이 경기 **이전**까지다 — 이 경기는 새 단 소속.
                events.Add((r.Dt, cur, r.MyRank.Value, r.MyChar, r.MyRating, segW + segL, segW));
                cur = r.MyRank.Value;
                segW = segL = 0;
            }
            if (r.Result == "W") segW++; else segL++;
        }

        events.Reverse();
        foreach (var x in events)
            t.Add(x.Dt.ToString("yyyy-MM-dd HH:mm"), x.From, x.To,
                x.To > x.From ? "▲ 승단" : "▼ 강등", x.Char, x.Rating, x.Games, Wr(x.W, x.Games));
        return t;
    }

    private static readonly string[] Weekdays = { "일", "월", "화", "수", "목", "금", "토" };

    /// <summary>
    /// 시간대·요일 패턴. 두 표를 한 시트에 담는다.
    /// <paramref name="shiftMinutes"/> 는 KST 로부터의 차이(분) — 0 이면 KST 로 묶는다.
    /// <b>이 표에만 적용할 것.</b> 일별·세션·기간 필터·파일명은 KST 로 남는다.
    /// 그쪽까지 흔들면 '기간 08-01~08-02' 의 의미가 조회 대상마다 달라진다.
    /// </summary>
    public static Table BuildTimePatterns(IEnumerable<MatchRecord> df, int shiftMinutes = 0)
    {
        var t = new Table("Unit", "Bucket", "Games", "W", "L", "WinRate(%)", "AvgRatingDelta");
        var list = df as IReadOnlyList<MatchRecord> ?? df.ToList();

        void Push(string unit, string label, List<MatchRecord> rows)
        {
            if (rows.Count == 0) return;
            int w = rows.Count(r => r.Result == "W");
            t.Add(unit, label, rows.Count, w, rows.Count - w, Wr(w, rows.Count),
                Avg(rows.Sum(r => r.MyDelta), rows.Count));
        }

        var byHour = new Dictionary<int, List<MatchRecord>>();
        var byDow = new Dictionary<int, List<MatchRecord>>();
        foreach (var r in list)
        {
            var d = shiftMinutes != 0 ? r.Dt.AddMinutes(shiftMinutes) : r.Dt;
            // 요일도 같이 민다: UTC-8 의 토요일 밤은 KST 로 일요일이라, 시각만 밀면 어긋난다.
            (byHour.TryGetValue(d.Hour, out var hl) ? hl : byHour[d.Hour] = new()).Add(r);
            int dow = (int)d.DayOfWeek;
            (byDow.TryGetValue(dow, out var dl) ? dl : byDow[dow] = new()).Add(r);
        }

        for (int h = 0; h < 24; h++)
            Push("시간대", $"{h:00}시", byHour.GetValueOrDefault(h) ?? new());
        for (int d = 0; d < 7; d++)
            Push("요일", Weekdays[d], byDow.GetValueOrDefault(d) ?? new());
        return t;
    }

    // 세션 안 몇 번째 경기인지 구간 (피로도 확인용).
    // 라벨에 '세션'을 붙인 이유: 흐름 표는 구분 열 없이 항목만 보여주므로
    // 라벨 하나만 읽고도 무슨 기준인지 알 수 있어야 한다.
    private static readonly (string Label, int Lo, int Hi)[] NthBands =
    {
        ("세션 1~5번째", 1, 5),
        ("세션 6~10번째", 6, 10),
        ("세션 11~20번째", 11, 20),
        ("세션 21~30번째", 21, 30),
        ("세션 31번째 이상", 31, int.MaxValue),
    };

    /// <summary>
    /// '흐름' — 최근 폼 / 세션 내 순번(피로도) / 연승·연패 직후 / 연속 기록.
    ///
    /// 넷을 한 시트에 담는 이유: 각각은 몇 줄이라 시트를 따로 낼 만큼이 아니고,
    /// 넷 다 "지금 계속할까 그만할까"라는 한 가지 질문에 답하기 때문이다.
    /// 구분 열은 두지 않는다 — 항목 라벨이 스스로 무슨 기준인지 말한다.
    /// </summary>
    public static Table BuildFlow(IEnumerable<MatchRecord> df, int gapMinutes = SessionGapMinutes)
    {
        var t = new Table("Bucket", "Games", "W", "L", "WinRate(%)");
        var ordered = df.OrderBy(r => r.Dt).ToList();
        if (ordered.Count == 0) return t;

        void Add(string label, IReadOnlyList<MatchRecord> rows)
        {
            if (rows.Count == 0) return;
            int w = rows.Count(r => r.Result == "W");
            t.Add(label, rows.Count, w, rows.Count - w, Wr(w, rows.Count));
        }

        // ── 최근 폼: 최근 N경기가 전체와 다른가 ──
        foreach (int n in new[] { 20, 50, 100 })
            if (ordered.Count > n)
                Add($"최근 {n}경기", ordered.TakeLast(n).ToList());
        Add("전체 평균", ordered);

        // ── 세션 내 순번: 오래 할수록 떨어지는가 ──
        var nth = NthBands.Select(_ => new List<MatchRecord>()).ToList();
        int idx = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && (ordered[i].Dt - ordered[i - 1].Dt).TotalMinutes > gapMinutes) idx = 0;
            idx++;
            int b = Array.FindIndex(NthBands, x => idx >= x.Lo && idx <= x.Hi);
            if (b >= 0) nth[b].Add(ordered[i]);
        }
        for (int i = 0; i < NthBands.Length; i++) Add(NthBands[i].Label, nth[i]);

        // ── 연승·연패 직후: 흐름을 타는가, 무너지는가 ──
        var after = new (string Label, List<MatchRecord> Rows)[]
        {
            ("2연승 직후", new()), ("3연승 이상 직후", new()),
            ("2연패 직후", new()), ("3연패 이상 직후", new()),
        };
        int run = 0;   // 양수=연승, 음수=연패
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                if (run >= 3) after[1].Rows.Add(ordered[i]);
                else if (run == 2) after[0].Rows.Add(ordered[i]);
                else if (run <= -3) after[3].Rows.Add(ordered[i]);
                else if (run == -2) after[2].Rows.Add(ordered[i]);
            }
            bool won = ordered[i].Result == "W";
            run = won ? (run > 0 ? run + 1 : 1) : (run < 0 ? run - 1 : -1);
        }
        foreach (var (label, rows) in after) Add(label, rows);

        // ── 연속 기록: 최장 연승/연패와 현재 상태 ──
        int bestW = 0, bestL = 0, cur = 0;
        foreach (var r in ordered)
        {
            cur = r.Result == "W" ? (cur > 0 ? cur + 1 : 1) : (cur < 0 ? cur - 1 : -1);
            if (cur > bestW) bestW = cur;
            if (-cur > bestL) bestL = -cur;
        }
        t.Add("최장 연승", bestW, bestW, 0, 100);
        t.Add("최장 연패", bestL, 0, bestL, 0);
        t.Add(cur >= 0 ? "현재 연승" : "현재 연패", Math.Abs(cur),
            cur >= 0 ? cur : 0, cur < 0 ? -cur : 0, cur >= 0 ? 100 : 0);
        return t;
    }
}
