using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KasaDesktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var mutex = new Mutex(true, @"Local\KasaDesktop.Stage1", out var first))
            {
                if (first) Application.Run(new MainWindow());
                else MessageBox.Show("Касса уже открыта.", "Касса минимаркета");
            }
        }
    }

    internal sealed partial class MainWindow : Form
    {
        private const string Host = "kasa-desktop.local";
        private const string Page = "https://" + Host + "/index.html";
        private readonly string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KasaDesktop");
        private readonly WebView2 view = new WebView2 { Dock = DockStyle.Fill };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly HttpClient http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxResponseHeadersLength = 32
        }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        private StandSession session;
        private bool busy;

        internal MainWindow()
        {
            Text = "Касса минимаркета · Пилот";
            MinimumSize = new Size(680, 440);
            ClientSize = new Size(1250, 705);
            WindowState = FormWindowState.Maximized;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(view);
            Shown += Initialize;
        }

        private async void Initialize(object sender, EventArgs args)
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDirectory, "WebView"),
                    new CoreWebView2EnvironmentOptions { Language = "ru-RU" });
                await view.EnsureCoreWebView2Async(env);
                var core = view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;
                core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Page;
                core.FrameNavigationStarting += (_, e) => e.Cancel = true;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
                core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"),
                    CoreWebView2HostResourceAccessKind.Deny);
                core.WebMessageReceived += Receive;
                view.Source = new Uri(Page);
            }
            catch
            {
                MessageBox.Show("Не удалось открыть окно кассы. Проверьте, что установлен Microsoft Edge WebView2 Runtime, и распакуйте ZIP целиком.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private async void Receive(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (args.Source != Page || args.WebMessageAsJson.Length > json.MaxJsonLength) return;
            int id = 0;
            bool ownsBusy = false;
            try
            {
                var message = json.Deserialize<Dictionary<string, object>>(args.WebMessageAsJson);
                id = (int)message["id"];
                if (busy) throw new BridgeError(429, "bridge_busy");
                busy = ownsBusy = true;
                var action = (string)message["action"];
                var data = message.TryGetValue("data", out var payload) ? payload as Dictionary<string, object> : null;
                Reply(id, new { data = await RunAction(action, data) });
            }
            catch (BridgeError e)
            {
                Reply(id, new { error = new { status = e.Status, code = e.Code, requestId = e.RequestId, row = e.Row } });
            }
            catch (Exception)
            {
                Reply(id, new { error = new { status = 0, code = "desktop_error" } });
            }
            finally { if (ownsBusy) busy = false; }
        }

        private void Reply(int id, object result)
        {
            if (!IsDisposed) view.CoreWebView2?.PostWebMessageAsJson(json.Serialize(new { id, result }));
        }

        private async Task<object> RunAction(string action, Dictionary<string, object> data)
        {
            if (action.StartsWith("store.", StringComparison.Ordinal))
                return await RunStoreAction(action.Substring(6), data);

            switch (action)
            {
                case "locale.change":
                    session = null;
                    storeSession = null;
                    goto case "locale";
                case "locale":
                    uzbek = data.TryGetValue("value", out var language) && Equals(language, "uz");
                    Text = T("Касса минимаркета · Пилот", "Minimarket kassasi · Sinov");
                    return true;
                case "connect":
                    var profile = ReadPilotProfile();
                    StandSession candidate;
                    if (profile != null) candidate = await BrowserCashier(profile);
                    else
                    {
                        using (var dialog = new StandDialog(session))
                            candidate = dialog.ShowDialog(this) == DialogResult.OK ? dialog.Connection : null;
                    }
                    if (candidate == null) return null;
                    var subject = ReadTokenSubject(candidate.Token);
                    candidate.SetSubject(subject);
                    if (session != null && session.Scope != candidate.Scope)
                        throw new BridgeError(400, "identity_mismatch");
                    await Request(candidate, "search", new Dictionary<string, object> { ["value"] = "KasaStandCheck" });
                    object roles = null;
                    if (profile?.Store != null)
                        roles = await SendRequest(candidate.BaseAddress,
                            "/v1/me/roles?storeId=" + Uri.EscapeDataString(profile.Store),
                            HttpMethod.Get, candidate.Token, null);
                    session = candidate;
                    return new { register = candidate.Register, server = candidate.BaseAddress.Host,
                        endpoint = candidate.BaseAddress.AbsoluteUri, roles };
                case "device.printers":
                    var defaults = new System.Drawing.Printing.PrinterSettings();
                    return new { defaultPrinter = defaults.IsValid ? defaults.PrinterName : null,
                        installed = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().ToArray() };
                case "device.printerDrivers":
                    return await Task.Run(() => ReadPrinterDrivers());
                case "device.printerSupport":
                case "device.printerTerms":
                case "device.installPrinterDriver":
                    return await OpenPrinterDriver(data?["value"] as string, action);
                case "disconnect":
                    session = null;
                    return true;
                case "load":
                    RequireSession();
                    return LoadJournal();
                case "save":
                    RequireSession();
                    Save((string)data["value"]);
                    return true;
                case "approveRefund":
                    RequireSession();
                    return await ApproveRefund(data);
                case "print":
                    view.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                    return true;
                default:
                    RequireSession();
                    return await Request(session, action, data);
            }
        }

        private void RequireSession()
        {
            if (session == null) throw new BridgeError(401, "stand_session_required");
        }

        private string StatePath(string scopeName)
        {
            var scope = Encoding.UTF8.GetBytes(scopeName);
            using (var sha = SHA256.Create())
                return Path.Combine(dataDirectory, BitConverter.ToString(sha.ComputeHash(scope)).Replace("-", "") + ".bin");
        }

        private string LoadJournal(string scope = null)
        {
            try
            {
                var path = StatePath(scope ?? session.Scope);
                if (!File.Exists(path)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
            }
            catch { throw new BridgeError(0, "local_storage_error"); }
        }

        private void Save(string value, string scope = null)
        {
            var path = StatePath(scope ?? session.Scope);
            var temporary = path + ".tmp";
            try
            {
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch { throw new BridgeError(0, "local_storage_error"); }
        }

        private async Task<object> ApproveRefund(Dictionary<string, object> data)
        {
            var approval = (Dictionary<string, object>)data["body"];
            var refund = (Dictionary<string, object>)approval["refund"];
            var operationId = ParseGuid(refund["operationId"] as string);
            var saleId = ParseGuid(refund["saleId"] as string);
            var reason = refund["reason"] as string;
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 ||
                !decimal.TryParse(Convert.ToString(approval["amount"], System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture,
                    out var amount) || amount < 0)
                throw new BridgeError(400, "invalid_refund_approval");

            var body = new { registerId = session.Register, amount, refund };
            var profile = ReadPilotProfile();
            if (profile != null)
            {
                if (profile.Store == null || profile.Api != session.BaseAddress)
                    throw new BridgeError(400, "pilot_store_required");
                var question = T("Открыть вход администратора для подтверждения возврата?", "Qaytarishni tasdiqlash uchun administrator kirishini ochamizmi?");
                if (MessageBox.Show(this, question + "\r\n" + operationId + " · " + amount.ToString("N2") + " UZS",
                    T("Подтверждение возврата", "Qaytarishni tasdiqlash"), MessageBoxButtons.YesNo) != DialogResult.Yes) return null;
                var token = await BrowserToken(profile, true);
                if (token == null) return null;
                return await SendRequest(session.BaseAddress, "/v1/stores/" + profile.Store + "/refund-approvals",
                    HttpMethod.Post, token, body, activeRole: "admin");
            }
            using (var dialog = new ApprovalDialog(session.BaseAddress, operationId, saleId, amount, reason))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                return await SendRequest(session.BaseAddress, "/v1/stores/" + dialog.Store + "/refund-approvals",
                    HttpMethod.Post, dialog.Token, body, activeRole: "admin");
            }
        }

        private static int ParseOffset(string value)
        {
            if (int.TryParse(value, out int offset) && offset >= 0 && offset <= 10000) return offset;
            throw new BridgeError(400, "invalid_offset");
        }

        private async Task<object> Request(StandSession credentials, string action, Dictionary<string, object> data)
        {
            var value = data != null && data.TryGetValue("value", out var raw) ? raw as string : null;
            var payload = data != null && data.TryGetValue("body", out var bodyValue) ? bodyValue : null;
            var method = HttpMethod.Get;
            string route;
            switch (action)
            {
                case "search": route = "/v1/catalog/search?q=" + Uri.EscapeDataString(value ?? ""); break;
                case "barcode":
                    if (!System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"\A(?:[0-9]{1,48}|(?i:(?:LOT|WPL)-[a-f0-9]{32}))\z"))
                        throw new BridgeError(400, "invalid_barcode");
                    route = "/v1/catalog/barcodes/" + value;
                    break;
                case "product": route = "/v1/catalog/products/" + ParseGuid(value); break;
                case "shift": route = "/v1/shifts/" + ParseGuid(value); break;
                case "quote": route = "/v1/shifts/" + ParseGuid(value) + "/quote"; method = HttpMethod.Post; break;
                case "parkedCarts":
                    route = "/v1/shifts/" + ParseGuid(value) + "/carts?offset=" + ParseOffset(payload as string);
                    break;
                case "cartReview":
                    route = "/v1/shifts/" + ParseGuid(value) + "/carts/" + ParseGuid(payload as string);
                    break;
                case "parkCart": case "cancelCart":
                    var cart = (Dictionary<string, object>)payload;
                    route = "/v1/shifts/" + ParseGuid(value) + "/carts/" + ParseGuid(cart["cartId"] as string);
                    if (action == "parkCart")
                    {
                        method = HttpMethod.Put;
                        payload = new { expectedVersion = cart["expectedVersion"], items = cart["items"] };
                    }
                    else
                    {
                        route += "/cancel";
                        method = HttpMethod.Post;
                        payload = new { expectedVersion = cart["expectedVersion"], reason = cart["reason"] };
                    }
                    break;
                case "sale": route = "/v1/sales/" + ParseGuid(value); break;
                case "history":
                    route = "/v1/sales/history?offset=" + ParseOffset(value);
                    if (data.TryGetValue("body", out var receiptValue) && receiptValue is string receipt && receipt.Length > 0) route += "&receiptId=" + ParseGuid(receipt);
                    break;
                case "refundOptions":
                    route = "/v1/sales/" + ParseGuid(data["body"] as string) + "/" + ParseGuid(value) + "/refund-options";
                    break;
                case "refund": route = "/v1/refunds/" + ParseGuid(value); break;
                case "createRefund": route = "/v1/refunds"; method = HttpMethod.Post; break;
                case "cashMovements":
                    route = "/v1/shifts/" + ParseGuid(value) + "/cash-movements?offset=" + ParseOffset(data["body"] as string);
                    break;
                case "createCash":
                    route = "/v1/shifts/" + ParseGuid(value) + "/cash-movements"; method = HttpMethod.Post;
                    break;
                case "expiry":
                    if (!new[] { "all", "today", "within3", "within7", "expired", "missing" }.Contains(value))
                        throw new BridgeError(400, "invalid_expiry_filter");
                    route = "/v1/expiry/cashier?filter=" + value + "&offset=" + ParseOffset(data["body"] as string);
                    break;
                case "shelf": route = "/v1/expiry/cashier/shelf-tasks"; method = HttpMethod.Post; break;
                case "open": route = "/v1/shifts"; method = HttpMethod.Post; break;
                case "close": route = "/v1/shifts/" + ParseGuid(value) + "/close"; method = HttpMethod.Post; break;
                case "commit":
                    var body = (Dictionary<string, object>)data["body"];
                    if (!Equals(body["method"], "cash")) throw new BridgeError(400, "cash_only");
                    route = "/v1/sales"; method = HttpMethod.Post; break;
                default: throw new BridgeError(400, "unsupported_action");
            }

            return await SendRequest(credentials.BaseAddress, route, method, credentials.Token,
                method != HttpMethod.Get ? payload : null, credentials, activeRole: "cashier");
        }

        private async Task<object> SendRequest(Uri address, string route, HttpMethod method, string token, object payload,
            StandSession register = null, HttpContent content = null, string fileName = null, string activeRole = null)
        {
            using (var request = new HttpRequestMessage(method, new Uri(address, route)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (!string.IsNullOrEmpty(activeRole)) request.Headers.Add("X-Active-Role", activeRole);
                if (register != null)
                {
                    request.Headers.Add("X-Register-Id", register.Register);
                    request.Headers.Add("X-Register-Key", register.Key);
                }
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (fileName != null) request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
                if (content != null) request.Content = content;
                else if (payload != null)
                    request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await http.SendAsync(request))
                    {
                        var text = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) throw ReadHttpError(response, text);
                        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return true;
                        try { return json.DeserializeObject(text); }
                        catch (Exception) { throw new BridgeError(0, "invalid_response"); }
                    }
                }
                catch (TaskCanceledException) { throw new BridgeError(0, "timeout"); }
                catch (HttpRequestException) { throw new BridgeError(0, "connection_failed"); }
            }
        }

        private BridgeError ReadHttpError(HttpResponseMessage response, string text)
        {
            string code = null;
            string trace = null;
            int? row = null;
            if (response.Headers.TryGetValues("X-Request-Id", out var ids)) trace = ids.FirstOrDefault();
            try
            {
                var problem = json.Deserialize<Dictionary<string, object>>(text);
                if (problem.TryGetValue("code", out var codeValue)) code = codeValue as string;
                if (problem.TryGetValue("row", out var rowValue) && rowValue is int line) row = line;
                if (trace == null && problem.TryGetValue("traceId", out var traceValue)) trace = traceValue as string;
            }
            catch (Exception) { }
            return new BridgeError((int)response.StatusCode, code ?? "http_error", trace, row);
        }

        private string ReadTokenSubject(string token)
        {
            try
            {
                // Identity here only scopes the local journal. The API authenticates the token.
                var parts = token.Split('.');
                if (parts.Length != 3) throw new FormatException();
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                var claims = json.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                if (claims.TryGetValue("sub", out var raw) && raw is string subject &&
                    !string.IsNullOrWhiteSpace(subject) && subject.Length <= 256)
                    return subject;
            }
            catch (Exception) { }
            throw new BridgeError(400, "invalid_stand_identity");
        }

        private static string ParseGuid(string value)
        {
            if (Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty) return id.ToString();
            throw new BridgeError(400, "invalid_id");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) http.Dispose();
            base.Dispose(disposing);
        }
    }

}
