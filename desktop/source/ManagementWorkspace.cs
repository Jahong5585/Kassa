using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed partial class MainWindow
    {
        private static string Segment(string value, int length = 80)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > length || value == "." || value == ".." ||
                value.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0 || Regex.IsMatch(value, "[\\x00-\\x1f]"))
                throw new BridgeError(400, "invalid_segment");
            return Uri.EscapeDataString(value);
        }

        private static string IsoDate(string value)
        {
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new BridgeError(400, "invalid_date");
            return value;
        }

        private async Task<object> RunManagementAction(string action, Dictionary<string, object> data)
        {
            data.TryGetValue("value", out var valueObject);
            data.TryGetValue("body", out var body);
            var value = valueObject as string;
            if (action == "localCsv")
            {
                var text = body as string;
                if (text == null || text.Length > 1_000_000) throw new BridgeError(400, "csv_too_large");
                return SaveCsv("Kassa-demo.csv", new UTF8Encoding(true).GetPreamble(), Encoding.UTF8.GetBytes(text));
            }

            var current = storeSession ?? throw new BridgeError(401, "stand_session_required");
            current.RequireAdmin();
            var prefix = current.Prefix;
            var method = HttpMethod.Get;
            var query = body as Dictionary<string, object>;
            string Arg(string key) => query != null && query.TryGetValue(key, out var item) ? item as string : null;
            string route;
            switch (action)
            {
                case "catalog":
                    route = "/catalog/products?includeArchived=true&offset=" + ParseOffset(value);
                    if (body is string search && search.Length > 0) route += "&q=" + Uri.EscapeDataString(search);
                    break;
                case "product": route = "/catalog/products/" + ParseGuid(value); break;
                case "saveProduct": route = "/catalog/products/" + ParseGuid(value); method = HttpMethod.Put; break;
                case "priceHistory": route = "/catalog/products/" + ParseGuid(value) + "/price-history?offset=0"; break;
                case "savePrice": route = "/catalog/products/" + ParseGuid(value) + "/price"; method = HttpMethod.Put; break;
                case "promotion": case "savePromotion":
                    route = "/catalog/products/" + ParseGuid(value) + "/promotion";
                    if (action == "savePromotion") method = HttpMethod.Put;
                    break;
                case "catalogImport": route = "/catalog/import"; method = HttpMethod.Post; break;
                case "proposals":
                    route = "/receiving/catalog/proposals?status=pending&offset=" + ParseOffset(value); break;
                case "approveProposal": case "rejectProposal":
                    route = "/catalog/proposals/" + ParseGuid(value) + (action == "approveProposal" ? "/approve" : "/reject");
                    method = HttpMethod.Post; break;
                case "suppliers": route = "/suppliers?activeOnly=false&offset=" + ParseOffset(value); break;
                case "supplier": case "saveSupplier":
                    route = "/suppliers/" + Segment(value, 32);
                    if (action == "saveSupplier") method = HttpMethod.Put;
                    break;
                case "quotes":
                    route = "/procurement/suppliers/" + Segment(value, 32) + "/prices?offset=" + ParseOffset(body as string); break;
                case "quoteHistory": case "saveQuote": case "recommend":
                    var parts = (value ?? "").Split('|');
                    if (parts.Length != 2) throw new BridgeError(400, "invalid_segment");
                    var code = Segment(parts[0], 32);
                    var sku = Segment(parts[1]);
                    route = action == "recommend" ? "/procurement/recommendations/" + code + "/" + sku :
                        "/procurement/suppliers/" + code + "/prices/" + sku;
                    if (action == "saveQuote") method = HttpMethod.Put;
                    if (action == "recommend") method = HttpMethod.Post;
                    break;
                case "drafts": route = "/procurement/orders?offset=" + ParseOffset(value); break;
                case "draft": case "saveDraft": case "approveDraft": case "markSent":
                    route = "/procurement/orders/" + ParseGuid(value);
                    if (action == "saveDraft") method = HttpMethod.Put;
                    if (action == "approveDraft") { route += "/approve"; method = HttpMethod.Post; }
                    if (action == "markSent") { route += "/mark-sent"; method = HttpMethod.Post; }
                    break;
                case "report": case "reportCsv":
                    if (value != "sales" && value != "profit" && value != "stock-movements" && value != "stock-losses" && value != "audit")
                        throw new BridgeError(400, "invalid_report");
                    var period = "?from=" + IsoDate(Arg("from")) + "&to=" + IsoDate(Arg("to"));
                    if (value != "audit") prefix = "/v1/reports/stores/" + current.StoreId;
                    if (action == "reportCsv")
                    {
                        if (value == "audit") throw new BridgeError(400, "invalid_report");
                        return await DownloadCsv(current, prefix + "/" + value + "/csv" + period, value);
                    }
                    route = "/" + value + period;
                    if (!string.IsNullOrEmpty(Arg("after")) && Arg("after").Length <= 2048)
                        route += "&after=" + Uri.EscapeDataString(Arg("after"));
                    break;
                default: throw new BridgeError(400, "unsupported_action");
            }
            return await SendRequest(current.Address, prefix + route, method, current.Token,
                method == HttpMethod.Get ? null : body, activeRole: current.Role);
        }

        private bool SaveCsv(string filename, params byte[][] parts)
        {
            using (var dialog = new SaveFileDialog { FileName = filename, Filter = "CSV|*.csv", DefaultExt = "csv",
                AddExtension = true, OverwritePrompt = true, RestoreDirectory = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return false;
                if (!string.Equals(Path.GetExtension(dialog.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeError(400, "invalid_csv_extension");
                using (var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
                    foreach (var part in parts) stream.Write(part, 0, part.Length);
                return true;
            }
        }

        private async Task<object> DownloadCsv(StoreSession current, string route, string report)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri(current.Address, route)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Token);
                request.Headers.Add("X-Active-Role", current.Role);
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw ReadHttpError(response, await response.Content.ReadAsStringAsync());
                        if (response.Content.Headers.ContentType?.MediaType != "text/csv")
                            throw new BridgeError(400, "invalid_csv_format");
                        const int maxCsvBytes = 8 * 1024 * 1024;
                        if (response.Content.Headers.ContentLength > maxCsvBytes)
                            throw new BridgeError(400, "csv_too_large");
                        using (var source = await response.Content.ReadAsStreamAsync())
                        using (var buffer = new MemoryStream())
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                        {
                            var chunk = new byte[32768];
                            int count;
                            while ((count = await source.ReadAsync(chunk, 0, chunk.Length, timeout.Token)) != 0)
                            {
                                if (buffer.Length + count > maxCsvBytes) throw new BridgeError(400, "csv_too_large");
                                buffer.Write(chunk, 0, count);
                            }
                            return SaveCsv(report + ".csv", buffer.ToArray());
                        }
                    }
                }
                catch (TaskCanceledException) { throw new BridgeError(0, "timeout"); }
                catch (HttpRequestException) { throw new BridgeError(0, "connection_failed"); }
            }
        }
    }
}
