using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace KasaDesktop
{
    internal sealed class PilotProfile
    {
        internal Uri Api { get; private set; }
        internal string Issuer { get; private set; }
        internal string ClientId { get; private set; }
        internal string Scope { get; private set; }
        internal string Resource { get; private set; }
        internal string Store { get; private set; }
        internal string Register { get; private set; }
        internal string AdminAcrValues { get; private set; }

        internal static Uri Https(string value, bool rootOnly = false)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                (rootOnly && (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))))
                throw new FormatException();
            return uri;
        }

        internal static PilotProfile Parse(Dictionary<string, object> data)
        {
            string Get(string key, int maximum, bool required = true)
            {
                if (!data.TryGetValue(key, out var raw) || raw == null || (raw is string empty && empty.Length == 0))
                {
                    if (!required) return null;
                    throw new FormatException();
                }
                if (!(raw is string value) || value.Length > maximum || value.Trim() != value) throw new FormatException();
                foreach (var c in value) if (char.IsControl(c)) throw new FormatException();
                return value;
            }
            string Id(string key)
            {
                var value = Get(key, 36, false);
                if (value == null) return null;
                if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new FormatException();
                return id.ToString();
            }
            var issuer = Get("issuer", 500);
            if (!string.IsNullOrEmpty(Https(issuer).Query)) throw new FormatException();
            var scope = Get("scope", 350);
            if (Array.IndexOf(scope.Split(' '), "openid") == -1) throw new FormatException();
            var profile = new PilotProfile {
                Api = Https(Get("apiBase", 500), true), Issuer = issuer, ClientId = Get("clientId", 250),
                Scope = scope, Resource = Get("resource", 500, false), Store = Id("storeId"),
                Register = Id("registerId"), AdminAcrValues = Get("adminAcrValues", 350, false)
            };
            if (profile.Resource != null) Https(profile.Resource);
            return profile;
        }
    }

    internal sealed class DesktopAuthorization
    {
        internal const string CallbackPath = "/oauth/callback";
        private readonly HttpClient http;
        private readonly Func<string, Dictionary<string, object>> parse;

        internal DesktopAuthorization(HttpClient http, Func<string, Dictionary<string, object>> parse)
        {
            this.http = http;
            this.parse = parse;
        }

        internal async Task<string> Authorize(PilotProfile profile, bool admin, Action<string> openBrowser,
            CancellationToken cancel, bool uzbek = false)
        {
            var discovery = await ReadJson(HttpMethod.Get, profile.Issuer.TrimEnd('/') + "/.well-known/openid-configuration", null, cancel)
                .ConfigureAwait(false);
            if (Get(discovery, "issuer") != profile.Issuer) throw new BridgeError(400, "identity_issuer_mismatch");
            if (discovery.TryGetValue("code_challenge_methods_supported", out var methods) && methods is System.Collections.IEnumerable codes)
            {
                var supportsS256 = false;
                foreach (var code in codes) if (Equals(code, "S256")) supportsS256 = true;
                if (!supportsS256) throw new BridgeError(400, "identity_pkce_unsupported");
            }
            var authorization = SecureEndpoint(discovery, "authorization_endpoint");
            var tokenEndpoint = SecureEndpoint(discovery, "token_endpoint");
            var verifier = RandomValue();
            var state = RandomValue();
            var nonce = RandomValue();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(4);
            string codeValue;
            var redirect = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + CallbackPath;
            using (cancel.Register(listener.Stop))
            {
                try
                {
                    var args = new Dictionary<string, string> {
                        ["client_id"] = profile.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
                        ["scope"] = profile.Scope, ["state"] = state, ["nonce"] = nonce,
                        ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256", ["prompt"] = "login"
                    };
                    if (profile.Resource != null) args["resource"] = profile.Resource;
                    if (admin && profile.AdminAcrValues != null) args["acr_values"] = profile.AdminAcrValues;
                    using (var content = new FormUrlEncodedContent(args))
                        openBrowser(authorization.AbsoluteUri + (authorization.Query.Length == 0 ? "?" : "&") +
                            await content.ReadAsStringAsync().ConfigureAwait(false));
                    codeValue = await ReceiveCode(listener, new Uri(redirect), profile.Issuer, state, cancel, uzbek).ConfigureAwait(false);
                }
                finally { listener.Stop(); }
            }
            var parameters = new Dictionary<string, string> {
                ["grant_type"] = "authorization_code", ["code"] = codeValue, ["redirect_uri"] = redirect,
                ["client_id"] = profile.ClientId, ["code_verifier"] = verifier
            };
            if (profile.Resource != null) parameters["resource"] = profile.Resource;
            var response = await ReadJson(HttpMethod.Post, tokenEndpoint.AbsoluteUri, parameters, cancel).ConfigureAwait(false);
            var token = Get(response, "access_token");
            if (!string.Equals(Get(response, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(token) || token.Length > 12000 || token.Split('.').Length != 3)
                throw new BridgeError(400, "identity_token_invalid");
            return token;
        }

        internal static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create()) return Encode(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private static string RandomValue()
        {
            var data = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(data);
            return Encode(data);
        }

        private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_');

        private static Uri SecureEndpoint(Dictionary<string, object> data, string key)
        {
            try { return PilotProfile.Https(Get(data, key)); }
            catch (FormatException) { throw new BridgeError(400, "identity_discovery_invalid"); }
        }

        private static string Get(Dictionary<string, object> data, string key) => data.TryGetValue(key, out var raw) ? raw as string : null;

        private async Task<Dictionary<string, object>> ReadJson(HttpMethod method, string url,
            Dictionary<string, string> form, CancellationToken cancel)
        {
            using (var request = new HttpRequestMessage(method, url))
            {
                if (form != null) request.Content = new FormUrlEncodedContent(form);
                using (var response = await http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new BridgeError(401, "identity_request_rejected");
                    try { return parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) ?? throw new FormatException(); }
                    catch (Exception) { throw new BridgeError(400, "identity_discovery_invalid"); }
                }
            }
        }

        private static async Task<string> ReceiveCode(TcpListener listener, Uri redirect, string issuer,
            string state, CancellationToken cancel, bool uzbek)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                TcpClient next;
                try { next = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception) when (cancel.IsCancellationRequested) { throw new OperationCanceledException(cancel); }
                using (next)
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(3));
                    using (limit.Token.Register(next.Close))
                    try
                    {
                        var stream = next.GetStream();
                        var request = await ReadHeaders(stream, limit.Token).ConfigureAwait(false);
                        var result = CallbackCode(request, redirect, issuer, state, out var denied);
                        if (result == null && !denied)
                        {
                            await Respond(stream, 400, "Invalid callback.", limit.Token).ConfigureAwait(false);
                            continue;
                        }
                        // A disconnected browser cannot change an already validated result.
                        try
                        {
                            await Respond(stream, 200, denied ? (uzbek ? "Kirish bekor qilindi." : "Вход отменён.") :
                                (uzbek ? "Javob olindi. Kassaga qayting." : "Ответ получен. Вернитесь в кассу."),
                                limit.Token).ConfigureAwait(false);
                        }
                        catch (IOException) { }
                        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                        if (denied) throw new BridgeError(401, "identity_login_denied");
                        return result;
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) when (limit.IsCancellationRequested) { }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                }
            }
        }

        internal static string CallbackCode(string headers, Uri redirect, string issuer, string state, out bool denied)
        {
            denied = false;
            var lines = headers.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var start = lines[0].Split(' ');
            if (start.Length != 3 || start[0] != "GET" || !start[1].StartsWith(CallbackPath + "?", StringComparison.Ordinal)) return null;
            int hostCount = 0;
            foreach (var line in lines)
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(line.Substring(5).Trim(), redirect.Authority, StringComparison.OrdinalIgnoreCase)) return null;
                    hostCount++;
                }
            if (hostCount != 1) return null;
            var query = HttpUtility.ParseQueryString(start[1].Substring(CallbackPath.Length + 1));
            string Single(string key) => query.GetValues(key) is string[] values && values.Length == 1 ? values[0] : null;
            var issuerValues = query.GetValues("iss");
            if (Single("state") != state || (issuerValues != null && Single("iss") != issuer)) return null;
            var code = Single("code");
            var error = Single("error");
            if (error != null && code == null && query.GetValues("code") == null) { denied = true; return null; }
            if (query.GetValues("error") != null || string.IsNullOrWhiteSpace(code) || code.Length > 4096) return null;
            return code;
        }

        private static async Task<string> ReadHeaders(NetworkStream stream, CancellationToken cancel)
        {
            var bytes = new byte[8192];
            int length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes, length, bytes.Length - length, cancel).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                var text = Encoding.ASCII.GetString(bytes, 0, length);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
            }
            return "";
        }

        private static async Task Respond(NetworkStream stream, int status, string message, CancellationToken cancel)
        {
            var body = Encoding.UTF8.GetBytes(message);
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + (status == 200 ? " OK" : " Bad Request") +
                "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer" +
                "\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length, cancel).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, cancel).ConfigureAwait(false);
        }
    }
}
