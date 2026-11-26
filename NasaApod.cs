// NASA APOD for Windows
// A system-tray app that shows NASA's Astronomy Picture of the Day in a
// popup by the tray — and quietly archives the original picture every
// day. It is the Windows sibling of the SwiftBar plugin
// swiftbar-nasa-apod and mirrors it feature for feature: the day's
// photo from the hero figure in front of the page's main heading
// (NASA's og:image is a fixed site template and is never used), the
// full-size original archived as "YYYY-MM-DD Title.jpg" with a
// same-named .txt holding the credit and the full explanation, video
// days archived as .mp4 (first frame shown in the popup), pure-black
// borders trimmed for display only, a Refresh that answers back
// ("Up to date!" / "Update successfully"), and an Archive submenu
// that owns the archive folder (default Pictures\NASA-Apod, stored
// in ~/.nasa-apod.conf — the same file format as the macOS plugin).
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
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

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

        // Hovering over the tray icon pops the preview (Settings...
        // can turn it off). Default on.
        public static bool HoverPreview
        {
            get { return GetString("HoverPreview", "1") != "0"; }
            set { SetString("HoverPreview", value ? "1" : "0"); }
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
        public static string SHover { get { return T("鼠标悬停在图标上时弹出预览", "Show preview when hovering over the tray icon"); } }
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
        public static string MExplanation { get { return T("查看说明…", "Explanation..."); } }
        public static string MWallpaper { get { return T("设为壁纸", "Set as Wallpaper"); } }
        public static string MPrevDay { get { return T("◀ 前一天", "◀ Previous Day"); } }
        public static string MNextDay { get { return T("后一天 ▶", "Next Day ▶"); } }
        public static string MPlayVideo { get { return T("播放视频", "Play Video"); } }
        public static string MVideoNote { get { return T("今日条目是视频", "Today's entry is a video"); } }
        public static string WallpaperSet { get { return T("已设为壁纸", "Wallpaper set"); } }
    }

    // ------------------------------------------------------------------
    // Core fetch logic — a direct port of the macOS plugin's:
    // the day's photo hangs off the hero figure in front of the
    // entry's title; og:image is a fixed site template and is never
    // used. The archive stores the full-size original (the
    // content/dam twin of the rendered dynamicimage file).
    // ------------------------------------------------------------------

    public class ApodInfo
    {
        public string Date;        // yyyy-MM-dd
        public string Title;       // as written on the page, unescaped
        public string ImageUrl;    // rendered image for display
        public string ArchiveUrl;  // full-size original for the archive
        public string Credit = "";
        public string Explanation = "";
        public bool IsVideo;
        public string VideoUrl;
    }

    public class FetchResult
    {
        public ApodInfo Info;
        public Image DisplayImage;   // black borders trimmed, full size
        public string ArchivePath;   // where today's original lives (null = archiving off)
        public string TxtPath;       // today's same-named .txt sidecar
        public string VideoPath;     // today's archived .mp4 (video days)
        public bool VideoTooLarge;   // video day, over the 100 MB cap: link only
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
            return MakeClient(60);
        }

        private static HttpClient MakeClient(int timeoutSeconds)
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NasaApod-Windows/1.1");
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

        // --------------------------------------------------------------
        // Page parsing — a 1:1 port of plugin v1.9.3's extraction.
        // Everything anchors on the entry's own title heading: the
        // page's first image-article links and "YYYY Month DD" strings
        // belong to the PREVIOUS entry's navigation, and og:image is
        // the site's template image — all three have silently poisoned
        // archives before, so none of them is consulted any more.
        // --------------------------------------------------------------

        private static readonly Regex TitleRe =
            new Regex("display-48 margin-top-0 margin-bottom-3\">([^<]+)<");

        private static bool FindTitle(string html, out string title, out int start, out int end)
        {
            title = null; start = -1; end = -1;
            if (string.IsNullOrEmpty(html)) return false;
            var m = TitleRe.Match(html);
            if (!m.Success) return false;
            title = HttpUtility.HtmlDecode(m.Groups[1].Value).Trim();
            start = m.Index;
            end = m.Index + m.Length;
            return true;
        }

        // The entry's date, from the byline near its own title
        // ("November 25, 2026"); null when the byline is not found.
        public static string ParseDate(string html)
        {
            string title; int start, end;
            if (!FindTitle(html, out title, out start, out end)) return null;
            int segStart = Math.Max(0, start - 4000);
            int segLen = Math.Min(html.Length - segStart, (start - segStart) + 8000);
            string seg = html.Substring(segStart, segLen);
            var m = Regex.Match(seg,
                "(January|February|March|April|May|June|July|August|September|October|November|December) (\\d{1,2}), (\\d{4})");
            if (!m.Success) return null;
            int month;
            if (!Months.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out month)) return null;
            return string.Format(CultureInfo.InvariantCulture, "{0}-{1:00}-{2:00}",
                m.Groups[3].Value, month, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        public class HeroMedia
        {
            public string ImageUrl;
            public string ArchiveUrl;
            public string VideoUrl;
        }

        private static string AbsUrl(string u)
        {
            if (string.IsNullOrEmpty(u)) return u;
            if (u.StartsWith("/", StringComparison.Ordinal)) return "https://www.nasa.gov" + u;
            return u;
        }

        // The day's media hangs in the hero figure in front of the
        // title (hds-media-inner): on image days the figure's link is
        // the original file and its <img> the rendered one; on video
        // days the figure holds a <video>. Only the last figure/video
        // in the 30,000 characters before the title counts.
        public static HeroMedia ParseHero(string html)
        {
            string title; int start, end;
            if (!FindTitle(html, out title, out start, out end)) return null;
            int winStart = Math.Max(0, start - 30000);
            string win = html.Substring(winStart, start - winStart);
            int fi = win.LastIndexOf("<figure class=\"hds-media-inner", StringComparison.Ordinal);
            if (fi >= 0)
            {
                string seg = win.Substring(fi, Math.Min(6000, win.Length - fi));
                var im = Regex.Match(seg, "<img[^>]+src=\"([^\"]+)\"");
                if (im.Success)
                {
                    var hero = new HeroMedia();
                    string disp = HttpUtility.HtmlDecode(im.Groups[1].Value);
                    var am = Regex.Match(seg, "<a[^>]+href=\"([^\"]+)\"");
                    string href = am.Success ? HttpUtility.HtmlDecode(am.Groups[1].Value) : "";
                    string hrefNoQ = href.Split('?')[0];
                    if (href.IndexOf("/content/dam/", StringComparison.Ordinal) >= 0
                        || Regex.IsMatch(hrefNoQ, "\\.(jpg|jpeg|png)$", RegexOptions.IgnoreCase))
                    {
                        hero.ArchiveUrl = AbsUrl(href);
                    }
                    else
                    {
                        string arch = disp.Replace("dynamicimage/assets/science/", "content/dam/science/");
                        int q = arch.IndexOf('?');
                        if (q >= 0) arch = arch.Substring(0, q);
                        hero.ArchiveUrl = AbsUrl(arch);
                    }
                    hero.ImageUrl = AbsUrl(disp);
                    return hero;
                }
            }
            int vi = win.LastIndexOf("<video", StringComparison.Ordinal);
            if (vi >= 0)
            {
                string seg = win.Substring(vi, Math.Min(4000, win.Length - vi));
                var sm = Regex.Match(seg, "<source[^>]+src=\"([^\"]+)\"");
                if (sm.Success)
                {
                    var hero = new HeroMedia();
                    hero.VideoUrl = AbsUrl(HttpUtility.HtmlDecode(sm.Groups[1].Value));
                    return hero;
                }
            }
            return null;
        }

        private static string PlainText(string frag)
        {
            frag = Regex.Replace(frag, "(?i)<br\\s*/?>", "\n");
            frag = Regex.Replace(frag, "<[^>]+>", "");
            frag = HttpUtility.HtmlDecode(frag);
            var lines = new List<string>();
            foreach (string ln in frag.Split('\n'))
            {
                string t = Regex.Replace(ln, "\\s+", " ").Trim();
                if (t.Length > 0) lines.Add(t);
            }
            return string.Join("\n", lines.ToArray());
        }

        // Credit (the hds-credits block in front of the title) and the
        // explanation (the media-detail-hero__description paragraph
        // after the title that carries the "Explanation:" lead-in,
        // cut at "Tomorrow's picture"). The page mixes in JSON-escaped
        // copies, so escapes are undone before any tag work.
        public static void ParseEntryText(string html, out string credit, out string explanation)
        {
            credit = "";
            explanation = "";
            string title; int start, end;
            if (!FindTitle(html, out title, out start, out end)) return;
            int preStart = Math.Max(0, start - 6000);
            string pre = html.Substring(preStart, start - preStart);
            int ci = pre.LastIndexOf("hds-credits", StringComparison.Ordinal);
            if (ci >= 0)
            {
                string seg = pre.Substring(ci, Math.Min(3000, pre.Length - ci));
                var cm = Regex.Match(seg, "<div>(.*?)</div>", RegexOptions.Singleline);
                if (cm.Success) credit = PlainText(cm.Groups[1].Value);
            }
            int pos = end;
            for (int i = 0; i < 6; i++)
            {
                int ei = html.IndexOf("media-detail-hero__description", pos, StringComparison.Ordinal);
                if (ei < 0) break;
                int gt = html.IndexOf('>', ei);
                int pe = gt >= 0 ? html.IndexOf("</p>", gt, StringComparison.Ordinal) : -1;
                if (gt >= 0 && pe > gt)
                {
                    string frag = html.Substring(gt + 1, pe - gt - 1);
                    frag = frag.Replace("\\/", "/").Replace("\\'", "'")
                        .Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"");
                    if (frag.IndexOf("Explanation:", StringComparison.Ordinal) >= 0)
                    {
                        var lead = Regex.Match(frag, "(?is)^.*?<strong>\\s*Explanation:\\s*</strong>");
                        if (lead.Success) frag = frag.Substring(lead.Length);
                        int cut = frag.IndexOf("Tomorrow's picture", StringComparison.Ordinal);
                        if (cut >= 0)
                        {
                            frag = frag.Substring(0, cut);
                            frag = Regex.Replace(frag, "(?i)<strong>\\s*$", "");
                        }
                        explanation = PlainText(frag);
                        break;
                    }
                }
                pos = ei + 10;
            }
        }

        // APOD's day rolls over at midnight US Eastern.
        public static string EasternToday()
        {
            try
            {
                TimeZoneInfo et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch { return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        }

        // The same-named .txt sidecar: title, date, credit, the full
        // explanation and the source links. Written once, never
        // overwritten — the plugin's completeness rule (every day
        // leaves its artefact in the archive).
        public static string WriteEntryTxt(string dir, string date, string title,
            string credit, string expl, string mediaLabel, string mediaUrl)
        {
            try
            {
                string name = !string.IsNullOrEmpty(title) ? date + " " + title + ".txt" : date + ".txt";
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
                var lines = new List<string>();
                lines.Add(title ?? "");
                lines.Add("Date: " + date);
                if (!string.IsNullOrEmpty(credit)) lines.Add(credit);
                lines.Add("");
                lines.Add("Explanation:");
                lines.Add(!string.IsNullOrEmpty(expl) ? expl : "(extraction failed)");
                lines.Add("");
                if (!string.IsNullOrEmpty(mediaUrl)) lines.Add(mediaLabel + ": " + mediaUrl);
                lines.Add("APOD: https://apod.nasa.gov/apod/");
                File.WriteAllText(path, string.Join("\n", lines.ToArray()) + "\n", new UTF8Encoding(false));
                return path;
            }
            catch (Exception ex)
            {
                Log("WriteEntryTxt failed: " + ex.Message);
                return null;
            }
        }

        public static async Task<long?> HeadLength(string url)
        {
            try
            {
                using (HttpClient client = MakeClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Head, url);
                    HttpResponseMessage resp = await client.SendAsync(req).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength.HasValue)
                        return resp.Content.Headers.ContentLength.Value;
                }
            }
            catch (Exception ex) { Log("HEAD failed (" + url + "): " + Describe(ex)); }
            return null;
        }

        public class BinaryResult
        {
            public byte[] Data;
            public bool TooLarge;
        }

        // Download with a size cap (the video rule): the length header
        // is checked before a single byte is read, and the body again
        // after, in case the header lied or was absent.
        public static async Task<BinaryResult> DownloadCapped(string url, long maxBytes)
        {
            try
            {
                return await DownloadCappedOnce(url, maxBytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("DownloadCapped first attempt failed (" + url + "): " + Describe(ex));
            }
            await Task.Delay(1500).ConfigureAwait(false);
            return await DownloadCappedOnce(url, maxBytes).ConfigureAwait(false);
        }

        private static async Task<BinaryResult> DownloadCappedOnce(string url, long maxBytes)
        {
            var r = new BinaryResult();
            using (HttpClient client = MakeClient(300))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                HttpResponseMessage resp = await client.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength.HasValue
                    && resp.Content.Headers.ContentLength.Value > maxBytes)
                {
                    r.TooLarge = true;
                    return r;
                }
                byte[] data = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (data.Length > maxBytes) { r.TooLarge = true; return r; }
                r.Data = data;
                return r;
            }
        }

        // Just the latest entry's date, for the Refresh comparison.
        public static async Task<string> PeekLatestDate()
        {
            try
            {
                string html = await FetchText(PageUrl).ConfigureAwait(false);
                return ParseDate(html);
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

        // One full cycle: fetch the page, extract the day (plugin
        // 1.9.3's rules), archive the artefact plus its .txt, remember
        // the date. Video days archive the .mp4 itself (100 MB cap)
        // and show its first frame.
        public static async Task<FetchResult> FetchLatest()
        {
            var result = new FetchResult();
            string html;
            try
            {
                html = await FetchText(PageUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Fetch failed: " + Describe(ex));
                result.Error = S.Error;
                return result;
            }
            string title; int tStart, tEnd;
            if (!FindTitle(html, out title, out tStart, out tEnd))
            {
                Log("Title anchor not found on the APOD page");
                result.Error = S.Error;
                return result;
            }
            var info = new ApodInfo();
            info.Title = title;
            info.Date = ParseDate(html) ?? EasternToday();
            string credit, expl;
            ParseEntryText(html, out credit, out expl);
            info.Credit = credit;
            info.Explanation = expl;
            result.Info = info;

            HeroMedia hero = ParseHero(html);
            if (hero == null)
            {
                Log("Hero extraction failed");
                result.Error = S.Error;
                return result;
            }

            Settings.SaveStateDate(info.Date);
            Settings.LastFetch = DateTime.Now;
            string dir = Settings.EffectiveDir();
            string safeTitle = SanitizeFileName(info.Title);

            if (!string.IsNullOrEmpty(hero.VideoUrl))
            {
                info.IsVideo = true;
                info.VideoUrl = hero.VideoUrl;
                await ArchiveVideoDay(result, dir, safeTitle).ConfigureAwait(false);
                return result;
            }

            info.ImageUrl = hero.ImageUrl;
            info.ArchiveUrl = hero.ArchiveUrl;
            byte[] displayBytes;
            try
            {
                displayBytes = await DownloadBinary(info.ImageUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("Display download failed: " + Describe(ex));
                result.Error = S.Error;
                return result;
            }

            // Archive the full-size original (never overwrite) and
            // the day's .txt sidecar.
            if (dir != null)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string name = safeTitle.Length > 0 ? info.Date + " " + safeTitle + ".jpg" : info.Date + ".jpg";
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
                    result.TxtPath = WriteEntryTxt(dir, info.Date, safeTitle, info.Credit,
                        info.Explanation, "Image",
                        !string.IsNullOrEmpty(info.ArchiveUrl) ? info.ArchiveUrl : info.ImageUrl);
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

        // A video day: the .txt always lands; the .mp4 itself too,
        // unless it exceeds the 100 MB cap (then the .txt's link is
        // the artefact). The popup shows the video's first frame,
        // taken by the shell's image factory — no poster exists on
        // the site for these videos.
        private static async Task ArchiveVideoDay(FetchResult result, string dir, string safeTitle)
        {
            ApodInfo info = result.Info;
            if (dir == null) return;
            try
            {
                Directory.CreateDirectory(dir);
                result.TxtPath = WriteEntryTxt(dir, info.Date, safeTitle, info.Credit,
                    info.Explanation, "Video", info.VideoUrl);
                string vname = safeTitle.Length > 0 ? info.Date + " " + safeTitle + ".mp4" : info.Date + ".mp4";
                string vpath = Path.Combine(dir, vname);
                if (File.Exists(vpath))
                {
                    result.VideoPath = vpath;
                }
                else
                {
                    long? size = await HeadLength(info.VideoUrl).ConfigureAwait(false);
                    if (size.HasValue && size.Value > 100L * 1024 * 1024)
                    {
                        result.VideoTooLarge = true;
                        Log("Video too large (" + size.Value + " bytes), link only");
                    }
                    else
                    {
                        BinaryResult dl = await DownloadCapped(info.VideoUrl, 100L * 1024 * 1024)
                            .ConfigureAwait(false);
                        if (dl.TooLarge)
                        {
                            result.VideoTooLarge = true;
                            Log("Video too large (download), link only");
                        }
                        else if (dl.Data != null)
                        {
                            File.WriteAllBytes(vpath, dl.Data);
                            result.VideoPath = vpath;
                            Log("Archived video " + vname + " (" + dl.Data.Length + " bytes)");
                        }
                    }
                }
                result.ArchivePath = result.VideoPath ?? result.TxtPath;
                if (result.VideoPath != null)
                {
                    Image frame = ShellThumb.GetImage(result.VideoPath, 720);
                    if (frame != null)
                    {
                        result.DisplayImage = TrimBlackBorders(new Bitmap(frame));
                        frame.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Video archive failed: " + ex.Message);
            }
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
        private CheckBox hoverBox;

        public string PathText { get { return box.Text.Trim(); } }
        public bool LaunchChecked { get { return loginBox != null && loginBox.Checked; } }
        public bool HoverChecked { get { return hoverBox != null && hoverBox.Checked; } }

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
            ClientSize = new Size(520, firstRun ? 218 : 284);

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

                hoverBox = new CheckBox();
                hoverBox.Text = S.SHover;
                hoverBox.Location = new Point(14, 168);
                hoverBox.Size = new Size(400, 24);
                hoverBox.Checked = Settings.HoverPreview;
                Controls.Add(hoverBox);
                btnY = 206;
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
    // Shell thumbnails: a video's first frame (or any file's preview)
    // through the same image factory Explorer uses. Null when
    // Windows has no thumbnail for the file.
    // ------------------------------------------------------------------

    static class ShellThumb
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static Image GetImage(string path, int maxDim)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                factory.GetImage(new SIZE(maxDim, maxDim), 0x1 /* SIIGBF_BIGGERSIZEOK */, out hbm);
                if (hbm == IntPtr.Zero) return null;
                using (Image img = Image.FromHbitmap(hbm))
                {
                    return new Bitmap(img);
                }
            }
            catch { return null; }
            finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        }
    }

    static class Wallpaper
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        public static bool Set(string path)
        {
            // SPI_SETDESKWALLPAPER, persisted + broadcast.
            return SystemParametersInfo(20, 0, path, 0x01 | 0x02);
        }
    }

    // ------------------------------------------------------------------
    // The explanation window's data: one entry per archived day,
    // rebuilt from the archive folder itself (the .txt sidecars are
    // the record — the same files the Mac app pages through).
    // ------------------------------------------------------------------

    class ArchiveDay
    {
        public string Date;
        public string Title = "";
        public string Credit = "";
        public string Explanation = "";
        public string ImagePath;
        public string VideoPath;
        public string MediaUrl = "";
    }

    static class ArchiveScan
    {
        public static List<ArchiveDay> Scan(string dir)
        {
            var map = new Dictionary<string, ArchiveDay>();
            if (dir != null && Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    var m = Regex.Match(name, "^(\\d{4}-\\d{2}-\\d{2})");
                    if (!m.Success) continue;
                    string date = m.Groups[1].Value;
                    ArchiveDay day;
                    if (!map.TryGetValue(date, out day))
                    {
                        day = new ArchiveDay();
                        day.Date = date;
                        map[date] = day;
                    }
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".png") day.ImagePath = f;
                    else if (ext == ".mp4") day.VideoPath = f;
                    else if (ext == ".txt") ReadTxt(f, day);
                }
            }
            var list = new List<ArchiveDay>(map.Values);
            list.Sort(delegate(ArchiveDay a, ArchiveDay b)
            {
                return string.Compare(a.Date, b.Date, StringComparison.Ordinal);
            });
            return list;
        }

        private static void ReadTxt(string path, ArchiveDay day)
        {
            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length > 0) day.Title = lines[0];
                var expl = new List<string>();
                bool inExpl = false;
                foreach (string ln in lines)
                {
                    if (ln.StartsWith("Image: ", StringComparison.Ordinal)
                        || ln.StartsWith("Video: ", StringComparison.Ordinal))
                    {
                        int c = ln.IndexOf(':');
                        if (c >= 0) day.MediaUrl = ln.Substring(c + 2);
                        inExpl = false;
                        continue;
                    }
                    if (ln.StartsWith("APOD: ", StringComparison.Ordinal)) { inExpl = false; continue; }
                    if (inExpl) { expl.Add(ln); continue; }
                    if (ln == "Explanation:") inExpl = true;
                }
                for (int i = 1; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("Date: ", StringComparison.Ordinal))
                    {
                        if (i + 1 < lines.Length && lines[i + 1].Length > 0 && lines[i + 1] != "Explanation:")
                            day.Credit = lines[i + 1];
                        break;
                    }
                }
                while (expl.Count > 0 && expl[expl.Count - 1].Length == 0) expl.RemoveAt(expl.Count - 1);
                day.Explanation = string.Join("\n", expl.ToArray());
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Explanation window: the day's picture (or a video's first
    // frame) with its title, date, credit and full explanation, and
    // prev/next paging through the local archive — the Mac app's
    // detail window, Windows-style.
    // ------------------------------------------------------------------

    class ExplanationForm : Form
    {
        private List<ArchiveDay> days;
        private int index;
        private PictureBox pic;
        private Label titleLabel;
        private Label metaLabel;
        private TextBox explBox;
        private Button prevBtn;
        private Button nextBtn;
        private Button playBtn;
        private Button wallBtn;
        private Image shownImage;

        public ExplanationForm(List<ArchiveDay> days, string startDate)
        {
            this.days = days;
            index = days.Count - 1;
            if (startDate != null)
            {
                for (int i = 0; i < days.Count; i++)
                    if (days[i].Date == startDate) { index = i; break; }
            }

            Text = "NASA APOD";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(780, 860);
            Font = new Font("Microsoft YaHei UI", 9f);
            FormClosed += delegate { SetImage(null); };

            pic = new PictureBox();
            pic.Location = new Point(12, 12);
            pic.Size = new Size(756, 430);
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            pic.BackColor = Color.Black;
            pic.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(pic);

            titleLabel = new Label();
            titleLabel.Location = new Point(12, 452);
            titleLabel.Size = new Size(756, 28);
            titleLabel.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
            titleLabel.AutoEllipsis = true;
            titleLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(titleLabel);

            metaLabel = new Label();
            metaLabel.Location = new Point(12, 484);
            metaLabel.Size = new Size(756, 22);
            metaLabel.ForeColor = Color.FromArgb(110, 110, 115);
            metaLabel.AutoEllipsis = true;
            metaLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(metaLabel);

            explBox = new TextBox();
            explBox.Location = new Point(12, 512);
            explBox.Size = new Size(756, 292);
            explBox.Multiline = true;
            explBox.ReadOnly = true;
            explBox.ScrollBars = ScrollBars.Vertical;
            explBox.Font = new Font("Microsoft YaHei UI", 10f);
            explBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(explBox);

            prevBtn = new Button();
            prevBtn.Text = S.MPrevDay;
            prevBtn.Location = new Point(12, 816);
            prevBtn.Size = new Size(110, 30);
            prevBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            prevBtn.Click += delegate { if (index > 0) { index--; ShowDay(); } };
            Controls.Add(prevBtn);

            nextBtn = new Button();
            nextBtn.Text = S.MNextDay;
            nextBtn.Location = new Point(130, 816);
            nextBtn.Size = new Size(110, 30);
            nextBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            nextBtn.Click += delegate { if (index < days.Count - 1) { index++; ShowDay(); } };
            Controls.Add(nextBtn);

            playBtn = new Button();
            playBtn.Text = S.MPlayVideo;
            playBtn.Location = new Point(360, 816);
            playBtn.Size = new Size(110, 30);
            playBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            playBtn.Click += delegate
            {
                ArchiveDay d = days[index];
                if (d.VideoPath != null && File.Exists(d.VideoPath)) Process.Start(d.VideoPath);
                else if (d.MediaUrl.Length > 0) Process.Start(d.MediaUrl);
            };
            Controls.Add(playBtn);

            wallBtn = new Button();
            wallBtn.Text = S.MWallpaper;
            wallBtn.Location = new Point(478, 816);
            wallBtn.Size = new Size(110, 30);
            wallBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            wallBtn.Click += delegate
            {
                ArchiveDay d = days[index];
                if (d.ImagePath != null && File.Exists(d.ImagePath))
                {
                    if (Wallpaper.Set(d.ImagePath)) TimedMessage.Show(S.WallpaperSet);
                }
            };
            Controls.Add(wallBtn);

            var closeBtn = new Button();
            closeBtn.Text = S.MClose;
            closeBtn.Location = new Point(658, 816);
            closeBtn.Size = new Size(110, 30);
            closeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            closeBtn.Click += delegate { Close(); };
            Controls.Add(closeBtn);

            ShowDay();
        }

        private void SetImage(Image img)
        {
            Image old = shownImage;
            shownImage = img;
            pic.Image = img;
            if (old != null) old.Dispose();
        }

        private void ShowDay()
        {
            ArchiveDay d = days[index];
            titleLabel.Text = d.Title;
            string meta = d.Date;
            if (d.Credit.Length > 0) meta += "  ·  " + d.Credit;
            if (d.VideoPath != null || (d.ImagePath == null && d.MediaUrl.Length > 0)) meta += "  ·  " + S.MVideoNote;
            metaLabel.Text = meta;
            explBox.Text = d.Explanation;
            explBox.SelectionStart = 0;
            explBox.ScrollToCaret();
            prevBtn.Enabled = index > 0;
            nextBtn.Enabled = index < days.Count - 1;
            playBtn.Visible = d.VideoPath != null || (d.ImagePath == null && d.MediaUrl.Length > 0);
            wallBtn.Enabled = d.ImagePath != null && File.Exists(d.ImagePath);
            Image img = null;
            try
            {
                if (d.ImagePath != null && File.Exists(d.ImagePath))
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(d.ImagePath)))
                    using (var raw = Image.FromStream(ms))
                    {
                        img = Core.TrimBlackBorders(new Bitmap(raw));
                    }
                }
                else if (d.VideoPath != null && File.Exists(d.VideoPath))
                {
                    Image frame = ShellThumb.GetImage(d.VideoPath, 900);
                    if (frame != null)
                    {
                        img = Core.TrimBlackBorders(new Bitmap(frame));
                        frame.Dispose();
                    }
                }
            }
            catch (Exception ex) { Core.Log("Explanation image failed: " + ex.Message); }
            SetImage(img);
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
        private ToolStripMenuItem wallpaperItem;
        private System.Windows.Forms.Timer hoverTimer;
        private System.Windows.Forms.Timer hoverWatchdog;
        private DateTime lastTrayMove = DateTime.MinValue;
        private bool hoverPending;
        private bool hoverShown;
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
            tray.MouseMove += delegate { NoteTrayMove(); };
            hoverTimer = new System.Windows.Forms.Timer();
            hoverTimer.Tick += delegate { HoverPop(); };
            hoverWatchdog = new System.Windows.Forms.Timer();
            hoverWatchdog.Interval = 250;
            hoverWatchdog.Tick += delegate
            {
                // Re-arm the hover pop only after the pointer has
                // left the icon for a while (no move events).
                if (hoverShown && preview != null && !preview.Visible
                    && (DateTime.Now - lastTrayMove).TotalMilliseconds > 900)
                    hoverShown = false;
            };
            hoverWatchdog.Start();
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

            var explanationItem = new ToolStripMenuItem(S.MExplanation);
            explanationItem.Click += delegate { ShowExplanation(); };
            menu.Items.Add(explanationItem);

            wallpaperItem = new ToolStripMenuItem(S.MWallpaper);
            wallpaperItem.Click += delegate { SetWallpaperCurrent(); };
            menu.Items.Add(wallpaperItem);

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
            if (hoverTimer != null) hoverTimer.Stop();
            if (hoverWatchdog != null) hoverWatchdog.Stop();
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
                // The Mac version's menu carries a thumbnail of the
                // day; the tray menu's title item carries one too.
                if (current.DisplayImage != null)
                {
                    try
                    {
                        int w = 56;
                        int h = Math.Max(16, (int)(w * (double)current.DisplayImage.Height / current.DisplayImage.Width));
                        if (h > 42) { h = 42; w = Math.Max(16, (int)(h * (double)current.DisplayImage.Width / current.DisplayImage.Height)); }
                        Image old = titleItem.Image;
                        titleItem.Image = new Bitmap(current.DisplayImage, new Size(w, h));
                        if (old != null) old.Dispose();
                    }
                    catch { }
                }
                if (wallpaperItem != null)
                {
                    wallpaperItem.Enabled = !current.Info.IsVideo
                        && !string.IsNullOrEmpty(current.ArchivePath)
                        && File.Exists(current.ArchivePath);
                }
            }
        }

        private void ShowExplanation()
        {
            var days = ArchiveScan.Scan(Settings.EffectiveDir());
            if (days.Count == 0)
            {
                if (current != null && current.Info != null)
                {
                    var d = new ArchiveDay();
                    d.Date = current.Info.Date;
                    d.Title = current.Info.Title;
                    d.Credit = current.Info.Credit;
                    d.Explanation = current.Info.Explanation;
                    d.ImagePath = current.Info.IsVideo ? null : current.ArchivePath;
                    d.VideoPath = current.VideoPath;
                    d.MediaUrl = current.Info.IsVideo ? current.Info.VideoUrl : (current.Info.ArchiveUrl ?? "");
                    days.Add(d);
                }
                else
                {
                    TimedMessage.Show(S.NoImageYet);
                    return;
                }
            }
            string start = current != null && current.Info != null ? current.Info.Date : null;
            using (var f = new ExplanationForm(days, start)) f.ShowDialog();
        }

        private void SetWallpaperCurrent()
        {
            if (current != null && current.Info != null && !current.Info.IsVideo
                && !string.IsNullOrEmpty(current.ArchivePath) && File.Exists(current.ArchivePath))
            {
                if (Wallpaper.Set(current.ArchivePath)) TimedMessage.Show(S.WallpaperSet);
                else TimedMessage.Show(S.Error);
            }
        }

        // Hover pop: a tray icon raises no enter/leave events, only
        // MouseMove, so a fresh approach (first move after a quiet
        // gap) starts a short dwell; if the preview is still down
        // when it elapses, it pops. Once shown or dismissed it will
        // not pop again until the pointer has left and returned
        // (the watchdog re-arms it), so sweeping across the tray or
        // wiggling on the icon never makes it flicker.
        private void NoteTrayMove()
        {
            DateTime now = DateTime.Now;
            bool fresh = (now - lastTrayMove).TotalMilliseconds > 900;
            lastTrayMove = now;
            if (!fresh || hoverShown || hoverPending) return;
            if (!Settings.HoverPreview || preview == null || preview.Visible) return;
            hoverPending = true;
            hoverTimer.Interval = 450;
            hoverTimer.Start();
        }

        private void HoverPop()
        {
            hoverTimer.Stop();
            hoverPending = false;
            if (!Settings.HoverPreview || preview == null || preview.Visible) return;
            if (current != null && current.DisplayImage != null)
            {
                preview.ShowFor(current);
                hoverShown = true;
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
                    Settings.HoverPreview = form.HoverChecked;
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
