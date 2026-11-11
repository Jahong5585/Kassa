using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed class StandSession
    {
        internal Uri BaseAddress { get; }
        internal string Register { get; }
        internal string Key { get; }
        internal string Token { get; }
        internal string Scope { get; private set; }

        internal void SetSubject(string subject) => Scope = BaseAddress.AbsoluteUri + "|" + Register + "|" + subject;

        internal StandSession(Uri baseAddress, string register, string key, string token)
        {
            BaseAddress = baseAddress;
            Register = register;
            Key = key;
            Token = token;
        }
    }

    internal sealed class StandDialog : Form
    {
        internal StandSession Connection { get; private set; }

        internal static bool ValidToken(string token) => Regex.IsMatch(token, @"\A[A-Za-z0-9._~+/-]+={0,2}\z");

        internal StandDialog(StandSession current)
        {
            Text = "Подключение учебного стенда";
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(510, 335);
            MaximizeBox = MinimizeBox = false;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, Padding = new Padding(16)
            };
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(470, 0),
                Text = "Вводите данные отдельного тестового сервера. Ключ и токен действуют только в текущем сеансе."
            });
            TextBox Field(string label, string initial = "", bool secret = false)
            {
                layout.Controls.Add(new Label { AutoSize = true, Text = label });
                var box = new TextBox { Dock = DockStyle.Top, Text = initial, UseSystemPasswordChar = secret, MaxLength = 12000 };
                layout.Controls.Add(box);
                return box;
            }

            var address = Field("HTTPS-адрес учебного API", current?.BaseAddress.AbsoluteUri ?? "");
            var register = Field("ID зарегистрированной кассы", current?.Register ?? "");
            var key = Field("Ключ кассы", "", true);
            var token = Field("Тестовый токен доступа", "", true);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var confirm = new Button { AutoSize = true, Text = "Подключить" };
            var cancel = new Button { AutoSize = true, Text = "Отмена", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(confirm);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            AcceptButton = confirm;
            CancelButton = cancel;
            confirm.Click += (_, __) =>
            {
                if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                    !Guid.TryParseExact(register.Text.Trim(), "D", out var id) || id == Guid.Empty ||
                    !Regex.IsMatch(key.Text.Trim(), @"\A[!-~]{1,512}\z") ||
                    !ValidToken(token.Text.Trim()))
                {
                    MessageBox.Show(this, "Проверьте HTTPS-адрес без пути, ID кассы, ключ и токен.", Text);
                    return;
                }
                Connection = new StandSession(uri, id.ToString(), key.Text.Trim(), token.Text.Trim());
                DialogResult = DialogResult.OK;
                key.Clear();
                token.Clear();
            };
        }
    }
    internal sealed class ApprovalDialog : Form
    {
        internal string Store { get; private set; }
        internal string Token { get; private set; }

        internal ApprovalDialog(Uri server, string operationId, string saleId, decimal amount, string reason)
        {
            Text = "Подтверждение возврата администратором";
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(570, 360);
            MaximizeBox = MinimizeBox = false;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1, RowCount = 8, Padding = new Padding(16)
            };
            Controls.Add(layout);
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(515, 0),
                Text = $"Стенд {server.Host}\r\nК выдаче {amount:N2} сум\r\nВозврат {operationId}\r\nЧек {saleId}"
            });
            layout.Controls.Add(new TextBox
            {
                Text = reason, ReadOnly = true, Multiline = true, Height = 46, Dock = DockStyle.Top,
                AccessibleName = "Причина возврата"
            });
            layout.Controls.Add(new Label { Text = "ID магазина (UUID)", AutoSize = true });
            var store = new TextBox { MaxLength = 36, Dock = DockStyle.Top };
            layout.Controls.Add(store);
            layout.Controls.Add(new Label { Text = "Токен администратора учебного стенда", AutoSize = true });
            var token = new TextBox { UseSystemPasswordChar = true, MaxLength = 12000, Dock = DockStyle.Top };
            layout.Controls.Add(token);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var confirm = new Button { AutoSize = true, Text = "Подтвердить" };
            var cancel = new Button { AutoSize = true, Text = "Отмена", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(confirm);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons);
            AcceptButton = confirm;
            CancelButton = cancel;
            confirm.Click += (_, __) =>
            {
                if (!Guid.TryParseExact(store.Text.Trim(), "D", out var id) || id == Guid.Empty ||
                    !StandDialog.ValidToken(token.Text.Trim()))
                {
                    MessageBox.Show(this, "Проверьте ID магазина и токен администратора.", Text);
                    return;
                }
                Store = id.ToString();
                Token = token.Text.Trim();
                token.Clear();
                DialogResult = DialogResult.OK;
            };
        }
    }
}
