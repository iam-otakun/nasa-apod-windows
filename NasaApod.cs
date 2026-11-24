// NASA APOD for Windows
// A system-tray app that shows NASA's Astronomy Picture of the Day in a
// popup by the tray — and quietly archives the original picture every
// day. It is the Windows sibling of the SwiftBar plugin
// swiftbar-nasa-apod and mirrors it feature for feature: the day's
// photo from the page's image-article link (NASA's og:image is a fixed
// site template), the title from the page's main heading, the full-size
// original archived as "YYYY-MM-DD Title.jpg", pure-black borders
// trimmed for display only, a Refresh that answers back ("Up to date!"
// / "Update successfully"), and an Archive submenu that owns the
// archive folder (default Pictures\NASA-Apod, stored in
// ~/.nasa-apod.conf — the same file format as the macOS plugin).
//
// Single-file C# (C# 5, .NET Framework 4.8), built with the framework's
// csc.exe — the same recipe as our BingWallpaper and NetworkSwitch
// tools. No SDK, no NuGet.
//
// Based on Jake Vossen's xbar plugin (MIT, (c) 2019 Jacob Vossen),
// revived and extended by Otakun and Dash. Idea by Fred (Otakun),
// code by Dash.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NasaApod")]
[assembly: AssemblyProduct("NASA APOD for Windows")]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]

namespace NasaApod
{
    // ------------------------------------------------------------------
    // Settings: the archive config lives in ~/.nasa-apod.conf exactly
    // like the macOS plugin (one line, ARCHIVE_DIR=<path>; an empty
    // value disables archiving; a missing file means "not set up yet"
    // until the first run writes it). The last shown APOD date lives in
    // ~/.nasa-apod.state. App-internal settings (login autostart, last
    // update check) live in %AppData%\NasaApod\settings.txt.
    // ------------------------------------------------------------------

    static class Settings
    {
        public static readonly string ConfPath = Path.Combine(HomeDir, ".nasa-apod.conf");
        public static readonly string StatePath = Path.Combine(HomeDir, ".nasa-apod.state");
        public static readonly string StateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NasaApod");

