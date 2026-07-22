using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ShepherdMD
{
    // In-process rendering engine: serves the viewer UI + a read-only file API + persisted
    // session state over a loopback TcpListener (127.0.0.1 only). Replaces the old Node server,
    // so the app has no external runtime dependency. A loopback TCP socket needs no admin rights
    // or URL reservation, unlike HttpListener/http.sys.
    class LocalServer
    {
        readonly string appDir, publicDir, libDir, configPath, statePath, runningPath, initialFile;
        readonly List<string> roots = new List<string>();
        readonly HashSet<string> extraAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly object rootsLock = new object();
        TcpListener listener;
        int port;

        public event Action<string> OpenFile; // absolute .md path requested by another instance
        public event Action Focus;
        public int Port { get { return port; } }

        static readonly HashSet<string> MdExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd", ".txt" };
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".svn", ".hg", "vendor", "__pycache__", ".venv", "venv", "dist", "build", ".next", ".cache", "bower_components", ".idea", ".vs" };
        static readonly Dictionary<string, string> ImgExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" }, { ".svg", "image/svg+xml" }, { ".webp", "image/webp" }, { ".bmp", "image/bmp" }, { ".ico", "image/x-icon" }, { ".avif", "image/avif" } };
        static readonly Dictionary<string, string> StaticTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" }, { ".json", "application/json; charset=utf-8" }, { ".svg", "image/svg+xml" }, { ".woff2", "font/woff2" }, { ".ico", "image/x-icon" }, { ".png", "image/png" }, { ".webmanifest", "application/manifest+json" } };
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public LocalServer(string initialFile)
        {
            appDir = Program.AppDir.TrimEnd('\\');
            publicDir = Path.Combine(appDir, "public");
            libDir = Path.Combine(appDir, "lib");
            configPath = Path.Combine(appDir, "config.json");
            statePath = Path.Combine(appDir, "session.json");
            runningPath = Path.Combine(appDir, "running.json");
            this.initialFile = initialFile;
            LoadConfig();
            if (!string.IsNullOrEmpty(initialFile)) { try { extraAllowed.Add(Path.GetDirectoryName(Path.GetFullPath(initialFile))); } catch { } }
        }

        public int Start()
        {
            foreach (int p in new[] { 7733, 7734, 7735, 7736, 7737 }) { if (TryListen(p)) { port = p; break; } }
            if (listener == null) { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            WriteRunning();
            Thread t = new Thread(AcceptLoop); t.IsBackground = true; t.Start();
            return port;
        }

        bool TryListen(int p)
        {
            try { TcpListener l = new TcpListener(IPAddress.Loopback, p); l.Start(); listener = l; return true; }
            catch { return false; }
        }

        public void Stop()
        {
            try { if (listener != null) listener.Stop(); } catch { }
            try { if (File.Exists(runningPath)) { string r = File.ReadAllText(runningPath); Match m = Regex.Match(r, "\"port\"\\s*:\\s*(\\d+)"); if (m.Success && int.Parse(m.Groups[1].Value) == port) File.Delete(runningPath); } } catch { }
        }

        void WriteRunning()
        {
            try { File.WriteAllText(runningPath, "{\"port\":" + port + ",\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + ",\"started\":" + (long)(DateTime.UtcNow - Epoch).TotalMilliseconds + "}"); } catch { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(c); });
            }
        }

        void Handle(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 20000; client.SendTimeout = 20000;
                    NetworkStream ns = client.GetStream();
                    MemoryStream buf = new MemoryStream();
                    byte[] tmp = new byte[8192];
                    int headerEnd = -1;
                    while (headerEnd < 0)
                    {
                        int n = ns.Read(tmp, 0, tmp.Length);
                        if (n <= 0) return;
                        buf.Write(tmp, 0, n);
                        headerEnd = FindHeaderEnd(buf.GetBuffer(), (int)buf.Length);
                        if (buf.Length > 32L * 1024 * 1024) return;
                    }
                    byte[] all = buf.GetBuffer(); int total = (int)buf.Length;
                    string header = Encoding.ASCII.GetString(all, 0, headerEnd);
                    string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] rl = lines[0].Split(' ');
                    if (rl.Length < 2) { WriteStatus(ns, 400, "Bad request"); return; }
                    string method = rl[0].ToUpperInvariant();
                    string rawTarget = rl[1];
                    int contentLength = 0;
                    for (int i = 1; i < lines.Length; i++) { int c = lines[i].IndexOf(':'); if (c > 0 && lines[i].Substring(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) { int.TryParse(lines[i].Substring(c + 1).Trim(), out contentLength); } }

                    int bodyStart = headerEnd + 4;
                    byte[] body = new byte[0];
                    if (contentLength > 0 && contentLength < 64 * 1024 * 1024)
                    {
                        body = new byte[contentLength];
                        int have = Math.Min(total - bodyStart, contentLength);
                        if (have > 0) Array.Copy(all, bodyStart, body, 0, have);
                        int off = have;
                        while (off < contentLength) { int n = ns.Read(body, off, contentLength - off); if (n <= 0) break; off += n; }
                    }
                    Route(ns, method, rawTarget, body);
                }
            }
            catch { }
        }

        static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++) if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        void Route(NetworkStream ns, string method, string rawTarget, byte[] body)
        {
            string pathPart = rawTarget; string query = "";
            int q = rawTarget.IndexOf('?');
            if (q >= 0) { pathPart = rawTarget.Substring(0, q); query = rawTarget.Substring(q + 1); }
            string pathname; try { pathname = Uri.UnescapeDataString(pathPart); } catch { pathname = pathPart; }
            string qpath = GetQuery(query, "path");
            string qq = GetQuery(query, "q");

            if (pathname == "/api/ping") { WriteStatus(ns, 204, "No Content"); return; }
            if (pathname == "/api/bye") { WriteStatus(ns, 204, "No Content"); return; }
            if (pathname == "/api/focus") { if (Focus != null) Focus(); WriteStatus(ns, 204, "No Content"); return; }
            if (pathname == "/api/events") { WriteJson(ns, "{\"ok\":true}"); return; } // legacy no-op (open/focus now delivered host-side)

            if (pathname == "/api/open")
            {
                if (string.IsNullOrEmpty(qpath) || !Openable(qpath) || !File.Exists(qpath)) { WriteStatus(ns, 400, "bad"); return; }
                string full = Path.GetFullPath(qpath);
                lock (rootsLock) { try { extraAllowed.Add(Path.GetDirectoryName(full)); } catch { } }
                if (OpenFile != null) OpenFile(full);
                WriteJson(ns, "{\"ok\":true}"); return;
            }
            if (pathname == "/api/addroot")
            {
                try
                {
                    string rp = Path.GetFullPath(qpath ?? "");
                    if (string.IsNullOrEmpty(qpath) || !Directory.Exists(rp)) { WriteStatus(ns, 400, "bad"); return; }
                    lock (rootsLock) { if (!roots.Any(r => string.Equals(r, rp, StringComparison.OrdinalIgnoreCase))) { roots.Add(rp); SaveConfig(); } }
                    WriteJson(ns, "{\"ok\":true,\"root\":" + JsonStr(rp) + "}"); return;
                }
                catch { WriteStatus(ns, 400, "bad"); return; }
            }
            if (pathname == "/api/removeroot")
            {
                try
                {
                    string rp = Path.GetFullPath(qpath ?? "");
                    lock (rootsLock) { roots.RemoveAll(r => string.Equals(r, rp, StringComparison.OrdinalIgnoreCase)); SaveConfig(); }
                    WriteJson(ns, "{\"ok\":true}"); return;
                }
                catch { WriteStatus(ns, 400, "bad"); return; }
            }

            if (pathname == "/api/state" && method == "GET")
            {
                string s = "{}"; try { if (File.Exists(statePath)) s = File.ReadAllText(statePath); } catch { }
                WriteJson(ns, s); return;
            }
            if (pathname == "/api/state" && (method == "PUT" || method == "POST"))
            {
                string b = Encoding.UTF8.GetString(body).Trim();
                if (b.Length > 0 && (b[0] == '{' || b[0] == '[')) { try { File.WriteAllText(statePath, b); WriteStatus(ns, 204, "No Content"); } catch { WriteStatus(ns, 500, "err"); } }
                else WriteStatus(ns, 400, "bad json");
                return;
            }

            if (pathname == "/api/config")
            {
                string ini = string.IsNullOrEmpty(initialFile) ? "null" : JsonStr(Path.GetFullPath(initialFile));
                WriteJson(ns, "{\"roots\":[" + RootsJson() + "],\"initialFile\":" + ini + "}"); return;
            }
            if (pathname == "/api/tree")
            {
                StringBuilder sb = new StringBuilder("{\"roots\":[");
                string[] snap; lock (rootsLock) snap = roots.ToArray();
                for (int i = 0; i < snap.Length; i++) { if (i > 0) sb.Append(','); bool has; sb.Append(TreeNode(snap[i], 0, out has)); }
                sb.Append("]}"); WriteJson(ns, sb.ToString()); return;
            }
            if (pathname == "/api/search")
            {
                string qt = (qq ?? "").Trim();
                WriteJson(ns, "{\"results\":" + (qt.Length < 2 ? "[]" : SearchJson(qt)) + "}"); return;
            }
            if (pathname == "/api/file")
            {
                if (string.IsNullOrEmpty(qpath) || !IsAllowed(qpath) || !MdExt.Contains(Path.GetExtension(qpath))) { WriteStatus(ns, 403, "Forbidden"); return; }
                try
                {
                    string full = Path.GetFullPath(qpath);
                    string content = File.ReadAllText(full);
                    long mtime = 0; try { mtime = (long)(File.GetLastWriteTimeUtc(full) - Epoch).TotalMilliseconds; } catch { }
                    WriteJson(ns, "{\"path\":" + JsonStr(full) + ",\"name\":" + JsonStr(Path.GetFileName(full)) + ",\"content\":" + JsonStr(content) + ",\"mtime\":" + mtime + "}");
                }
                catch { WriteStatus(ns, 404, "Not found"); }
                return;
            }
            if (pathname == "/api/raw")
            {
                if (string.IsNullOrEmpty(qpath) || !IsAllowed(qpath)) { WriteStatus(ns, 403, "Forbidden"); return; }
                string ct; if (!ImgExt.TryGetValue(Path.GetExtension(qpath), out ct)) ct = "application/octet-stream";
                ServeFile(ns, qpath, ct); return;
            }

            // ---- static assets ----
            string asset = RootAsset(pathname);
            if (asset != null) { ServeFile(ns, Path.Combine(appDir, asset), TypeOf(asset)); return; }
            if (pathname == "/" || pathname == "/index.html") { ServeFile(ns, Path.Combine(publicDir, "index.html"), StaticTypes[".html"]); return; }
            if (pathname.StartsWith("/lib/"))
            {
                string target = Path.Combine(libDir, pathname.Substring(5).Replace('/', '\\'));
                if (!IsInside(target, libDir)) { WriteStatus(ns, 403, "Forbidden"); return; }
                ServeFile(ns, target, TypeOf(target)); return;
            }
            if (pathname.StartsWith("/public/") || pathname == "/app.js" || pathname == "/app.css")
            {
                string rel = pathname.StartsWith("/public/") ? pathname.Substring(8) : pathname.Substring(1);
                string target = Path.Combine(publicDir, rel.Replace('/', '\\'));
                if (!IsInside(target, publicDir)) { WriteStatus(ns, 403, "Forbidden"); return; }
                ServeFile(ns, target, TypeOf(target)); return;
            }

            WriteStatus(ns, 404, "Not found");
        }

        static string RootAsset(string pathname)
        {
            switch (pathname)
            {
                case "/favicon.ico": return "app.ico";
                case "/icon.png": return "icon-256.png";
                case "/icon-192.png": return "icon-192.png";
                case "/icon-512.png": return "icon-512.png";
                case "/icon.svg": return "icon.svg";
                case "/manifest.webmanifest": return "manifest.webmanifest";
            }
            return null;
        }

        static string TypeOf(string p) { string t; return StaticTypes.TryGetValue(Path.GetExtension(p), out t) ? t : "application/octet-stream"; }

        bool Openable(string name) { return MdExt.Contains(Path.GetExtension(name)); }

        static bool IsInside(string child, string parent)
        {
            try
            {
                string c = Path.GetFullPath(child).TrimEnd('\\');
                string p = Path.GetFullPath(parent).TrimEnd('\\');
                if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
                return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        bool IsAllowed(string p)
        {
            lock (rootsLock)
            {
                return roots.Any(r => IsInside(p, r)) || extraAllowed.Any(r => IsInside(p, r));
            }
        }

        // Allow a single picked file to be read (whitelists its containing directory) without adding a sidebar root.
        public void AllowFileDir(string path)
        {
            try { string d = Path.GetDirectoryName(Path.GetFullPath(path)); if (!string.IsNullOrEmpty(d)) lock (rootsLock) extraAllowed.Add(d); } catch { }
        }

        string RootsJson()
        {
            string[] snap; lock (rootsLock) snap = roots.ToArray();
            return string.Join(",", snap.Select(JsonStr));
        }

        string TreeNode(string dir, int depth, out bool hasChildren)
        {
            List<string> kids = new List<string>();
            if (depth <= 12)
            {
                List<string> subdirs = new List<string>(), files = new List<string>();
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                    {
                        string name = Path.GetFileName(entry);
                        if (name.StartsWith(".") && name != ".claude") continue;
                        bool isDir; try { isDir = (File.GetAttributes(entry) & FileAttributes.Directory) != 0; } catch { continue; }
                        if (isDir) { if (SkipDirs.Contains(name)) continue; subdirs.Add(entry); }
                        else if (Openable(name)) files.Add(entry);
                    }
                }
                catch { }
                subdirs.Sort(delegate (string a, string b) { return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase); });
                files.Sort(delegate (string a, string b) { return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase); });
                foreach (string d in subdirs) { bool ch; string cj = TreeNode(d, depth + 1, out ch); if (ch) kids.Add(cj); }
                foreach (string f in files) kids.Add("{\"name\":" + JsonStr(Path.GetFileName(f)) + ",\"path\":" + JsonStr(f) + ",\"type\":\"file\"}");
            }
            hasChildren = kids.Count > 0;
            string label = Path.GetFileName(dir.TrimEnd('\\')); if (string.IsNullOrEmpty(label)) label = dir;
            return "{\"name\":" + JsonStr(label) + ",\"path\":" + JsonStr(dir) + ",\"type\":\"dir\",\"children\":[" + string.Join(",", kids) + "]}";
        }

        void CollectFiles(string dir, int depth, List<string> outp)
        {
            if (depth > 12 || outp.Count > 5000) return;
            IEnumerable<string> entries; try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { return; }
            foreach (string e in entries)
            {
                string name = Path.GetFileName(e);
                if (name.StartsWith(".") && name != ".claude") continue;
                bool isDir; try { isDir = (File.GetAttributes(e) & FileAttributes.Directory) != 0; } catch { continue; }
                if (isDir) { if (SkipDirs.Contains(name)) continue; CollectFiles(e, depth + 1, outp); }
                else if (Openable(name)) outp.Add(e);
            }
        }

        string SearchJson(string qraw)
        {
            string needle = qraw.ToLowerInvariant();
            List<string> files = new List<string>();
            string[] snap; lock (rootsLock) snap = roots.ToArray();
            foreach (string r in snap) CollectFiles(r, 0, files);
            StringBuilder sb = new StringBuilder("["); bool first = true; int count = 0;
            foreach (string f in files)
            {
                if (count >= 60) break;
                string baseName = Path.GetFileName(f);
                bool nameHit = baseName.ToLowerInvariant().Contains(needle);
                string snippet = ""; int line = 0; bool contentHit = false; int occ = 0;
                try
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length < 2L * 1024 * 1024)
                    {
                        string text = File.ReadAllText(f);
                        string lower = text.ToLowerInvariant();
                        int idx = lower.IndexOf(needle, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            contentHit = true;
                            int start = Math.Max(0, idx - 40);
                            int end = Math.Min(text.Length, idx + needle.Length + 60);
                            snippet = Regex.Replace(text.Substring(start, end - start), "\\s+", " ").Trim();
                            int nl = 0; for (int i = 0; i < idx; i++) if (text[i] == '\n') nl++; line = nl + 1;
                            int k = 0; while ((k = lower.IndexOf(needle, k, StringComparison.Ordinal)) >= 0) { occ++; k += needle.Length; }
                        }
                    }
                }
                catch { }
                if (nameHit || contentHit)
                {
                    if (!first) sb.Append(',');
                    sb.Append("{\"path\":").Append(JsonStr(f)).Append(",\"name\":").Append(JsonStr(baseName)).Append(",\"snippet\":").Append(JsonStr(snippet)).Append(",\"line\":").Append(line).Append(",\"count\":").Append(occ).Append('}');
                    first = false; count++;
                }
            }
            sb.Append(']'); return sb.ToString();
        }

        void LoadConfig()
        {
            List<string> found = new List<string>();
            string raw = null; try { raw = File.ReadAllText(configPath); } catch { }
            if (raw != null)
            {
                Match m = Regex.Match(raw, "\"roots\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (m.Success)
                    foreach (Match sm in Regex.Matches(m.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                        found.Add(JsonUnescape(sm.Groups[1].Value));
            }
            // No hardcoded fallback root: a fresh install starts with no folders and shows the
            // welcome screen ("Open folder"). Never ship a developer-specific path here.
            foreach (string r in found)
            {
                try { string rp = Path.GetFullPath(r); if (Directory.Exists(rp) && !roots.Any(x => string.Equals(x, rp, StringComparison.OrdinalIgnoreCase))) roots.Add(rp); } catch { }
            }
        }

        void SaveConfig()
        {
            try
            {
                StringBuilder sb = new StringBuilder("{\n  \"roots\": [");
                for (int i = 0; i < roots.Count; i++) { if (i > 0) sb.Append(','); sb.Append("\n    ").Append(JsonStr(roots[i])); }
                sb.Append(roots.Count > 0 ? "\n  ]" : "]").Append("\n}\n");
                File.WriteAllText(configPath, sb.ToString());
            }
            catch { }
        }

        void ServeFile(NetworkStream ns, string filePath, string contentType)
        {
            byte[] data;
            try { data = File.ReadAllBytes(filePath); }
            catch { WriteStatus(ns, 404, "Not found"); return; }
            WriteResponse(ns, 200, "OK", contentType, data);
        }

        void WriteJson(NetworkStream ns, string json) { WriteResponse(ns, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json)); }
        void WriteStatus(NetworkStream ns, int code, string text) { WriteResponse(ns, code, text, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text)); }

        void WriteResponse(NetworkStream ns, int code, string reason, string contentType, byte[] body)
        {
            try
            {
                StringBuilder h = new StringBuilder();
                h.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
                h.Append("Content-Type: ").Append(contentType).Append("\r\n");
                h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                h.Append("Cache-Control: no-cache\r\n");
                h.Append("Connection: close\r\n\r\n");
                byte[] head = Encoding.ASCII.GetBytes(h.ToString());
                ns.Write(head, 0, head.Length);
                if (body.Length > 0) ns.Write(body, 0, body.Length);
                ns.Flush();
            }
            catch { }
        }

        static string GetQuery(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (k == key) { string v = eq >= 0 ? pair.Substring(eq + 1) : ""; try { return Uri.UnescapeDataString(v.Replace('+', ' ')); } catch { return v; } }
            }
            return null;
        }

        static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder(s.Length + 2); sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"'); return sb.ToString();
        }

        static string JsonUnescape(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': if (i + 4 < s.Length) { try { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); } catch { } i += 4; } break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
