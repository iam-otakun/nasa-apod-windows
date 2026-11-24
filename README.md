# NASA APOD for Windows / NASA 每日天文图（Windows 版）

A Windows system-tray app that shows the [NASA Astronomy Picture of the Day (APOD)](https://apod.nasa.gov/apod/) by your clock — and quietly archives the original picture to your Pictures folder every day. It is the Windows sibling of [swiftbar-nasa-apod](https://github.com/iam-otakun/swiftbar-nasa-apod), and mirrors it feature for feature.

一个 Windows 系统托盘程序：把 [NASA 每日天文图（APOD）](https://apod.nasa.gov/apod/) 放在时钟旁边——并在每天悄悄把原图存进你的图片文件夹归档。它是 [swiftbar-nasa-apod](https://github.com/iam-otakun/swiftbar-nasa-apod)（macOS SwiftBar 插件）的 Windows 姊妹版，功能一一对应。

## About this app / 关于这个应用

Made by **Otakun** ([@iam-otakun](https://github.com/iam-otakun)), who had the idea and sets the direction, and **Dash**, his Muse assistant, who writes the code. Otakun used Jake Vossen's original APOD plugin on his Mac until NASA's redesign broke it; we revived it there first, and he liked it enough to ask for the same sky on Windows too. This is that port — same page parsing, same archive habit, same manners — rebuilt as a single-file C# tray app in the style of our BingWallpaper and NetworkSwitch tools. It runs on Otakun's own Windows machines every day.

由 **Otakun**（[@iam-otakun](https://github.com/iam-otakun)）与他的 Muse 助手 **Dash** 共同完成：Otakun 出想法、定方向，Dash 写代码。原插件是 Jake Vossen 的作品，NASA 改版后在 Mac 上失效；我们先在 Mac 上把它救了回来，Otakun 用着喜欢，又提出 Windows 上也要有同一片星空——于是有了这个移植版：同样的页面解析、同样的归档习惯、同样的脾气，用单文件 C# 托盘程序重写，做法与我们之前的 BingWallpaper、NetworkSwitch 一脉相承。它每天都在 Otakun 自己的 Windows 电脑上运行。

## Origin / 出处

The logic is a port of our SwiftBar plugin, which is itself a revival of Jake Vossen's xbar plugin:

逻辑移植自我们的 SwiftBar 插件；插件本身是 Jake Vossen 的 xbar 插件的复活版：

> Jake Vossen — [xbar](https://xbarapp.com/) plugin "NASA APOD" — <https://xbarapp.com/docs/plugins/Astronomy/nasa-apod.1d.py.html>
>
> (c) 2019 Jacob Vossen. MIT License.

What the original did, and this app keeps: fetch the APOD page, parse out the picture's date, URL and title, download the picture, show it.

原插件做的事情，本程序保留了下来：抓 APOD 网页、解析出图片的日期、地址与标题、下载图片、展示出来。

## Why the parsing looks the way it does / 解析为什么长这样

NASA's redesigned APOD page misleads in three places — the same three the macOS plugin documents:

NASA 改版后的 APOD 页面有三处会把人带偏——与 macOS 插件里记的是同样的三处：

1. **`og:image` is a fixed template.** Every page — even video days — points it at the same stock photo (an eagle over a flag). Never use it. The real photo hangs off the day's **image-article link**. / **`og:image` 是固定模板图**：每个页面（包括视频日）都指向同一张库存图（一只鹰与一面旗），千万别用。当天的真图挂在当天的 **image-article 链接**上。
2. **The main heading is the title.** The image-article slug's text describes the thumbnail ("…up-close view…"), not the picture. The title is the page's big heading (`display-48`). / **主标题才是图片标题**：image-article 链接里的文字描述的是缩略图（"……的特写"之类），不是图片本身。标题在页面的大标题里（`display-48`）。
3. **The rendered image is not the original.** The page shows a dynamically rendered, resized JPEG; the full-size original is the same file under `content/dam/science/` instead of `dynamicimage/assets/science/`. The archive stores the original. / **页面展示图不是原图**：页面上是动态渲染的缩放 JPEG；全尺寸原图是同一文件、路径在 `content/dam/science/` 而非 `dynamicimage/assets/science/`。归档存的是原图。

## What it does / 功能

- **Left-click the tray icon**: a popup shows today's picture (pure-black side bars trimmed for display only — the archive always keeps the original bytes), with its title and date. Click the title to reveal the archived file in Explorer. / **左键点托盘图标**：弹窗显示今天的图片（纯黑边只在显示时裁掉——归档永远存原始字节），带标题与日期。点标题在资源管理器中定位归档文件。
- **The menu mirrors the plugin** / **菜单与插件版一致**：
  - The first line is the picture's title (click it to reveal the file). / 第一行是图片标题（点它定位文件）。
  - **Refresh** answers before it fetches: it compares the remembered date with NASA's latest and shows **"Up to date!"** or **"Update successfully"** (the note dismisses itself after 3 seconds). / **刷新**先回话再抓图：把记住的日期与 NASA 最新一期比对，显示 **"Up to date!"** 或 **"Update successfully"**（提示 3 秒后自动消失）。
  - **Archive** submenu shows the current target and owns it: **Choose Folder...**, **Type a Path...**, **Turn Off Archiving**, **Restore Default Folder**. / **归档**子菜单显示当前目录，并可**选择文件夹…**、**输入路径…**、**关闭归档**、**恢复默认目录**。
  - Plus **Open Archive Folder**, **Open APOD Website**, **Launch at Login**, **Check for Updates...** and **Quit**. / 另有**打开归档文件夹**、**打开 APOD 网站**、**开机自启**、**检查更新…**与**退出**。
- **Timing**: fetches at startup when the remembered date is behind (APOD rolls over at midnight US Eastern), then checks every 30 minutes; a fetch also repeats if the last one is over 12 hours old — the plugin's 12-hour cycle. / **时机**：启动时若记住的日期已落后就抓一次（APOD 在美国东部时间午夜换期），之后每 30 分钟检查一次；距上次抓取超过 12 小时也会重抓——与插件的 12 小时周期一致。
- **First run**: a setup dialog asks where to archive (default `Pictures\NASA-Apod`), with a folder picker; clearing the field disables archiving. / **首次运行**：设置对话框询问归档目录（默认 `Pictures\NASA-Apod`），可点选文件夹；清空则不归档。
- **Self-update**: checks GitHub Releases at most once a day and offers new versions with a balloon; **Check for Updates...** checks on demand. Installing downloads, swaps and restarts automatically; a failed update leaves the current version untouched. / **在线更新**：每天最多自动检查一次 GitHub 发布，有新版弹气泡提示；**检查更新…**可随时手动查。点安装即自动下载、替换、重启；更新失败时现有版本原样不动。
- **Interface language** follows the Windows display language: Chinese on Chinese systems, English elsewhere. / **界面语言**跟随 Windows 显示语言：中文系统中文，其余英文。

## Install / 安装

1. Download `NasaApod-x.y.z.zip` from [Releases](https://github.com/iam-otakun/nasa-apod-windows/releases) and unzip it anywhere (keep `NasaApod.exe` and `NasaApod.exe.config` together — the config is what enables modern TLS; without it NASA refuses the connection).
2. Run `NasaApod.exe`. The telescope appears in the tray; the first run asks where to archive.
3. Optional: tick **Launch at Login** in the tray menu.

1. 从 [Releases](https://github.com/iam-otakun/nasa-apod-windows/releases) 下载 `NasaApod-x.y.z.zip`，解压到任意位置（`NasaApod.exe` 与 `NasaApod.exe.config` 须放在一起——这个配置文件启用现代 TLS，缺了它 NASA 会拒绝连接）。
2. 运行 `NasaApod.exe`，托盘里出现望远镜图标；首次运行会询问归档目录。
3. 可选：在托盘菜单里勾选**开机自启**。

## Files & behavior / 文件与行为

Kept deliberately identical to the macOS plugin, so both editions can share one archive folder (e.g. over a synced drive):

刻意与 macOS 插件保持一致，两个版本可以共用同一个归档文件夹（例如放在同步盘里）：

- `~/.nasa-apod.conf` — one line, `ARCHIVE_DIR=<path>`. Empty value = archiving off; missing file = not set up yet. / 一行：`ARCHIVE_DIR=<路径>`。值为空 = 关闭归档；文件不存在 = 尚未设置。
- `~/.nasa-apod.state` — the last APOD date shown. / 上次展示的 APOD 日期。
- Archive file names: `YYYY-MM-DD <title>.jpg`, never overwritten. / 归档文件名：`YYYY-MM-DD <标题>.jpg`，从不覆盖。
- `%AppData%\NasaApod\` — app-internal settings, the log (`NasaApod.log`) and update staging. / 程序内部设置、日志（`NasaApod.log`）与更新暂存。

A video day (or an unreachable network) leaves the previous picture in place and says so in the menu. / 遇到视频日（或网络不通），保留上一张图片，并在菜单里说明。

## Build / 构建

Single-file C# (C# 5, .NET Framework 4.8), built with the compiler that ships with Windows — no SDK, no NuGet:

单文件 C#（C# 5、.NET Framework 4.8），用 Windows 自带的编译器构建——不需要 SDK、不需要 NuGet：

```
build.cmd
```

produces `NasaApod.exe`. `NasaApod.exe --selftest` runs one fetch cycle headlessly and writes the result to `%AppData%\NasaApod\selftest.txt`.

产出 `NasaApod.exe`。`NasaApod.exe --selftest` 无头跑一次抓取周期，结果写到 `%AppData%\NasaApod\selftest.txt`。

## Changelog / 更新日志

### v1.0.3 — 2026-11-24

- Menu layout aligned with BingWallpaper: the tray menu's last group is now **Settings... / Check for Updates... / About... / Quit**, with the functional items (Archive submenu, Open Archive Folder, Open APOD Website, Launch at Login) in the group above. The new **Settings...** opens the archive settings form (seeded with the current folder) with a launch-at-login checkbox; the Archive submenu stays for quick changes. / 菜单分组与 BingWallpaper 对齐：托盘菜单最后一组固定为 **设置… / 检查更新… / 关于… / 退出**，功能项（归档子菜单、打开归档文件夹、打开 APOD 网站、开机自启）归到上面一组。新增的 **设置…** 打开归档设置窗（预填当前目录），内含开机自启勾选；归档子菜单保留作快捷更改。

### v1.0.2 — 2026-11-24

- Fixed the first-run Archive Settings dialog showing the macOS-style default `~/Pictures/NASA-Apod`. It now shows the real Windows default (the Pictures folder + NASA-Apod, as an absolute path), the folder is created as soon as the app runs, and a config saved by an older version in the tilde form is rewritten to the absolute path on startup. / 修复首次运行的归档设置对话框里默认路径是 macOS 格式 `~/Pictures/NASA-Apod` 的问题：现在显示真正的 Windows 默认路径（图片文件夹下的 NASA-Apod，绝对路径），文件夹在程序运行时即建立；旧版本以波浪线形式存下的配置会在启动时自动改写为绝对路径。

### v1.0.1 — 2026-11-24

- Added the unified About dialog (name, version, Idea: Otakun, Coding: Dash, and a link labelled "GitHub Repository") to the tray menu — the same About as our other Windows and macOS programs. Release packages are now named with the platform (NasaApod-Windows-x.y.z.zip). / 托盘菜单新增统一规格的 About 对话框（程序名、版本、Idea: Otakun、Coding: Dash、一条写着「GitHub Repository」的链接）——与我们其它 Windows、macOS 程序同一规格。发布包文件名从此带平台标识（NasaApod-Windows-x.y.z.zip）。

### v1.0.0 — 2026-11-24

- First release: the macOS SwiftBar plugin's feature set as a Windows tray app — daily picture popup by the tray, full-size originals archived as `YYYY-MM-DD Title.jpg`, Refresh that answers back, the Archive submenu, bilingual UI, launch at login, and self-update from GitHub Releases. / 首个版本：macOS SwiftBar 插件的全部功能移植为 Windows 托盘程序——托盘旁的每日图片弹窗、全尺寸原图按 `YYYY-MM-DD 标题.jpg` 归档、会回话的刷新、归档子菜单、双语界面、开机自启、GitHub 发布在线更新。
- Networking asks for TLS 1.2 with strong crypto (in code and in the shipped `NasaApod.exe.config`); without it, a bare csc-built exe gets legacy TLS defaults and NASA's servers refuse the handshake. HTTP requests get one automatic retry. / 网络层显式要求 TLS 1.2 强加密（代码与随附的 `NasaApod.exe.config` 双管齐下）；否则裸 csc 构建的 exe 会落回旧式 TLS 默认，NASA 服务器直接拒绝握手。HTTP 请求带一次自动重试。

## License / 许可证

MIT — see `LICENSE`. The original plugin is (c) 2019 Jacob Vossen; this revival and port are by Otakun and Dash.

MIT——见 `LICENSE`。原插件版权 (c) 2019 Jacob Vossen；本次复活与移植由 Otakun 与 Dash 完成。
