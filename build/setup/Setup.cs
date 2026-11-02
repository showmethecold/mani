// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Mani.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--trust-certificate")
            {
                // The only elevated operation: import the embedded public signing certificate.
                // No paths, package commands, language strings or arbitrary operations are accepted.
                try
                {
                    using (var identity = WindowsIdentity.GetCurrent())
                        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
                    SetupCore.TrustEmbeddedCertificate(); return 0;
                }
                catch { return 1; }
            }
            if (args.Length == 2 && args[0] == "--verify-payload") return VerifyPayload(args[1]);
            if (args.Length != 0) return 2;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupWindow());
            return 0;
        }

        private static int VerifyPayload(string reportPath)
        {
            string staging = null;
            try
            {
                // Read-only diagnostic: never requests elevation, imports trust or installs a package.
                using (var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(report))
                using (var certificate = SetupCore.Certificate())
                {
                    staging = SetupCore.CreateStagingDirectory();
                    string package = SetupCore.ExtractPackage(staging, CancellationToken.None, delegate { });
                    var validation = SetupCore.Deploy("validate", package);
                    var current = SetupCore.Deploy("inspect", null);
                    writer.Write(new JavaScriptSerializer().Serialize(new
                    {
                        valid = true, packageVersion = BuildInfo.PackageVersion, packageSha256 = BuildInfo.PackageSha256,
                        certificateThumbprint = certificate.Thumbprint, signature = validation.message,
                        supportedPlatform = SetupCore.SupportedPlatform(), current = current,
                        installed = false, certificateImported = false
                    }));
                }
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            finally { if (staging != null) SetupCore.Cleanup(staging); }
        }
    }

    internal sealed class SetupWindow : Form
    {
        private readonly ComboBox language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Right };
        private readonly Label title = new Label { AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 20, FontStyle.Bold) };
        private readonly Label intro = LabelBlock();
        private readonly Label status = LabelBlock();
        private readonly Label trustNote = LabelBlock();
        private readonly CheckBox trust = new CheckBox { AutoSize = false, Height = 54, Dock = DockStyle.Fill, CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Height = 18 };
        private readonly TextBox details = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 85, Visible = false };
        private readonly Button install = new Button { AutoSize = true, MinimumSize = new Size(145, 38) };
        private readonly Button cancel = new Button { AutoSize = true, MinimumSize = new Size(105, 38) };
        private readonly LinkLabel certificateLink = new LinkLabel { AutoSize = true, Dock = DockStyle.Fill };
        private string stage = "checking", family = "";
        private bool chinese, busy, needsTrust, complete, canCancel;
        private CancellationTokenSource cancellation;

        private static Label LabelBlock() { return new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 0, 9) }; }
        private string L(string en, string zh) { return chinese ? zh : en; }

        internal SetupWindow()
        {
            Text = "Mani Setup";
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(640, 530);
            MinimumSize = new Size(640, 530);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 246, 252);
            Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25, 25, 25, 8), ColumnCount = 1, RowCount = 8, AutoScroll = true };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 16) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(title, 0, 0); header.Controls.Add(language, 1, 0);
            language.Items.AddRange(new object[] { "English", "简体中文" }); language.SelectedIndex = 0;
            language.AccessibleName = "Language / 语言";
            language.SelectedIndexChanged += delegate { chinese = language.SelectedIndex == 1; Translate(); };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(25, 8, 25, 18), Margin = Padding.Empty };
            buttons.Controls.Add(cancel); buttons.Controls.Add(install);
            layout.Controls.Add(header); layout.Controls.Add(intro); layout.Controls.Add(trustNote); layout.Controls.Add(trust);
            layout.Controls.Add(certificateLink); layout.Controls.Add(status); layout.Controls.Add(progress); layout.Controls.Add(details);
            var frame = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Margin = Padding.Empty };
            frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); frame.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            frame.Controls.Add(layout, 0, 0); frame.Controls.Add(buttons, 0, 1);
            Controls.Add(frame);
            trust.CheckedChanged += delegate { UpdateButtons(); };
            install.Click += async delegate
            {
                if (complete) { Launch(); return; }
                await InstallAsync();
            };
            cancel.Click += delegate { if (busy && canCancel) cancellation.Cancel(); else if (!busy) Close(); };
            certificateLink.LinkClicked += delegate
            {
                try
                {
                    using (var cert = SetupCore.Certificate())
                        MessageBox.Show(this, cert.Subject + "\r\n\r\n" + L("Expires: ", "有效期至：") + cert.NotAfter.ToString("yyyy-MM-dd") +
                            "\r\n\r\nSHA-1: " + cert.Thumbprint + "\r\nSHA-256: " + BuildInfo.CertificateSha256, L("Signing certificate", "签名证书"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception error) { ShowError(error); }
            };
            Shown += async delegate { await PrepareAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; if (canCancel) cancellation.Cancel(); }
            };
            Translate();
        }

        private void Translate()
        {
            Text = L("Mani Setup", "Mani 安装程序");
            title.Text = L("Install Mani", "安装 Mani") + "\r\n" + BuildInfo.DisplayVersion;
            intro.Text = L("Windows 11 24H2 or later · x64\r\nInstall for this Windows account. Your existing Mani Beta Test settings are kept when upgrading.",
                "Windows 11 24H2 及以上 · x64\r\n安装到当前 Windows 账户。升级时保留已有 Mani Beta Test 设置。");
            trustNote.Text = L("This beta uses a test signing certificate. Administrator approval is needed to trust it on this computer. Windows will then trust packages signed by this certificate.",
                "此 Beta 使用测试签名证书。信任证书需要管理员批准；此后 Windows 会信任由该证书签名的软件包。");
            trust.Text = L("I agree to trust the Mani beta signing certificate on this computer.", "我同意在这台电脑上信任 Mani 的 Beta 签名证书。");
            certificateLink.Text = L("Certificate details", "查看证书详情");
            trustNote.Visible = needsTrust; trust.Visible = needsTrust; certificateLink.Visible = needsTrust;
            switch (stage)
            {
                case "checking": status.Text = L("Checking this computer…", "正在检查此电脑……"); break;
                case "ready": status.Text = L("Ready to install. Close Mani Beta Test before continuing.", "准备就绪。请先关闭 Mani Beta Test，再开始安装。"); break;
                case "extracting": status.Text = L("Preparing and checking the installer…", "正在准备并校验安装文件……"); break;
                case "verifying": status.Text = L("Checking the package signature and identity…", "正在校验软件包签名和身份……"); break;
                case "trusting": status.Text = L("Waiting for Windows administrator approval…", "正在等待 Windows 管理员批准……"); break;
                case "installing": status.Text = L("Windows is installing Mani. Please keep this window open…", "Windows 正在安装 Mani，请保持此窗口打开……"); break;
                case "finished": status.Text = L("Mani is installed. Open it now, or find Mani Beta Test in Start. Uninstall through Windows Settings > Apps.", "Mani 已安装。可立即打开，或从开始菜单启动 Mani Beta Test。卸载入口为 Windows 设置 > 应用。"); break;
                case "newer": status.Text = L("A newer Mani Beta Test version is already installed. This installer will keep that version and its settings.", "已安装更新版本的 Mani Beta Test。此安装程序会保留该版本及其设置。"); break;
                case "canceled": status.Text = L("Canceled. You can try again when ready.", "已取消，可在准备就绪后重试。"); break;
                case "unsupported": status.Text = L("This installer requires Windows 11 24H2 (build 26100) or later on an x64 PC.", "此安装程序需要 Windows 11 24H2（内部版本 26100）或更新版本的 x64 电脑。"); break;
                default: status.Text = L("Installation could not finish. Check the details below, close Mani if it is running, and try again. Any certificate trust you approved is kept.", "安装未完成。请查看下方详情；若 Mani 正在运行，请关闭后重试。已批准的证书信任会保留。"); break;
            }
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            install.Text = complete ? L("Open Mani", "打开 Mani") : L("Install", "安装");
            install.Enabled = !busy && stage != "unsupported" && (complete || !needsTrust || trust.Checked);
            cancel.Text = L(busy && canCancel ? "Cancel" : "Close", busy && canCancel ? "取消" : "关闭");
            cancel.Enabled = !busy || canCancel;
            language.Enabled = !busy;
            trust.Enabled = !busy;
        }

        private void SetStage(string value, bool cancellable)
        {
            stage = value; canCancel = cancellable;
            progress.Style = value == "extracting" ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            progress.Visible = busy;
            Translate();
        }

        private async Task PrepareAsync()
        {
            busy = true; SetStage("checking", false);
            try
            {
                if (!SetupCore.SupportedPlatform()) { stage = "unsupported"; return; }
                var current = await Task.Run(() => SetupCore.Deploy("inspect", null));
                if (current.state == "installed" || current.state == "newer") { Finish(current); return; }
                needsTrust = !await Task.Run(() => SetupCore.CertificateTrusted());
                stage = "ready";
            }
            catch (Exception error) { ShowError(error); }
            finally { busy = false; progress.Visible = false; Translate(); }
        }

        private async Task InstallAsync()
        {
            if (needsTrust && !trust.Checked) return;
            busy = true; details.Visible = false; cancellation = new CancellationTokenSource();
            string staging = null;
            try
            {
                SetStage("checking", false);
                var current = await Task.Run(() => SetupCore.Deploy("inspect", null));
                if (current.state == "installed" || current.state == "newer") { Finish(current); return; }
                staging = SetupCore.CreateStagingDirectory();
                SetStage("extracting", true); progress.Value = 0;
                string package = await Task.Run(() => SetupCore.ExtractPackage(staging, cancellation.Token,
                    percent => BeginInvoke(new Action(() => progress.Value = percent))));
                cancellation.Token.ThrowIfCancellationRequested();
                SetStage("verifying", false);
                await Task.Run(() => SetupCore.Deploy("validate", package));
                current = await Task.Run(() => SetupCore.Deploy("inspect", null));
                if (current.state == "installed" || current.state == "newer") { Finish(current); return; }
                if (!await Task.Run(() => SetupCore.CertificateTrusted()))
                {
                    // A missing trust decision is never inferred from elapsed time or a previous run.
                    if (!trust.Checked) { needsTrust = true; stage = "ready"; return; }
                    SetStage("trusting", false);
                    await Task.Run(() => SetupCore.RequestCertificateTrust());
                }
                needsTrust = false;
                SetStage("installing", false);
                Finish(await Task.Run(() => SetupCore.Deploy("install", package)));
            }
            catch (OperationCanceledException) { stage = "canceled"; }
            catch (Win32Exception error) { if (error.NativeErrorCode == 1223) stage = "canceled"; else ShowError(error); }
            catch (Exception error) { ShowError(error); }
            finally
            {
                if (staging != null)
                    try { SetupCore.Cleanup(staging); } catch (Exception error) { details.Text += "\r\n" + error.Message; details.Visible = true; }
                cancellation.Dispose(); cancellation = null;
                busy = false; canCancel = false; progress.Visible = false; Translate();
            }
        }

        private void Finish(DeploymentResult result)
        {
            complete = true; needsTrust = false; family = result.family;
            stage = result.state == "newer" ? "newer" : "finished";
        }

        private void ShowError(Exception error)
        {
            stage = "failed"; details.Text = error.Message; details.Visible = true;
        }

        private void Launch()
        {
            try
            {
                if (String.IsNullOrEmpty(family)) throw new InvalidOperationException(L("Use Start to open Mani Beta Test.", "请从开始菜单打开 Mani Beta Test。"));
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + family + "!" + BuildInfo.ApplicationId) { UseShellExecute = true });
                Close();
            }
            catch (Exception error) { ShowError(error); Translate(); }
        }
    }
}
