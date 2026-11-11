using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed partial class MainWindow
    {
        private bool uzbek;
        private string T(string russian, string uzbekText) => uzbek ? uzbekText : russian;

        private PilotProfile ReadPilotProfile()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KasaPilot.json");
            if (!File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > 16384) throw new FormatException();
                return PilotProfile.Parse(json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)));
            }
            catch { throw new BridgeError(400, "pilot_profile_invalid"); }
        }

        private async Task<StandSession> BrowserCashier(PilotProfile profile)
        {
            if (profile.Register == null) throw new BridgeError(400, "pilot_register_required");
            var key = session != null && session.BaseAddress == profile.Api && session.Register == profile.Register
                ? session.Key : null;
            if (key == null)
            {
                using (var dialog = new Form { Text = T("Подключение кассы", "Kassani ulash"), ClientSize = new Size(480, 145),
                    FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                    MinimizeBox = false, MaximizeBox = false, AutoScaleMode = AutoScaleMode.Font })
                {
                    var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12),
                        FlowDirection = FlowDirection.TopDown, WrapContents = false };
                    var input = new TextBox { Width = 430, MaxLength = 512, UseSystemPasswordChar = true,
                        AccessibleName = T("Ключ кассы", "Kassa kaliti") };
                    var accept = new Button { Text = T("Открыть браузер", "Brauzerni ochish"), AutoSize = true, DialogResult = DialogResult.OK };
                    panel.Controls.Add(new Label { Text = T("Введите ключ кассы", "Kassa kalitini kiriting"), AutoSize = true });
                    panel.Controls.Add(input);
                    panel.Controls.Add(accept);
                    dialog.Controls.Add(panel);
                    dialog.AcceptButton = accept;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                    key = input.Text.Trim();
                    input.Clear();
                }
                foreach (var c in key) if (c < '!' || c > '~') throw new BridgeError(400, "pilot_register_key_invalid");
                if (key.Length == 0) throw new BridgeError(400, "pilot_register_key_invalid");
            }
            var token = await BrowserToken(profile, false);
            return token == null ? null : new StandSession(profile.Api, profile.Register, key, token);
        }

        private async Task<string> BrowserToken(PilotProfile profile, bool admin)
        {
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(110)))
            using (var progress = new Form { Text = T("Вход в браузере", "Brauzer orqali kirish"), ClientSize = new Size(490, 125),
                FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, AutoScaleMode = AutoScaleMode.Font })
            {
                var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12),
                    FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var stop = new Button { Text = T("Отмена", "Bekor qilish"), AutoSize = true };
                var userCanceled = false;
                var completed = false;
                stop.Click += (_, __) => { userCanceled = true; cancel.Cancel(); };
                progress.FormClosing += (_, e) => {
                    if (completed) return;
                    e.Cancel = true;
                    userCanceled = true;
                    cancel.Cancel();
                };
                panel.Controls.Add(new Label { Text = T("Завершите вход в системном браузере.", "Tizim brauzerida kirishni yakunlang."), AutoSize = true });
                panel.Controls.Add(stop);
                progress.Controls.Add(panel);
                progress.Show(this);
                try
                {
                    var oauth = new DesktopAuthorization(http, raw => json.Deserialize<Dictionary<string, object>>(raw));
                    var token = await oauth.Authorize(profile, admin, url => {
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { throw new BridgeError(0, "browser_open_failed"); }
                    }, cancel.Token, uzbek);
                    return token;
                }
                catch (OperationCanceledException) when (userCanceled) { return null; }
                catch (OperationCanceledException) { throw new BridgeError(0, "browser_login_timeout"); }
                catch (HttpRequestException) { throw new BridgeError(0, "identity_connection_failed"); }
                finally { completed = true; progress.Close(); }
            }
        }
    }

}
