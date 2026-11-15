using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed class PlatformWindow : Form
    {
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 1024 * 1024 };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
        private readonly TextBox address = new TextBox { Dock = DockStyle.Fill, Text = "http://127.0.0.1:3510/" };
        private readonly TextBox login = new TextBox { Dock = DockStyle.Fill, Text = "developer-test", MaxLength = 256 };
        private readonly TextBox password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 256 };
        private readonly Button enter = new Button { AutoSize = true, Text = "Тестовый вход разработчика", Visible = false };
        private readonly Label state = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly ListBox stores = new ListBox { Dock = DockStyle.Fill, DisplayMember = "Display" };
        private readonly TextBox storeName = new TextBox { Dock = DockStyle.Fill, MaxLength = 120 };
        private readonly Button create = new Button { AutoSize = true, Text = "Создать тестовый магазин", Enabled = false };
        private readonly Button rename = new Button { AutoSize = true, Text = "Переименовать выбранный", Enabled = false };
        private string token;

        internal PlatformWindow(Uri initial)
        {
            Text = "Касса · Платформа · Локальный тест";
            Font = new Font("Segoe UI", 10);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(720, 520);
            ClientSize = new Size(820, 590);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(18) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            address.Text = initial.AbsoluteUri;
            Add(layout, 0, "Локальный API", address);
            var check = new Button { AutoSize = true, Text = "Проверить Development-режим" };
            layout.Controls.Add(check, 1, 1);
            Add(layout, 2, "Логин", login);
            Add(layout, 3, "Пароль", password);
            layout.Controls.Add(enter, 1, 4);
            layout.Controls.Add(new Label { AutoSize = true, Text = "Тестовые магазины" }, 0, 5);
            layout.Controls.Add(stores, 1, 5);
            Add(layout, 6, "Название", storeName);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            actions.Controls.Add(create);
            actions.Controls.Add(rename);
            layout.Controls.Add(actions, 1, 7);
            layout.Controls.Add(state, 1, 8);
            check.Click += async (_, __) => await Probe();
            enter.Click += async (_, __) => await Login();
            create.Click += async (_, __) => await CreateStore();
            rename.Click += async (_, __) => await RenameStore();
            Shown += async (_, __) => await Probe();
            FormClosed += (_, __) => { token = null; password.Clear(); http.Dispose(); };
        }

        private static void Add(TableLayoutPanel layout, int row, string label, Control control)
        {
            layout.Controls.Add(new Label { AutoSize = true, Text = label, Margin = new Padding(3, 7, 3, 3) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private bool TryAddress(out Uri uri)
        {
            if (Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                uri.Host == IPAddress.Loopback.ToString() && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                uri.Query.Length == 0 && uri.AbsolutePath == "/") return true;
            MessageBox.Show(this, "Разрешён только адрес вида http://127.0.0.1:порт/.", Text);
            return false;
        }

        private async Task Probe()
        {
            enter.Visible = false;
            token = null;
            create.Enabled = rename.Enabled = false;
            stores.Items.Clear();
            if (!TryAddress(out var api)) return;
            state.Text = "Проверка локального сервера…";
            try
            {
                using (var response = await http.GetAsync(new Uri(api, "development/platform/status")))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var value = response.IsSuccessStatusCode ? json.Deserialize<Dictionary<string, object>>(body) : null;
                    if (value == null || !Equals(value["enabled"], true) || !Equals(value["environment"], "Development"))
                        throw new InvalidOperationException();
                }
                enter.Visible = true;
                state.Text = "Development подтверждён; API доступен только через 127.0.0.1.";
            }
            catch
            {
                state.Text = "Локальный Development-режим не подтверждён.";
            }
        }

        private async Task Login()
        {
            if (!TryAddress(out var api)) return;
            if (login.Text.Trim().Length == 0 || password.Text.Length == 0)
            {
                MessageBox.Show(this, "Введите логин и пароль.", Text);
                return;
            }
            try
            {
                var body = json.Serialize(new { login = login.Text.Trim(), password = password.Text });
                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                using (var response = await http.PostAsync(new Uri(api, "development/platform/login"), content))
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Вход отклонён: HTTP " + (int)response.StatusCode);
                    var value = json.Deserialize<Dictionary<string, object>>(responseBody);
                    token = value?["accessToken"] as string;
                    if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Сервер не вернул токен.");
                }
                password.Clear();
                create.Enabled = rename.Enabled = true;
                state.Text = "Вход выполнен. Открыт раздел «Платформа».";
                await RefreshStores(api);
            }
            catch (Exception error)
            {
                token = null;
                password.Clear();
                MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task RefreshStores(Uri api)
        {
            var items = await Send(api, HttpMethod.Get, "v1/platform/test-stores", null) as object[] ?? Array.Empty<object>();
            stores.Items.Clear();
            foreach (var raw in items.OfType<Dictionary<string, object>>())
                stores.Items.Add(new StoreItem((string)raw["storeId"], (string)raw["name"]));
        }

        private async Task CreateStore()
        {
            if (!TryAddress(out var api) || string.IsNullOrWhiteSpace(storeName.Text)) return;
            try
            {
                await Send(api, HttpMethod.Post, "v1/platform/test-stores", new { name = storeName.Text.Trim() });
                storeName.Clear();
                await RefreshStores(api);
                state.Text = "Тестовый магазин создан.";
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, Text); }
        }

        private async Task RenameStore()
        {
            if (!TryAddress(out var api) || !(stores.SelectedItem is StoreItem selected) || string.IsNullOrWhiteSpace(storeName.Text)) return;
            try
            {
                await Send(api, new HttpMethod("PATCH"), "v1/platform/test-stores/" + selected.Id,
                    new { name = storeName.Text.Trim() });
                storeName.Clear();
                await RefreshStores(api);
                state.Text = "Тестовый магазин переименован.";
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, Text); }
        }

        private async Task<object> Send(Uri api, HttpMethod method, string route, object value)
        {
            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Сначала выполните вход.");
            using (var request = new HttpRequestMessage(method, new Uri(api, route)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (value != null) request.Content = new StringContent(json.Serialize(value), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request))
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Операция отклонена: HTTP " + (int)response.StatusCode);
                    return json.DeserializeObject(body);
                }
            }
        }

        private sealed class StoreItem
        {
            internal string Id { get; }
            internal string Display { get; }
            internal StoreItem(string id, string name) { Id = id; Display = name + " · " + id; }
        }
    }
}
