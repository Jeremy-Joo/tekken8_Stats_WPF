using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace TekkenStats.Core;

/// <summary>
/// 실제 크롬을 디버그 모드로 직접 띄우고 CDP 로 연결한다(파이썬 launch_chrome_and_connect 포팅).
/// Playwright 가 띄우는 '자동화 브라우저'가 아니라 정상 크롬이라 Cloudflare 봇 탐지를 크게 회피한다.
///
/// 중요: Cloudflare 챌린지가 떠 있는 동안에는 Playwright(CDP 클라이언트)를 붙이지 않는다.
/// CDP 로 Runtime 도메인이 켜지면 Cloudflare Turnstile 이 자동화로 탐지해 사람이 체크박스를
/// 눌러도 토큰을 무효 처리하고 챌린지를 무한 재출제하기 때문이다. 그래서 챌린지 통과 감지는
/// CDP 의 HTTP 엔드포인트(/json/list)의 페이지 title 로만 하고, 통과 후에야 Playwright 를 붙인다.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    public const int CdpPort = 9222;
    public static string CdpUrl => $"http://127.0.0.1:{CdpPort}";
    public static string DefaultProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TekkenRecordMaker", "browser_profile");
    public const int ChallengeWaitMs = 180_000;  // Cloudflare 수동 통과 대기
    public const int RowsWaitMs = 15_000;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly string _profileDir;
    private readonly Action<string>? _log;
    private readonly bool _ownBrowser;
    private int _cdpPort = CdpPort;
    private string DebuggingUrl => $"http://127.0.0.1:{_cdpPort}";
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private Process? _chrome;
    private string _startUrl = "";
    private bool _freshLaunch;
    private bool _started;
    private bool _closed;

    public IBrowserContext Context { get; private set; } = default!;
    public IPage Page { get; private set; } = default!;

    public BrowserSession(string profileDir, Action<string>? log = null, bool ownBrowser = false)
    {
        _profileDir = string.IsNullOrWhiteSpace(profileDir)
            ? DefaultProfileDir
            : Environment.ExpandEnvironmentVariables(profileDir);
        _log = log;
        _ownBrowser = ownBrowser;
    }

    /// <summary>예외 메시지를 사용자에게 보일 친절한 안내로 변환.</summary>
    public static string Friendly(Exception ex)
    {
        string m = ex.Message ?? "";
        string t = ex.GetType().Name;
        // TargetClosed = CDP 연결 도중 크롬(브라우저) 프로세스가 종료됨.
        if (t.Contains("TargetClosed", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Process exited", StringComparison.OrdinalIgnoreCase))
            return "자동화 구성요소(드라이버 node.exe 또는 크롬)가 실행 직후 종료됐습니다. 보통 ① Visual C++ 2015–2022 재배포 패키지(x64)가 없거나 ② 백신/보안 프로그램이 node.exe·크롬을 차단한 경우입니다. 위 [진단] 로그를 확인하고, 먼저 VC++ x64 재배포 패키지를 설치해 보세요(https://aka.ms/vs/17/release/vc_redist.x64.exe).";
        return m;
    }

    /// <summary>설치된 크롬(우선) 또는 엣지 실행 파일 경로.</summary>
    public static string? FindBrowserExe()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(pf,    @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86,  @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86,  @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf,    @"Microsoft\Edge\Application\msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task<bool> CdpReadyAsync()
    {
        try
        {
            using var r = await Http.GetAsync(DebuggingUrl + "/json/version");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// 크롬을 디버그 모드로 띄운다. 이 단계에서는 Playwright 를 붙이지 않는다(순수 크롬 상태에서
    /// Cloudflare 를 통과시키기 위함). 연결은 통과 후 <see cref="ConnectAsync"/> 에서 한다.
    /// </summary>
    public async Task StartAsync(string startUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _startUrl = startUrl;
        Directory.CreateDirectory(_profileDir);

        // EWGF 의 사전 접속/재실행은 이 수집기가 띄운 브라우저만 사용한다.
        if (_ownBrowser)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _cdpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        if (_ownBrowser || !await CdpReadyAsync())
        {
            string exe = FindBrowserExe()
                ?? throw new InvalidOperationException("크롬/엣지 실행 파일을 찾지 못했습니다.");
            _log?.Invoke($"[브라우저] 디버그 모드로 실행: {exe}");
            _log?.Invoke($"[Browser] profile: {_profileDir}");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                ArgumentList =
                {
                    $"--remote-debugging-port={_cdpPort}",
                    $"--user-data-dir={_profileDir}",
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-blink-features=AutomationControlled",
                    "--hide-crash-restore-bubble",
                    "--disable-session-crashed-bubble",
                    startUrl,
                },
            };
            _chrome = Process.Start(psi);
            _started = true;
            _freshLaunch = true;
            _log?.Invoke($"[브라우저] PID={_chrome?.Id}, {DebuggingUrl}");
            for (int i = 0; i < 60; i++)
            {
                if (await CdpReadyAsync()) break;
                if (_chrome is { HasExited: true })
                    throw new InvalidOperationException("수집용 브라우저가 시작 직후 종료됐습니다. 같은 브라우저 프로필을 사용 중인 창을 닫고 다시 시도해 주세요.");
                await Task.Delay(500, ct);
            }
            if (!await CdpReadyAsync())
                throw new InvalidOperationException("크롬 디버그 포트 연결 실패 (CDP 미응답)");
        }
        else
        {
            _freshLaunch = false;
            _started = true;
            _log?.Invoke("[브라우저] 이미 실행 중인 디버그 크롬에 연결");
        }
    }

    private static readonly string[] ChallengeMarkers =
    {
        "just a moment", "잠시만", "보안 확인", "사람인지", "attention required",
        "checking your browser", "cloudflare",
    };

    /// <summary>CDP HTTP(/json/list)로 요청한 URL 탭의 title 을 읽는다(Playwright 미사용).</summary>
    private async Task<(bool found, string title)> TryGetTargetTitleAsync()
    {
        try
        {
            using var r = await Http.GetAsync(DebuggingUrl + "/json/list");
            if (!r.IsSuccessStatusCode) return (false, "");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("type", out var t) || t.GetString() != "page") continue;
                string url = el.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";
                if (!SameTargetUrl(url, _startUrl)) continue;
                string title = el.TryGetProperty("title", out var ti) ? (ti.GetString() ?? "") : "";
                return (true, title);
            }
        }
        catch { }
        return (false, "");
    }

    /// <summary>
    /// Cloudflare 가 사라질 때까지(사용자 수동 통과 포함) 대기. CDP HTTP 의 title 로만 판정하므로
    /// 이 단계에서 Playwright/CDP Runtime 이 켜지지 않아 자동화로 탐지되지 않는다.
    /// 이미 실행 중이던 크롬에 붙은 경우(_freshLaunch=false)는 여기서 판정하지 않고 ConnectAsync 에서 처리.
    /// 사용자가 대상 탭을 강제로 닫아도(크롬 프로세스는 살아있음) 몇 초 후 새 탭을 자동으로 다시 연다.
    /// </summary>
    public async Task<bool> WaitPastCloudflareAsync(int maxWaitMs = ChallengeWaitMs, CancellationToken ct = default)
    {
        if (!_freshLaunch) return true;

        int waited = 0;
        bool notified = false;
        int notFoundStreak = 0;
        bool triedReopen = false;
        await Task.Delay(1500, ct);  // Cloudflare 인터스티셜이 렌더될 여유
        while (waited < maxWaitMs)
        {
            if (!await CdpReadyAsync())
                throw new InvalidOperationException("크롬 창이 닫혀 연결이 끊겼습니다. 다시 시도해 주세요.");

            var (found, title) = await TryGetTargetTitleAsync();
            string low = title.ToLowerInvariant();
            bool challenge = ChallengeMarkers.Any(m => low.Contains(m));
            // 통과: 대상 페이지의 title 이 비어있지 않고 챌린지 문구가 아님
            if (found && title.Length > 0 && !challenge) return true;

            if (!found)
            {
                notFoundStreak++;
                // 사용자가 탭을 강제로 닫은 경우: 계속 기다리지 않고 새 탭을 직접 열어본다.
                if (!triedReopen && notFoundStreak >= 4)
                {
                    triedReopen = true;
                    _log?.Invoke("[브라우저] 대상 탭을 찾지 못해 새로 엽니다…");
                    await TryReopenTabAsync(_startUrl);
                }
            }
            else
            {
                notFoundStreak = 0;
            }

            if (challenge && !notified)
            {
                _log?.Invoke("[수동 확인 필요] Cloudflare 보안 확인 — 크롬 창에서 체크박스를 눌러주세요.");
                notified = true;
            }
            await Task.Delay(1000, ct);
            waited += 1000;
        }
        _log?.Invoke("[Cloudflare] Verification did not complete. Open ewgf.gg once in normal Chrome on this PC, then run again.");
        return false;
    }

    /// <summary>CDP HTTP(/json/new)로 새 탭을 연다. 사용자가 탭을 강제로 닫았을 때의 복구용.</summary>
    private async Task TryReopenTabAsync(string url)
    {
        try
        {
            using var r = await Http.PutAsync(DebuggingUrl + "/json/new?" + Uri.EscapeDataString(url), null);
            if (!r.IsSuccessStatusCode)
                _log?.Invoke($"[브라우저] 새 탭 열기 실패: HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[브라우저] 새 탭 열기 실패: {ex.Message}");
        }
    }

    /// <summary>Cloudflare 통과 후 Playwright 를 CDP 로 연결하고 대상 페이지를 잡는다.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _log?.Invoke("[Playwright] 드라이버 시작…");
        try
        {
            _pw = await Playwright.CreateAsync();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[진단] 드라이버 시작 실패: {ex.GetType().Name}: {ex.Message}");
            DiagnoseDriver();   // node.exe 를 직접 실행해 실제 원인(의존 DLL/차단) 파악
            throw;
        }
        if (_chrome is { HasExited: true })
            _log?.Invoke("[경고] 크롬 프로세스가 이미 종료됨 — 보안 프로그램 차단 또는 크롬 크래시 가능성");
        else if (!await CdpReadyAsync())
            _log?.Invoke("[경고] CDP 포트 응답 없음 — 크롬이 닫혔을 수 있음");
        _log?.Invoke("[Playwright] CDP 연결…");
        _browser = await _pw.Chromium.ConnectOverCDPAsync(DebuggingUrl);
        Context = _browser.Contexts.Count > 0 ? _browser.Contexts[0] : await _browser.NewContextAsync();

        string host;
        try { host = new Uri(_startUrl).Host; }
        catch { host = ""; }

        // 같은 사이트의 다른 플레이어 탭이 복원돼 있을 수 있다. 식별코드까지 일치하는 탭을 우선한다.
        Page = Context.Pages.FirstOrDefault(p => SameTargetUrl(p.Url, _startUrl))
               ?? Context.Pages.FirstOrDefault(p => SameHost(p.Url, host))
               ?? (Context.Pages.Count > 0 ? Context.Pages[0] : await Context.NewPageAsync());

        // 이미 떠 있던 크롬에 붙었거나(대상 URL 이 아님) 대상 페이지가 아니면 이동 + (필요 시) 통과 대기.
        if (!_freshLaunch || !SameTargetUrl(Page.Url, _startUrl))
        {
            await Page.GotoAsync(_startUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            if (await LooksLikeChallengeAsync() && !await WaitPastCloudflarePlaywrightAsync(ct: ct))
                throw new InvalidOperationException("Cloudflare 보안 확인을 완료하지 못했습니다. 다시 시도해 주세요.");
        }

        if (!SameTargetUrl(Page.Url, _startUrl))
            throw new InvalidOperationException($"요청한 페이지와 열린 페이지가 다릅니다. 요청: {_startUrl}, 현재: {Page.Url}");
        _log?.Invoke($"[대상 페이지] {Page.Url}");
    }

    /// <summary>드라이버 node.exe 를 직접 실행해 즉시 종료의 실제 원인(의존 DLL 부재/보안 차단 등)을 로그로 남긴다.</summary>
    private void DiagnoseDriver()
    {
        try
        {
            string node = Path.Combine(AppContext.BaseDirectory, ".playwright", "node", "win32_x64", "node.exe");
            _log?.Invoke($"[진단] node 경로: {node}");
            _log?.Invoke($"[진단] node 존재={File.Exists(node)}");
            if (!File.Exists(node)) return;

            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            _log?.Invoke($"[진단] node 종료코드={p.ExitCode} 버전='{outp.Trim()}' 에러='{err.Trim()}'");
        }
        catch (Exception dex)
        {
            // Win32Exception 이면 메시지에 '0xc000007b'(의존 DLL/비트수) 또는 '액세스가 거부'(보안 차단) 단서가 담긴다.
            _log?.Invoke($"[진단] node 직접 실행 실패: {dex.GetType().Name}: {dex.Message}");
        }
    }

    private static bool SameHost(string url, string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        try { return new Uri(url).Host.Equals(host, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>호스트뿐 아니라 플레이어 경로와 조회 조건까지 일치해야 같은 수집 대상이다.</summary>
    internal static bool SameTargetUrl(string url, string targetUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var actual) ||
            !Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)) return false;
        return actual.Scheme.Equals(target.Scheme, StringComparison.OrdinalIgnoreCase)
               && actual.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)
               && actual.Port == target.Port
               && actual.AbsolutePath.TrimEnd('/').Equals(target.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal)
               && actual.Query.Equals(target.Query, StringComparison.Ordinal);
    }

    /// <summary>현재 페이지가 Cloudflare 보안 확인 페이지인지 추정(Playwright 연결 후 사용).</summary>
    public async Task<bool> LooksLikeChallengeAsync()
    {
        string title;
        try { title = (await Page.TitleAsync() ?? "").ToLowerInvariant(); }
        catch { title = ""; }
        if (ChallengeMarkers.Any(m => title.Contains(m))) return true;

        string body;
        try
        {
            var loc = Page.Locator("body");
            body = ((await loc.InnerTextAsync()) ?? "");
            body = body[..Math.Min(3000, body.Length)].ToLowerInvariant();
        }
        catch { body = ""; }
        return ChallengeMarkers.Any(m => body.Contains(m));
    }

    /// <summary>Playwright 기반 통과 대기(이미 실행 중이던 크롬에 붙은 경우의 폴백).</summary>
    private async Task<bool> WaitPastCloudflarePlaywrightAsync(int maxWaitMs = ChallengeWaitMs, CancellationToken ct = default)
    {
        int waited = 0;
        bool notified = false;
        while (waited < maxWaitMs)
        {
            if (!await LooksLikeChallengeAsync()) return true;
            if (!notified)
            {
                _log?.Invoke("[수동 확인 필요] Cloudflare 보안 확인 — 크롬 창에서 체크박스를 눌러주세요.");
                notified = true;
            }
            await Task.Delay(1000, ct);
            waited += 1000;
        }
        return !await LooksLikeChallengeAsync();
    }

    /// <summary>
    /// 페이지 HTML 을 안전하게 읽는다. 저사양 PC 에서는 Cloudflare 통과 직후 리다이렉트/렌더가
    /// 늦게 끝나 ContentAsync 가 "page is navigating and changing the content" 예외를 던질 수 있으므로,
    /// 네비게이션이 끝날 때까지 잠깐씩 기다리며 재시도한다.
    /// </summary>
    public async Task<string> GetStableContentAsync(int retries = 10, CancellationToken ct = default)
    {
        for (int i = 0; i < retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                try
                {
                    await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                        new PageWaitForLoadStateOptions { Timeout = 10_000 });
                }
                catch (Exception) { }  // 로드 상태 대기 실패는 무시하고 읽기 시도
                return await Page.ContentAsync();
            }
            catch (PlaywrightException ex) when (
                ex.Message.Contains("navigating", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("changing the content", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1000, ct);  // 네비게이션이 끝나길 기다린 뒤 재시도
            }
        }
        return await Page.ContentAsync();  // 마지막 시도(여기서 나는 예외는 그대로 전파)
    }

    /// <summary>경기 테이블이 렌더될 때까지 대기. 없으면 Cloudflare 로 보고 통과 대기.</summary>
    public async Task SettleRowsAsync(CancellationToken ct = default)
    {
        // DOM 에 테이블 셀이 붙을 때까지 대기(SSR 페이지는 즉시). 'visible' 이 아닌 'attached'.
        var opt = new PageWaitForSelectorOptions { Timeout = RowsWaitMs, State = WaitForSelectorState.Attached };
        try
        {
            await Page.WaitForSelectorAsync("table tr td", opt);
        }
        catch (Exception)  // 타임아웃 등은 무시하고 진행(파이썬과 동일). Cloudflare 면 통과 대기.
        {
            if (await LooksLikeChallengeAsync())
            {
                await WaitPastCloudflarePlaywrightAsync(ct: ct);
                try { await Page.WaitForSelectorAsync("table tr td", opt); }
                catch (Exception) { }
            }
        }
        ct.ThrowIfCancellationRequested();
        await Page.WaitForTimeoutAsync(300);
    }

    /// <summary>쿠키/프로필을 저장하도록 정상 종료하고, 종료 완료까지 기다린다.</summary>
    public async Task CloseBrowserAsync(CancellationToken ct = default)
    {
        if (!_started || _closed) return;
        ct.ThrowIfCancellationRequested();
        if (await CdpReadyAsync())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await Http.GetAsync(DebuggingUrl + "/json/version", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            string socketUrl = doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
                ?? throw new InvalidOperationException("브라우저 정상 종료 주소를 찾지 못했습니다.");
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(socketUrl), timeout.Token);
            var message = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}");
            await socket.SendAsync(new ArraySegment<byte>(message), WebSocketMessageType.Text, true, timeout.Token);
        }

        try
        {
            if (_chrome != null)
            {
                await _chrome.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            // 프로세스 종료와 함께 다음 실행 전에 기존 연결이 사라졌는지도 확인한다.
            for (int i = 0; i < 40; i++)
            {
                if (!await CdpReadyAsync())
                {
                    _closed = true;
                    _log?.Invoke("[브라우저] 정상 종료 완료 (프로필 유지)");
                    return;
                }
                await Task.Delay(250, ct);
            }
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("브라우저 정상 종료를 확인하지 못했습니다. 플레이어 조회를 진행하지 않습니다.", ex);
        }
        throw new InvalidOperationException("브라우저 연결이 종료되지 않았습니다. 플레이어 조회를 진행하지 않습니다.");
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseBrowserAsync(); } catch { }
        try { if (_browser != null) await _browser.CloseAsync(); } catch { }
        _pw?.Dispose();
        if (_chrome != null)
        {
            try { if (!_chrome.HasExited) _chrome.Kill(entireProcessTree: true); } catch { }
            _chrome.Dispose();
        }
    }
}
