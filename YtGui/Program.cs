using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace YtGui
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class QueueItem
    {
        public string Url { get; set; } = string.Empty;
        public bool AudioOnly { get; set; }
        public string Codec { get; set; } = "mp3";
        public string SelectedFormat { get; set; } = string.Empty;
        public string CookiePath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "未ダウンロード";
        public string OutputFilePath { get; set; } = string.Empty;
        public double ProgressPercent { get; set; } = -1;
        public bool IsLive { get; set; }
        public bool LiveFromStart { get; set; }
        public bool DownloadChatReplay { get; set; }
        public CancellationTokenSource? ActiveCts { get; set; }
        public int ActiveProcPid { get; set; }
    }

    public class MainForm : Form
    {
        readonly TextBox tbUrl = new();
        readonly Button btnAdd = new() { Text = "キューに追加", AutoSize = true };
        readonly ListView lvQueue = new()
        {
            View = View.Details,
            FullRowSelect = true,
            Scrollable = true,
            HideSelection = true,
            OwnerDraw = true,
            Dock = DockStyle.Fill
        };
        readonly Button btnStart = new() { Text = "開始", AutoSize = true };
        readonly Button btnCancelItem = new() { Text = "選択を中止", AutoSize = true };
        readonly Button btnRemove = new() { Text = "削除", AutoSize = true };
        readonly Button btnRemoveCompleted = new() { Text = "完了項目を削除", AutoSize = true, Width = 140 };
        readonly Button btnRetry = new() { Text = "失敗を再キュー", AutoSize = true };
        readonly CheckBox chkApplyPlaylistDefault = new() { Text = "プレイリストに既定フォーマットを適用", AutoSize = true };
        readonly Button btnSettings = new() { Text = "設定", AutoSize = true };
        readonly CheckBox chkLive = new() { Text = "ライブ", AutoSize = true };
        readonly CheckBox chkLiveFromStart = new() { Text = "配信開始から録画 (--live-from-start)", AutoSize = true };
        readonly CheckBox chkChatReplay = new() { Text = "チャットリプレイ取得", AutoSize = true };
        readonly CheckBox chkAudioOnly = new() { Text = "音声のみ抽出", AutoSize = true };
        readonly ComboBox cbCodec = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox tbCookie = new();
        readonly Button btnBrowseCookie = new() { Text = "Cookie指定", AutoSize = true };
        readonly TextBox tbLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly Button btnTopMost = new() { Text = "常に最前面: OFF", AutoSize = true };
        readonly HashSet<Control> selectionActionButtons = new();
        readonly System.Windows.Forms.Timer progressUiTimer = new() { Interval = 1000 };
        int chatReplayMarqueeTick;

        readonly Queue<QueueItem> queue = new();
        readonly List<QueueItem> allItems = new();
        readonly object queueLock = new();
        readonly object logLock = new();
        readonly StringBuilder pendingLog = new();
        bool logFlushScheduled;
        CancellationTokenSource? cts;
        bool running;
        Settings settings = Settings.Load();

        public MainForm()
        {
            Text = "yt-dlp GUI";
            Width = 920;
            Height = 760;
            MinimumSize = new Size(760, 560);

            cbCodec.Items.AddRange(new[] { "mp3", "m4a", "opus", "wav" });
            cbCodec.SelectedIndex = 0;
            cbCodec.Enabled = false;
            chkLiveFromStart.Enabled = false;
            chkLive.CheckedChanged += (_, _) => chkLiveFromStart.Enabled = chkLive.Checked;
            chkAudioOnly.CheckedChanged += (_, _) => cbCodec.Enabled = chkAudioOnly.Checked;

            lvQueue.Columns.Add("Title", 520);
            lvQueue.Columns.Add("Status", 180);
            lvQueue.DrawColumnHeader += (_, e) => e.DrawDefault = true;
            lvQueue.DrawItem += (_, e) => e.DrawDefault = false;
            lvQueue.DrawSubItem += LvQueue_DrawSubItem;
            lvQueue.MouseDown += LvQueue_MouseDown;
            lvQueue.DoubleClick += LvQueue_DoubleClick;

            var urlRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            urlRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tbUrl.Dock = DockStyle.Fill;
            urlRow.Controls.Add(tbUrl, 0, 0);
            urlRow.Controls.Add(btnAdd, 1, 0);
            urlRow.Controls.Add(chkApplyPlaylistDefault, 2, 0);

            var optRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            tbCookie.Width = 280;
            optRow.Controls.Add(chkAudioOnly);
            optRow.Controls.Add(cbCodec);
            optRow.Controls.Add(chkLive);
            optRow.Controls.Add(chkLiveFromStart);
            optRow.Controls.Add(chkChatReplay);
            optRow.Controls.Add(tbCookie);
            optRow.Controls.Add(btnBrowseCookie);

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            btnRow.Controls.Add(btnStart);
            btnRow.Controls.Add(btnCancelItem);
            btnRow.Controls.Add(btnRemove);
            btnRow.Controls.Add(btnRemoveCompleted);
            btnRow.Controls.Add(btnRetry);
            btnRow.Controls.Add(btnSettings);
            btnRow.Controls.Add(btnTopMost);

            selectionActionButtons.Add(btnStart);
            selectionActionButtons.Add(btnCancelItem);
            selectionActionButtons.Add(btnRemove);
            selectionActionButtons.Add(btnRemoveCompleted);
            selectionActionButtons.Add(btnRetry);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
            root.Controls.Add(urlRow, 0, 0);
            root.Controls.Add(optRow, 0, 1);
            root.Controls.Add(btnRow, 0, 2);
            root.Controls.Add(lvQueue, 0, 3);
            root.Controls.Add(tbLog, 0, 4);
            Controls.Add(root);

            btnAdd.Click += BtnAdd_Click;
            btnStart.Click += BtnStart_Click;
            btnCancelItem.Click += BtnCancelItem_Click;
            btnRemove.Click += BtnRemove_Click;
            btnRemoveCompleted.Click += BtnRemoveCompleted_Click;
            btnRetry.Click += BtnRetry_Click;
            btnBrowseCookie.Click += BtnBrowseCookie_Click;
            btnSettings.Click += BtnSettings_Click;
            btnTopMost.Click += (_, _) =>
            {
                TopMost = !TopMost;
                btnTopMost.Text = TopMost ? "常に最前面: ON" : "常に最前面: OFF";
            };

            progressUiTimer.Tick += (_, _) =>
            {
                bool any;
                lock (queueLock) any = allItems.Exists(x => x.Status is "ダウンロード中" or "チャット取得中");
                if (any)
                {
                    chatReplayMarqueeTick++;
                    lvQueue.Invalidate();
                }
            };
            progressUiTimer.Start();

            AttachDeselectHandlers(this);
            MouseDown += (_, _) => ClearQueueSelection();
            FormClosing += MainForm_FormClosing;

            var menu = new ContextMenuStrip();
            menu.Items.Add("選択を中止", null, (_, _) => BtnCancelItem_Click(this, EventArgs.Empty));
            menu.Items.Add("再キュー", null, (_, _) => RetryItems(GetSelectedItems()));
            menu.Items.Add("保存先を開く", null, (_, _) => OpenSelectedOutput());
            lvQueue.ContextMenuStrip = menu;
            Shown += async (_, _) => await CheckYtDlpUpdateAsync();
        }

        void AttachDeselectHandlers(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c != lvQueue && !selectionActionButtons.Contains(c))
                    c.MouseDown += (_, _) => ClearQueueSelection();
                AttachDeselectHandlers(c);
            }
        }

        void ClearQueueSelection()
        {
            if (lvQueue.SelectedItems.Count == 0) return;
            lvQueue.SelectedItems.Clear();
        }

        void LvQueue_MouseDown(object? sender, MouseEventArgs e)
        {
            var hit = lvQueue.HitTest(e.Location);
            if (hit.Item == null) ClearQueueSelection();
        }

        void LvQueue_DoubleClick(object? sender, EventArgs e) => OpenSelectedOutput();

        void OpenSelectedOutput()
        {
            var items = GetSelectedItems();
            if (items.Count == 0) return;
            var item = items[0];
            var path = item.OutputFilePath;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }
                var dir = !string.IsNullOrWhiteSpace(path) ? Path.GetDirectoryName(path) : settings.OutputDirectory;
                if (string.IsNullOrWhiteSpace(dir)) dir = Directory.GetCurrentDirectory();
                if (Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                UpdateStatus("保存先を開けませんでした: " + ex.Message);
            }
        }

        List<QueueItem> GetSelectedItems()
        {
            var list = new List<QueueItem>();
            foreach (ListViewItem lvi in lvQueue.SelectedItems)
            {
                if (lvi.Tag is QueueItem qi) list.Add(qi);
            }
            return list;
        }

        void BtnSettings_Click(object? sender, EventArgs e)
        {
            using var f = new SettingsForm(settings);
            f.TopMost = TopMost;
            if (f.ShowDialog() == DialogResult.OK)
            {
                settings = Settings.Load();
                if (string.IsNullOrWhiteSpace(tbCookie.Text) && !string.IsNullOrWhiteSpace(settings.DefaultCookiePath))
                    tbCookie.Text = settings.DefaultCookiePath;
            }
        }

        void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            progressUiTimer.Stop();
            cts?.Cancel();
            lock (queueLock)
            {
                foreach (var item in allItems)
                {
                    try { item.ActiveCts?.Cancel(); } catch { }
                    try { KillProcessTree(item.ActiveProcPid); } catch { }
                }
            }
        }

        void BtnBrowseCookie_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog();
            ofd.Filter = "Cookies (cookies.txt)|cookies.txt|All files|*.*";
            if (ofd.ShowDialog() == DialogResult.OK) tbCookie.Text = ofd.FileName;
        }

        void CancelItems(IEnumerable<QueueItem> items)
        {
            foreach (var item in items)
            {
                try { item.ActiveCts?.Cancel(); } catch { }
                try { KillProcessTree(item.ActiveProcPid); } catch { }
            }
        }

        void BtnCancelItem_Click(object? sender, EventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count == 0) return;
            CancelItems(selected);
        }

        void BtnRemove_Click(object? sender, EventArgs e)
        {
            var toRemove = GetSelectedItems();
            if (toRemove.Count == 0) return;
            CancelItems(toRemove);
            lock (queueLock)
            {
                var list = new List<QueueItem>(queue);
                list.RemoveAll(toRemove.Contains);
                queue.Clear();
                foreach (var it in list) queue.Enqueue(it);
                allItems.RemoveAll(toRemove.Contains);
            }
            RefreshQueueDisplay();
        }

        void BtnRemoveCompleted_Click(object? sender, EventArgs e)
        {
            lock (queueLock) allItems.RemoveAll(x => x.Status == "ダウンロード完了");
            RefreshQueueDisplay();
        }

        void BtnRetry_Click(object? sender, EventArgs e)
        {
            var selected = GetSelectedItems();
            if (selected.Count > 0)
            {
                RetryItems(selected);
                return;
            }
            List<QueueItem> failed;
            lock (queueLock)
                failed = allItems.Where(x => x.Status is "失敗" or "キャンセル").ToList();
            RetryItems(failed);
        }

        void RetryItems(IReadOnlyList<QueueItem> items)
        {
            if (items.Count == 0) return;
            lock (queueLock)
            {
                foreach (var item in items)
                {
                    if (item.Status is not ("失敗" or "キャンセル" or "未ダウンロード")) continue;
                    if (item.Status == "ダウンロード中") continue;
                    item.Status = "ダウンロード待ち";
                    item.ProgressPercent = -1;
                    queue.Enqueue(item);
                }
            }
            RefreshQueueDisplay();
            UpdateStatus("再キューしました。");
        }

        void BtnStart_Click(object? sender, EventArgs e)
        {
            if (!running) StartProcessing();
            else StopProcessing();
        }

        void StartProcessing()
        {
            lock (queueLock)
            {
                if (queue.Count == 0)
                {
                    MessageBox.Show("キューが空です。URLを追加してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            running = true;
            btnStart.Text = "停止";
            cts = new CancellationTokenSource();
            _ = Task.Run(() => ProcessQueueAsync(cts.Token));
        }

        void StopProcessing()
        {
            cts?.Cancel();
            lock (queueLock)
            {
                foreach (var item in allItems)
                {
                    try { item.ActiveCts?.Cancel(); } catch { }
                    try { KillProcessTree(item.ActiveProcPid); } catch { }
                }
            }
            running = false;
            btnStart.Text = "開始";
            UpdateStatus("停止しました。");
        }

        string CookieFromUi() => tbCookie.Text.Trim();

        async Task<QueueItem?> BuildQueueItemAsync(string url, bool isLive, bool liveFromStart, string? knownTitle, bool applyDefaultFormat)
        {
            var audioOnly = chkAudioOnly.Checked;
            var downloadChatReplay = chkChatReplay.Checked;
            var codec = cbCodec.SelectedItem?.ToString() ?? "mp3";
            var cookie = CookieFromUi();
            string selectedFormat;
            string title = knownTitle ?? string.Empty;
            IReadOnlyList<MediaFormat> formats = Array.Empty<MediaFormat>();

            if (isLive)
            {
                selectedFormat = audioOnly ? "bestaudio" : "bestaudio+bestvideo";
                if (string.IsNullOrWhiteSpace(title))
                {
                    try { title = await YtDlp.GetTitleAsync(url, cookie, settings); }
                    catch { }
                }
            }
            else
            {
                UpdateStatus("動画情報を取得中...");
                VideoInfo info;
                try
                {
                    info = await YtDlp.GetVideoInfoAsync(url, cookie, settings);
                }
                catch (Exception ex)
                {
                    UpdateStatus("情報取得失敗: " + ex.Message);
                    return null;
                }
                formats = info.Formats;
                if (string.IsNullOrWhiteSpace(title)) title = info.Title;
                selectedFormat = string.Empty;
                if (applyDefaultFormat)
                    selectedFormat = audioOnly ? "bestaudio" : "bestaudio+bestvideo";
                if (string.IsNullOrWhiteSpace(selectedFormat))
                {
                    using var frm = new FormatSelectionForm(formats, audioOnly);
                    frm.TopMost = TopMost;
                    if (frm.ShowDialog() != DialogResult.OK)
                        return null;
                    selectedFormat = frm.SelectedFormat?.Trim() ?? string.Empty;
                }
            }

            var item = new QueueItem
            {
                Url = url,
                AudioOnly = audioOnly,
                Codec = codec,
                CookiePath = cookie,
                SelectedFormat = selectedFormat,
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Status = "ダウンロード待ち",
                IsLive = isLive,
                LiveFromStart = liveFromStart,
                DownloadChatReplay = downloadChatReplay
            };

            var dir = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? Directory.GetCurrentDirectory() : settings.OutputDirectory;
            var ext = YtDlp.GuessExtension(selectedFormat, formats, audioOnly, codec);
            var fname = YtDlp.SanitizeFileName(item.Title) + "." + ext;
            item.OutputFilePath = YtDlp.MakeUniquePath(Path.Combine(dir, fname));
            return item;
        }

        void EnqueueItem(QueueItem item)
        {
            lock (queueLock)
            {
                allItems.Add(item);
                queue.Enqueue(item);
            }
            RefreshQueueDisplay();
        }

        async void BtnAdd_Click(object? sender, EventArgs e)
        {
            var raw = tbUrl.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;
            var (url, isPlaylist) = YtDlp.NormalizeUrl(raw);
            var isLive = chkLive.Checked;
            var liveFromStart = chkLiveFromStart.Checked;

            if (isPlaylist && !isLive)
            {
                List<PlaylistEntry> entries;
                try
                {
                    entries = await YtDlp.GetPlaylistEntriesAsync(url, CookieFromUi(), settings);
                }
                catch (Exception ex)
                {
                    UpdateStatus("プレイリスト取得失敗: " + ex.Message);
                    return;
                }

                foreach (var entry in entries)
                {
                    var qitem = await BuildQueueItemAsync(entry.Url, false, false, entry.Title, chkApplyPlaylistDefault.Checked);
                    if (qitem == null)
                    {
                        UpdateStatus("スキップしました: " + (string.IsNullOrWhiteSpace(entry.Title) ? entry.Url : entry.Title));
                        continue;
                    }
                    EnqueueItem(qitem);
                }
                tbUrl.Clear();
                UpdateStatus("プレイリストの処理が終わりました。");
                return;
            }

            var item = await BuildQueueItemAsync(url, isLive, liveFromStart, null, false);
            if (item == null)
            {
                UpdateStatus("フォーマット選択がキャンセルされました。");
                return;
            }
            EnqueueItem(item);
            tbUrl.Clear();
            UpdateStatus("キューに追加しました: " + item.Title);
            if (isLive && !running) StartProcessing();
        }

        void RefreshQueueDisplay()
        {
            QueueItem[] snapshot;
            lock (queueLock) snapshot = allItems.ToArray();
            BeginInvoke(() =>
            {
                var selected = lvQueue.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).ToHashSet();
                lvQueue.BeginUpdate();
                lvQueue.Items.Clear();
                foreach (var it in snapshot)
                {
                    var title = string.IsNullOrWhiteSpace(it.Title) ? it.Url : it.Title;
                    var lvi = new ListViewItem(title);
                    lvi.SubItems.Add(it.Status);
                    lvi.Tag = it;
                    if (selected.Contains(it)) lvi.Selected = true;
                    lvQueue.Items.Add(lvi);
                }
                lvQueue.EndUpdate();
            });
        }

        void LvQueue_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            var subItem = e.SubItem;
            var listItem = e.Item;
            if (subItem == null || listItem == null) return;
            var selected = listItem.Selected && (lvQueue.Focused || !lvQueue.HideSelection);
            var background = selected ? SystemColors.Highlight : (subItem.BackColor.IsEmpty ? lvQueue.BackColor : subItem.BackColor);
            var foreground = selected ? SystemColors.HighlightText : (subItem.ForeColor.IsEmpty ? lvQueue.ForeColor : subItem.ForeColor);
            using var backgroundBrush = new SolidBrush(background);
            e.Graphics.FillRectangle(backgroundBrush, e.Bounds);

            if (e.ColumnIndex != 1 || listItem.Tag is not QueueItem item)
            {
                TextRenderer.DrawText(e.Graphics, subItem.Text, lvQueue.Font, e.Bounds, foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            if (item.Status == "チャット取得中")
            {
                var marqueeBar = Rectangle.Inflate(e.Bounds, -4, -4);
                using var marqueeTrackBrush = new SolidBrush(selected ? Color.FromArgb(90, Color.White) : Color.Gainsboro);
                e.Graphics.FillRectangle(marqueeTrackBrush, marqueeBar);
                // 実進捗が取得できないため（チャットツールの出力形式が未確認）、往復するブロックで
                // 「動いている」ことだけを示す不定進捗（indeterminate）表示にする。
                var blockWidth = Math.Max(20, marqueeBar.Width / 5);
                var travel = Math.Max(1, marqueeBar.Width - blockWidth);
                var pos = chatReplayMarqueeTick % (travel * 2);
                if (pos > travel) pos = travel * 2 - pos;
                using var marqueeBrush = new SolidBrush(selected ? Color.FromArgb(190, Color.White) : Color.FromArgb(45, 135, 70));
                e.Graphics.FillRectangle(marqueeBrush, new Rectangle(marqueeBar.X + pos, marqueeBar.Y, blockWidth, marqueeBar.Height));
                e.Graphics.DrawRectangle(SystemPens.ControlDark, marqueeBar);
                TextRenderer.DrawText(e.Graphics, subItem.Text, lvQueue.Font, marqueeBar, foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            if (item.ProgressPercent < 0)
            {
                TextRenderer.DrawText(e.Graphics, subItem.Text, lvQueue.Font, e.Bounds, foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            var bar = Rectangle.Inflate(e.Bounds, -4, -4);
            double value;
            lock (queueLock) value = Math.Clamp(item.ProgressPercent, 0, 100);
            using var trackBrush = new SolidBrush(selected ? Color.FromArgb(90, Color.White) : Color.Gainsboro);
            using var progressBrush = new SolidBrush(selected ? Color.FromArgb(190, Color.White) : Color.FromArgb(45, 135, 70));
            e.Graphics.FillRectangle(trackBrush, bar);
            e.Graphics.FillRectangle(progressBrush, new Rectangle(bar.X, bar.Y, (int)Math.Round(bar.Width * value / 100), bar.Height));
            e.Graphics.DrawRectangle(SystemPens.ControlDark, bar);
            TextRenderer.DrawText(e.Graphics, $"{value:0.0}%", lvQueue.Font, bar, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static bool TryGetDownloadProgress(string line, out double percent)
        {
            percent = 0;
            if (!line.StartsWith("[download]", StringComparison.OrdinalIgnoreCase)) return false;
            var percentPosition = line.IndexOf('%');
            if (percentPosition < 0) return false;
            var first = percentPosition;
            while (first > 0 && (char.IsDigit(line[first - 1]) || line[first - 1] == '.')) first--;
            return first < percentPosition && double.TryParse(line[first..percentPosition], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out percent);
        }

        void ReportDownloadProgress(QueueItem item, string line)
        {
            if (!TryGetDownloadProgress(line, out var percent))
            {
                UpdateStatus(line);
                return;
            }
            lock (queueLock) item.ProgressPercent = Math.Clamp(percent, 0, 100);
        }
        async Task CheckYtDlpUpdateAsync()
        {
            AppendLog("yt-dlp のアップデートを確認しています...");
            try
            {
                var (exit, stdout, stderr) = await YtDlp.RunAsync(new[] { "-U" }, null, settings);
                var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
                AppendLog(exit == 0
                    ? "yt-dlp: " + output.Trim()
                    : $"yt-dlp アップデート確認に失敗しました (exit {exit}): {output.Trim()}");
            }
            catch (Exception ex)
            {
                AppendLog("yt-dlp アップデート確認でエラー: " + ex.Message);
            }
        }
        void UpdateStatus(string s) => AppendLog(s);

        void AppendLog(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            lock (logLock)
            {
                pendingLog.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(s);
                if (logFlushScheduled) return;
                logFlushScheduled = true;
            }
            try { BeginInvoke(FlushPendingLog); }
            catch (InvalidOperationException) { lock (logLock) logFlushScheduled = false; }
        }

        void FlushPendingLog()
        {
            string batch;
            lock (logLock)
            {
                batch = pendingLog.ToString();
                pendingLog.Clear();
                logFlushScheduled = false;
            }
            tbLog.AppendText(batch);
            const int maxLines = 2000;
            var lines = tbLog.Lines;
            if (lines.Length > maxLines)
            {
                var newLines = new string[maxLines];
                Array.Copy(lines, lines.Length - maxLines, newLines, 0, maxLines);
                tbLog.Lines = newLines;
            }
        }

        public static void KillProcessTree(int pid)
        {
            if (pid <= 0) return;
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
        }

        async Task FinalizeLiveOutputFileAsync(QueueItem item)
        {
            if (!item.IsLive) return;
            try
            {
                var path = item.OutputFilePath;
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return;
                var partPath = path + ".part";
                if (!File.Exists(partPath)) return;
                var finalPath = YtDlp.MakeUniquePath(path);
                File.Move(partPath, finalPath);
                item.OutputFilePath = finalPath;
                await EmbedThumbnailAsync(finalPath);
            }
            catch { }
        }

        async Task EmbedThumbnailAsync(string videoPath)
        {
            var dir = Path.GetDirectoryName(videoPath);
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrWhiteSpace(dir)) return;
            string[] thumbExts = { ".webp", ".jpg", ".jpeg", ".png" };
            var thumbPath = thumbExts
                .Select(ext => Path.Combine(dir, baseName + ext))
                .FirstOrDefault(File.Exists);
            if (thumbPath == null) return;

            var tempPath = Path.Combine(dir, baseName + ".thumbtmp.mp4");
            var embedded = false;
            try
            {
                var ffmpeg = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? "ffmpeg" : settings.FfmpegPath;
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(thumbPath);
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
                psi.ArgumentList.Add("-c:v:1"); psi.ArgumentList.Add("mjpeg");
                psi.ArgumentList.Add("-disposition:v:1"); psi.ArgumentList.Add("attached_pic");
                psi.ArgumentList.Add(tempPath);

                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, _) => { };
                proc.ErrorDataReceived += (_, _) => { };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync();
                embedded = proc.ExitCode == 0 && File.Exists(tempPath);
            }
            catch { }

            try
            {
                if (embedded)
                {
                    File.Replace(tempPath, videoPath, null);
                }
                else
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch { }
            try { File.Delete(thumbPath); } catch { }
        }

        async Task ProcessQueueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                QueueItem? item = null;
                lock (queueLock)
                {
                    if (queue.Count > 0) item = queue.Dequeue();
                }
                if (item == null) break;
                lock (queueLock)
                {
                    if (!allItems.Contains(item)) continue;
                    item.Status = "ダウンロード中";
                    item.ProgressPercent = 0;
                }
                RefreshQueueDisplay();
                UpdateStatus($"処理中: {item.Url}");
                try
                {
                    var rc = await RunYtDlpAsync(item, token);
                    if (rc != 0) throw new InvalidOperationException($"yt-dlp が終了コード {rc} を返しました。");
                    UpdateStatus($"完了: {item.Url} (exit {rc})");
                    await FinalizeLiveOutputFileAsync(item);
                    lock (queueLock)
                    {
                        item.Status = "ダウンロード完了";
                        item.ProgressPercent = 100;
                    }
                    RefreshQueueDisplay();
                    await ProcessChatReplayIfRequestedAsync(item, token);
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested)
                    {
                        await FinalizeLiveOutputFileAsync(item);
                        lock (queueLock)
                        {
                            item.Status = "キャンセル";
                            item.ProgressPercent = -1;
                        }
                        RefreshQueueDisplay();
                        UpdateStatus("キャンセルされました。");
                        break;
                    }
                    await FinalizeLiveOutputFileAsync(item);
                    lock (queueLock)
                    {
                        if (allItems.Contains(item))
                        {
                            item.Status = "キャンセル";
                            item.ProgressPercent = -1;
                        }
                    }
                    RefreshQueueDisplay();
                    UpdateStatus("項目を中止しました: " + item.Title);
                }
                catch (Exception ex)
                {
                    UpdateStatus($"エラー: {ex.Message}");
                    lock (queueLock)
                    {
                        if (allItems.Contains(item))
                        {
                            item.Status = "失敗";
                            item.ProgressPercent = -1;
                        }
                    }
                    RefreshQueueDisplay();
                }
            }
            BeginInvoke(() =>
            {
                running = false;
                btnStart.Text = "開始";
            });
        }

        async Task<int> RunYtDlpAsync(QueueItem item, CancellationToken token)
        {
            var args = new List<string> { "--no-playlist", "--newline", "--progress", "--progress-delta", "1", "--encoding", "utf-8" };
            args.Add("--js-runtimes");
            args.Add("deno");
            args.Add("--remote-components");
            args.Add("ejs:github");
            if (item.AudioOnly)
            {
                args.Add("-x");
                args.Add("--audio-format");
                args.Add(item.Codec);
            }
            if (!string.IsNullOrWhiteSpace(item.SelectedFormat))
            {
                args.Add("-f");
                args.Add(item.SelectedFormat);
            }
            if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
            {
                args.Add("--ffmpeg-location");
                args.Add(settings.FfmpegPath);
            }
            args.Add("--embed-thumbnail");
            args.Add("--add-metadata");
            if (settings.UseNoPart) args.Add("--no-part");
            if (item.DownloadChatReplay && YtDlp.DetermineSiteKind(item.Url) == SiteKind.YouTube)
                args.Add("--write-live-chat");

            if (!string.IsNullOrWhiteSpace(item.OutputFilePath))
            {
                args.Add("-o");
                args.Add(item.OutputFilePath);
            }
            else if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
            {
                args.Add("-o");
                args.Add(Path.Combine(settings.OutputDirectory, "%(title)s.%(ext)s"));
            }
            else
            {
                args.Add("-o");
                args.Add("%(title)s.%(ext)s");
            }
            if (item.IsLive)
                args.Add(item.LiveFromStart ? "--live-from-start" : "--no-live-from-start");
            args.Add(item.Url);

            var lastExit = -1;
            var retryCount = Math.Max(1, settings.RetryCount);
            for (int attempt = 1; attempt <= retryCount; attempt++)
            {
                var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var stdoutCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stderrCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var psi = YtDlp.CreateStartInfo(args, item.CookiePath, settings);
                using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stderrBuffer = new StringBuilder();
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) stdoutCompleted.TrySetResult();
                    else ReportDownloadProgress(item, e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) stderrCompleted.TrySetResult();
                    else { stderrBuffer.AppendLine(e.Data); ReportDownloadProgress(item, e.Data); }
                };
                proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                try
                {
                    item.ActiveCts = linkedCts;
                    using var reg = linkedCts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
                    try
                    {
                        proc.Start();
                        try { item.ActiveProcPid = proc.Id; } catch { item.ActiveProcPid = 0; }
                        proc.BeginOutputReadLine();
                        proc.BeginErrorReadLine();
                        var rc = await tcs.Task.ConfigureAwait(false);
                        await Task.WhenAll(stdoutCompleted.Task, stderrCompleted.Task).ConfigureAwait(false);
                        linkedCts.Token.ThrowIfCancellationRequested();
                        lastExit = rc;
                        if (rc == 0) return 0;
                        UpdateStatus($"失敗: exit {rc} (試行 {attempt}/{retryCount})");
                        try
                        {
                            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YtGui", "last_error.log");
                            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? "");
                            File.AppendAllText(logPath, $"\n[{DateTime.Now}] URL: {item.Url} Exit: {rc}\n{stderrBuffer}\n");
                        }
                        catch { }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) when (linkedCts.IsCancellationRequested)
                    {
                        throw new OperationCanceledException();
                    }
                }
                finally
                {
                    item.ActiveCts = null;
                    item.ActiveProcPid = 0;
                }

                if (attempt < retryCount)
                {
                    UpdateStatus($"{settings.RetryDelaySeconds} 秒後に再試行します...");
                    await Task.Delay(TimeSpan.FromSeconds(settings.RetryDelaySeconds), token).ConfigureAwait(false);
                }
            }

            return lastExit;
        }

        async Task ProcessChatReplayIfRequestedAsync(QueueItem item, CancellationToken token)
        {
            if (!item.DownloadChatReplay) return;

            var siteKind = YtDlp.DetermineSiteKind(item.Url);
            if (siteKind == SiteKind.YouTube) return; // RunYtDlpAsync内の --write-live-chat で完結済み

            if (siteKind != SiteKind.Twitch)
            {
                UpdateStatus("チャット取得をスキップしました（非対応のサイト種別）: " + item.Url);
                return;
            }

            lock (queueLock)
            {
                if (!allItems.Contains(item)) return;
                item.Status = "チャット取得中";
            }
            RefreshQueueDisplay();
            try
            {
                var chatPath = YtDlp.BuildChatReplayOutputPath(item.OutputFilePath);
                var (exit, stderr) = await RunTwitchChatDownloadAsync(item, chatPath, token);
                UpdateStatus(exit == 0
                    ? "チャットリプレイを取得しました: " + chatPath
                    : $"チャット取得に失敗しました (exit {exit}): {stderr.Trim()}");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("チャット取得を中止しました。");
            }
            catch (Exception ex)
            {
                UpdateStatus("チャット取得でエラー: " + ex.Message);
            }
            finally
            {
                lock (queueLock)
                {
                    if (allItems.Contains(item)) item.Status = "ダウンロード完了";
                }
                RefreshQueueDisplay();
            }
        }

        async Task<(int ExitCode, string Stderr)> RunTwitchChatDownloadAsync(QueueItem item, string outputPath, CancellationToken token)
        {
            var args = new List<string> { "chatdownload", "--id", item.Url, "-o", outputPath };
            var psi = YtDlp.CreateTwitchChatToolStartInfo(args, settings);
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderrBuffer = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuffer.AppendLine(e.Data); };

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                item.ActiveCts = linkedCts;
                using var reg = linkedCts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
                proc.Start();
                try { item.ActiveProcPid = proc.Id; } catch { item.ActiveProcPid = 0; }
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(linkedCts.Token);
                return (proc.ExitCode, stderrBuffer.ToString());
            }
            finally
            {
                item.ActiveCts = null;
                item.ActiveProcPid = 0;
            }
        }
    }
}
