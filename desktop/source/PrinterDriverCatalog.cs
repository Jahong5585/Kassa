using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KasaDesktop
{
    internal sealed class PrinterDriverModel
    {
        internal PrinterDriverModel(string id, string brand, string model, string support,
            PrinterDriverDownload download = null, string terms = null)
        {
            Id = id;
            Display = brand + " " + model;
            Support = new Uri(support);
            Download = download;
            Terms = terms == null ? null : new Uri(terms);
            var parts = Regex.Split(model, @"[^A-Za-z0-9]+").Select(Regex.Escape);
            Pattern = new Regex(@"(?<![A-Za-z0-9])" + string.Join(@"[ _-]*", parts) + @"(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        internal string Id { get; }
        internal string Display { get; }
        internal Uri Support { get; }
        internal PrinterDriverDownload Download { get; }
        internal Uri Terms { get; }
        internal Regex Pattern { get; }
    }

    internal sealed class PrinterDriverDownload
    {
        internal PrinterDriverDownload(string source, string installer, string sha256, string entry = null, string archiveHash = null)
        {
            Source = new Uri(source);
            Installer = installer;
            Sha256 = sha256;
            Entry = entry;
            ArchiveHash = archiveHash;
        }

        internal Uri Source { get; }
        internal string Installer { get; }
        internal string Sha256 { get; }
        internal string Entry { get; }
        internal string ArchiveHash { get; }
    }

    internal static class PrinterDriverCatalog
    {
        private const string Xprinter = "https://www.xprintertech.com/download/receipt-printer-series";
        private const string Rongta = "https://www.rongtatech.com/driver-download/";

        private static readonly PrinterDriverDownload RongtaWindows = new PrinterDriverDownload(
            "https://file.globalso.com/file_manage/4365/20260612/thermal-printer-driver-windows.zip",
            "Rongta-Driver-2.70.exe", "d3c31b8b06d04c76852ef0c611a5e210ad41487972615c1cff38a8a3634c8dc8",
            "RongTaDriverInstall V2.70.exe",
            "4f83c445d9d86650a1b25a5345f962765c82c217d0fcf2afc1064ae667675d50");

        internal static readonly IReadOnlyList<PrinterDriverModel> Models = Array.AsReadOnly(new[]
        {
            new PrinterDriverModel("xprinter-xp-58iih", "Xprinter", "XP-58IIH", Xprinter),
            new PrinterDriverModel("xprinter-xp-q200ii", "Xprinter", "XP-Q200II", Xprinter),
            new PrinterDriverModel("xprinter-xp-q890k", "Xprinter", "XP-Q890K", Xprinter),
            new PrinterDriverModel("xprinter-xp-q809k", "Xprinter", "XP-Q809K", Xprinter),
            new PrinterDriverModel("xprinter-xp-d300h", "Xprinter", "XP-D300H", Xprinter),
            new PrinterDriverModel("xprinter-xp-e200l", "Xprinter", "XP-E200L", Xprinter),
            new PrinterDriverModel("xprinter-xp-n160ii", "Xprinter", "XP-N160II",
                "https://ru.xprintertech.com/all-products/xprinter-xp-n160ii-driver"),
            new PrinterDriverModel("rongta-rp58e", "Rongta", "RP58E", Rongta, RongtaWindows),
            new PrinterDriverModel("rongta-rp80", "Rongta", "RP80", Rongta, RongtaWindows),
            new PrinterDriverModel("rongta-rp326", "Rongta", "RP326", Rongta, RongtaWindows),
            new PrinterDriverModel("rongta-rp850", "Rongta", "RP850", Rongta, RongtaWindows),
            new PrinterDriverModel("rongta-ace-h1", "Rongta", "ACE H1", Rongta, RongtaWindows),
            new PrinterDriverModel("rongta-ace-g1y", "Rongta", "ACE G1Y", Rongta, RongtaWindows),
            new PrinterDriverModel("bixolon-srp-e300", "Bixolon", "SRP-E300",
                "https://www.bixolon.com/download_view.php?idx=73&s_key=Windows"),
            new PrinterDriverModel("epson-tm-t20iii", "Epson", "TM-T20III",
                "https://download-center.epson.com/softwares/?device_id=TM-T20III&language=en&os=WIN1164&region=PT",
                new PrinterDriverDownload("https://ftp.epson.com/drivers/pos/APD_612_T20III_WM.exe",
                    "APD_612_T20III_WM.exe", "4fad0b3161001a3ec8ea5c9be9d6f766fe5005299f59b8bff8e79cbec0c9d4f0"),
                "https://epson.com/SoftwareLicenseAgreement"),
        });

        internal static string MatchModel(params string[] names)
        {
            var matches = Models.Where(model => names.Any(name => name != null && model.Pattern.IsMatch(name)))
                .Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Id : null;
        }

        internal static bool IsPrinterInterface(string deviceId, string[] compatibleIds, string model)
        {
            if (deviceId == null) return false;
            if (deviceId.StartsWith(@"USBPRINT\", StringComparison.OrdinalIgnoreCase)) return true;
            if (!deviceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return false;
            return model != null || (compatibleIds ?? Array.Empty<string>()).Any(id =>
                string.Equals(id, @"USB\Class_07", StringComparison.OrdinalIgnoreCase) ||
                (id?.StartsWith(@"USB\Class_07&", StringComparison.OrdinalIgnoreCase) ?? false));
        }

        internal static bool IsSafeRepository(Uri uri) =>
            uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal);

        internal static bool IsInstallerFile(string name) => name != null &&
            Regex.IsMatch(name, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,115}\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsSha256(string hash) => hash != null && Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z");
    }
}
