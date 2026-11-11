using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed partial class MainWindow
    {
        private const int InvoiceLimit = 2 * 1024 * 1024;
        private StoreSession storeSession;

        private async Task<object> RunStoreAction(string action, Dictionary<string, object> data)
        {
            if (action.StartsWith("management.", StringComparison.Ordinal))
                return await RunManagementAction(action.Substring(11), data);

            var value = data.TryGetValue("value", out var raw) ? raw as string : null;
            if (action == "connect")
            {
                if (value != "receiver" && value != "admin") throw new BridgeError(400, "invalid_role");
                var profile = ReadPilotProfile();
                StoreSession next;
                if (profile != null)
                {
                    if (profile.Store == null) throw new BridgeError(400, "pilot_store_required");
                    var token = await BrowserToken(profile, value == "admin");
                    next = token == null ? null : new StoreSession(profile.Api, profile.Store, value, token);
                }
                else
                {
                    using (var dialog = new StoreDialog(value, storeSession))
                        next = dialog.ShowDialog(this) == DialogResult.OK ? dialog.Session : null;
                }
                if (next == null) return null;
                next.Subject = ReadTokenSubject(next.Token);
                if (storeSession != null && storeSession.Scope != next.Scope)
                    throw new BridgeError(400, "identity_mismatch");
                var probe = value == "admin" ? "/stock/lots?offset=0" : "/receipts?offset=0";
                await SendRequest(next.Address, next.Prefix + probe, HttpMethod.Get, next.Token, null,
                    activeRole: next.Role);
                var roles = await SendRequest(next.Address,
                    "/v1/me/roles?storeId=" + Uri.EscapeDataString(next.StoreId), HttpMethod.Get, next.Token, null);
                storeSession = next;
                return new { storeId = next.StoreId, server = next.Address.Host, role = next.Role, roles };
            }
            if (action == "disconnect") { storeSession = null; return true; }
            var current = storeSession ?? throw new BridgeError(401, "stand_session_required");
            if (action == "load") return LoadJournal(current.Scope);
            if (action == "save") { Save(value, current.Scope); return true; }

            data.TryGetValue("body", out var body);
            var method = HttpMethod.Get;
            string route;
            switch (action)
            {
                case "receipts":
                    var status = body as string;
                    if (status != null && status != "" && status != "draft" && status != "submitted" &&
                        status != "posted" && status != "cancelled") throw new BridgeError(400, "invalid_receipt_filter");
                    route = "/receipts?offset=" + ParseOffset(value) + (string.IsNullOrEmpty(status) ? "" : "&status=" + status);
                    break;
                case "receipt": route = "/receipts/" + ParseGuid(value); break;
                case "create": route = "/receipts"; method = HttpMethod.Post; break;
                case "edit": route = "/receipts/" + ParseGuid(value); method = HttpMethod.Put; break;
                case "submit": case "cancel":
                    route = "/receipts/" + ParseGuid(value) + "/" + action; method = HttpMethod.Post; break;
                case "return": case "post":
                    current.RequireAdmin();
                    route = "/receipts/" + ParseGuid(value) + "/" + (action == "return" ? "return-to-draft" : "post");
                    method = HttpMethod.Post;
                    break;
                case "barcode":
                    if (!Regex.IsMatch(value ?? "", @"\A[0-9]{1,48}\z")) throw new BridgeError(400, "invalid_barcode");
                    route = "/receiving/catalog/barcodes/" + value;
                    break;
                case "suppliers": route = "/suppliers?activeOnly=true&offset=" + ParseOffset(value); break;
                case "orders": route = "/purchase-orders?openOnly=true&offset=" + ParseOffset(value); break;
                case "order": route = "/purchase-orders/" + ParseGuid(value); break;
                case "propose": route = "/receiving/catalog/proposals"; method = HttpMethod.Post; break;
                case "costs": case "saveCosts":
                    current.RequireAdmin();
                    route = "/receipts/" + ParseGuid(value) + "/costs";
                    if (action == "saveCosts") method = HttpMethod.Put;
                    break;
                case "files": route = "/receipts/" + ParseGuid(value) + "/files"; break;
                case "upload": case "scan": case "download": case "delete":
                    route = "/receipts/" + ParseGuid(value) + "/files/" + ParseGuid(body as string);
                    if (action == "upload") return await UploadInvoice(current, route);
                    if (action == "download")
                    {
                        current.RequireAdmin();
                        return await DownloadInvoice(current, route);
                    }
                    if (action == "scan") { route += "/scan"; method = HttpMethod.Post; }
                    if (action == "delete") method = HttpMethod.Delete;
                    body = null;
                    break;
                case "lots":
                    current.RequireAdmin();
                    route = "/stock/lots?offset=" + ParseOffset(value);
                    if (body is string product && product.Length > 0) route += "&productId=" + ParseGuid(product);
                    break;
                case "lotRetail": case "saveLotRetail": case "issuePriceLabel":
                    current.RequireAdmin();
                    var lotKey = (value ?? "").Split('|');
                    if (lotKey.Length != 2) throw new BridgeError(400, "invalid_guid");
                    route = "/stock/lots/" + ParseGuid(lotKey[0]) + "/" + ParseGuid(lotKey[1]);
                    route += action == "issuePriceLabel" ? "/weighed-price-labels" : "/retail";
                    if (action == "saveLotRetail") method = HttpMethod.Put;
                    if (action == "issuePriceLabel") method = HttpMethod.Post;
                    break;
                case "shelfTasks":
                    current.RequireAdmin();
                    route = "/stock/shelf-tasks?openOnly=true&offset=" + ParseOffset(value);
                    break;
                case "resolveShelf":
                    current.RequireAdmin();
                    route = "/stock/shelf-tasks/" + ParseGuid(value) + "/resolve"; method = HttpMethod.Post; break;
                case "stockOperation":
                    current.RequireAdmin();
                    route = "/stock/operations"; method = HttpMethod.Post; break;
                case "inventoryList":
                    current.RequireAdmin();
                    route = "/stock/inventories?offset=" + ParseOffset(value);
                    break;
                case "inventory":
                    current.RequireAdmin();
                    route = "/stock/inventories/" + ParseGuid(value); break;
                case "inventoryCreate":
                    current.RequireAdmin();
                    route = "/stock/inventories"; method = HttpMethod.Post; break;
                case "inventoryCount": case "inventoryPost":
                    current.RequireAdmin();
                    route = "/stock/inventories/" + ParseGuid(value) + (action == "inventoryCount" ? "/count" : "/post");
                    method = HttpMethod.Post;
                    break;
                default: throw new BridgeError(400, "unsupported_action");
            }
            return await SendRequest(current.Address, current.Prefix + route, method, current.Token,
                method == HttpMethod.Get ? null : body, activeRole: current.Role);
        }

        private static string InvoiceType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                default: throw new BridgeError(415, "invalid_invoice_media_type");
            }
        }

        private async Task<object> UploadInvoice(StoreSession current, string route)
        {
            using (var dialog = new OpenFileDialog { Filter = "Накладные PDF, JPEG, PNG|*.pdf;*.jpg;*.jpeg;*.png",
                CheckFileExists = true, Multiselect = false, RestoreDirectory = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                var name = Path.GetFileName(dialog.FileName);
                if (name.Length > 120 || name.StartsWith(".", StringComparison.Ordinal))
                    throw new BridgeError(400, "invalid_invoice_file_name");
                var type = InvoiceType(name);
                using (var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, useAsync: true))
                {
                    if (file.Length < 1 || file.Length > InvoiceLimit) throw new BridgeError(413, "invoice_file_too_large");
                    var bytes = new byte[(int)file.Length];
                    for (int offset = 0; offset < bytes.Length;)
                    {
                        var count = await file.ReadAsync(bytes, offset, bytes.Length - offset);
                        if (count == 0) throw new BridgeError(400, "invoice_file_changed");
                        offset += count;
                    }
                    using (var content = new ByteArrayContent(bytes))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue(type);
                        return await SendRequest(current.Address, current.Prefix + route, HttpMethod.Put, current.Token,
                            null, content: content, fileName: name, activeRole: current.Role);
                    }
                }
            }
        }

        private async Task<object> DownloadInvoice(StoreSession current, string route)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(current.Address, current.Prefix + route)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
                request.Headers.Add("X-Active-Role", current.Role);
                try
                {
                    using (var response = await http.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw ReadHttpError(response, await response.Content.ReadAsStringAsync());
                        string ext;
                        switch (response.Content.Headers.ContentType?.MediaType)
                        {
                            case "application/pdf": ext = "pdf"; break;
                            case "image/jpeg": ext = "jpg"; break;
                            case "image/png": ext = "png"; break;
                            default: throw new BridgeError(415, "invalid_invoice_media_type");
                        }
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        if (bytes.Length == 0 || bytes.Length > InvoiceLimit) throw new BridgeError(413, "invoice_file_too_large");
                        using (var dialog = new SaveFileDialog { FileName = "Nakladnaya." + ext, DefaultExt = ext,
                            Filter = ext.ToUpperInvariant() + "|*." + ext, OverwritePrompt = true, RestoreDirectory = true })
                        {
                            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                            if (InvoiceType(dialog.FileName) != response.Content.Headers.ContentType.MediaType)
                                throw new BridgeError(415, "invalid_invoice_media_type");
                            using (var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None,
                                4096, useAsync: true))
                                await file.WriteAsync(bytes, 0, bytes.Length);
                            return true;
                        }
                    }
                }
                catch (TaskCanceledException) { throw new BridgeError(0, "timeout"); }
                catch (HttpRequestException) { throw new BridgeError(0, "connection_failed"); }
            }
        }
    }

    internal sealed class StoreSession
    {
        internal Uri Address { get; }
        internal string StoreId { get; }
        internal string Role { get; }
        internal string Token { get; }
        internal string Subject { get; set; }
        internal string Prefix => "/v1/stores/" + StoreId;
        internal string Scope => "store|" + Address.AbsoluteUri + "|" + StoreId + "|" + Role + "|" + Subject;

        internal StoreSession(Uri address, string storeId, string role, string token)
        {
            Address = address;
            StoreId = storeId;
            Role = role;
            Token = token;
        }

        internal void RequireAdmin()
        {
            if (Role != "admin") throw new BridgeError(403, "store_admin_required");
        }
    }

    internal sealed class StoreDialog : Form
    {
        internal StoreSession Session { get; private set; }

        internal StoreDialog(string role, StoreSession previous)
        {
            Text = role == "admin" ? "Учебный стенд · Администратор" : "Учебный стенд · Приёмщик";
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(525, 280);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(16) };
            Controls.Add(panel);
            panel.Controls.Add(new Label { AutoSize = true, Text = "Используйте данные отдельного учебного стенда." });
            TextBox Field(string label, string initial, bool secret = false)
            {
                panel.Controls.Add(new Label { Text = label, AutoSize = true });
                var box = new TextBox { Text = initial, UseSystemPasswordChar = secret, MaxLength = 12000, Dock = DockStyle.Top };
                panel.Controls.Add(box);
                return box;
            }
            var address = Field("HTTPS-адрес API", previous?.Address.AbsoluteUri ?? "");
            var store = Field("ID магазина (UUID)", previous?.StoreId ?? "");
            var token = Field(role == "admin" ? "Токен администратора с MFA" : "Токен приёмщика", "", true);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var confirm = new Button { AutoSize = true, Text = "Подключить" };
            var cancel = new Button { AutoSize = true, Text = "Отмена", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(confirm);
            buttons.Controls.Add(cancel);
            panel.Controls.Add(buttons);
            AcceptButton = confirm;
            CancelButton = cancel;
            confirm.Click += (_, __) =>
            {
                if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                    !Guid.TryParseExact(store.Text.Trim(), "D", out var id) || id == Guid.Empty ||
                    !StandDialog.ValidToken(token.Text.Trim()))
                {
                    MessageBox.Show(this, "Проверьте HTTPS-адрес, ID магазина и токен.", Text);
                    return;
                }
                Session = new StoreSession(uri, id.ToString(), role, token.Text.Trim());
                token.Clear();
                DialogResult = DialogResult.OK;
            };
        }
    }
}
