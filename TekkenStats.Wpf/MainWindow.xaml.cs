using System.Diagnostics;
using System.IO;
using System.Windows;
using TekkenStats.Core;

namespace TekkenStats.Wpf;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        if (txtProfile.Text.Trim().Equals(LegacyProfileDir, StringComparison.OrdinalIgnoreCase))
            txtProfile.Text = BrowserSession.DefaultProfileDir;
        if (txtOutDir.Text.Trim().Equals(LegacyOutDir, StringComparison.OrdinalIgnoreCase))
            txtOutDir.Text = DefaultOutDir();
        LoadSettings();                       // 지난번 입력값 복원
        UpdateEnabled();                      // 복원된 소스에 맞춰 입력칸 활성화
        Closing += (_, _) => SaveSettings();  // 종료 시 저장
    }

    private void OnAllChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void OnSourceChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void OnCacheChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

    /// <summary>현재 선택된 데이터 소스.</summary>
    private DataSource Source => rbWavu?.IsChecked == true ? DataSource.Wavu : DataSource.Ewgf;

    private void UpdateEnabled()
    {
        if (dpStart == null || dpEnd == null || chkAll == null) return;
        bool all = chkAll.IsChecked == true;
        dpStart.IsEnabled = !all;
        dpEnd.IsEnabled = !all;

        // wavu 는 JSON API 라 브라우저를 안 쓴다 — 프로필 칸을 꺼서 무관함을 드러낸다.
        bool wavu = Source == DataSource.Wavu;
        if (lblProfile != null) lblProfile.IsEnabled = !wavu;
        if (txtProfile != null) txtProfile.IsEnabled = !wavu;
        if (txtSrcNote != null)
            txtSrcNote.Text = wavu
                ? "wavu: 요청 한 번으로 전체 랭크전 이력을 받습니다. 브라우저 프로필은 쓰지 않습니다."
                : "EWGF: 홈페이지 접속 확인 → 브라우저 종료 → 플레이어 페이지 수집. 보안 확인이 뜨면 직접 통과해 주세요.";

        // 캐시는 wavu 전용이다 — EWGF 는 원본을 저장하지 않는다(브라우저 HTML 이라 형태가 다르다).
        if (chkCache != null) chkCache.IsEnabled = wavu;
        if (txtCacheTtl != null) txtCacheTtl.IsEnabled = wavu && chkCache?.IsChecked == true;
        // 인구 리포트는 wavu 전용 — 소스가 바뀌면 버튼 상태를 맞춘다(목록이 있을 때만 켬)
        if (btnPopReport != null)
            btnPopReport.IsEnabled = wavu && ParseIds(txtIds.Text).Count >= 2;
        if (txtCacheNote != null)
            txtCacheNote.Text = wavu
                ? "같은 식별코드 재수집 시 wavu 를 다시 안 때립니다 (결과폴더\\.cache)"
                : "캐시는 wavu 소스에서만 씁니다.";
    }

    /// <summary>
    /// 수집에 쓸 캐시 폴더와 유효기간. 캐시 꺼짐/비-wavu 면 (null, 0) — 항상 새로 받는다.
    /// <paramref name="permanent"/> 이면 TTL 입력칸을 무시하고 영구 캐시로 돌린다
    /// (인구 리포트 전용 — 한 번 받은 사람은 다시 안 받는다. 최신화하려면 그 사람의
    /// .cache 파일을 지우고 다시 돌릴 것).
    /// </summary>
    private (string? Dir, TimeSpan? Ttl) CacheConfig(string outRoot, bool permanent = false)
    {
        if (Source != DataSource.Wavu || chkCache.IsChecked != true) return (null, TimeSpan.Zero);
        if (permanent) return (ReplayCache.DefaultDir(outRoot), null);
        int hours = int.TryParse(txtCacheTtl.Text.Trim(), out var h) && h > 0 ? h : 24;
        return (ReplayCache.DefaultDir(outRoot), TimeSpan.FromHours(hours));
    }

    /// <summary>
    /// wavu 요청 사이 간격(ms). 여러 명 수집을 순차로 돌릴 때 요청을 이만큼 벌린다.
    /// <b>병렬이 아니다 — 언제나 한 번에 하나씩</b>이고, 이 값은 그 사이 간격일 뿐이다.
    /// 낮추면 빠르지만 wavu 레이트리밋/Cloudflare 차단 위험이 커진다. 음수·비숫자는 300 으로.
    /// </summary>
    private int RequestDelayMs =>
        int.TryParse(txtDelayMs.Text.Trim(), out var ms) && ms >= 0 ? ms : 300;

    private void OnClearCache(object sender, RoutedEventArgs e)
    {
        string outRoot = txtOutDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(outRoot)) outRoot = DefaultOutDir();
        string dir = ReplayCache.DefaultDir(outRoot);

        var (files, bytes) = ReplayCache.Stats(dir);
        if (files == 0) { SetStatus("비울 캐시가 없습니다"); AppendLog($"[캐시] 비어 있음 ({dir})"); return; }

        var (cleared, freed) = ReplayCache.Clear(dir);
        double mb = freed / 1024.0 / 1024.0;
        AppendLog($"[캐시 비움] {cleared}개 파일 삭제, {mb:0.0}MB 확보 ({dir})");
        SetStatus($"캐시 {cleared}개 비움 ({mb:0.0}MB)");
    }

    /// <summary>현재 결과 폴더 기준 캐시 폴더.</summary>
    private string CurrentCacheDir()
    {
        string outRoot = txtOutDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(outRoot)) outRoot = DefaultOutDir();
        return ReplayCache.DefaultDir(outRoot);
    }

    private void OnExportCache(object sender, RoutedEventArgs e)
    {
        string dir = CurrentCacheDir();
        var (files, _) = ReplayCache.Stats(dir);
        if (files == 0) { SetStatus("내보낼 캐시가 없습니다"); AppendLog($"[캐시] 비어 있음 ({dir})"); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "캐시 내보내기",
            Filter = "캐시 zip (*.zip)|*.zip",
            FileName = $"tekken_cache_{files}명_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var (n, bytes) = CacheArchive.Export(dir, dlg.FileName);
            AppendLog($"[캐시 내보냄] {n}개 → {dlg.FileName} ({bytes / 1024.0 / 1024.0:0.0}MB)");
            SetStatus($"캐시 {n}개 내보냄");
            MarkSaved(dlg.FileName);   // '파일 위치 열기'로 바로 찾아갈 수 있게
        }
        catch (Exception ex) { AppendLog($"[캐시 내보내기 실패] {ex.Message}"); }
    }

    private void OnImportCache(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "캐시 불러오기",
            Filter = "캐시 zip (*.zip)|*.zip|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        string dir = CurrentCacheDir();
        try
        {
            var (imported, skipped) = CacheArchive.Import(dlg.FileName, dir);
            AppendLog($"[캐시 불러옴] {imported}개 추가" +
                      (skipped > 0 ? $", {skipped}개는 이미 최신이라 건너뜀" : "") +
                      $" → {dir}");
            SetStatus($"캐시 {imported}개 불러옴");
        }
        catch (Exception ex) { AppendLog($"[캐시 불러오기 실패] {ex.Message}"); }
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var ids = ParseIds(txtIds.Text);
        if (ids.Count == 0) { SetStatus("식별코드를 입력하세요"); return; }

        string outRoot = txtOutDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(outRoot))
        {
            outRoot = DefaultOutDir();
            txtOutDir.Text = outRoot;
        }
        string profile = txtProfile.Text.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = BrowserSession.DefaultProfileDir;
            txtProfile.Text = profile;
        }

        DateTime? start = null, end = null;
        if (chkAll.IsChecked != true)
        {
            start = dpStart.SelectedDate ?? new DateTime(2024, 4, 1);
            end = dpEnd.SelectedDate ?? DateTime.Today;
        }

        void Log(string s) => Dispatcher.Invoke(() => AppendLog(s));

        var source = Source;
        string srcName = source == DataSource.Wavu ? "wavu" : "EWGF";
        var (cacheDir, cacheTtl) = CacheConfig(outRoot);

        SaveSettings();   // 실행 시점의 입력값 저장
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        SetStatus($"{srcName} 수집 중…");
        AppendLog($"=== {srcName} 수집 시작: {ids.Count}명 ===");
        AppendLog(source == DataSource.Wavu
            ? "(wavu 공식 JSON API — 브라우저를 쓰지 않습니다. 랭크전만 있습니다)"
            : "(Cloudflare 창이 뜨면 체크박스를 직접 통과해 주세요)");
        if (cacheDir != null)
            AppendLog($"(캐시 사용: 최근 {cacheTtl?.TotalHours:0}시간 내 저장본은 wavu 재요청 없이 사용)");

        bool wantCompare = chkCompare.IsChecked == true;
        bool wantPopulation = chkPopulation.IsChecked == true;
        bool canceled = false;
        try
        {
            var collected = new List<CompareReport.Player>();
            var population = new List<PopulationReport.Entry>();
            bool first = true;
            foreach (var id in ids)
            {
                if (ct.IsCancellationRequested) { canceled = true; break; }

                // wavu 레이트리밋 회피: 요청을 한 번에 하나씩만 보낸다(공식 문서).
                // 순차 수집이라 이미 지켜지지만, 사람 사이에 숨은 한 박자 둔다.
                if (!first && source == DataSource.Wavu) await Task.Delay(RequestDelayMs, ct);
                first = false;

                AppendLog($"\n--- {id} ---");
                var r = await Task.Run(() => source == DataSource.Wavu
                    ? WavuApiCollector.CollectAsync(id, start, end, outRoot, Log, ct, cacheDir, cacheTtl)
                    : EwgfCollector.CollectAsync(id, start, end, outRoot, profile, Log, ct));
                if (r.Error == Collect.CanceledError) { canceled = true; break; }

                AppendLog(string.IsNullOrEmpty(r.Error)
                    ? $"[OK] {r.Count}경기 → {Path.GetFileName(r.OutPath)}"
                    : $"[실패] {r.Error}");
                if (string.IsNullOrEmpty(r.Error) && r.Records is { Count: > 0 })
                {
                    MarkSaved(r.OutPath);
                    if (wantCompare)
                        collected.Add(new CompareReport.Player(r.PlayerId, r.PlayerName, r.Records));
                    if (wantPopulation)
                        population.Add(new PopulationReport.Entry(r.PlayerId, r.PlayerName,
                            _views.GetValueOrDefault(r.PlayerId), r.Records));
                }
            }

            if (canceled)
            {
                AppendLog("\n=== 중지됨 ===");
                SetStatus("중지됨");
            }
            else
            {
                if (wantCompare)
                {
                    if (collected.Count >= 2)
                    {
                        AppendLog($"\n--- 비교 리포트 ({collected.Count}명) ---");
                        try
                        {
                            string cmp = await Task.Run(() => CompareReport.WriteWorkbook(collected, outRoot));
                            AppendLog($"[OK] 비교 리포트 → {Path.GetFileName(cmp)}");
                            MarkSaved(cmp);   // 비교 리포트가 있으면 이게 대표 결과다
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[비교 리포트 실패] {ex.Message}");
                        }
                    }
                    else if (ids.Count >= 2)
                    {
                        AppendLog("\n(비교하려면 2명 이상 정상 수집되어야 합니다 — 실패한 식별코드를 확인하세요)");
                    }
                }

                if (wantPopulation)
                {
                    if (population.Count >= 2)
                    {
                        AppendLog($"\n--- 인구 리포트 ({population.Count}명) ---");
                        try
                        {
                            string pop = await Task.Run(() => PopulationReport.WriteWorkbook(population, outRoot));
                            AppendLog($"[OK] 인구 리포트 → {Path.GetFileName(pop)}");
                            MarkSaved(pop);   // 인구 리포트가 최종 종합물이라 대표로 삼는다
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[인구 리포트 실패] {ex.Message}");
                        }
                    }
                    else if (ids.Count >= 2)
                    {
                        AppendLog("\n(인구 리포트는 2명 이상 정상 수집되어야 합니다)");
                    }
                }

                SetStatus("완료");
                AppendLog("\n=== 전체 완료 ===");

                // 완료되면 마지막 결과 파일을 탐색기에서 선택해 띄운다.
                if (!string.IsNullOrEmpty(_lastSaved))
                {
                    RevealInExplorer(_lastSaved);
                    AppendLog($"[열기] {_lastSaved}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("\n=== 중지됨 ===");
            SetStatus("중지됨");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] {ex.Message}");
            SetStatus("오류");
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ── 식별코드 목록 불러오기 (관리자 JSON / CSV / 텍스트) ──────────────
    private AdminLog? _adminLog;                        // 이용통계 엑셀용
    private Dictionary<string, int> _views = new();     // 식별코드 → 조회수 (인구 리포트에 실음)
    private Dictionary<string, string> _names = new();  // 식별코드 → 이름 (수집이 이름을 못 줄 때 폴백)

    private void OnImportIds(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "식별코드 목록 불러오기",
            Filter = "지원 형식 (*.json;*.csv;*.txt)|*.json;*.csv;*.txt|모든 파일 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var res = IdImport.FromFile(dlg.FileName);
            if (res.Items.Count == 0)
            {
                txtImportNote.Text = $"[{res.Kind}] 식별코드를 찾지 못했습니다 — {Path.GetFileName(dlg.FileName)}";
                return;
            }

            int topN = int.TryParse(txtTopN.Text.Trim(), out var n) && n > 0 ? n : res.Items.Count;
            var picked = res.Items.Take(topN).ToList();
            txtIds.Text = string.Join(Environment.NewLine, picked.Select(x => x.Id));

            _adminLog = res.Log;
            _views = res.Items.ToDictionary(x => x.Id, x => x.Views, StringComparer.OrdinalIgnoreCase);
            _names = res.Items.Where(x => !string.IsNullOrEmpty(x.Name))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            btnUsage.IsEnabled = _adminLog != null;
            btnPopReport.IsEnabled = picked.Count >= 2 && Source == DataSource.Wavu;

            string note = $"[{res.Kind}] {res.Items.Count}개 중 {picked.Count}명 불러옴";
            if (_adminLog != null)
            {
                var (f, l) = _adminLog.DateRange();
                // 파일명의 '28d' 를 그대로 믿지 않게 실제 데이터 기간을 같이 보여준다.
                note += $" · 실제 데이터 {f}~{l} ({_adminLog.Daily.Count}일), 총 조회 {_adminLog.TotalViews}";
            }
            if (picked.Count > 30)
                note += $"\n{picked.Count}명이면 순차 수집이라 {picked.Count * 4 / 60 + 1}분쯤 걸립니다.";
            txtImportNote.Text = note;
            AppendLog($"[불러옴] {Path.GetFileName(dlg.FileName)} — {note.Replace("\n", " ")}");
        }
        catch (Exception ex)
        {
            txtImportNote.Text = $"불러오기 실패: {ex.Message}";
        }
    }

    /// <summary>
    /// 불러온 식별코드 전부를 수집해 인구 리포트 하나만 만든다(개별 엑셀 없음).
    /// '수집 시작'과 달리 각자 파일을 쓰지 않고 레코드만 모아 종합 리포트로 낸다.
    /// wavu 전용 — EWGF 는 목록 대량 수집에 안 맞는다(브라우저·Cloudflare).
    /// </summary>
    private async void OnPopulationFromList(object sender, RoutedEventArgs e)
    {
        var ids = ParseIds(txtIds.Text);
        if (ids.Count < 2) { SetStatus("식별코드가 2개 이상 필요합니다"); return; }
        if (Source != DataSource.Wavu)
        {
            SetStatus("인구 리포트는 wavu 소스에서만 됩니다");
            AppendLog("[안내] 데이터 소스를 wavu 로 바꾼 뒤 다시 눌러 주세요.");
            return;
        }

        string outRoot = txtOutDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(outRoot)) { outRoot = DefaultOutDir(); txtOutDir.Text = outRoot; }
        var (cacheDir, cacheTtl) = CacheConfig(outRoot, permanent: true);

        void Log(string s) => Dispatcher.Invoke(() => AppendLog(s));

        SaveSettings();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true);
        SetStatus("인구 리포트 수집 중…");
        AppendLog($"\n=== 인구 리포트 수집: {ids.Count}명 (개별 파일 없이 종합 1개) ===");
        if (cacheDir != null)
            AppendLog("(캐시 사용: 영구 — 한 번 받은 사람은 다시 안 받습니다. 최신화하려면 그 사람의 .cache 파일을 지우세요)");

        var entries = new List<PopulationReport.Entry>();
        bool canceled = false;
        try
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (ct.IsCancellationRequested) { canceled = true; break; }
                if (i > 0) await Task.Delay(RequestDelayMs, ct);   // wavu 레이트리밋 회피(순차, 병렬 아님)

                string id = ids[i];
                try
                {
                    var (name, recs) = await Task.Run(async () =>
                    {
                        var replays = await WavuApiCollector.FetchWithCacheAsync(id, cacheDir, cacheTtl, Log, ct);
                        var (r, nm, _) = WavuApiCollector.Normalize(replays, Collect.NormalizeId(id));
                        return (nm, r);
                    });
                    AppendLog($"[{i + 1}/{ids.Count}] {id} {name} — {recs.Count}경기");
                    if (recs.Count > 0)
                        entries.Add(new PopulationReport.Entry(id,
                            string.IsNullOrEmpty(name) ? _names.GetValueOrDefault(id, "") : name,
                            _views.GetValueOrDefault(id), recs));
                }
                catch (OperationCanceledException) { canceled = true; break; }
                catch (Exception ex) { AppendLog($"[{i + 1}/{ids.Count}] {id} — 실패: {ex.Message}"); }
            }

            if (canceled) { AppendLog("\n=== 중지됨 ==="); SetStatus("중지됨"); }
            else if (entries.Count < 2)
            {
                AppendLog($"\n[실패] 전적이 있는 사람이 {entries.Count}명뿐 — 리포트를 만들 수 없습니다.");
                SetStatus("수집 실패");
            }
            else
            {
                AppendLog($"\n--- 인구 리포트 ({entries.Count}명) ---");
                string pop = await Task.Run(() => PopulationReport.WriteWorkbook(entries, outRoot));
                AppendLog($"[OK] 인구 리포트 → {Path.GetFileName(pop)}");
                MarkSaved(pop);
                RevealInExplorer(pop);
                SetStatus("인구 리포트 완료");
            }
        }
        catch (OperationCanceledException) { AppendLog("\n=== 중지됨 ==="); SetStatus("중지됨"); }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); SetStatus("오류"); }
        finally { SetRunning(false); _cts?.Dispose(); _cts = null; }
    }

    private void OnUsageReport(object sender, RoutedEventArgs e)
    {
        if (_adminLog == null) return;
        try
        {
            string outRoot = txtOutDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(outRoot)) outRoot = DefaultOutDir();
            string saved = UsageReport.WriteWorkbook(_adminLog, outRoot);
            AppendLog($"[OK] 이용통계 → {saved}");
            SetStatus("이용통계 저장됨");
            MarkSaved(saved);
            RevealInExplorer(saved);   // 버튼 한 번 = 즉시 결과라, 바로 위치를 열어준다
        }
        catch (Exception ex)
        {
            AppendLog($"[이용통계 실패] {ex.Message}");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        SetStatus("중지 요청…");
        AppendLog("\n[중지 요청] 현재 진행 중인 작업을 정리하고 있습니다…");
        _cts.Cancel();
        btnCancel.IsEnabled = false;   // 중복 클릭 방지 (SetRunning(false) 에서 최종 정리)
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        string dir = txtOutDir.Text.Trim();
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else
            SetStatus("폴더가 없습니다");
    }

    // ── 결과 파일 위치 열기 ────────────────────────────────────────────
    private string? _lastSaved;   // 마지막으로 저장된 결과 파일 (완료 시 이걸 연다)

    /// <summary>저장 성공 시마다 호출 — 마지막 파일을 기억하고 '파일 위치 열기'를 켠다.</summary>
    private void MarkSaved(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _lastSaved = path;
        btnReveal.IsEnabled = true;
    }

    /// <summary>탐색기를 열되 그 파일을 <b>선택된 상태</b>로 띄운다(폴더만 여는 것과 다르다).</summary>
    private void RevealInExplorer(string path)
    {
        if (File.Exists(path))
            // /select 뒤에 경로를 붙일 때 콤마 다음 공백을 넣으면 안 먹는다 — "/select," 붙여쓸 것.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
        {
            // 파일이 사라졌으면(이동/삭제) 폴더라도 연다.
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else
                SetStatus("파일이 없습니다");
        }
    }

    private void OnRevealFile(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastSaved)) RevealInExplorer(_lastSaved);
    }

    private static List<string> ParseIds(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var tok in (text ?? "").Split(new[] { '\n', '\r', ',', ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var t = tok.Trim().Replace("-", "");   // 식별코드의 하이픈 제거 (예: 3Jae-qRbd-E8aa → 3JaeqRbdE8aa)
            if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    private void SetRunning(bool running)
    {
        btnRun.IsEnabled = !running;
        btnCancel.IsEnabled = running;
        prog.IsIndeterminate = running;
        txtIds.IsEnabled = !running;
        chkCompare.IsEnabled = !running;
        grpDate.IsEnabled = !running;
        rbEwgf.IsEnabled = !running;
        rbWavu.IsEnabled = !running;
        chkPopulation.IsEnabled = !running;
        btnImport.IsEnabled = !running;
        txtTopN.IsEnabled = !running;
        chkCache.IsEnabled = !running && Source == DataSource.Wavu;
        txtCacheTtl.IsEnabled = !running && Source == DataSource.Wavu && chkCache.IsChecked == true;
        txtDelayMs.IsEnabled = !running;
        btnClearCache.IsEnabled = !running;
        btnExportCache.IsEnabled = !running;
        btnImportCache.IsEnabled = !running;
        // 인구 리포트 버튼: 실행 중이 아니고, 목록 2명 이상, wavu 일 때만
        btnPopReport.IsEnabled = !running && Source == DataSource.Wavu
            && ParseIds(txtIds.Text).Count >= 2;
        btnUsage.IsEnabled = !running && _adminLog != null;
    }

    private void SetStatus(string s) => txtStatus.Text = s;

    private void AppendLog(string line)
    {
        txtLog.AppendText(line + "\n");
        txtLog.ScrollToEnd();
    }

    // ── 입력값 저장/복원 (마지막 값 기억) ──────────────────
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TekkenRecordMaker", "settings.json");

    private const string LegacyProfileDir = @"D:\TekkenResult\.browser_profile";
    private const string LegacyOutDir = @"D:\TekkenResult\User";

    /// <summary>
    /// 결과 폴더 기본값. C 외 추가 드라이브(특히 D)가 있으면 그 드라이브에, C 단일 PC면 C 드라이브에 둔다.
    /// </summary>
    private static string DefaultOutDir() => Path.Combine(PickDataDrive(), "TekkenResult", "User");

    /// <summary>저장용 드라이브 선택: D: 우선 → 그 외 비-C 고정 드라이브 → 없으면 C:.</summary>
    private static string PickDataDrive()
    {
        try
        {
            var fixedDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name)   // 예: "C:\"
                .ToList();
            var d = fixedDrives.FirstOrDefault(n => n.StartsWith("D:", StringComparison.OrdinalIgnoreCase));
            if (d != null) return d;
            var nonC = fixedDrives.FirstOrDefault(n => !n.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
            if (nonC != null) return nonC;
        }
        catch { /* 드라이브 조회 실패 시 C 로 폴백 */ }
        return @"C:\";
    }

    private sealed class UiSettings
    {
        public string? Ids { get; set; }
        public string? OutDir { get; set; }
        public string? Profile { get; set; }
        public bool? Compare { get; set; }
        public bool? Population { get; set; }
        public bool? Cache { get; set; }
        public int? CacheTtlHours { get; set; }
        public int? DelayMs { get; set; }
        public string? Source { get; set; }   // "Ewgf" | "Wavu"
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
            if (s == null) return;
            if (!string.IsNullOrWhiteSpace(s.Ids)) txtIds.Text = s.Ids;
            if (!string.IsNullOrWhiteSpace(s.OutDir)) txtOutDir.Text = s.OutDir;
            if (!string.IsNullOrWhiteSpace(s.Profile)) txtProfile.Text = s.Profile;
            if (s.Compare.HasValue) chkCompare.IsChecked = s.Compare.Value;
            if (s.Population.HasValue) chkPopulation.IsChecked = s.Population.Value;
            if (s.Cache.HasValue) chkCache.IsChecked = s.Cache.Value;
            if (s.CacheTtlHours is > 0) txtCacheTtl.Text = s.CacheTtlHours.Value.ToString();
            if (s.DelayMs is >= 0) txtDelayMs.Text = s.DelayMs.Value.ToString();
            if (Enum.TryParse<DataSource>(s.Source, ignoreCase: true, out var src))
            {
                rbWavu.IsChecked = src == DataSource.Wavu;
                rbEwgf.IsChecked = src == DataSource.Ewgf;
            }
            if (txtProfile.Text.Trim().Equals(LegacyProfileDir, StringComparison.OrdinalIgnoreCase))
                txtProfile.Text = BrowserSession.DefaultProfileDir;
            if (txtOutDir.Text.Trim().Equals(LegacyOutDir, StringComparison.OrdinalIgnoreCase))
                txtOutDir.Text = DefaultOutDir();
        }
        catch { /* 손상 시 기본값 사용 */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var s = new UiSettings
            {
                Ids = txtIds.Text, OutDir = txtOutDir.Text, Profile = txtProfile.Text,
                Compare = chkCompare.IsChecked == true,
                Population = chkPopulation.IsChecked == true,
                Cache = chkCache.IsChecked == true,
                CacheTtlHours = int.TryParse(txtCacheTtl.Text.Trim(), out var _ttl) && _ttl > 0 ? _ttl : 24,
                DelayMs = RequestDelayMs,
                Source = Source.ToString(),
            };
            File.WriteAllText(SettingsPath,
                System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 저장 실패 무시 */ }
    }
}
