// Off-screen component checks; never shows a window, installs a package or imports trust.
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Mani.Setup;

internal static class SetupLayoutChecks
{
    private static void Set(object instance, string field, object value)
    {
        instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        CheckDefaultLanguage();
        int rendered = 0;
        foreach (bool chinese in new[] { false, true })
        foreach (string state in new[] { "ready", "failed", "finished" })
        {
            using (var form = new SetupWindow())
            {
                var language = (ComboBox)typeof(SetupWindow).GetField("language", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                language.SelectedIndex = chinese ? 1 : 0;
                Set(form, "stage", state);
                Set(form, "needsTrust", state != "finished");
                Set(form, "complete", state == "finished");
                typeof(SetupWindow).GetMethod("Translate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                if (state == "failed")
                {
                    var details = (TextBox)typeof(SetupWindow).GetField("details", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    details.Text = "0x80073D02\r\nClose Mani and try again."; details.Visible = true;
                }
                var content = form.Controls[0];
                form.Controls.Remove(content);
                using (content)
                {
                    content.Dock = DockStyle.None;
                    content.Size = form.ClientSize;
                    content.Visible = true;
                    content.CreateControl(); content.PerformLayout();
                    using (var bitmap = new Bitmap(content.Width, content.Height))
                    {
                        content.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        CheckBounds(content);
                        var button = (Button)typeof(SetupWindow).GetField("install", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                        var position = content.PointToClient(button.PointToScreen(Point.Empty));
                        if (position.Y < 0 || position.Y + button.Height > content.ClientSize.Height)
                            throw new Exception("Install button is outside the visible frame.");
                        var colors = new System.Collections.Generic.HashSet<int>();
                        for (int y = 0; y < bitmap.Height; y += 3)
                            for (int x = 0; x < bitmap.Width; x += 3) colors.Add(bitmap.GetPixel(x, y).ToArgb());
                        if (colors.Count < 20) throw new Exception("Empty UI render.");
                        bitmap.Save(Path.Combine(args[0], "setup-" + (chinese ? "zh" : "en") + "-" + state + ".png"));
                    }
                }
                var install = (Button)typeof(SetupWindow).GetField("install", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                if (state == "ready" && install.Enabled) throw new Exception("Fresh installation must require the certificate consent checkbox.");
                if (state == "finished" && !install.Enabled) throw new Exception("Completed installation must offer Open Mani.");
                rendered++;
            }
        }
        Console.WriteLine(rendered + " bilingual off-screen setup states rendered; consent and button states checked.");
        return 0;
    }

    private static void CheckDefaultLanguage()
    {
        var originalUi = Thread.CurrentThread.CurrentUICulture;
        var originalFormatting = Thread.CurrentThread.CurrentCulture;
        int checkedLocales = 0;
        try
        {
            foreach (var locale in new[] { "zh-CN", "zh-TW", "zh-HK", "zh-SG", "zh-Hans", "zh-Hant", "en-US", "ja-JP", "fr-FR", "" })
            {
                bool expectedChinese = locale.StartsWith("zh-", StringComparison.Ordinal);
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
                // Opposing regional settings must not determine the install language.
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(expectedChinese ? "en-US" : "zh-CN");
                using (var form = new SetupWindow())
                {
                    var language = (ComboBox)typeof(SetupWindow).GetField("language", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    var consent = (CheckBox)typeof(SetupWindow).GetField("trust", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    var install = (Button)typeof(SetupWindow).GetField("install", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    if (language.SelectedIndex != (expectedChinese ? 1 : 0) || form.Text != (expectedChinese ? "Mani 安装程序" : "Mani Setup"))
                        throw new Exception("Incorrect initial install language: " + locale);
                    Set(form, "stage", "ready"); Set(form, "needsTrust", true);
                    typeof(SetupWindow).GetMethod("Translate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
                    if (consent.Checked || install.Enabled) throw new Exception("Language selection must not grant certificate consent.");
                    language.SelectedIndex = expectedChinese ? 0 : 1;
                    if (form.Text != (expectedChinese ? "Mani Setup" : "Mani 安装程序") || consent.Checked || install.Enabled)
                        throw new Exception("Manual language override failed: " + locale);
                    consent.Checked = true;
                    language.SelectedIndex = expectedChinese ? 1 : 0;
                    if (!consent.Checked || !install.Enabled) throw new Exception("Switching language lost existing consent.");
                    if (Thread.CurrentThread.CurrentUICulture.Name != locale)
                        throw new Exception("Installer changed the user's UI culture.");
                }
                checkedLocales++;
            }
        }
        finally
        {
            Thread.CurrentThread.CurrentUICulture = originalUi;
            Thread.CurrentThread.CurrentCulture = originalFormatting;
        }
        Console.WriteLine(checkedLocales + " UI locales checked, including opposing regional formats, manual override and consent preservation.");
    }

    private static void CheckBounds(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            // Form.Visible is false in an off-screen test; inspect its laid-out controls directly.
            if (child.Width > parent.ClientSize.Width + 2 && child.Dock != DockStyle.Fill)
                throw new Exception("Control exceeds its container: " + child.GetType().Name);
            CheckBounds(child);
        }
    }
}