        private static string HomeDir
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        public static string DefaultArchiveDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "NASA-Apod");
            }
        }

        // null = never configured; "" = archiving off; otherwise a path.
        public static string ConfValue()
        {
            try
            {
                if (!File.Exists(ConfPath)) return null;
                foreach (string line in File.ReadAllLines(ConfPath))
                {
                    if (line.StartsWith("ARCHIVE_DIR=", StringComparison.Ordinal))
                        return line.Substring("ARCHIVE_DIR=".Length).Trim();
                }
            }
            catch { }
            return null;
        }

        // The folder archiving actually writes to (null = off).
        public static string EffectiveDir()
        {
            string v = ConfValue();
            if (v == null) return DefaultArchiveDir;
            if (v.Length == 0) return null;
            return ExpandHome(v);
        }

        public static string ExpandHome(string p)
        {
            if (p == "~") return HomeDir;
            if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(HomeDir, p.Substring(2));
            return p;
        }

        public static void SaveConf(string value)
        {
            try { File.WriteAllText(ConfPath, "ARCHIVE_DIR=" + value + "\n"); }
            catch (Exception ex) { Core.Log("SaveConf failed: " + ex.Message); }
        }

        public static string StateDate()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    string s = File.ReadAllText(StatePath).Trim();
                    return s.Length == 0 ? null : s;
                }
            }
            catch { }
            return null;
        }

        public static void SaveStateDate(string date)
        {
            try { File.WriteAllText(StatePath, date + "\n"); }
            catch { }
        }

        // ---- app-internal key=value store ----

        private static string StorePath
        {
            get { return Path.Combine(StateDir, "settings.txt"); }
        }

        public static string GetString(string key, string fallback)
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    foreach (string line in File.ReadAllLines(StorePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && line.Substring(0, eq) == key)
                            return line.Substring(eq + 1);
                    }
                }
            }
            catch { }
            return fallback;
        }

        public static void SetString(string key, string value)
        {
            try
            {
                if (!Directory.Exists(StateDir)) Directory.CreateDirectory(StateDir);
                var lines = new List<string>();
                bool found = false;
                if (File.Exists(StorePath))
                {
                    foreach (string line in File.ReadAllLines(StorePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && line.Substring(0, eq) == key)
                        {
                            lines.Add(key + "=" + value);
                            found = true;
                        }
                        else lines.Add(line);
                    }
                }
                if (!found) lines.Add(key + "=" + value);
                File.WriteAllLines(StorePath, lines.ToArray());
            }
            catch (Exception ex) { Core.Log("SetString failed: " + ex.Message); }
        }

        public static DateTime LastAppCheck
        {
            get
            {
                DateTime dt;
                if (DateTime.TryParse(GetString("LastAppCheck", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out dt))
                    return dt;
                return DateTime.MinValue;
            }
            set { SetString("LastAppCheck", value.ToString("o", CultureInfo.InvariantCulture)); }
        }

        public static DateTime LastFetch
        {
            get
            {
                DateTime dt;
                if (DateTime.TryParse(GetString("LastFetch", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out dt))
                    return dt;
                return DateTime.MinValue;
            }
            set { SetString("LastFetch", value.ToString("o", CultureInfo.InvariantCulture)); }
        }

        public static bool LaunchAtLogin
        {
            get
            {
                try
                {
                    using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(
                        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", false))
                    {
                        if (rk == null) return false;
                        object v = rk.GetValue("NasaApod");
                        return v != null && (v as string) == "\"" + Application.ExecutablePath + "\"";
                    }
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(
                        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                    {
                        if (rk == null) return;
                        if (value) rk.SetValue("NasaApod", "\"" + Application.ExecutablePath + "\"");
                        else rk.DeleteValue("NasaApod", false);
                    }
                }
                catch (Exception ex) { Core.Log("LaunchAtLogin failed: " + ex.Message); }
            }
        }
    }

    // ------------------------------------------------------------------
    // UI strings: Chinese on Chinese systems, English elsewhere — the
    // same rule (and mostly the same strings) as the macOS plugin.
    // ------------------------------------------------------------------

    static class S
    {
        public static readonly bool Zh = DetectZh();

        private static bool DetectZh()
        {
            string ov = Environment.GetEnvironmentVariable("APOD_LANG");
            if (!string.IsNullOrEmpty(ov)) return ov.Trim().ToLowerInvariant().StartsWith("zh");
            return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        public static string T(string zh, string en) { return Zh ? zh : en; }

        public static string SetupTitle { get { return T("归档设置", "Archive Settings"); } }
        public static string FirstMsg { get { return T("欢迎使用 NASA 每日一图。", "Welcome to NASA APOD."); } }
        public static string FirstBody
        {
            get
            {
                return T("每天的原图会归档到下面的文件夹：可以直接改，也可以点「选择文件夹…」挑选；清空则不归档。点「取消」将使用默认目录，之后随时可在托盘菜单的「归档」里更改。",
                         "Each day's original picture is archived to the folder below. Edit it, or click \"Choose Folder...\" to pick one; clear it to disable archiving. \"Cancel\" uses the default folder. You can change this any time in the tray menu's Archive submenu.");
            }
        }
        public static string Cancel { get { return T("取消", "Cancel"); } }
        public static string Ok { get { return T("确定", "OK"); } }
        public static string Save { get { return T("保存", "Save"); } }
        public static string ChooseBtn { get { return T("选择文件夹…", "Choose Folder..."); } }
        public static string ChoosePrompt { get { return T("选择归档 NASA 每日图片的文件夹", "Choose a folder for archiving the NASA picture of the day"); } }
        public static string InputMsg { get { return T("输入归档目录（清空 = 不归档）", "Archive folder (clear it to disable archiving)"); } }
        public static string Archive { get { return T("归档", "Archive"); } }
        public static string Colon { get { return Zh ? "：" : ": "; } }
        public static string Off { get { return T("已关闭", "Off"); } }
        public static string MChoose { get { return T("选择文件夹…", "Choose Folder..."); } }
        public static string MInput { get { return T("输入路径…", "Type a Path..."); } }
        public static string MDisable { get { return T("关闭归档", "Turn Off Archiving"); } }
        public static string MDefault { get { return T("恢复默认目录", "Restore Default Folder"); } }
        public static string MRefresh { get { return T("刷新", "Refresh"); } }
        public static string MsgUpToDate { get { return "Up to date!"; } }
        public static string MsgUpdated { get { return "Update successfully"; } }
        public static string Error { get { return T("未能完成请求：今天的每日一图可能是视频，或网络暂时不通", "Could not complete the request: today's picture may be a video, or the network is unreachable."); } }
        public static string MOpenSite { get { return T("打开 APOD 网站", "Open APOD Website"); } }
        public static string MOpenFolder { get { return T("打开归档文件夹", "Open Archive Folder"); } }
        public static string MLogin { get { return T("开机自启", "Launch at Login"); } }
        public static string MSettings { get { return T("设置…", "Settings..."); } }
        public static string SettingsMsg { get { return T("归档设置", "Archive Settings"); } }
        public static string SettingsBody
        {
            get
            {
                return T("每天的原图会归档到下面的文件夹：可以直接改，也可以点「选择文件夹…」挑选；清空则不归档。",
                         "Each day's original picture is archived to the folder below. Edit it, or click \"Choose Folder...\" to pick one; clear it to disable archiving.");
            }
        }
        public static string MUpdate { get { return T("检查更新…", "Check for Updates..."); } }
        public static string MQuit { get { return T("退出", "Quit"); } }
        public static string MAbout { get { return T("关于…", "About…"); } }
        public static string MClose { get { return T("关闭", "Close"); } }
        public static string NoImageYet { get { return T("（还没有图片）", "(no image yet)"); } }
    }

    // ------------------------------------------------------------------
    // Core fetch logic — a direct port of the macOS plugin's:
    // the day's photo hangs off the page's image-article link (its
    // slug carries the entry date); og:image is a fixed site template
    // and is never used. The archive stores the full-size original
    // (the content/dam twin of the rendered dynamicimage file).
    // ------------------------------------------------------------------

    public class ApodInfo
    {
        public string Date;        // yyyy-MM-dd
        public string Title;       // as written on the page, unescaped
        public string ImageUrl;    // rendered image for display
        public string ArchiveUrl;  // full-size original for the archive
    }

    public class FetchResult
    {
        public ApodInfo Info;
        public Image DisplayImage;   // black borders trimmed, full size
        public string ArchivePath;   // where today's original lives (null = archiving off)
        public string Error;         // non-null when the fetch failed
    }

    public static class Core
    {
        public const string PageUrl = "https://apod.nasa.gov/apod/";

        public static void Log(string message)
        {
            try
            {
                if (!Directory.Exists(Settings.StateDir)) Directory.CreateDirectory(Settings.StateDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine;
                File.AppendAllText(Path.Combine(Settings.StateDir, "NasaApod.log"), line);
            }
            catch { }
        }

        private static HttpClient MakeClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NasaApod-Windows/1.0");
            return client;
        }

        public static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        // One retry: a single dropped request on a home line should
        // not fail the whole cycle (seen in the wild, 2026-11-24).
        // (C# 5 has no await inside catch, so the retry sits outside.)
        public static async Task<string> FetchText(string url)
        {
            try
            {
                using (HttpClient client = MakeClient())
                {
                    return await client.GetStringAsync(url).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log("FetchText first attempt failed (" + url + "): " + Describe(ex));
            }
            await Task.Delay(1500).ConfigureAwait(false);
            using (HttpClient client = MakeClient())
            {
                return await client.GetStringAsync(url).ConfigureAwait(false);
            }
        }

        public static async Task<byte[]> DownloadBinary(string url)
        {
            try
            {
                using (HttpClient client = MakeClient())
                {
                    return await client.GetByteArrayAsync(url).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log("DownloadBinary first attempt failed (" + url + "): " + Describe(ex));
            }
            await Task.Delay(1500).ConfigureAwait(false);
            using (HttpClient client = MakeClient())
            {
                return await client.GetByteArrayAsync(url).ConfigureAwait(false);
            }
        }

        private static readonly Dictionary<string, int> Months = new Dictionary<string, int>
        {
            { "january", 1 }, { "february", 2 }, { "march", 3 }, { "april", 4 },
            { "may", 5 }, { "june", 6 }, { "july", 7 }, { "august", 8 },
            { "september", 9 }, { "october", 10 }, { "november", 11 }, { "december", 12 }
        };

        // Parse the APOD front page; null when there is no picture
        // entry (a video day, or a page redesign).
        public static ApodInfo Parse(string html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var m = Regex.Match(html,
                "image-article/apod-(\\d{4})-([a-z]+)-(\\d{1,2})-[^\"]*\"[^>]*>\\s*<img[^>]*?src=\"([^\"]+)\"");
            if (!m.Success) return null;
            int month;
            if (!Months.TryGetValue(m.Groups[2].Value, out month)) return null;
            var info = new ApodInfo();
            info.Date = string.Format(CultureInfo.InvariantCulture, "{0}-{1:00}-{2:00}",
                m.Groups[1].Value, month, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            string img = HttpUtility.HtmlDecode(m.Groups[4].Value);
            if (img.StartsWith("/", StringComparison.Ordinal)) img = "https://apod.nasa.gov" + img;
            info.ImageUrl = img;
            string arch = Regex.Replace(img, "dynamicimage/assets/science/", "content/dam/science/");
            int q = arch.IndexOf('?');
            if (q >= 0) arch = arch.Substring(0, q);
            info.ArchiveUrl = arch;
            var tm = Regex.Match(html, "class=\"display-48 margin-top-0 margin-bottom-3\">([^<]+)<");
            info.Title = tm.Success ? HttpUtility.HtmlDecode(tm.Groups[1].Value).Trim() : "";
            return info;
        }

        // Just the latest entry's date, for the Refresh comparison.
        public static async Task<string> PeekLatestDate()
        {
            try
            {
                string html = await FetchText(PageUrl).ConfigureAwait(false);
                ApodInfo info = Parse(html);
                return info == null ? null : info.Date;
            }
            catch (Exception ex)
            {
                Log("PeekLatestDate failed: " + Describe(ex));
                return null;
            }
        }

        public static string SanitizeFileName(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            var sb = new StringBuilder();
            foreach (char c in title)
            {
                bool bad = c == '/' || c == '\\' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|' || char.IsControl(c);
                sb.Append(bad ? '-' : c);
            }
            return sb.ToString().Trim();
        }

        // One full cycle: fetch the page, download the picture, archive
        // the original, remember the date. Mirrors the plugin's run.
        public static async Task<FetchResult> FetchLatest()
        {
            var result = new FetchResult();
            ApodInfo info;
            byte[] displayBytes;
            try
            {
                string html = await FetchText(PageUrl).ConfigureAwait(false);
                info = Parse(html);
                if (info == null)
                {
                    result.Error = S.Error;
                    return result;
                }
                displayBytes = await DownloadBinary(info.ImageUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Fetch failed: " + Describe(ex));
                result.Error = S.Error;
                return result;
            }

            result.Info = info;
            Settings.SaveStateDate(info.Date);
            Settings.LastFetch = DateTime.Now;

            // Archive the full-size original (never overwrite).
            string dir = Settings.EffectiveDir();
            if (dir != null)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string title = SanitizeFileName(info.Title);
                    string name = title.Length > 0 ? info.Date + " " + title + ".jpg" : info.Date + ".jpg";
                    string path = Path.Combine(dir, name);
                    result.ArchivePath = path;
                    if (!File.Exists(path))
                    {
                        byte[] data = null;
                        if (!string.IsNullOrEmpty(info.ArchiveUrl) && info.ArchiveUrl != info.ImageUrl)
                        {
                            try { data = await DownloadBinary(info.ArchiveUrl).ConfigureAwait(false); }
                            catch (Exception ex) { Log("Archive download failed, using display image: " + Describe(ex)); data = null; }
                        }
                        File.WriteAllBytes(path, data ?? displayBytes);
                    }
                }
                catch (Exception ex)
                {
                    Log("Archive failed: " + ex.Message);
                }
            }

            // Display image with the pure-black borders trimmed off.
            try
            {
                using (var ms = new MemoryStream(displayBytes))
                using (var raw = Image.FromStream(ms))
                {
                    result.DisplayImage = TrimBlackBorders(new Bitmap(raw));
                }
            }
            catch (Exception ex)
            {
                Log("Display image failed: " + ex.Message);
                result.Error = S.Error;
            }
            return result;
        }

        // Trim rows/columns that are entirely (sampled) pure black —
        // some of NASA's files bake black side bars around a portrait
        // photo. Display only; the archive keeps the original bytes.
        public static Bitmap TrimBlackBorders(Bitmap src)
        {
            try
            {
                int w = src.Width, h = src.Height;
                Func<int, int, bool> black = delegate(int x, int y)
                {
                    Color c = src.GetPixel(x, y);
                    return (c.R + c.G + c.B) / 3 <= 10;
                };
                Func<int, bool> colBlack = delegate(int x)
                {
                    for (int y = 0; y < h; y += 7) if (!black(x, y)) return false;
                    return true;
                };
                Func<int, bool> rowBlack = delegate(int y)
                {
                    for (int x = 0; x < w; x += 7) if (!black(x, y)) return false;
                    return true;
                };
                int l = 0; while (l < w && colBlack(l)) l++;
                int r = 0; while (r < w && colBlack(w - 1 - r)) r++;
                int t = 0; while (t < h && rowBlack(t)) t++;
                int b = 0; while (b < h && rowBlack(h - 1 - b)) b++;
                if ((l + r) < w && (t + b) < h && (l > 0 || r > 0 || t > 0 || b > 0))
                {
                    var rect = new Rectangle(l, t, w - l - r, h - t - b);
                    Bitmap cropped = src.Clone(rect, src.PixelFormat);
                    src.Dispose();
                    return cropped;
                }
            }
            catch (Exception ex)
            {
                Log("Trim failed: " + ex.Message);
            }
            return src;
        }
    }

    public class ReleaseInfo
    {
        public string Tag;
        public Version Version;
        public string ZipUrl;
    }

    // ------------------------------------------------------------------
    // Self-update from GitHub Releases — the same mechanism as
    // BingWallpaper for Windows: check the latest release, download its
    // zip, swap the running exe (rename to .old, copy the new one in,
    // relaunch with --updated so the new instance waits for the old
    // one's mutex). Any failure before the swap leaves the install
    // untouched.
    // ------------------------------------------------------------------

    public static class AppUpdate
    {
        private const string LatestUrl = "https://api.github.com/repos/iam-otakun/nasa-apod-windows/releases/latest";

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public static async Task<ReleaseInfo> FetchLatest()
        {
            try
            {
                string json;
                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", "NasaApod-Windows/" + CurrentVersion.ToString());
                    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    HttpResponseMessage resp = await client.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Core.Log("Update check failed: HTTP " + (int)resp.StatusCode);
                        return null;
                    }
                    json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                var ser = new JavaScriptSerializer();
                var root = ser.Deserialize<Dictionary<string, object>>(json);
                object tagObj;
                if (!root.TryGetValue("tag_name", out tagObj)) return null;
                string tag = tagObj as string;
                if (string.IsNullOrEmpty(tag)) return null;
                Version ver;
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out ver)) return null;
                string zip = null;
                object assetsObj;
                if (root.TryGetValue("assets", out assetsObj))
                {
                    var assets = assetsObj as System.Collections.ArrayList;
                    if (assets != null)
                    {
                        foreach (object a in assets)
                        {
                            var d = a as Dictionary<string, object>;
                            if (d == null) continue;
                            object nameObj, urlObj;
                            string name = d.TryGetValue("name", out nameObj) ? nameObj as string : null;
                            if (name != null
                                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                && d.TryGetValue("browser_download_url", out urlObj))
                            {
                                zip = urlObj as string;
                                break;
                            }
                        }
                    }
                }
                if (zip == null) return null;
                var info = new ReleaseInfo();
                info.Tag = tag;
                info.Version = ver;
                info.ZipUrl = zip;
                return info;
            }
            catch (Exception ex)
            {
                Core.Log("Update check failed: " + ex.Message);
                return null;
            }
        }

        public static async Task<string> Stage(ReleaseInfo rel)
        {
            try
            {
                string dir = Path.Combine(Settings.StateDir, "update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                byte[] zipBytes = await Core.DownloadBinary(rel.ZipUrl).ConfigureAwait(false);
                string zipPath = Path.Combine(dir, "pkg.zip");
                File.WriteAllBytes(zipPath, zipBytes);
                string extract = Path.Combine(dir, "extract");
                ZipFile.ExtractToDirectory(zipPath, extract);
                string exe = FindExe(extract);
                if (exe == null)
                {
                    Core.Log("Update package has no NasaApod.exe");
                    return null;
                }
                var fi = new FileInfo(exe);
                if (fi.Length < 30000)
                {
                    Core.Log("Staged exe looks too small: " + fi.Length);
                    return null;
                }
                return exe;
            }
            catch (Exception ex)
            {
                Core.Log("Update staging failed: " + ex.Message);
                return null;
            }
        }

        private static string FindExe(string dir)
        {
            string direct = Path.Combine(dir, "NasaApod.exe");
            if (File.Exists(direct)) return direct;
            foreach (string f in Directory.GetFiles(dir, "NasaApod.exe", SearchOption.AllDirectories))
                return f;
            return null;
        }

        public static bool SwapAndRelaunch(string stagedExe)
        {
            string exePath = Application.ExecutablePath;
            string oldPath = exePath + ".old";
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(exePath, oldPath);
                try
                {
                    File.Copy(stagedExe, exePath);
                }
                catch
                {
                    File.Move(oldPath, exePath);
                    throw;
                }
                Process.Start(exePath, "--updated");
                return true;
            }
            catch (Exception ex)
            {
                Core.Log("Update swap failed: " + ex.Message);
                return false;
            }
        }

        public static void CleanupOld()
        {
            try
            {
                string oldPath = Application.ExecutablePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                string dir = Path.Combine(Settings.StateDir, "update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Small dialogs: the first-run archive setup (the plugin's setup
    // dialog, with its Choose Folder loop), a type-a-path dialog, and
    // a message that answers and dismisses itself after 3 seconds —
    // the plugin's "giving up after 3" refresh answer.
    // ------------------------------------------------------------------

    class SetupForm : Form
    {
        private TextBox box;
        private CheckBox loginBox;

        public string PathText { get { return box.Text.Trim(); } }
        public bool LaunchChecked { get { return loginBox != null && loginBox.Checked; } }

        // firstRun: the one-time welcome (no startup checkbox, Cancel
        // means "use the default"). Otherwise this is the Settings
        // dialog: seeded with the current folder, plus the launch-at-
        // login checkbox, and Cancel simply discards changes.
        public SetupForm(string initialPath, bool firstRun)
        {
            Text = S.SetupTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, firstRun ? 218 : 256);

            var msg = new Label();
            msg.Text = firstRun ? S.FirstMsg : S.SettingsMsg;
            msg.Location = new Point(14, 12);
            msg.Size = new Size(492, 20);
            msg.Font = new Font(Font, FontStyle.Bold);
            Controls.Add(msg);

            var body = new Label();
            body.Text = firstRun ? S.FirstBody : S.SettingsBody;
            body.Location = new Point(14, 36);
            body.Size = new Size(492, 58);
            Controls.Add(body);

            box = new TextBox();
            box.Location = new Point(14, 102);
            box.Size = new Size(492, 23);
            box.Text = initialPath;
            Controls.Add(box);

            int btnY = 140;
            if (!firstRun)
            {
                loginBox = new CheckBox();
                loginBox.Text = S.MLogin;
                loginBox.Location = new Point(14, 140);
                loginBox.Size = new Size(300, 24);
                loginBox.Checked = Settings.LaunchAtLogin;
                Controls.Add(loginBox);

                var turnOff = new Button();
                turnOff.Text = S.MDisable;
                turnOff.Location = new Point(330, 138);
                turnOff.Size = new Size(176, 26);
                turnOff.Click += delegate { box.Text = ""; };
                Controls.Add(turnOff);
                btnY = 178;
            }

            var choose = new Button();
            choose.Text = S.ChooseBtn;
            choose.Location = new Point(14, btnY);
            choose.Size = new Size(firstRun ? 140 : 126, 30);
            choose.Click += delegate
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = S.ChoosePrompt;
                    if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
                }
            };
            Controls.Add(choose);

            if (!firstRun)
            {
                var defBtn = new Button();
                defBtn.Text = S.MDefault;
                defBtn.Location = new Point(148, btnY);
                defBtn.Size = new Size(164, 30);
                defBtn.Click += delegate { box.Text = Settings.DefaultArchiveDir; };
                Controls.Add(defBtn);
            }

            var ok = new Button();
            ok.Text = S.Ok;
            ok.Location = new Point(firstRun ? 336 : 330, btnY);
            ok.Size = new Size(80, 30);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            var cancel = new Button();
            cancel.Text = S.Cancel;
            cancel.Location = new Point(firstRun ? 426 : 420, btnY);
            cancel.Size = new Size(80, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    static class TimedMessage
    {
        public static void Show(string text)
        {
            var form = new Form();
            form.Text = "NASA APOD";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ClientSize = new Size(360, 110);

            var label = new Label();
            label.Text = text;
            label.Location = new Point(14, 14);
            label.Size = new Size(332, 46);
            label.TextAlign = ContentAlignment.MiddleCenter;
            form.Controls.Add(label);

            var ok = new Button();
            ok.Text = S.Ok;
            ok.Location = new Point(140, 68);
            ok.Size = new Size(80, 28);
            ok.Click += delegate { form.Close(); };
            form.Controls.Add(ok);
            form.AcceptButton = ok;

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += delegate { timer.Stop(); form.Close(); };
            form.Shown += delegate { timer.Start(); };
            form.FormClosed += delegate { timer.Dispose(); };
            form.ShowDialog();
        }
    }

    // ------------------------------------------------------------------
    // The preview popup: the picture (trimmed, scaled to fit), its
    // title underneath — click the title to reveal the archived file
    // in Explorer, like the plugin's clickable title in Finder.
    // ------------------------------------------------------------------

    class PreviewForm : Form
    {
        private PictureBox pic;
        private Label titleLabel;
        private Label dateLabel;
        private string revealPath;

        public PreviewForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 28);
            StartPosition = FormStartPosition.Manual;

            pic = new PictureBox();
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.Black;
            Controls.Add(pic);

            titleLabel = new Label();
            titleLabel.ForeColor = Color.White;
            titleLabel.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            titleLabel.Cursor = Cursors.Hand;
            titleLabel.TextAlign = ContentAlignment.MiddleLeft;
            titleLabel.AutoEllipsis = true;
            titleLabel.Click += delegate
            {
                if (!string.IsNullOrEmpty(revealPath) && File.Exists(revealPath))
                {
                    Process.Start("explorer.exe", "/select,\"" + revealPath + "\"");
                    Hide();
                }
            };
            Controls.Add(titleLabel);

            dateLabel = new Label();
            dateLabel.ForeColor = Color.FromArgb(170, 170, 175);
            dateLabel.Font = new Font("Segoe UI", 9f);
            Controls.Add(dateLabel);

            Deactivate += delegate { Hide(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Hide();
            };
        }

        public void ShowFor(FetchResult result)
        {
            if (result == null || result.DisplayImage == null)
            {
                // Nothing to show (a video day or a failed fetch): the
                // menu carries the message; the popup stays away.
                return;
            }
            Image img = result.DisplayImage;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int maxW = Math.Min(720, wa.Width - 24);
            int maxH = (int)((wa.Height - 110) * 0.85);
            double scale = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
            int iw = Math.Max(160, (int)(img.Width * scale));
            int ih = Math.Max(120, (int)(img.Height * scale));

            ClientSize = new Size(iw + 16, ih + 66);
            pic.Location = new Point(8, 8);
            pic.Size = new Size(iw, ih);
            pic.Image = img;
            titleLabel.Location = new Point(10, ih + 14);
            titleLabel.Size = new Size(iw - 4, 22);
            titleLabel.Text = result.Info != null ? result.Info.Title : "";
            dateLabel.Location = new Point(10, ih + 38);
            dateLabel.Size = new Size(iw - 4, 18);
            dateLabel.Text = result.Info != null ? result.Info.Date : "";
            revealPath = result.ArchivePath;

            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            Show();
            Activate();
        }
    }

    // ------------------------------------------------------------------
    // Tray application: the telescope in the tray. Left-click toggles
    // the preview popup; the menu mirrors the plugin's dropdown —
    // title (clickable), Refresh with its answer, the Archive submenu,
    // plus the Windows-sibling extras (launch at login, self-update).
    // ------------------------------------------------------------------

    // ---------------- about dialog ----------------
    // The house About, shared by all three Windows tray apps:
    // name, version, Idea: Otakun, Coding: Dash, and a link labelled
    // "GitHub Repository" (never the raw URL as the link text).
    class AboutForm : Form
    {
        public AboutForm()
        {
            Text = S.MAbout.Replace("…", "");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(340, 196);
            Font = new Font("Microsoft YaHei UI", 9f);

            var title = new Label();
            title.Text = "NASA APOD for Windows";
            title.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            title.Location = new Point(16, 14);
            title.Size = new Size(308, 24);
            Controls.Add(title);

            var ver = new Label();
            ver.Text = "Version " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            ver.Location = new Point(16, 46);
            ver.Size = new Size(308, 18);
            Controls.Add(ver);

            var idea = new Label();
            idea.Text = "Idea: Otakun";
            idea.Location = new Point(16, 78);
            idea.Size = new Size(308, 18);
            Controls.Add(idea);

            var coding = new Label();
            coding.Text = "Coding: Dash";
            coding.Location = new Point(16, 100);
            coding.Size = new Size(308, 18);
            Controls.Add(coding);

            var link = new LinkLabel();
            link.Text = "GitHub Repository";
            link.Location = new Point(16, 128);
            link.Size = new Size(308, 18);
            link.LinkClicked += delegate { Process.Start("https://github.com/iam-otakun/nasa-apod-windows"); };
            Controls.Add(link);

            var btn = new Button();
            btn.Text = S.MClose;
            btn.Location = new Point(244, 158);
            btn.Size = new Size(80, 28);
            btn.DialogResult = DialogResult.OK;
            Controls.Add(btn);
            AcceptButton = btn;
            CancelButton = btn;
        }
    }

    class TrayApp : ApplicationContext
    {
        private NotifyIcon tray;
        private ToolStripMenuItem titleItem;
        private ToolStripMenuItem updateItem;
        private PreviewForm preview;
        private FetchResult current;
        private ReleaseInfo pendingUpdate;
        private bool fetching;
        private bool checkingUpdate;
        private System.Windows.Forms.Timer timer;

        public TrayApp()
        {
            tray = new NotifyIcon();
            tray.Text = "NASA APOD";
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { tray.Icon = SystemIcons.Application; }
            tray.Visible = true;
            tray.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) TogglePreview();
            };
            tray.BalloonTipClicked += delegate
            {
                if (pendingUpdate != null)
                {
                    if (MessageBox.Show("Download and install NASA APOD " + pendingUpdate.Tag + " now? The app will restart.", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        var ignored = InstallUpdate();
                    }
                }
            };

            var menu = new ContextMenuStrip();

            titleItem = new ToolStripMenuItem(S.NoImageYet);
            titleItem.Click += delegate { RevealCurrent(); };
            menu.Items.Add(titleItem);

            var refreshItem = new ToolStripMenuItem(S.MRefresh);
            refreshItem.Click += async delegate { await ManualRefresh(); };
            menu.Items.Add(refreshItem);

            var siteItem = new ToolStripMenuItem(S.MOpenSite);
            siteItem.Click += delegate { Process.Start(Core.PageUrl); };
            menu.Items.Add(siteItem);

            menu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem(S.MSettings);
            settingsItem.Click += delegate { ShowSettings(); };
            menu.Items.Add(settingsItem);

            updateItem = new ToolStripMenuItem(S.MUpdate);
            updateItem.Click += async delegate { await CheckForUpdates(true); };
            menu.Items.Add(updateItem);

            var aboutItem = new ToolStripMenuItem(S.MAbout);
            aboutItem.Click += delegate
            {
                using (var f = new AboutForm()) f.ShowDialog();
            };
            menu.Items.Add(aboutItem);

            var quitItem = new ToolStripMenuItem(S.MQuit);
            quitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(quitItem);

            tray.ContextMenuStrip = menu;
            preview = new PreviewForm();

            // First run: offer the archive setup once, like the plugin.
            if (Settings.ConfValue() == null)
            {
                using (var form = new SetupForm(Settings.DefaultArchiveDir, true))
                {
                    if (form.ShowDialog() == DialogResult.OK) Settings.SaveConf(Settings.ExpandHome(form.PathText));
                    else Settings.SaveConf(Settings.DefaultArchiveDir);
                }
            }
            else if (Settings.ConfValue() == "~/Pictures/NASA-Apod" || Settings.ConfValue() == "~/Pictures\\NASA-Apod")
            {
                // Heal the pre-1.0.2 default, which was saved in the
                // macOS plugin's tilde form instead of a Windows path.
                Settings.SaveConf(Settings.DefaultArchiveDir);
            }

            // The archive folder exists from the moment the app runs,
            // not only after the first picture lands in it.
            string archiveDir = Settings.EffectiveDir();
            if (archiveDir != null)
            {
                try { Directory.CreateDirectory(archiveDir); }
                catch (Exception ex) { Core.Log("Create archive dir failed: " + ex.Message); }
            }

            RefreshMenuState();

            if (NeedsFetch())
            {
                var ignored = SilentFetch();
            }

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 30 * 60 * 1000;
            timer.Tick += delegate
            {
                if (NeedsFetch())
                {
                    var ignored = SilentFetch();
                }
            };
            timer.Start();

            AppUpdate.CleanupOld();
            if (DateTime.Now - Settings.LastAppCheck > TimeSpan.FromHours(20))
            {
                var ignoredCheck = CheckForUpdates(false);
            }
        }

        protected override void ExitThreadCore()
        {
            if (timer != null) timer.Stop();
            if (preview != null) preview.Dispose();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.ExitThreadCore();
        }

        // APOD rolls over at midnight US Eastern; fetch when the
        // remembered date is behind, or when the last fetch is stale
        // (the plugin's 12-hour cycle).
        private static bool NeedsFetch()
        {
            string state = Settings.StateDate();
            string easternToday;
            try
            {
                TimeZoneInfo et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                easternToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch { easternToday = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
            if (state == null || string.Compare(state, easternToday, StringComparison.Ordinal) < 0) return true;
            return DateTime.Now - Settings.LastFetch > TimeSpan.FromHours(12);
        }

        private async Task SilentFetch()
        {
            if (fetching) return;
            fetching = true;
            try
            {
                FetchResult result = await Core.FetchLatest();
                if (result.Error == null && result.Info != null)
                {
                    FetchResult old = current;
                    current = result;
                    if (preview != null && preview.Visible) preview.Hide();
                    if (old != null && old.DisplayImage != null && !object.ReferenceEquals(old.DisplayImage, result.DisplayImage))
                        old.DisplayImage.Dispose();
                }
                else if (current == null)
                {
                    titleItem.Text = result.Error ?? S.Error;
                }
                RefreshMenuState();
            }
            catch (Exception ex) { Core.Log("SilentFetch failed: " + ex.Message); }
            finally { fetching = false; }
        }

        // The plugin's Refresh: compare the remembered date with
        // NASA's latest, answer out loud, then fetch for real.
        private async Task ManualRefresh()
        {
            if (fetching) return;
            string prev = Settings.StateDate();
            string latest = await Core.PeekLatestDate();
            if (latest == null) TimedMessage.Show(S.Error);
            else if (prev != null && string.Compare(latest, prev, StringComparison.Ordinal) <= 0) TimedMessage.Show(S.MsgUpToDate);
            else TimedMessage.Show(S.MsgUpdated);
            await SilentFetch();
            if (current != null && current.DisplayImage != null && preview != null && preview.Visible)
                preview.ShowFor(current);
        }

        private void RefreshMenuState()
        {
            if (current != null && current.Info != null)
            {
                titleItem.Text = current.Info.Title;
                string trayText = "NASA APOD - " + current.Info.Title;
                if (trayText.Length >= 64) trayText = trayText.Substring(0, 63);
                tray.Text = trayText;
            }
        }

        private void TogglePreview()
        {
            if (preview == null) return;
            if (preview.Visible) { preview.Hide(); return; }
            if (current != null && current.DisplayImage != null)
            {
                preview.ShowFor(current);
            }
            else if (!fetching)
            {
                var ignored = SilentFetch();
            }
        }

        private void RevealCurrent()
        {
            if (current != null && !string.IsNullOrEmpty(current.ArchivePath) && File.Exists(current.ArchivePath))
            {
                Process.Start("explorer.exe", "/select,\"" + current.ArchivePath + "\"");
                return;
            }
            string dir = Settings.EffectiveDir();
            if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
        }

        // The Settings... item in the menu's last group (the same
        // final group as our other apps): the archive folder plus
        // launch-at-login, in the first-run form's Settings mode.
        private void ShowSettings()
        {
            string cur = Settings.EffectiveDir() ?? "";
            using (var form = new SetupForm(cur, false))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    Settings.SaveConf(Settings.ExpandHome(form.PathText));
                    Settings.LaunchAtLogin = form.LaunchChecked;
                    RefreshMenuState();
                    var ignored = SilentFetch();
                }
            }
        }

        private async Task CheckForUpdates(bool interactive)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            try
            {
                ReleaseInfo rel = await AppUpdate.FetchLatest();
                if (rel == null)
                {
                    if (interactive)
                        MessageBox.Show("Could not check for updates. Please try again later.", "NASA APOD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Settings.LastAppCheck = DateTime.Now;
                Version cur = AppUpdate.CurrentVersion;
                if (rel.Version > cur)
                {
                    pendingUpdate = rel;
                    updateItem.Text = (S.Zh ? "更新到 " : "Update to ") + rel.Tag + "…";
                    if (interactive)
                    {
                        if (MessageBox.Show("NASA APOD " + rel.Tag + " is available (you have v" + cur.ToString(3) + ").\n\nDownload and install it now? The app will restart.", "Update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            await InstallUpdate();
                        }
                    }
                    else
                    {
                        tray.BalloonTipTitle = "NASA APOD update";
                        tray.BalloonTipText = "Version " + rel.Tag + " is available. Click to update.";
                        tray.BalloonTipIcon = ToolTipIcon.Info;
                        tray.ShowBalloonTip(8000);
                    }
                }
                else if (interactive)
                {
                    MessageBox.Show("NASA APOD is up to date (v" + cur.ToString(3) + ").", S.MUpdate.Replace("…", ""), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex) { Core.Log("CheckForUpdates failed: " + ex.Message); }
            finally { checkingUpdate = false; }
        }

        private async Task InstallUpdate()
        {
            if (pendingUpdate == null) return;
            updateItem.Enabled = false;
            try
            {
                tray.BalloonTipTitle = "NASA APOD";
                tray.BalloonTipText = "Downloading update " + pendingUpdate.Tag + "…";
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(4000);
                string staged = await AppUpdate.Stage(pendingUpdate);
                if (staged == null)
                {
                    MessageBox.Show("The update could not be downloaded. Please try again later, or download it from GitHub.", "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (AppUpdate.SwapAndRelaunch(staged))
                {
                    ExitThread();
                }
                else
                {
                    MessageBox.Show("The update could not be installed. Your current version is untouched.", "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex) { Core.Log("InstallUpdate failed: " + ex.Message); }
            finally { updateItem.Enabled = true; }
        }
    }

    // ------------------------------------------------------------------
    // Entry point
    // ------------------------------------------------------------------

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // A bare csc-built exe gets legacy TLS defaults (strong
            // crypto off) and modern hosts refuse the handshake —
            // NASA did (2026-11-24). Opt into strong crypto and
            // TLS 1.2 before the networking stack wakes up. The
            // shipped NasaApod.exe.config declares the same.
            try { AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false); }
            catch { }
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11; }
            catch { }

            bool selftest = false;
            bool updated = false;
            foreach (string a in args)
            {
                if (a == "--selftest") selftest = true;
                if (a == "--updated") updated = true;
            }

            if (selftest) return RunSelfTest();

            bool createdNew = false;
            Mutex mutex = null;
            int attempts = updated ? 50 : 1;
            for (int i = 0; i < attempts && !createdNew; i++)
            {
                if (i > 0) Thread.Sleep(200);
                mutex = new Mutex(true, "NasaApodForWindows", out createdNew);
                if (!createdNew) { mutex.Dispose(); mutex = null; }
            }
            if (!createdNew) return 0; // already running

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
            return 0;
        }

        // Headless verification: one fetch cycle, results written to
        // selftest.txt in the state dir; exit code 0 unless it failed.
        private static int RunSelfTest()
        {
            var lines = new List<string>();
            int code = 0;
            try
            {
                FetchResult result = Core.FetchLatest().GetAwaiter().GetResult();
                if (result.Info != null)
                {
                    lines.Add("Date=" + result.Info.Date);
                    lines.Add("Title=" + result.Info.Title);
                    lines.Add("ArchivePath=" + (result.ArchivePath ?? ""));
                    lines.Add("DisplaySize=" + (result.DisplayImage == null
                        ? "none"
                        : result.DisplayImage.Width + "x" + result.DisplayImage.Height));
                }
                if (result.Error != null)
                {
                    lines.Add("Error=" + result.Error);
                    code = 2;
                }
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
                code = 3;
            }
            try
            {
                if (!Directory.Exists(Settings.StateDir)) Directory.CreateDirectory(Settings.StateDir);
                File.WriteAllLines(Path.Combine(Settings.StateDir, "selftest.txt"), lines.ToArray());
            }
            catch { }
            return code;
        }
    }
}
