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
        public bool IsChatReplayMissing { get; set; }
        public bool ShouldFetchChatReplayOnly { get; set; }
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
        const string EmojiCacheButtonText = "絵文字キャッシュ ▼";
        readonly Button btnEmojiCache = new() { Text = EmojiCacheButtonText, AutoSize = true };
        readonly ContextMenuStrip emojiCacheMenu = new();
        CancellationTokenSource? emojiCacheCts;
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
        readonly ExecutionLogWriter executionLogWriter;
        CancellationTokenSource? cts;
        QueueProcessingState queueProcessingState = QueueProcessingState.Idle;
        bool isStartReserved;
        Settings settings = Settings.Load();

        public MainForm()
        {
            executionLogWriter = new ExecutionLogWriter(tbLog);
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

            lvQueue.Columns.Add("Title", 440);
            lvQueue.Columns.Add("Status", 260);
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
            btnRow.Controls.Add(btnEmojiCache);
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
            emojiCacheMenu.Items.Add("チャンネル事前投入", null, (_, _) => _ = StartChannelPrefetchAsync());
            emojiCacheMenu.Items.Add("フォルダ一括投入…", null, (_, _) => _ = StartFolderBulkEmojiCacheAsync());
            btnEmojiCache.Click += BtnEmojiCache_Click;
            btnTopMost.Click += (_, _) =>
            {
                TopMost = !TopMost;
                btnTopMost.Text = TopMost ? "常に最前面: ON" : "常に最前面: OFF";
            };

            progressUiTimer.Tick += (_, _) =>
            {
                bool any;
                lock (queueLock) any = allItems.Exists(x => x.Status is "ダウンロード中" or "チャット取得中" or "絵文字キャッシュ投入中" or "仕上げ中");
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
            var fetchChatReplayMenuItem = menu.Items.Add("チャットリプレイを取得", null, (_, _) => FetchChatReplayForItems(GetSelectedItems()));
            menu.Opening += (_, _) =>
            {
                var selected = GetSelectedItems();
                bool canEnqueue;
                lock (queueLock) canEnqueue = selected.Any(x => LiveChatReplay.CanEnqueueChatReplayFetch(x.Status, x.IsChatReplayMissing, x.ShouldFetchChatReplayOnly));
                fetchChatReplayMenuItem.Enabled = canEnqueue;
            };
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
            emojiCacheCts?.Cancel();
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
                    if (item.ShouldFetchChatReplayOnly) continue;
                    item.IsChatReplayMissing = false;
                    item.Status = "ダウンロード待ち";
                    item.ProgressPercent = -1;
                    queue.Enqueue(item);
                }
            }
            RefreshQueueDisplay();
            UpdateStatus("再キューしました。");
        }

        void FetchChatReplayForItems(IReadOnlyList<QueueItem> items)
        {
            var enqueuedCount = 0;
            lock (queueLock)
            {
                foreach (var item in items)
                {
                    if (!allItems.Contains(item)) continue;
                    if (!LiveChatReplay.CanEnqueueChatReplayFetch(item.Status, item.IsChatReplayMissing, item.ShouldFetchChatReplayOnly)) continue;
                    item.ShouldFetchChatReplayOnly = true;
                    queue.Enqueue(item);
                    enqueuedCount++;
                }
            }
            if (enqueuedCount == 0) return;
            UpdateStatus($"チャットリプレイの取得をキューに入れました（{enqueuedCount}件）。");
            StartOrReserveProcessing();
        }

        void BtnStart_Click(object? sender, EventArgs e)
        {
            if (queueProcessingState == QueueProcessingState.Idle) StartProcessing();
            else if (queueProcessingState == QueueProcessingState.Running) StopProcessing();
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
            StartQueueProcessingLoop();
        }

        void StartQueueProcessingLoop()
        {
            queueProcessingState = QueueProcessingState.Running;
            btnStart.Text = "停止";
            btnStart.Enabled = true;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                var hasFaulted = false;
                try
                {
                    await ProcessQueueAsync(token);
                }
                catch (Exception ex)
                {
                    hasFaulted = true;
                    UpdateStatus("キュー処理がエラーで止まりました: " + ex.Message);
                }
                finally
                {
                    // 例外で抜けても受け取らないと、停止中のままボタンが押せなくなる。
                    try { BeginInvoke(() => OnQueueProcessingFinished(hasFaulted)); }
                    catch (InvalidOperationException) { }
                }
            });
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
            queueProcessingState = QueueProcessingState.Stopping;
            btnStart.Text = "停止中…";
            btnStart.Enabled = false;
            UpdateStatus("停止しました。");
        }

        void OnQueueProcessingFinished(bool hasFaulted)
        {
            // 判定に isStartReserved を直接渡さない。捨てる行より後で読むと、予約が常に false になる。
            var wasStartReserved = isStartReserved;
            isStartReserved = false;
            bool hasQueuedItems;
            lock (queueLock) hasQueuedItems = queue.Count > 0;
            if (QueueProcessingLifecycle.ShouldStartNextAfterFinished(queueProcessingState, wasStartReserved, hasQueuedItems, hasFaulted))
            {
                StartQueueProcessingLoop();
                return;
            }
            queueProcessingState = QueueProcessingState.Idle;
            btnStart.Text = "開始";
            btnStart.Enabled = true;
        }

        void StartOrReserveProcessing()
        {
            switch (QueueProcessingLifecycle.DecideAutoStart(queueProcessingState, isStartReserved))
            {
                case AutoStartDecision.Start:
                    StartProcessing();
                    break;
                case AutoStartDecision.Reserve:
                    isStartReserved = true;
                    UpdateStatus("停止の処理が終わったら、キュー処理を開始します。");
                    break;
            }
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
            if (isLive) StartOrReserveProcessing();
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
                    lvi.SubItems.Add(LiveChatReplay.BuildStatusText(it.Status, it.IsChatReplayMissing));
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

            if (item.Status is "チャット取得中" or "絵文字キャッシュ投入中" or "仕上げ中")
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

            if (item.ProgressPercent < 0 || item.Status == "ダウンロード完了")
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
                // ライブチャットのフラグメント取得行（[youtube_live_chat] ...）には%表示がなく進捗が計算できないため、
                // 既存の「チャット取得中」マーキー表示に切り替えてフリーズして見えるのを防ぐ。
                if (line.Contains("[youtube_live_chat]"))
                {
                    bool changed;
                    lock (queueLock)
                    {
                        changed = allItems.Contains(item) && item.Status != "チャット取得中";
                        if (changed) item.Status = "チャット取得中";
                    }
                    if (changed) RefreshQueueDisplay();
                }
                if (!ExecutionLogFilter.IsFfmpegNoiseLine(line)) UpdateStatus(line);
                return;
            }
            // チャット取得フェーズの後に動画/音声のダウンロードが再開するケース（%表示が戻ってくる）では
            // マーキー表示に固定されたままにならないよう、通常の進捗表示に戻す。
            bool reverted;
            lock (queueLock)
            {
                item.ProgressPercent = Math.Clamp(percent, 0, 100);
                reverted = allItems.Contains(item) && item.Status == "チャット取得中";
                if (reverted) item.Status = "ダウンロード中";
            }
            if (reverted) RefreshQueueDisplay();
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

        void BtnEmojiCache_Click(object? sender, EventArgs e)
        {
            if (emojiCacheCts != null)
            {
                // 1ファイル（または1アーカイブ）の処理中は止まるまで時間がかかる。押し直しで反応が無く見えたりログが増えたりしないよう、ここで無効にする。
                // 元に戻すのは RunEmojiCacheJobAsync の finally。
                emojiCacheCts.Cancel();
                btnEmojiCache.Text = "中止しています...";
                btnEmojiCache.Enabled = false;
                UpdateStatus("絵文字キャッシュの処理を中止しています...");
                return;
            }
            emojiCacheMenu.Show(btnEmojiCache, new Point(0, btnEmojiCache.Height));
        }

        // emojiCacheCts は、フォルダ一括投入とチャンネル事前投入が共有する「実行中」の印。キュー項目の絵文字キャッシュ投入とは無関係に並行して動く。
        // キューの停止・項目の中止からは止まらない。止めるのはボタンの「中止」とアプリの終了だけ。
        async Task RunEmojiCacheJobAsync(string jobName, Func<CancellationToken, Task> body)
        {
            using var localCts = new CancellationTokenSource();
            try
            {
                emojiCacheCts = localCts;
                btnEmojiCache.Text = "中止";
                await Task.Run(() => body(localCts.Token));
            }
            catch (OperationCanceledException) when (localCts.IsCancellationRequested)
            {
                UpdateStatus($"{jobName}を中止しました。");
            }
            catch (Exception ex)
            {
                UpdateStatus($"{jobName}でエラー: {ex.Message}");
            }
            finally
            {
                emojiCacheCts = null;
                btnEmojiCache.Text = EmojiCacheButtonText;
                btnEmojiCache.Enabled = true;
            }
        }

        bool TryGetEmojiCacheDirectory(out string cacheDirectory)
        {
            cacheDirectory = settings.EmojiCacheOutputDirectory;
            if (!string.IsNullOrWhiteSpace(cacheDirectory)) return true;
            MessageBox.Show(this, "設定で「絵文字キャッシュ出力フォルダ」を指定してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        async Task StartFolderBulkEmojiCacheAsync()
        {
            if (emojiCacheCts != null) return;
            if (!TryGetEmojiCacheDirectory(out var cacheDirectory)) return;
            using var fbd = new FolderBrowserDialog();
            if (Directory.Exists(settings.OutputDirectory)) fbd.SelectedPath = settings.OutputDirectory;
            if (fbd.ShowDialog(this) != DialogResult.OK) return;
            var folder = fbd.SelectedPath;
            await RunEmojiCacheJobAsync("フォルダ一括投入", token => RunFolderBulkEmojiCacheAsync(folder, cacheDirectory, token));
        }

        async Task StartChannelPrefetchAsync()
        {
            if (emojiCacheCts != null) return;
            if (!TryGetEmojiCacheDirectory(out var cacheDirectory)) return;
            // 出力先が無いと、各アーカイブのチャット（数分）を取り終えてから全件が失敗し、初回の時間が無駄になる。
            if (!Directory.Exists(cacheDirectory))
            {
                MessageBox.Show(this, "絵文字キャッシュ出力フォルダが存在しません: " + cacheDirectory, "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 開始時の値で固定する。バックグラウンドのスレッドに渡すため、UIスレッドでローカルに取る。
            var channelUrls = settings.ChannelUrls;
            var count = Math.Max(1, settings.ChannelPrefetchCount);
            var currentSettings = settings;
            if (!ChannelPrefetch.HasChannelLines(channelUrls))
            {
                MessageBox.Show(this, "設定で「チャンネルURL」を指定してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await RunEmojiCacheJobAsync("チャンネル事前投入", token => RunChannelPrefetchAsync(currentSettings, cacheDirectory, channelUrls, count, token));
        }

        // バックグラウンドスレッドで動くので、コントロールには触らず UpdateStatus だけで出力する。
        async Task RunFolderBulkEmojiCacheAsync(string folder, string cacheDirectory, CancellationToken token)
        {
            UpdateStatus("フォルダ一括投入を開始します: " + folder);
            var files = EmojiCache.FindChatReplayFiles(folder, UpdateStatus, token);
            UpdateStatus($"対象のチャットリプレイ: {files.Count}件");

            var result = await EmojiCache.PopulateFilesAsync(files, folder, cacheDirectory, UpdateStatus, token);
            var summary = $"失敗ファイル: {result.FailedFileCount}件, 絵文字の取得: {result.DownloadedCount}件, 失敗（延べ）: {result.FailedUrlCount}件";
            if (result.IsCanceled)
                UpdateStatus($"フォルダ一括投入を中止しました（処理済みファイル: {result.CompletedFileCount}/{result.TargetFileCount}件, {summary}）");
            else
                UpdateStatus($"フォルダ一括投入が完了しました（対象ファイル: {result.TargetFileCount}件, {summary}）");
        }

        // バックグラウンドスレッドで動くので、コントロールには触らず UpdateStatus だけで出力する。
        async Task RunChannelPrefetchAsync(Settings currentSettings, string cacheDirectory, string channelUrls, int count, CancellationToken token)
        {
            UpdateStatus($"チャンネル事前投入を開始します（チャンネルごとに新しい順で {count}件）");
            var store = ProcessedArchiveStore.Load(Path.Combine(Settings.GetDataDirectory(), DataDirectory.ProcessedArchivesFileName), UpdateStatus);
            var workRoot = Path.Combine(Path.GetTempPath(), "YtGui", "prefetch");
            var result = await ChannelPrefetch.RunAsync(channelUrls, count, cacheDirectory, workRoot, store, new ChannelPrefetchTools(currentSettings), UpdateStatus, token);
            var summary = $"チャンネル: {result.ChannelCount}件（飛ばした: {result.FailedChannelCount}件）, アーカイブ: {result.ArchiveCount}件（処理済みのため飛ばした: {result.SkippedArchiveCount}件, 失敗した: {result.FailedArchiveCount}件）, 絵文字の取得: {result.DownloadedCount}件, 失敗（延べ）: {result.FailedUrlCount}件";
            UpdateStatus(result.IsCanceled ? $"チャンネル事前投入を中止しました（{summary}）" : $"チャンネル事前投入が完了しました（{summary}）");
        }

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
            executionLogWriter.Append(batch);
        }

        public static void KillProcessTree(int pid)
        {
            if (pid <= 0) return;
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
        }

        async Task FinalizeLiveOutputFileAsync(QueueItem item, CancellationToken token)
        {
            if (!item.IsLive) return;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var path = item.OutputFilePath;
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return;
                var partPath = path + ".part";
                var hasSinglePart = File.Exists(partPath);
                var splitPartPaths = hasSinglePart ? new List<string>() : LivePartMerger.FindSplitParts(path);
                var action = LivePartMerger.DecideFinalizeAction(hasSinglePart, File.Exists(path), splitPartPaths.Count);
                if (action == LiveFinalizeAction.None) return;
                if (action == LiveFinalizeAction.RefuseUnexpectedCount)
                {
                    UpdateStatus($"途中のファイルが想定外の数（{splitPartPaths.Count}個）だったので、映像と音声を結合しませんでした。途中のファイルを残しています: " + string.Join("、", splitPartPaths.Select(Path.GetFileName)));
                    return;
                }
                // 項目の中止（CancelItems）が止めるのは item.ActiveCts と ActiveProcPid だけ。ここで入れないと、仕上げ中の ffmpeg は止まらない。
                item.ActiveCts = linkedCts;
                lock (queueLock)
                {
                    if (allItems.Contains(item)) item.Status = "仕上げ中";
                }
                RefreshQueueDisplay();
                var finalPath = YtDlp.MakeUniquePath(path);
                var ffmpegPath = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? "ffmpeg" : settings.FfmpegPath;
                if (action == LiveFinalizeAction.Rename)
                {
                    File.Move(partPath, finalPath);
                }
                else
                {
                    var mergeOutcome = await LivePartMerger.MergeAsync(ffmpegPath, splitPartPaths, finalPath, linkedCts.Token);
                    if (mergeOutcome != LivePartMergeOutcome.Merged)
                    {
                        var leftNames = string.Join("、", splitPartPaths.Select(Path.GetFileName));
                        UpdateStatus(mergeOutcome == LivePartMergeOutcome.Canceled
                            ? "映像と音声の結合を中止しました。途中のファイルを残しています: " + leftNames
                            : "映像と音声の結合に失敗しました。途中のファイルを残しています: " + leftNames);
                        return;
                    }
                    LivePartMerger.DeleteLeftovers(splitPartPaths);
                    if (action == LiveFinalizeAction.MergeSingle)
                    {
                        item.OutputFilePath = finalPath;
                        // 音声だけの mp4 には webp を埋め込めず、ThumbnailEmbedder が失敗して画像を消してしまうので、埋め込まずに画像を残す。
                        UpdateStatus("途中のファイルが1つだけだったので、映像（または音声）だけの動画になりました（サムネイルは埋め込んでいません）: " + Path.GetFileName(finalPath));
                        return;
                    }
                }
                item.OutputFilePath = finalPath;
                var outcome = await ThumbnailEmbedder.EmbedAsync(ffmpegPath, finalPath, linkedCts.Token);
                if (outcome == ThumbnailEmbedOutcome.Canceled) UpdateStatus("サムネイルの埋め込みを中止しました。");
            }
            catch { }
            finally
            {
                item.ActiveCts = null;
            }
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
                bool shouldFetchChatReplayOnly;
                lock (queueLock)
                {
                    if (!allItems.Contains(item)) continue;
                    shouldFetchChatReplayOnly = item.ShouldFetchChatReplayOnly;
                    item.ShouldFetchChatReplayOnly = false;
                    if (!shouldFetchChatReplayOnly)
                    {
                        item.Status = "ダウンロード中";
                        item.ProgressPercent = 0;
                    }
                }
                if (shouldFetchChatReplayOnly)
                {
                    await FetchYouTubeLiveChatReplayAsync(item, token);
                    continue;
                }
                RefreshQueueDisplay();
                UpdateStatus($"処理中: {item.Url}");
                try
                {
                    var rc = await RunYtDlpAsync(item, token);
                    if (rc != 0) throw new InvalidOperationException($"yt-dlp が終了コード {rc} を返しました。");
                    UpdateStatus($"完了: {item.Url} (exit {rc})");
                    string? youTubeChatPath = null;
                    if (item.DownloadChatReplay && !item.IsLive && YtDlp.DetermineSiteKind(item.Url) == SiteKind.YouTube)
                        youTubeChatPath = YtDlp.BuildYouTubeLiveChatPath(item.OutputFilePath);
                    await FinalizeLiveOutputFileAsync(item, token);
                    lock (queueLock)
                    {
                        item.Status = "ダウンロード完了";
                        item.ProgressPercent = 100;
                    }
                    RefreshQueueDisplay();
                    await ProcessChatReplayIfRequestedAsync(item, youTubeChatPath, token);
                }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested)
                    {
                        // 停止で中止済みの token と連動させると、仕上げが始まった瞬間に止まってしまう。
                        await FinalizeLiveOutputFileAsync(item, CancellationToken.None);
                        lock (queueLock)
                        {
                            item.Status = "キャンセル";
                            item.ProgressPercent = -1;
                            item.IsChatReplayMissing = LiveChatReplay.IsFetchedAfterRecording(item.IsLive, item.DownloadChatReplay, YtDlp.DetermineSiteKind(item.Url));
                        }
                        RefreshQueueDisplay();
                        if (item.IsChatReplayMissing) UpdateStatus("チャットリプレイは、配信の終了後に右クリックの「チャットリプレイを取得」で取得できます: " + item.Title);
                        UpdateStatus("キャンセルされました。");
                        break;
                    }
                    await FinalizeLiveOutputFileAsync(item, token);
                    lock (queueLock)
                    {
                        if (allItems.Contains(item))
                        {
                            item.Status = "キャンセル";
                            item.ProgressPercent = -1;
                            item.IsChatReplayMissing = LiveChatReplay.IsFetchedAfterRecording(item.IsLive, item.DownloadChatReplay, YtDlp.DetermineSiteKind(item.Url));
                        }
                    }
                    RefreshQueueDisplay();
                    UpdateStatus("項目を中止しました: " + item.Title);
                    if (item.IsChatReplayMissing) UpdateStatus("チャットリプレイは、配信の終了後に右クリックの「チャットリプレイを取得」で取得できます: " + item.Title);
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
        }

        async Task<int> RunYtDlpAsync(QueueItem item, CancellationToken token)
        {
            // ライブ配信の録画中は総サイズが分からず、進捗の行に % が付かない。進捗の表示には使えず、実行ログに流れるだけになるので、
            // 録画が続いていると分かる程度に間引く。
            var progressDeltaSeconds = item.IsLive ? "60" : "1";
            var args = new List<string> { "--no-playlist", "--newline", "--progress", "--progress-delta", progressDeltaSeconds, "--encoding", "utf-8" };
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
            if (item.DownloadChatReplay && !item.IsLive && YtDlp.DetermineSiteKind(item.Url) == SiteKind.YouTube)
            {
                // yt-dlpが--write-live-chatを廃止し、ライブチャットを疑似言語"live_chat"の字幕として扱う方式に統一したため（docs/adr/0003参照）
                // ライブでは付けない。配信中のチャットは配信が終わるまで取り続けて録画が始まらず、vlc-chat も読めない形式のため、録画の後にアーカイブから取る（docs/adr/0005参照）
                args.Add("--write-subs");
                args.Add("--sub-langs");
                args.Add("live_chat");
            }

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
                            var logPath = Path.Combine(Settings.GetDataDirectory(), DataDirectory.ErrorLogFileName);
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

        async Task ProcessChatReplayIfRequestedAsync(QueueItem item, string? youTubeChatPath, CancellationToken token)
        {
            if (!item.DownloadChatReplay) return;

            var siteKind = YtDlp.DetermineSiteKind(item.Url);
            if (siteKind == SiteKind.YouTube)
            {
                if (item.IsLive)
                {
                    await FetchYouTubeLiveChatReplayAsync(item, token);
                    return;
                }
                // RunYtDlpAsync内の --write-subs --sub-langs live_chat で完結済み。
                // 絵文字キャッシュ設定の有無に関わらず、まずファイル名を想定の名前に揃える。
                if (youTubeChatPath != null)
                {
                    var alignedPath = AlignYouTubeLiveChatFileName(youTubeChatPath);
                    if (alignedPath != null)
                        await ProcessEmojiCacheIfRequestedAsync(item, alignedPath, siteKind, token);
                }
                return;
            }

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
            var chatPath = YtDlp.BuildChatReplayOutputPath(item.OutputFilePath); // 1回だけ計算、以後再計算しない
            var chatSucceeded = false;
            try
            {
                var (exit, stderr) = await RunTwitchChatDownloadAsync(item, chatPath, token);
                chatSucceeded = exit == 0;
                UpdateStatus(chatSucceeded
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

            if (chatSucceeded)
                await ProcessEmojiCacheIfRequestedAsync(item, chatPath, siteKind, token);
        }

        async Task FetchYouTubeLiveChatReplayAsync(QueueItem item, CancellationToken token)
        {
            var chatPath = YtDlp.BuildYouTubeLiveChatPath(item.OutputFilePath);
            string previousStatus;
            lock (queueLock)
            {
                if (!allItems.Contains(item)) return;
                previousStatus = item.Status;
                item.Status = "チャット取得中";
            }
            RefreshQueueDisplay();
            var isFetched = false;
            try
            {
                var (exit, output) = await RunYouTubeChatReplayDownloadAsync(item, chatPath, token);
                // チャットリプレイがまだ無いと、yt-dlp は終了コード0でファイルを作らない。
                isFetched = File.Exists(chatPath);
                if (isFetched)
                    UpdateStatus("チャットリプレイを取得しました: " + chatPath);
                else
                    UpdateStatus($"チャットリプレイを取得できませんでした（配信中か、配信の直後でまだ用意されていない可能性があります。配信の処理が終わった後に、右クリックの「チャットリプレイを取得」で取り直せます） (exit {exit}): {item.Title} {output.Trim()}");
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
                    if (allItems.Contains(item))
                    {
                        // キャンセルの項目のチャットを後から取ることがあるので、ダウンロード完了に固定しない。
                        item.Status = previousStatus;
                        item.IsChatReplayMissing = !isFetched;
                    }
                }
                RefreshQueueDisplay();
            }

            if (isFetched)
                await ProcessEmojiCacheIfRequestedAsync(item, chatPath, SiteKind.YouTube, token);
        }

        async Task<(int ExitCode, string Output)> RunYouTubeChatReplayDownloadAsync(QueueItem item, string chatPath, CancellationToken token)
        {
            var psi = YtDlp.CreateStartInfo(LiveChatReplay.BuildDownloadArgs(item.Url, chatPath), item.CookiePath, settings);
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stderrBuffer = new StringBuilder();
            // 取れなかった理由（絞り込みで飛ばした、チャットが無い）は標準出力に出ることがある。進捗の行が大量に出うるので、最後の数行だけ残す。
            var stdoutTail = new Queue<string>();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stdoutTail.Enqueue(e.Data);
                if (stdoutTail.Count > 3) stdoutTail.Dequeue();
            };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuffer.AppendLine(e.Data); };

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                item.ActiveCts = linkedCts;
                using var reg = linkedCts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
                // 中止済みのまま起動すると、WaitForExitAsync がすぐ戻り、起動した yt-dlp を止められずに残してしまう。
                linkedCts.Token.ThrowIfCancellationRequested();
                proc.Start();
                try { item.ActiveProcPid = proc.Id; } catch { item.ActiveProcPid = 0; }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // WaitForExitAsync は、非同期で読んでいる標準出力・標準エラーの読み切りまで待つ。
                await proc.WaitForExitAsync(linkedCts.Token);
                return (proc.ExitCode, stderrBuffer.ToString() + string.Join(Environment.NewLine, stdoutTail));
            }
            finally
            {
                item.ActiveCts = null;
                item.ActiveProcPid = 0;
            }
        }

        // yt-dlpが実際に書き出したlive_chat.jsonを探し、想定ファイル名（BuildYouTubeLiveChatPath）に
        // リネームして揃える。見つからない場合はnullを返す。
        string? AlignYouTubeLiveChatFileName(string expectedPath)
        {
            if (File.Exists(expectedPath)) return expectedPath;

            var fallback = YtDlp.FindYouTubeLiveChatFileFallback(expectedPath);
            if (fallback == null)
            {
                UpdateStatus("チャットリプレイJSONが見つかりませんでした: " + expectedPath);
                return null;
            }

            try
            {
                File.Move(fallback, expectedPath);
                UpdateStatus("チャットリプレイJSONのファイル名を揃えました: " + Path.GetFileName(expectedPath));
                return expectedPath;
            }
            catch (Exception ex)
            {
                UpdateStatus("チャットJSONのリネームに失敗したため、元のファイル名のまま使用します: " + fallback + " (" + ex.Message + ")");
                return fallback;
            }
        }

        async Task ProcessEmojiCacheIfRequestedAsync(QueueItem item, string chatJsonPath, SiteKind siteKind, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(settings.EmojiCacheOutputDirectory)) return;

            var resolvedPath = chatJsonPath;
            if (!File.Exists(resolvedPath))
            {
                UpdateStatus("絵文字キャッシュ投入をスキップしました（チャットJSONが見つかりません）: " + resolvedPath);
                return;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            string previousStatus;
            lock (queueLock)
            {
                if (!allItems.Contains(item)) return;
                previousStatus = item.Status;
                item.Status = "絵文字キャッシュ投入中";
            }
            RefreshQueueDisplay();
            try
            {
                // 項目の中止（CancelItems）が止めるのは item.ActiveCts と ActiveProcPid だけ。プロセスを持たないこの処理は、キュー全体の token をそのまま渡しても止まらない。
                item.ActiveCts = linkedCts;
                var (downloaded, failed) = await EmojiCache.PopulateAsync(resolvedPath, siteKind, settings.EmojiCacheOutputDirectory, UpdateStatus, linkedCts.Token);
                UpdateStatus($"絵文字キャッシュ投入が完了しました（取得: {downloaded}件, 失敗: {failed}件）");
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("絵文字キャッシュ投入を中止しました。");
            }
            catch (Exception ex)
            {
                UpdateStatus("絵文字キャッシュ投入でエラー: " + ex.Message);
            }
            finally
            {
                item.ActiveCts = null;
                lock (queueLock)
                {
                    // キャンセルの項目のチャットを後から取ることがあるので、ダウンロード完了に固定しない。
                    if (allItems.Contains(item)) item.Status = previousStatus;
                }
                RefreshQueueDisplay();
                UpdateStatus($"完了: {item.Title}");
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
