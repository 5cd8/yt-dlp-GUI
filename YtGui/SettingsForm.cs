using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace YtGui
{
    public class SettingsForm : Form
    {
        TextBox tbYtDlp = new TextBox() { Width = 360 };
        Button btnBrowseYt = new Button() { Text = "参照" };
        TextBox tbFfmpeg = new TextBox() { Width = 360 };
        Button btnBrowseFf = new Button() { Text = "参照" };
        TextBox tbCookie = new TextBox() { Width = 360 };
        Button btnBrowseCookie = new Button() { Text = "参照" };
        TextBox tbOutputDir = new TextBox() { Width = 360 };
        Button btnBrowseOutput = new Button() { Text = "参照" };
        TextBox tbTwitchChatTool = new TextBox() { Width = 360 };
        Button btnBrowseTwitchChatTool = new Button() { Text = "参照" };
        CheckBox chkNoPart = new CheckBox() { Text = "ダウンロード中の .part を使わない (--no-part)", AutoSize = true };
        NumericUpDown nudRetry = new NumericUpDown() { Minimum = 1, Maximum = 20 };
        NumericUpDown nudDelay = new NumericUpDown() { Minimum = 1, Maximum = 300 };
        Button btnOk = new Button() { Text = "OK", DialogResult = DialogResult.OK };
        Button btnCancel = new Button() { Text = "キャンセル", DialogResult = DialogResult.Cancel };

        Settings settings;

        public SettingsForm(Settings s)
        {
            settings = s;
            Text = "設定";
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(640, 0);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Padding = new Padding(8);

            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            layout.RowStyles.Clear();
            for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(new Label { Text = "yt-dlp.exe パス", AutoSize = true }, 0, 0);
            layout.Controls.Add(tbYtDlp, 1, 0);
            layout.Controls.Add(btnBrowseYt, 2, 0);

            layout.Controls.Add(new Label { Text = "ffmpeg パス", AutoSize = true }, 0, 1);
            layout.Controls.Add(tbFfmpeg, 1, 1);
            layout.Controls.Add(btnBrowseFf, 2, 1);

            layout.Controls.Add(new Label { Text = "デフォルト Cookie (cookies.txt)", AutoSize = true }, 0, 2);
            layout.Controls.Add(tbCookie, 1, 2);
            layout.Controls.Add(btnBrowseCookie, 2, 2);

            layout.Controls.Add(new Label { Text = "出力フォルダ (保存先)", AutoSize = true }, 0, 3);
            layout.Controls.Add(tbOutputDir, 1, 3);
            layout.Controls.Add(btnBrowseOutput, 2, 3);

            layout.Controls.Add(new Label { Text = "Twitchチャット取得ツール パス (TwitchDownloaderCLI.exe)", AutoSize = true }, 0, 4);
            layout.Controls.Add(tbTwitchChatTool, 1, 4);
            layout.Controls.Add(btnBrowseTwitchChatTool, 2, 4);

            layout.Controls.Add(new Label { Text = "リトライ回数", AutoSize = true }, 0, 5);
            layout.Controls.Add(nudRetry, 1, 5);

            layout.Controls.Add(new Label { Text = "リトライ間隔(秒)", AutoSize = true }, 0, 6);
            layout.Controls.Add(nudDelay, 1, 6);

            layout.Controls.Add(chkNoPart, 1, 7);

            var pnlButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            pnlButtons.Controls.Add(btnOk);
            pnlButtons.Controls.Add(btnCancel);
            layout.Controls.Add(pnlButtons, 1, 8);

            Controls.Add(layout);

            btnBrowseYt.Click += (s, e) => { using var ofd = new OpenFileDialog(); ofd.Filter = "Executables (*.exe)|*.exe|All files|*.*"; if (ofd.ShowDialog() == DialogResult.OK) tbYtDlp.Text = ofd.FileName; };
            btnBrowseFf.Click += (s, e) => { using var ofd = new OpenFileDialog(); ofd.Filter = "Executables (*.exe)|*.exe|All files|*.*"; if (ofd.ShowDialog() == DialogResult.OK) tbFfmpeg.Text = ofd.FileName; };
            btnBrowseCookie.Click += (s, e) => { using var ofd = new OpenFileDialog(); ofd.Filter = "Cookies (cookies.txt)|cookies.txt|All files|*.*"; if (ofd.ShowDialog() == DialogResult.OK) tbCookie.Text = ofd.FileName; };
            btnBrowseOutput.Click += (s, e) => { using var fbd = new FolderBrowserDialog(); if (fbd.ShowDialog() == DialogResult.OK) tbOutputDir.Text = fbd.SelectedPath; };
            btnBrowseTwitchChatTool.Click += (s, e) => { using var ofd = new OpenFileDialog(); ofd.Filter = "Executables (*.exe)|*.exe|All files|*.*"; if (ofd.ShowDialog() == DialogResult.OK) tbTwitchChatTool.Text = ofd.FileName; };

            btnOk.Click += BtnOk_Click;

            // load values
            tbYtDlp.Text = settings.YtDlpPath;
            tbFfmpeg.Text = settings.FfmpegPath;
            tbCookie.Text = settings.DefaultCookiePath;
            tbOutputDir.Text = settings.OutputDirectory;
            tbTwitchChatTool.Text = settings.TwitchChatToolPath;
            chkNoPart.Checked = settings.UseNoPart;
            nudRetry.Value = Math.Max(nudRetry.Minimum, Math.Min(nudRetry.Maximum, settings.RetryCount));
            nudDelay.Value = Math.Max(nudDelay.Minimum, Math.Min(nudDelay.Maximum, settings.RetryDelaySeconds));
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            settings.YtDlpPath = tbYtDlp.Text.Trim();
            settings.FfmpegPath = tbFfmpeg.Text.Trim();
            settings.DefaultCookiePath = tbCookie.Text.Trim();
            settings.OutputDirectory = tbOutputDir.Text.Trim();
            settings.TwitchChatToolPath = tbTwitchChatTool.Text.Trim();
            settings.UseNoPart = chkNoPart.Checked;
            settings.RetryCount = (int)nudRetry.Value;
            settings.RetryDelaySeconds = (int)nudDelay.Value;
            try { settings.Save(); }
            catch (Exception ex)
            {
                MessageBox.Show("設定を保存できませんでした: " + ex.Message, "設定", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.None;
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
