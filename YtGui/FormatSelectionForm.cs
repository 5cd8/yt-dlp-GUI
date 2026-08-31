using System.Text.RegularExpressions;

namespace YtGui
{
    public class FormatSelectionForm : Form
    {
        readonly ListView lvVideo = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
        readonly ListView lvAudio = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
        readonly TextBox tbManual = new() { Dock = DockStyle.Fill };
        readonly Button btnOk = new() { Text = "OK", DialogResult = DialogResult.OK };
        readonly Button btnCancel = new() { Text = "キャンセル", DialogResult = DialogResult.Cancel };
        readonly bool audioOnly;

        public string SelectedFormat { get; private set; } = string.Empty;

        public FormatSelectionForm(IReadOnlyList<MediaFormat> formats, bool audioOnly)
        {
            this.audioOnly = audioOnly;
            Text = "フォーマット選択";
            Width = audioOnly ? 780 : 820;
            Height = audioOnly ? 460 : 680;
            MinimumSize = new Size(640, 360);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8) };
            layout.RowStyles.Clear();

            if (!audioOnly)
            {
                layout.RowCount = 6;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                layout.Controls.Add(new Label { Text = "動画フォーマット (解像度・拡張子) を選択してください", AutoSize = true }, 0, 0);
                lvVideo.Columns.Add("Code", 80);
                lvVideo.Columns.Add("Ext", 80);
                lvVideo.Columns.Add("Resolution", 120);
                layout.Controls.Add(lvVideo, 0, 1);
                layout.Controls.Add(new Label { Text = "音声フォーマット (ビットレート・サンプリングレート・言語) を選択してください", AutoSize = true }, 0, 2);
                AddAudioColumns();
                layout.Controls.Add(lvAudio, 0, 3);
                layout.Controls.Add(CreateManualPanel(), 0, 4);
                layout.Controls.Add(CreateButtonPanel(), 0, 5);
            }
            else
            {
                layout.RowCount = 4;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label { Text = "音声フォーマットを選択してください", AutoSize = true }, 0, 0);
                AddAudioColumns();
                layout.Controls.Add(lvAudio, 0, 1);
                layout.Controls.Add(CreateManualPanel(), 0, 2);
                layout.Controls.Add(CreateButtonPanel(), 0, 3);
            }

            Controls.Add(layout);
            Populate(formats);

            lvVideo.SelectedIndexChanged += (_, _) => UpdateManualFromSelection();
            lvAudio.SelectedIndexChanged += (_, _) => UpdateManualFromSelection();
            lvVideo.DoubleClick += (_, _) => UpdateManualFromSelection();
            lvAudio.DoubleClick += (_, _) => UpdateManualFromSelection();
            btnOk.Click += (_, _) => { SelectedFormat = tbManual.Text.Trim(); };

            AutoResizeAllColumns();
        }

        void AddAudioColumns()
        {
            lvAudio.Columns.Add("Code", 80);
            lvAudio.Columns.Add("Ext", 110);
            lvAudio.Columns.Add("Bitrate", 80);
            lvAudio.Columns.Add("Samplerate", 90);
            lvAudio.Columns.Add("Language", 90);
        }

        Control CreateManualPanel()
        {
            var pnl = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pnl.Controls.Add(new Label { Text = "選択フォーマット (動画+音声 または 単独):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            pnl.Controls.Add(tbManual, 1, 0);
            return pnl;
        }

        Control CreateButtonPanel()
        {
            var btnPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            btnPanel.Controls.Add(btnOk);
            btnPanel.Controls.Add(btnCancel);
            return btnPanel;
        }

        void Populate(IReadOnlyList<MediaFormat> formats)
        {
            var videoEntries = formats.Where(f => !f.IsAudioOnly).ToList();
            var audioEntries = formats.Where(f => f.IsAudioOnly).ToList();

            videoEntries.Sort((a, b) => FormatListParser.ParseResolutionValue(b.Resolution).CompareTo(FormatListParser.ParseResolutionValue(a.Resolution)));
            foreach (var v in videoEntries)
            {
                var it = new ListViewItem(v.Id);
                it.SubItems.Add(v.Ext);
                it.SubItems.Add(v.Resolution);
                it.Tag = v;
                lvVideo.Items.Add(it);
            }

            audioEntries.Sort((a, b) =>
            {
                var srv = FormatListParser.ParseSamplerateValue(b.Samplerate).CompareTo(FormatListParser.ParseSamplerateValue(a.Samplerate));
                if (srv != 0) return srv;
                return FormatListParser.ParseBitrateValue(b.Bitrate).CompareTo(FormatListParser.ParseBitrateValue(a.Bitrate));
            });
            foreach (var a in audioEntries)
            {
                var it = new ListViewItem(a.Id);
                var extDisplay = string.IsNullOrWhiteSpace(a.AudioCodec)
                    ? a.Ext
                    : (string.IsNullOrWhiteSpace(a.Ext) ? a.AudioCodec : $"{a.Ext} ({a.AudioCodec})");
                it.SubItems.Add(extDisplay);
                it.SubItems.Add(a.Bitrate);
                it.SubItems.Add(a.Samplerate);
                it.SubItems.Add(a.Language);
                it.Tag = a;
                lvAudio.Items.Add(it);
            }
        }

        void AutoResizeAllColumns()
        {
            void Resize(ListView lv)
            {
                var font = lv.Font;
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    lv.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                    var headerWidth = TextRenderer.MeasureText(lv.Columns[i].Text, font).Width + 16;
                    if (lv.Columns[i].Width < headerWidth) lv.Columns[i].Width = headerWidth;
                }
            }
            if (!audioOnly) Resize(lvVideo);
            Resize(lvAudio);
        }

        void UpdateManualFromSelection()
        {
            var vcode = !audioOnly && lvVideo.SelectedItems.Count > 0 ? lvVideo.SelectedItems[0].Text : string.Empty;
            var acode = lvAudio.SelectedItems.Count > 0 ? lvAudio.SelectedItems[0].Text : string.Empty;
            if (!string.IsNullOrEmpty(vcode) && !string.IsNullOrEmpty(acode)) tbManual.Text = $"{vcode}+{acode}";
            else if (!string.IsNullOrEmpty(vcode)) tbManual.Text = vcode;
            else if (!string.IsNullOrEmpty(acode)) tbManual.Text = acode;
        }
    }

    internal static class FormatListParser
    {
        public static List<MediaFormat> Parse(string formatsOutput)
        {
            var result = new List<MediaFormat>();
            if (string.IsNullOrWhiteSpace(formatsOutput)) return result;
            foreach (var raw in formatsOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("format code", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith('-')) continue;

                var code = ParseFormatCode(line);
                if (string.IsNullOrWhiteSpace(code)) continue;
                var ext = ParseExtension(line);
                var audio = IsAudioLine(line);
                result.Add(new MediaFormat
                {
                    Id = code,
                    Ext = ext,
                    AudioCodec = audio ? ParseAudioCodec(line) : string.Empty,
                    Resolution = audio ? string.Empty : ParseResolution(line),
                    Bitrate = ParseBitrate(line),
                    Samplerate = ParseSamplerate(line),
                    Language = YtDlp.ExtractLanguage(line),
                    IsAudioOnly = audio
                });
            }
            return result;
        }

        public static int ParseResolutionValue(string res)
        {
            if (string.IsNullOrWhiteSpace(res)) return 0;
            var m = Regex.Match(res, @"(\d{2,5})x(\d{2,5})");
            if (m.Success)
            {
                if (int.TryParse(m.Groups[2].Value, out var h)) return h;
                if (int.TryParse(m.Groups[1].Value, out var w)) return w;
            }
            return 0;
        }

        public static int ParseSamplerateValue(string sr)
        {
            if (string.IsNullOrWhiteSpace(sr)) return 0;
            var m = Regex.Match(sr, @"(\d{1,5})k", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var kv)) return kv * 1000;
            var m2 = Regex.Match(sr, @"(\d{4,5})Hz", RegexOptions.IgnoreCase);
            if (m2.Success && int.TryParse(m2.Groups[1].Value, out var hv)) return hv;
            return int.TryParse(sr, out var v) ? v : 0;
        }

        public static int ParseBitrateValue(string br)
        {
            if (string.IsNullOrWhiteSpace(br)) return 0;
            var m = Regex.Match(br, @"(\d{1,5})k", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var kv)) return kv * 1000;
            var m2 = Regex.Match(br, @"(\d{1,7})\b");
            return m2.Success && int.TryParse(m2.Groups[1].Value, out var v) ? v : 0;
        }

        static string ParseFormatCode(string line)
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            return parts.Length > 0 ? parts[0] : string.Empty;
        }

        static string ParseExtension(string line)
        {
            var m = Regex.Match(line, @"^\s*\S+\s+(\S+)");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        static string ParseAudioCodec(string line)
        {
            var m = Regex.Match(line, @"(\S+)\s+(\d{1,5}k)\s+(\d{1,5}k)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            var tokens = Regex.Split(line, @"\s+");
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (Regex.IsMatch(t, @"^[a-z0-9\.]+$", RegexOptions.IgnoreCase) && t.Length <= 20 && !Regex.IsMatch(t, @"^\d+$"))
                    return t;
            }
            return string.Empty;
        }

        static string ParseResolution(string line)
        {
            var m = Regex.Match(line, @"(\d{2,4}x\d{2,4})");
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        static string ParseBitrate(string line)
        {
            var matches = Regex.Matches(line, @"(\d{1,5}k)\s+(\d{1,5}k)", RegexOptions.IgnoreCase);
            if (matches.Count > 0) return matches[^1].Groups[1].Value;
            var m2 = Regex.Match(line, @"(\d{1,5}k)\b", RegexOptions.IgnoreCase);
            return m2.Success ? m2.Groups[1].Value : string.Empty;
        }

        static string ParseSamplerate(string line)
        {
            var matches = Regex.Matches(line, @"(\d{1,5}k)\s+(\d{1,5}k)", RegexOptions.IgnoreCase);
            if (matches.Count > 0) return matches[^1].Groups[2].Value;
            var m2 = Regex.Match(line, @"(\d{4,5}Hz)", RegexOptions.IgnoreCase);
            return m2.Success ? m2.Groups[1].Value : string.Empty;
        }

        static bool IsAudioLine(string line)
        {
            var lower = line.ToLowerInvariant();
            if (lower.Contains("audio only")) return true;
            return Regex.IsMatch(line, @"\d+k") && !Regex.IsMatch(line, @"\d+x\d+");
        }
    }
}
