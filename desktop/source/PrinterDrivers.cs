using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KasaDesktop
{
    internal sealed partial class MainWindow
    {
        private const long MaxDriverBytes = 64 * 1024 * 1024;
        private static readonly HttpClient DriverHttp = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            MaxResponseHeadersLength = 32
        }) { Timeout = Timeout.InfiniteTimeSpan };

        private object ReadPrinterDrivers()
        {
            object[] devices;
            var usbAvailable = true;
            try { devices = ReadUsbPrinters(); }
            catch (ManagementException) { devices = Array.Empty<object>(); usbAvailable = false; }
            catch (UnauthorizedAccessException) { devices = Array.Empty<object>(); usbAvailable = false; }
            catch (COMException) { devices = Array.Empty<object>(); usbAvailable = false; }

            DriverRepository repository;
            var repositoryValid = true;
            try { repository = ReadDriverRepository(); }
            catch (BridgeError) { repository = new DriverRepository(); repositoryValid = false; }
            var x64 = IsX64Windows();
            return new
            {
                devices, usbAvailable, repositoryValid,
                models = PrinterDriverCatalog.Models.Select(model => new
                {
                    id = model.Id, name = model.Display, host = model.Support.Host,
                    terms = model.Terms != null,
                    download = !x64 ? null : repository.packages.Any(package => package.model == model.Id) ? "server" :
                        model.Download != null ? "vendor" : null
                }).ToArray()
            };
        }

        private static object[] ReadUsbPrinters()
        {
            var options = new EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(5) };
            const string query = "SELECT Name, PNPDeviceID, HardwareID, CompatibleID, ConfigManagerErrorCode " +
                "FROM Win32_PnPEntity WHERE Present = TRUE AND PNPDeviceID LIKE 'USB%'";
            using (var search = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery(query), options))
            using (var entries = search.Get())
            {
                var devices = new List<object>();
                foreach (ManagementObject entry in entries)
                {
                    using (entry)
                    {
                        var deviceId = entry["PNPDeviceID"] as string;
                        var name = entry["Name"] as string;
                        var model = PrinterDriverCatalog.MatchModel(name);
                        if (!PrinterDriverCatalog.IsPrinterInterface(deviceId, entry["CompatibleID"] as string[], model)) continue;
                        // Use the hardware ID without the instance suffix, which can contain a serial number.
                        var hardwareId = (entry["HardwareID"] as string[])?.FirstOrDefault() ??
                            string.Join(@"\", deviceId.Split('\\').Take(2));
                        var status = entry["ConfigManagerErrorCode"] as uint?;
                        devices.Add(new { name, hardwareId, status, model });
                    }
                    if (devices.Count == 32) break;
                }
                return devices.ToArray();
            }
        }

        private DriverRepository ReadDriverRepository()
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrinterDrivers.json");
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream))
                {
                    if (stream.Length > 32768) throw new FormatException();
                    var config = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 32768 }
                        .Deserialize<DriverRepository>(reader.ReadToEnd());
                    if (config?.packages == null || config.packages.Length > 20 ||
                        !Uri.TryCreate(config.repository, UriKind.Absolute, out var repository) ||
                        !PrinterDriverCatalog.IsSafeRepository(repository))
                        throw new FormatException();
                    var models = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var package in config.packages)
                        if (package == null || !models.Add(package.model) || !PrinterDriverCatalog.Models.Any(m => m.Id == package.model) ||
                            !PrinterDriverCatalog.IsInstallerFile(package.file) || !PrinterDriverCatalog.IsSha256(package.sha256))
                            throw new FormatException();
                    return config;
                }
            }
            catch (FileNotFoundException) { return new DriverRepository(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException ||
                                           error is ArgumentException || error is InvalidOperationException)
            {
                throw new BridgeError(400, "printer_repository_invalid");
            }
        }

        private async Task<object> OpenPrinterDriver(string id, string action)
        {
            var model = PrinterDriverCatalog.Models.FirstOrDefault(item => item.Id == id);
            if (model == null) throw new BridgeError(404, "printer_model_unknown");
            if (action != "device.installPrinterDriver")
            {
                var page = action == "device.printerTerms" ? model.Terms : model.Support;
                if (page == null) throw new BridgeError(404, "printer_support_failed");
                try { using (Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })) { } }
                catch (Win32Exception) { throw new BridgeError(0, "printer_support_failed"); }
                return new { status = "browser" };
            }

            if (!IsX64Windows()) throw new BridgeError(409, "printer_windows_architecture");
            DriverRepository repository;
            try { repository = ReadDriverRepository(); }
            catch (BridgeError) when (model.Download != null) { repository = new DriverRepository(); }
            var package = repository.packages.SingleOrDefault(item => item.model == id);
            var download = package == null ? model.Download : new PrinterDriverDownload(
                new Uri(new Uri(repository.repository), package.file).AbsoluteUri, package.file, package.sha256);
            if (download == null) throw new BridgeError(404, "printer_package_unavailable");
            if (package == null && model.Terms != null)
            {
                var license = MessageBox.Show(this,
                    T("Скачать драйвер с сайта производителя? Скачивая файл, вы принимаете условия лицензии производителя.",
                      "Drayver ishlab chiqaruvchi saytidan yuklab olinsinmi? Faylni yuklab olish bilan litsenziya shartlarini qabul qilasiz.") +
                    Environment.NewLine + model.Display + Environment.NewLine + model.Terms.AbsoluteUri,
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (license != DialogResult.Yes) return new { status = "cancelled" };
            }
            var directory = Path.Combine(dataDirectory, "PrinterDrivers", Guid.NewGuid().ToString("N"));
            var destination = Path.Combine(directory, download.Installer);
            var started = false;
            try
            {
                Directory.CreateDirectory(directory);
                if (download.Entry == null) await DownloadPrinterDriver(download.Source, destination);
                else
                {
                    var archivePath = Path.Combine(directory, "driver.zip");
                    await DownloadPrinterDriver(download.Source, archivePath);
                    await Task.Run(() => ExtractPrinterInstaller(download, archivePath, destination));
                    File.Delete(archivePath);
                }
                File.WriteAllText(destination + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
                using (var locked = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await Task.Run(() =>
                    {
                        VerifyDriverHash(locked, download.Sha256);
                        if (!VerifyInstallerSignature(destination, locked))
                            throw new BridgeError(409, "printer_package_untrusted");
                    });
                    var confirm = MessageBox.Show(this,
                        T("Запустить проверенный установщик драйвера? Сверьте модель на наклейке принтера.",
                          "Tekshirilgan drayver o‘rnatuvchisi ishga tushirilsinmi? Printerning yorlig‘idagi modelni tekshiring.") +
                        Environment.NewLine + model.Display + Environment.NewLine + download.Source.Host,
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (confirm != DialogResult.Yes) return new { status = "cancelled" };
                    using (var process = Process.Start(new ProcessStartInfo(destination)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        WorkingDirectory = Environment.SystemDirectory
                    }))
                    {
                        started = process != null;
                        if (!started) throw new BridgeError(0, "printer_install_failed");
                    }
                }
                return new { status = "started" };
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return new { status = "cancelled" }; }
            catch (Win32Exception) { throw new BridgeError(0, "printer_install_failed"); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                                           error is HttpRequestException || error is OperationCanceledException || error is InvalidDataException)
            {
                throw new BridgeError(0, "printer_download_failed");
            }
            finally
            {
                if (!started)
                {
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static bool IsX64Windows()
        {
            try { return IsWow64Process2(new IntPtr(-1), out _, out var native) && native == 0x8664; }
            catch (EntryPointNotFoundException) { return Environment.Is64BitProcess; }
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        private static void ExtractPrinterInstaller(PrinterDriverDownload download, string archivePath, string destination)
        {
            using (var input = File.Open(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                VerifyDriverHash(input, download.ArchiveHash);
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                {
                    // The archive is pinned. Match its unique ASCII installer name regardless of folder encoding.
                    var entries = archive.Entries.Where(entry => entry.Name == download.Entry).Take(2).ToArray();
                    var entry = entries.Length == 1 ? entries[0] : null;
                    if (entry == null || entry.Length <= 0 || entry.Length > MaxDriverBytes)
                        throw new InvalidDataException();
                    using (var installer = entry.Open())
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        installer.CopyTo(output);
                }
            }
        }

        private static void VerifyDriverHash(Stream stream, string expected)
        {
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new BridgeError(409, "printer_package_hash_mismatch");
                stream.Position = 0;
            }
        }

        private static async Task DownloadPrinterDriver(Uri source, string destination)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100)))
            using (var response = await DriverHttp.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDriverBytes)
                    throw new BridgeError(413, "printer_package_too_large");
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) != 0)
                    {
                        total += read;
                        if (total > MaxDriverBytes) throw new BridgeError(413, "printer_package_too_large");
                        await output.WriteAsync(buffer, 0, read, timeout.Token);
                    }
                }
            }
        }

        private static bool VerifyInstallerSignature(string path, FileStream locked)
        {
            var file = new WinTrustFile
            {
                Size = (uint)Marshal.SizeOf(typeof(WinTrustFile)),
                Path = path,
                Handle = locked.SafeFileHandle.DangerousGetHandle()
            };
            var pointer = Marshal.AllocHGlobal((int)file.Size);
            try
            {
                Marshal.StructureToPtr(file, pointer, false);
                var data = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    UIChoice = 2, // WTD_UI_NONE
                    RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                    UnionChoice = 1, // WTD_CHOICE_FILE
                    File = pointer,
                    ProviderFlags = 0x80 // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                };
                var policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE"); // Authenticode
                return WinVerifyTrust(new IntPtr(-1), ref policy, ref data) == 0;
            }
            finally
            {
                Marshal.DestroyStructure(pointer, typeof(WinTrustFile));
                Marshal.FreeHGlobal(pointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr window, ref Guid policy, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFile
        {
            internal uint Size;
            [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
            internal IntPtr Handle, Subject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            internal uint Size;
            internal IntPtr PolicyData, SipData;
            internal uint UIChoice, RevocationChecks, UnionChoice;
            internal IntPtr File;
            internal uint StateAction;
            internal IntPtr StateData, Url;
            internal uint ProviderFlags, UIContext;
            internal IntPtr SignatureSettings;
        }
    }

    internal sealed class DriverRepository
    {
        public string repository { get; set; }
        public DriverPackage[] packages { get; set; } = Array.Empty<DriverPackage>();
    }

    internal sealed class DriverPackage
    {
        public string model { get; set; }
        public string file { get; set; }
        public string sha256 { get; set; }
    }
}
