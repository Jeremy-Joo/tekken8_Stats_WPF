using Microsoft.Playwright;

namespace TekkenStats.Core;

/// <summary>홈페이지 접속 확인 → 브라우저 종료 → 플레이어 페이지 새 실행 → 추출 → 엑셀.</summary>
public static class EwgfCollector
{
    public const string Ewgf = "https://ewgf.gg";
    public const string CanceledError = Collect.CanceledError;

    public static async Task<CollectResult> CollectAsync(
        string playerId, DateTime? start, DateTime? end, string outRoot, string profileDir, Action<string> log,
        CancellationToken ct = default)
    {
        string url = $"{Ewgf}/player/{playerId}";
        await using var session = new BrowserSession(profileDir, log, ownBrowser: true);
        try
        {
            log($"[사전 접속 1/3] {Ewgf}/ — 홈페이지 접근 확인");
            await using (var homepage = new BrowserSession(profileDir, log, ownBrowser: true))
            {
                await homepage.StartAsync(Ewgf + "/", ct);
                if (!await homepage.WaitPastCloudflareAsync(ct: ct))
                    return new CollectResult(playerId, 0, null,
                        "EWGF 홈페이지의 보안 확인을 완료하지 못했습니다. 플레이어 조회를 시작하지 않았습니다.");
                await homepage.ConnectAsync(ct);
                await homepage.Page.GetByRole(AriaRole.Textbox, new() { Name = "Search players" })
                    .First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }).WaitAsync(ct);
                if (!BrowserSession.SameTargetUrl(homepage.Page.Url, Ewgf + "/"))
                    throw new InvalidOperationException("EWGF 홈페이지 접근을 확인하는 중 페이지가 바뀌었습니다.");
                log("[사전 접속 2/3] 홈페이지 정상 접근 확인 — 브라우저를 종료합니다");
                await homepage.CloseBrowserAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            log($"[사전 접속 3/3] 같은 프로필로 플레이어 페이지를 새로 엽니다: {url}");
            await session.StartAsync(url, ct);
            // Cloudflare 통과(순수 크롬 상태에서 CDP HTTP title 로 감지) → 통과 후에만 Playwright 연결.
            if (!await session.WaitPastCloudflareAsync(ct: ct))
                return new CollectResult(playerId, 0, null,
                    "Cloudflare verification did not complete. Open ewgf.gg once in normal Chrome on this PC, then run again.");
            await session.ConnectAsync(ct);
            try
            {
                await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                    new PageWaitForLoadStateOptions { Timeout = 20_000 });
            }
            catch (Exception) { }  // NetworkIdle 미도달은 무시하고 진행
            ct.ThrowIfCancellationRequested();
            // 보안 확인 화면이나 로딩 중인 문서를 '경기 없음'으로 처리하지 않는다.
            string displayId = string.Join("-", playerId.Chunk(4).Select(chars => new string(chars)));
            await session.Page.GetByRole(AriaRole.Button, new() { Name = $"Copy Tekken ID {displayId}", Exact = true })
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }).WaitAsync(ct);

            string html = await session.GetStableContentAsync(ct: ct);
            if (!BrowserSession.SameTargetUrl(session.Page.Url, url))
                throw new InvalidOperationException($"수집 중 대상 페이지가 바뀌었습니다. 요청: {url}, 현재: {session.Page.Url}");
            var battles = EwgfExtractor.ExtractBattles(html);
            log($"[추출] battle {battles.Count}건");

            var (recs, name) = EwgfExtractor.Normalize(battles, playerId);
            log($"[정규화] 내 경기 {recs.Count}건  이름='{name}'");

            // 날짜 필터 (지정 시)
            string tag = Collect.DateTag(start, end);
            if (tag.Length > 0)
            {
                int before = recs.Count;
                recs = Collect.FilterByDate(recs, start, end);
                log($"[기간필터] {recs.Count}/{before}건");
            }

            if (recs.Count == 0)
                return new CollectResult(playerId, 0, null, "경기 없음(식별코드/기간 확인)");

            log($"[실제 기간] {recs.Min(r => r.Dt):yyyy-MM-dd HH:mm:ss} ~ {recs.Max(r => r.Dt):yyyy-MM-dd HH:mm:ss} (KST), {recs.Count}경기");

            string safe = Collect.Sanitize(string.IsNullOrEmpty(name) ? playerId : name);
            string stamp = DateTime.Now.ToString("yyyy_MMdd_HHmmss");   // 생성 시각: 연_월일_시분초
            string outPath = Path.Combine(outRoot, safe, $"{safe}_{playerId}_ewgf{tag}_{stamp}.xlsx");
            string saved = EwgfReport.WriteWorkbook(recs, outPath);
            log($"[완료] {saved}");
            return new CollectResult(playerId, recs.Count, saved, "", name, recs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log("[중지됨] 사용자 요청으로 중지했습니다.");
            return new CollectResult(playerId, 0, null, CanceledError);
        }
        catch (Exception ex)
        {
            log($"[오류 상세] {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                log($"[내부 오류] {inner.GetType().Name}: {inner.Message}");
            return new CollectResult(playerId, 0, null, BrowserSession.Friendly(ex));
        }
    }
}
