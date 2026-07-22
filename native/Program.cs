using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ShepherdMD
{
    static class Program
    {
        [DllImport("shell32.dll")] static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        internal static string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        // User data lives in a STABLE per-user location, independent of the install folder, so it
        // survives updates/reinstalls (the old scheme stored it next to the exe and lost it on rename).
        internal static string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shepherd Markdown");

        static void EnsureDataDir()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                // one-time migration from the legacy "next to the exe" location
                foreach (string name in new[] { "session.json", "config.json", "windowstate.txt" })
                {
                    string dst = Path.Combine(DataDir, name), src = Path.Combine(AppDir, name);
                    if (!File.Exists(dst) && File.Exists(src)) { try { File.Copy(src, dst, false); } catch { } }
                }
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            try { SetCurrentProcessExplicitAppUserModelID("Shepherd.MD.Reader"); } catch { }
            EnsureDataDir();

            string target = args.Length > 0 ? args[0] : null;
            if (target != null) { try { target = Path.GetFullPath(target); } catch { } }

            bool createdNew;
            Mutex mutex = new Mutex(true, "ShepherdMD_SingleInstance_v1", out createdNew);
            if (!createdNew)
            {
                int rp = ReadRunningPort();
                if (rp > 0)
                {
                    if (target != null) TryPost("http://127.0.0.1:" + rp + "/api/open?path=" + Uri.EscapeDataString(target));
                    else TryPost("http://127.0.0.1:" + rp + "/api/focus");
                }
                return;
            }

            LocalServer server = new LocalServer(target);
            int port; try { port = server.Start(); } catch { port = -1; }
            if (port <= 0)
            {
                MessageBox.Show("Could not start the Shepherd Markdown engine.", "Shepherd Markdown", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm("http://127.0.0.1:" + port + "/", server));
            GC.KeepAlive(mutex);
        }

        internal static int ReadRunningPort()
        {
            try
            {
                string f = Path.Combine(DataDir, "running.json");
                if (!File.Exists(f)) return -1;
                Match m = Regex.Match(File.ReadAllText(f), "\"port\"\\s*:\\s*(\\d+)");
                if (m.Success) return int.Parse(m.Groups[1].Value);
            }
            catch { }
            return -1;
        }

        static bool Ping(int port)
        {
            try { HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/ping"); req.Timeout = 1200; using (req.GetResponse()) { return true; } }
            catch { return false; }
        }

        internal static void TryPost(string url)
        {
            try { HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url); req.Method = "POST"; req.ContentLength = 0; req.Timeout = 2500; using (req.GetResponse()) { } }
            catch { }
        }
    }

    class MainForm : Form
    {
        WebView2 web;
        string url;
        LocalServer server;
        string statePath = Path.Combine(Program.DataDir, "windowstate.txt");
        const string AppVersion = "1.0.4";
        const string UpdateFeed = "https://github.com/shepherd8126/shepherd-md-releases/releases/latest/download/latest.json";
        string pendingUpdateUrl = null;
        bool updateChecked = false;

        // ---- update nudge: fetch the release feed (host-side, no CORS), compare, tell the page ----
        void CheckForUpdate()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)3072;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(UpdateFeed);
                req.Timeout = 8000; req.UserAgent = "ShepherdMD/" + AppVersion; req.AllowAutoRedirect = true;
                string json;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream())) { json = sr.ReadToEnd(); }
                string ver = JsonField(json, "version");
                string notes = JsonField(json, "notes");
                Match winUrl = Regex.Match(json, "\"windows\"\\s*:\\s*\\{[^}]*\"url\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Singleline);
                string url2 = winUrl.Success ? winUrl.Groups[1].Value : null;
                if (string.IsNullOrEmpty(ver) || string.IsNullOrEmpty(url2)) return;
                if (CompareVersions(ver, AppVersion) <= 0) return; // not newer
                pendingUpdateUrl = url2;
                string js = "window.__updateAvailable && window.__updateAvailable({version:" + JsStr(ver) + ",notes:" + JsStr(notes ?? "") + "})";
                BeginInvoke((Action)(() => { try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync(js); } catch { } }));
            }
            catch { }
        }

        static string JsonField(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
        }

        static int CompareVersions(string a, string b)
        {
            string[] pa = (a ?? "0").Split('.'), pb = (b ?? "0").Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int na = 0, nb = 0;
                if (i < pa.Length) int.TryParse(new string(pa[i].TakeWhile(char.IsDigit).ToArray()), out na);
                if (i < pb.Length) int.TryParse(new string(pb[i].TakeWhile(char.IsDigit).ToArray()), out nb);
                if (na != nb) return na > nb ? 1 : -1;
            }
            return 0;
        }

        void DownloadAndRunUpdate()
        {
            string u = pendingUpdateUrl;
            if (string.IsNullOrEmpty(u)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "Shepherd-MD-Setup-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)3072;
                    using (System.Net.WebClient wc = new System.Net.WebClient()) { wc.Headers.Add("User-Agent", "ShepherdMD/" + AppVersion); wc.DownloadFile(u, tmp); }
                    if (!File.Exists(tmp) || new FileInfo(tmp).Length < 100000) throw new Exception("download too small");
                    // /SILENT: the installer upgrades in place (same AppId) and closes this app via its AppMutex.
                    ProcessStartInfo psi = new ProcessStartInfo(tmp, "/SILENT /NOCANCEL") { UseShellExecute = true };
                    Process.Start(psi);
                    BeginInvoke((Action)(() => { try { Close(); } catch { } }));
                }
                catch
                {
                    BeginInvoke((Action)(() => { try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync("window.__updateFailed && window.__updateFailed()"); } catch { } }));
                }
            });
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        // ---- custom (borderless) window frame: keeps native resize/snap/shadow, no native caption ----
        // WS_CAPTION + WS_SYSMENU stay in the STYLE (the visible bar is stripped in WM_NCCALCSIZE) so Windows
        // still treats this as a normal window and plays the native minimize/maximize/open/close animations.
        const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        const int WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
        const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_GETMINMAXINFO = 0x0024;
        [StructLayout(LayoutKind.Sequential)] struct RECTS { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MMI { public PT reserved, maxSize, maxPos, minTrack, maxTrack; }
        [StructLayout(LayoutKind.Sequential)] struct MONINFO { public int cbSize; public RECTS rcMonitor; public RECTS rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] struct NCCALC { public RECTS r0, r1, r2; public IntPtr lppos; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECTS r);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flag);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr h, ref MONINFO mi);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        void StartNativeDragResize(int ht) { try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero); } catch { } }
        int EdgeToHt(string e)
        {
            switch (e) { case "left": return 10; case "right": return 11; case "top": return 12; case "topleft": return 13; case "topright": return 14; case "bottom": return 15; case "bottomleft": return 16; case "bottomright": return 17; }
            return 0;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.Style |= WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX; return cp; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                // Remove the whole non-client frame. When maximized, WM_GETMINMAXINFO already
                // sizes the window to the monitor work area, so returning 0 here fills it exactly
                // (no overhang to clip, no double-inset border).
                m.Result = IntPtr.Zero; return;
            }
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HitTest(m.LParam); return; }
            if (m.Msg == WM_GETMINMAXINFO) { WmMinMax(m.LParam); m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
        }
        int HitTest(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
            RECTS r; GetWindowRect(Handle, out r);
            int lx = x - r.Left, ly = y - r.Top, w = r.Right - r.Left, h = r.Bottom - r.Top;
            double s = DeviceDpi / 96.0;
            int rm = (int)(7 * s), th = (int)(34 * s), bw = (int)(140 * s);
            if (WindowState != FormWindowState.Maximized)
            {
                bool L = lx < rm, R = lx >= w - rm, T = ly < rm, B = ly >= h - rm;
                if (T && L) return 13; if (T && R) return 14; if (B && L) return 16; if (B && R) return 17;
                if (L) return 10; if (R) return 11; if (T) return 12; if (B) return 15;
            }
            if (ly < th && lx < w - bw) return 2; // HTCAPTION (draggable title strip, minus the window-button zone)
            return 1; // HTCLIENT
        }
        void WmMinMax(IntPtr lParam)
        {
            MMI mmi = (MMI)Marshal.PtrToStructure(lParam, typeof(MMI));
            IntPtr mon = MonitorFromWindow(Handle, 2);
            MONINFO mi = new MONINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONINFO));
            if (GetMonitorInfo(mon, ref mi))
            {
                mmi.maxPos.X = mi.rcWork.Left - mi.rcMonitor.Left; mmi.maxPos.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                mmi.maxSize.X = mi.rcWork.Right - mi.rcWork.Left; mmi.maxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                mmi.minTrack.X = 520; mmi.minTrack.Y = 400;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        void NotifyWinState() { try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync("window.__winState&&window.__winState('" + (WindowState == FormWindowState.Maximized ? "max" : "normal") + "')"); } catch { } }

        public MainForm(string startUrl, LocalServer srv)
        {
            url = startUrl;
            server = srv;
            Text = "Shepherd Markdown | MD Reader";
            try { Icon = new Icon(Path.Combine(Program.AppDir, "app.ico")); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = new Size(560, 440);
            BackColor = Color.FromArgb(38, 38, 36);
            LoadBounds();
            web = new WebView2(); web.Dock = DockStyle.Fill;
            Controls.Add(web);
            if (server != null)
            {
                // Another instance (e.g. an "Open with" launch) routes files/focus to us via the loopback API.
                server.OpenFile += (p) => { try { BeginInvoke((Action)(() => { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync("window.__externalOpen && window.__externalOpen(" + JsStr(p) + ")"); BringToFrontHard(); })); } catch { } };
                server.Focus += () => { try { BeginInvoke((Action)BringToFrontHard); } catch { } };
            }
            Load += OnLoad;
            Resize += (s, e) => NotifyWinState();
            FormClosing += OnClosing;
            FormClosed += OnClosed;
        }

        static string JsStr(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "") + "\"";
        }

        void LoadBounds()
        {
            StartPosition = FormStartPosition.Manual;
            bool applied = false;
            try
            {
                if (File.Exists(statePath))
                {
                    string[] p = File.ReadAllText(statePath).Trim().Split(',');
                    if (p.Length >= 5)
                    {
                        int x = int.Parse(p[0]), y = int.Parse(p[1]), w = int.Parse(p[2]), h = int.Parse(p[3]);
                        bool max = p[4] == "1";
                        Rectangle r = new Rectangle(x, y, w, h);
                        if (w >= 400 && h >= 300 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
                        {
                            Bounds = r;
                            if (max) WindowState = FormWindowState.Maximized;
                            applied = true;
                        }
                    }
                }
            }
            catch { }
            if (!applied)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = (int)(wa.Width * 0.85), h = (int)(wa.Height * 0.85);
                Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
            }
        }

        void SaveBounds()
        {
            try
            {
                Rectangle b = (WindowState == FormWindowState.Normal) ? Bounds : RestoreBounds;
                bool max = WindowState == FormWindowState.Maximized;
                File.WriteAllText(statePath, b.X + "," + b.Y + "," + b.Width + "," + b.Height + "," + (max ? "1" : "0"));
            }
            catch { }
        }

        async void OnLoad(object sender, EventArgs e)
        {
            ApplyTitleBar(31, 30, 29, 236, 233, 224); // default claude-dark until the page reports its theme
            try { int pref = 2; DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4); } catch { } // rounded corners
            try { SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED); } catch { } // re-strip the caption frame cleanly after WS_CAPTION
            try
            {
                string udf = Path.Combine(Program.AppDir, "webview2profile");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                await web.EnsureCoreWebView2Async(env);
                CoreWebView2 c = web.CoreWebView2;
                c.Settings.IsStatusBarEnabled = false;
                c.Settings.AreDefaultContextMenusEnabled = true;
                c.Settings.IsZoomControlEnabled = true;
                c.DocumentTitleChanged += (s2, e2) => { try { Text = string.IsNullOrEmpty(c.DocumentTitle) ? "Shepherd Markdown | MD Reader" : c.DocumentTitle; } catch { } };
                c.WebMessageReceived += OnWebMessage;
                c.NavigationCompleted += (s2, e2) => { try { BeginInvoke((Action)NotifyWinState); } catch { } if (!updateChecked) { updateChecked = true; System.Threading.ThreadPool.QueueUserWorkItem(delegate { System.Threading.Thread.Sleep(3500); CheckForUpdate(); }); } };
                c.Navigate(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show("WebView2 failed to initialize:\n\n" + ex.Message, "Shepherd Markdown", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            try
            {
                Match cmd = Regex.Match(json, "\"cmd\"\\s*:\\s*\"(\\w+)\"");
                if (cmd.Groups[1].Value == "focus") { BeginInvoke((Action)BringToFrontHard); return; }
                if (cmd.Groups[1].Value == "pickFolder") { BeginInvoke((Action)PickFolder); return; }
                if (cmd.Groups[1].Value == "pickFile") { BeginInvoke((Action)PickFile); return; }
                if (cmd.Groups[1].Value == "update") { DownloadAndRunUpdate(); return; }
                if (cmd.Groups[1].Value == "win")
                {
                    Match act = Regex.Match(json, "\"action\"\\s*:\\s*\"(\\w+)\"");
                    string a = act.Groups[1].Value;
                    BeginInvoke((Action)(() =>
                    {
                        if (a == "min") { WindowState = FormWindowState.Minimized; }
                        else if (a == "max") { WindowState = (WindowState == FormWindowState.Maximized) ? FormWindowState.Normal : FormWindowState.Maximized; NotifyWinState(); }
                        else if (a == "close") { Close(); }
                        else if (a == "drag") { StartNativeDragResize(2); } // HTCAPTION; when maximized Windows restores + follows the cursor
                        else if (a == "resize")
                        {
                            if (WindowState != FormWindowState.Maximized)
                            {
                                Match ed = Regex.Match(json, "\"edge\"\\s*:\\s*\"(\\w+)\"");
                                int ht = EdgeToHt(ed.Groups[1].Value);
                                if (ht > 0) StartNativeDragResize(ht);
                            }
                        }
                    }));
                    return;
                }
                if (cmd.Groups[1].Value == "theme")
                {
                    Match bg = Regex.Match(json, "\"bg\"\\s*:\\s*\\[\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)");
                    Match fg = Regex.Match(json, "\"fg\"\\s*:\\s*\\[\\s*(\\d+)\\s*,\\s*(\\d+)\\s*,\\s*(\\d+)");
                    if (bg.Success && fg.Success)
                    {
                        int br = int.Parse(bg.Groups[1].Value), bgc = int.Parse(bg.Groups[2].Value), bb = int.Parse(bg.Groups[3].Value);
                        int fr = int.Parse(fg.Groups[1].Value), fgc = int.Parse(fg.Groups[2].Value), fb = int.Parse(fg.Groups[3].Value);
                        BeginInvoke((Action)(() => { BackColor = Color.FromArgb(br, bgc, bb); ApplyTitleBar(br, bgc, bb, fr, fgc, fb); }));
                    }
                }
            }
            catch { }
        }

        void ApplyTitleBar(int br, int bg, int bb, int fr, int fg, int fb)
        {
            if (!IsHandleCreated) return;
            try
            {
                int caption = br | (bg << 8) | (bb << 16);
                int text = fr | (fg << 8) | (fb << 16);
                int dark = (0.299 * br + 0.587 * bg + 0.114 * bb) < 140 ? 1 : 0;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, 4);
                DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, 4);
                DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref caption, 4);
            }
            catch { }
        }

        void PickFolder()
        {
            try
            {
                string folder = null;
                try { folder = ModernFolderDialog.Show(this.Handle); }
                catch { using (FolderBrowserDialog dlg = new FolderBrowserDialog()) { dlg.ShowNewFolderButton = false; if (dlg.ShowDialog(this) == DialogResult.OK) folder = dlg.SelectedPath; } }
                if (!string.IsNullOrEmpty(folder))
                {
                    int p = Program.ReadRunningPort();
                    if (p > 0) Program.TryPost("http://127.0.0.1:" + p + "/api/addroot?path=" + Uri.EscapeDataString(folder));
                    if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync("window.__folderAdded && window.__folderAdded()");
                }
            }
            catch { }
        }

        void PickFile()
        {
            try
            {
                string file = null;
                try { file = ModernFolderDialog.ShowFile(this.Handle); }
                catch { using (OpenFileDialog dlg = new OpenFileDialog()) { dlg.Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.txt|All files|*.*"; dlg.Title = "Open a Markdown file"; if (dlg.ShowDialog(this) == DialogResult.OK) file = dlg.FileName; } }
                if (!string.IsNullOrEmpty(file))
                {
                    if (server != null) server.AllowFileDir(file);
                    if (web != null && web.CoreWebView2 != null) web.CoreWebView2.ExecuteScriptAsync("window.__externalOpen && window.__externalOpen(" + JsStr(Path.GetFullPath(file)) + ")");
                }
            }
            catch { }
        }

        void BringToFrontHard()
        {
            try { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); BringToFront(); SetForegroundWindow(Handle); }
            catch { }
        }

        void OnClosing(object sender, FormClosingEventArgs e) { SaveBounds(); }

        void OnClosed(object sender, FormClosedEventArgs e)
        {
            try { if (server != null) server.Stop(); } catch { }
        }
    }

    // Modern (Vista+) Explorer-style folder picker via IFileOpenDialog.
    internal static class ModernFolderDialog
    {
        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_NOCHANGEDIR = 0x8;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        public static string Show(IntPtr owner)
        {
            IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                uint opts; dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                dlg.SetTitle("Open folder in Shepherd Markdown");
                int hr = dlg.Show(owner);
                if (hr != 0) return null; // cancelled or error
                IShellItem item; dlg.GetResult(out item);
                string path; item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                Marshal.ReleaseComObject(item);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FILTERSPEC { public string pszName; public string pszSpec; }

        // Same modern dialog, in file mode (no FOS_PICKFOLDERS) - picks a single file, filtered to Markdown by default.
        public static string ShowFile(IntPtr owner)
        {
            IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            IntPtr specPtr = IntPtr.Zero;
            int count = 2;
            try
            {
                uint opts; dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                FILTERSPEC[] filters =
                {
                    new FILTERSPEC { pszName = "Markdown files", pszSpec = "*.md;*.markdown;*.mdown;*.mkd;*.txt" },
                    new FILTERSPEC { pszName = "All files", pszSpec = "*.*" },
                };
                int elem = Marshal.SizeOf(typeof(FILTERSPEC));
                specPtr = Marshal.AllocHGlobal(elem * count);
                for (int i = 0; i < count; i++) Marshal.StructureToPtr(filters[i], (IntPtr)(specPtr.ToInt64() + i * elem), false);
                dlg.SetFileTypes((uint)count, specPtr);
                dlg.SetFileTypeIndex(1); // 1-based: default to the Markdown filter
                dlg.SetTitle("Open a Markdown file");
                int hr = dlg.Show(owner);
                if (hr != 0) return null;
                IShellItem item; dlg.GetResult(out item);
                string path; item.GetDisplayName(SIGDN_FILESYSPATH, out path);
                Marshal.ReleaseComObject(item);
                return path;
            }
            finally
            {
                if (specPtr != IntPtr.Zero)
                {
                    int elem = Marshal.SizeOf(typeof(FILTERSPEC));
                    for (int i = 0; i < count; i++) { try { Marshal.DestroyStructure((IntPtr)(specPtr.ToInt64() + i * elem), typeof(FILTERSPEC)); } catch { } }
                    Marshal.FreeHGlobal(specPtr);
                }
                Marshal.ReleaseComObject(dlg);
            }
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    internal class FileOpenDialogRCW { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr parent);
        void SetFileTypes([In] uint cFileTypes, [In] IntPtr rgFilterSpec);
        void SetFileTypeIndex([In] uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise([In, MarshalAs(UnmanagedType.Interface)] object pfde, out uint pdwCookie);
        void Unadvise([In] uint dwCookie);
        void SetOptions([In] uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void SetFolder([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetCurrentSelection([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void AddPlace([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
        void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] object pFilter);
        void GetResults([MarshalAs(UnmanagedType.Interface)] out object ppenum);
        void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out object ppsai);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler([In] IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
        void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        void Compare([In, MarshalAs(UnmanagedType.Interface)] IShellItem psi, [In] uint hint, out int piOrder);
    }
}
