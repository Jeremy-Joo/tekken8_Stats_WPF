using NPOI.SS.UserModel;
using static TekkenStats.Core.Headers;

namespace TekkenStats.Core;

/// <summary>ewgf 레코드 → 엑셀 워크북 (파이썬 ewgf.py save_workbook 포팅, 차트 제외).</summary>
public static class EwgfReport
{
    private static readonly string[] TypeOrder = { "Ranked", "Quick", "Player", "Group" };
    private static readonly StringComparer OIC = StringComparer.OrdinalIgnoreCase;

    private static int SeasonOrder(string s) => s switch { "S3" => 0, "S2" => 1, "S1" => 2, _ => 99 };

    public static string WriteWorkbook(IReadOnlyList<MatchRecord> recs, string outPath)
    {
        var chars = recs.Select(r => r.MyChar).Distinct().OrderBy(c => c, OIC).ToList();
        var typesPresent = TypeOrder.Where(t => recs.Any(r => r.BattleType == t)).ToList();
        string oldest = recs.Min(r => r.Dt).ToString("yyyy-MM-dd HH:mm:ss");
        string newest = recs.Max(r => r.Dt).ToString("yyyy-MM-dd HH:mm:ss");

        return ExcelWriter.SaveWithFallback(outPath, wb =>
        {
            // ── Total + 우측 종류/시즌 요약 ──
            var total = Annotate(Aggregations.BuildTotal(recs), Style.Bracket);
            var sTotal = wb.CreateSheet("Total");
            ExcelWriter.WriteTable(wb, sTotal, total, startRow: 2);
            sTotal.CreateRow(0).CreateCell(0).SetCellValue("집계 범위[데이터 실제 범위]");
            sTotal.GetRow(0).CreateCell(1).SetCellValue($"{oldest} ~ {newest}");

            var typeSum = Annotate(Aggregations.SummaryBy(recs, r => r.BattleType, "battleType"), Style.Bracket);
            var seasonSum = Annotate(Aggregations.SummaryBy(recs, r => r.Season, "season", SeasonOrder), Style.Bracket);
            int nxt = ExcelWriter.PlaceTable(sTotal, 0, 6, "종류별(플레이/승률)", typeSum);
            nxt = ExcelWriter.PlaceTable(sTotal, nxt, 6, "시즌별(플레이/승률)", seasonSum);

            var seasonTypes = Aggregations.BuildSeasonTypeCounts(recs);
            var seasonTypeHeaders = new Dictionary<string, string>
            {
                ["Season"] = "시즌", ["Ranked"] = "랭크 매치", ["Quick"] = "퀵 매치",
                ["Player"] = "플레이어 매치", ["Group"] = "그룹 매치", ["Games"] = "합계",
            };
            for (int i = 0; i < seasonTypes.Columns.Count; i++)
                if (seasonTypeHeaders.TryGetValue(seasonTypes.Columns[i], out string? label))
                    seasonTypes.Columns[i] = label;
            nxt = ExcelWriter.PlaceTable(sTotal, nxt, 6, "시즌별 매치 종류별 경기 수", seasonTypes);
            ExcelWriter.PlaceTable(sTotal, nxt, 6, "시즌별 매치 종류별 승패/승률",
                Annotate(Aggregations.BuildSeasonTypeSummary(recs), Style.Bracket));

            // ── by_type ──
            WriteSheet(wb, "by_type", Annotate(Aggregations.BuildByType(recs), Style.Bracket));

            // ── power_trend (데이터 + 캐릭터별 라인 차트) ──
            var (trend, trendChars) = Aggregations.BuildPowerTrend(recs);
            var sTrend = wb.CreateSheet("power_trend");
            ExcelWriter.WriteTable(wb, sTrend, Annotate(trend, Style.Bracket));
            ExcelWriter.AddLineChart(sTrend, trend.Count, firstCharCol: 4, charCount: trendChars.Count);

            // ── round_stats / weak_TOTAL ──
            WriteSheet(wb, "round_stats", Annotate(Aggregations.BuildRound(recs), Style.Round));
            WriteSheet(wb, "weak_TOTAL", Annotate(Aggregations.BuildWeak(recs), Style.Bracket));

            // ── 종류별 분리: round/weak/h2h ──
            foreach (var t in typesPresent)
            {
                var sub = recs.Where(r => r.BattleType == t).ToList();
                WriteSheet(wb, $"{t}_round", Annotate(Aggregations.BuildRound(sub), Style.Round));
                WriteSheet(wb, $"{t}_weak", Annotate(Aggregations.BuildWeak(sub), Style.Bracket));
                WriteSheet(wb, $"{t}_h2h", Annotate(Aggregations.BuildH2h(sub), Style.Bracket));
            }

            // ── 캐릭터별 합친 시트 ──
            var weak = Aggregations.ConsolidatePerChar(recs, chars, s => Aggregations.BuildWeak(s));
            WriteSheet(wb, "weak", Annotate(weak, Style.Bracket));

            var h2h = Aggregations.ConsolidatePerChar(recs, chars, s => Aggregations.BuildH2h(s));
            WriteSheet(wb, "h2h", Annotate(h2h, Style.Bracket));

            WriteSheet(wb, "daily", Annotate(Aggregations.BuildDaily(recs), Style.Bracket));

            // summary_wr: my_char 만 한글, 나머지는 영문(파이썬과 동일)
            var sumwr = Aggregations.ConsolidatePerChar(recs, chars, s => Aggregations.BuildPivot(s, "winrate"));
            if (sumwr.Columns.Count > 0) sumwr.Columns[0] = "my_char[캐릭터]";
            WriteSheet(wb, "summary_wr", sumwr);

            // ── games_all ──
            WriteSheet(wb, "games_all", BuildGamesAll(recs));

            // ── 모든 시트 자동맞춤 ──
            for (int i = 0; i < wb.NumberOfSheets; i++)
                ExcelWriter.AutoFit(wb, wb.GetSheetAt(i));
        });
    }

    private static void WriteSheet(IWorkbook wb, string name, Table t)
    {
        var sheet = wb.CreateSheet(ExcelWriter.SafeSheetName(name));
        ExcelWriter.WriteTable(wb, sheet, t);
    }

    private static Table BuildGamesAll(IReadOnlyList<MatchRecord> recs)
    {
        var t = new Table("dt", "player", "my_char", "my_rating", "my_delta", "score",
            "my_rounds", "opp_rounds", "result", "opp_rating", "opp_char", "opp_name",
            "opp_polaris", "battleType", "game_version", "season", "my_dan", "opp_dan", "region");
        foreach (var r in recs.OrderByDescending(r => r.Dt))
            t.Add(r.Dt.ToString("yyyy-MM-dd HH:mm:ss"), r.Player, r.MyChar, r.MyRating, r.MyDelta, r.Score,
                r.MyRounds, r.OppRounds, r.Result, r.OppRating, r.OppChar, r.OppName, r.OppPolaris,
                r.BattleType, r.GameVersion, r.Season, r.MyDan, r.OppDan, r.Region);
        return t;
    }
}
