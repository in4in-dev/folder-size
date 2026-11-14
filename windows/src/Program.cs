// FolderSize Panel — панель с размерами папок, приклеенная к окну Проводника.
// Собирается компилятором C# из .NET Framework 4.x (C# 5), см. build.ps1.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FolderSizePanel
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--bench") { Bench(args[1], args.Skip(2).Select(int.Parse).ToArray()); return; }
            bool created;
            using (var mutex = new Mutex(true, "Local\\FolderSizePanel.Singleton", out created))
            {
                if (!created) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new PanelApp());
                GC.KeepAlive(mutex);
            }
        }

        // Замер: FolderSizePanel.exe --bench <папка> (вывод в консоль, если собран как консольный).
        static void Bench(string path, int[] degrees)
        {
            var sc = new Scanner(false);
            int auto = Scanner.DegreeFor(path);
            foreach (int deg in degrees.Length > 0 ? degrees : new[] { 1, auto, 1, auto })
            {
                var sw = Stopwatch.StartNew();
                var e = sc.ScanNow(path, deg);
                sw.Stop();
                Console.WriteLine("threads={0,2}  {1,8} ms  files={2:N0}  dirs={3:N0}  size={4}",
                    deg, sw.ElapsedMilliseconds, e.Files, e.Dirs, Fmt.Size(e.Size));
            }
        }
    }

    // ───────────────────────── tray + lifetime ─────────────────────────
    sealed class PanelApp : ApplicationContext
    {
        readonly PanelForm panel;
        readonly NotifyIcon tray;

        public PanelApp()
        {
            panel = new PanelForm();
            IntPtr h = panel.Handle; // создаём окно, чтобы заработали хуки и таймеры

            if (Settings.Get("AutostartInit", 0) == 0)
            {
                Autostart.Set(true);
                Settings.Set("AutostartInit", 1);
            }
            else if (Autostart.IsOn()) Autostart.Set(true); // обновить путь, если exe переехал

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "FolderSize — размеры папок в Проводнике";
            tray.Visible = true;
            tray.MouseDoubleClick += delegate { panel.SetShowPanel(!panel.ShowPanel); };
            tray.MouseUp += (o, e) => { if (e.Button == MouseButtons.Right) ShowTrayMenu(); };
        }

        // Системное (Win32) меню вместо WinForms-овского.
        void ShowTrayMenu()
        {
            IntPtr m = Native.CreatePopupMenu();
            Native.AppendMenu(m, panel.ShowPanel ? Native.MF_CHECKED : 0, (IntPtr)1, "Показывать панель");
            Native.AppendMenu(m, Autostart.IsOn() ? Native.MF_CHECKED : 0, (IntPtr)2, "Запускать вместе с Windows");
            Native.AppendMenu(m, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenu(m, panel.PreferRight ? Native.MF_CHECKED : 0, (IntPtr)3, "Панель справа от окна");
            Native.AppendMenu(m, panel.PreferRight ? 0 : Native.MF_CHECKED, (IntPtr)4, "Панель слева от окна");
            Native.AppendMenu(m, Native.MF_SEPARATOR, IntPtr.Zero, null);
            Native.AppendMenu(m, 0, (IntPtr)5, "Сбросить кэш размеров");
            Native.AppendMenu(m, 0, (IntPtr)6, "Выход");
            var pt = Cursor.Position;
            Native.SetForegroundWindow(panel.Handle);
            int cmd = Native.TrackPopupMenuEx(m, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN, pt.X, pt.Y, panel.Handle, IntPtr.Zero);
            Native.PostMessage(panel.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(m);
            switch (cmd)
            {
                case 1: panel.SetShowPanel(!panel.ShowPanel); break;
                case 2: Autostart.Set(!Autostart.IsOn()); break;
                case 3: panel.SetPreferRight(true); break;
                case 4: panel.SetPreferRight(false); break;
                case 5: panel.ResetCache(); break;
                case 6: ExitThread(); break;
            }
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(Color.FromArgb(0x4C, 0xA8, 0xF0)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillRectangle(b, 3, 18, 7, 12);
                    g.FillRectangle(b, 13, 10, 7, 20);
                    g.FillRectangle(b, 23, 2, 7, 28);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;
            tray.Dispose();
            panel.Shutdown();
            base.ExitThreadCore();
        }
    }

    // ───────────────────────── settings ─────────────────────────
    static class Settings
    {
        const string Key = @"Software\FolderSizePanel";
        public static int Get(string name, int def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : def;
                }
            }
            catch { return def; }
        }
        public static void Set(string name, int value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.DWord); } catch { }
        }
    }

    // Автозапуск — задача Планировщика с триггером «при входе» текущего пользователя (прав администратора не требует).
    // Раньше был ключ Run, но Explorer молча пропускал эту запись при входе; старое значение убираем при любом Set.
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "FolderSizePanel";

        public static bool IsOn() { return TaskExists() || LegacyRunExists(); }

        public static void Set(bool on)
        {
            if (on) CreateTask(); else Schtasks("/delete /tn \"" + Name + "\" /f");
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(Name, false); } catch { }
        }

        static bool TaskExists() { return Schtasks("/query /tn \"" + Name + "\"") == 0; }

        static bool LegacyRunExists()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }

        static void CreateTask()
        {
            string exe = Application.ExecutablePath;
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>FolderSize — размеры папок в Проводнике</Description></RegistrationInfo>\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId></LogonTrigger></Triggers>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n" +
                "  <Settings>\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
                "    <Priority>4</Priority>\n" + // по умолчанию 7 — процесс получил бы пониженный приоритет
                "    <Enabled>true</Enabled>\n" +
                "  </Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command><WorkingDirectory>" + Esc(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\n" +
                "</Task>\n";
            string tmp = Path.Combine(Path.GetTempPath(), "FolderSizePanel.task.xml");
            try
            {
                File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
                Schtasks("/create /tn \"" + Name + "\" /xml \"" + tmp + "\" /f");
            }
            catch { }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }

        static int Schtasks(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    static class Fmt
    {
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        public static string Size(long b)
        {
            if (b < 0) return "…";
            double tb = b / 1099511627776.0;
            if (tb >= 1) return tb.ToString("0.00", Ru) + " ТБ";
            double gb = b / 1073741824.0;
            if (gb >= 1) return gb.ToString(gb >= 100 ? "0" : "0.0", Ru) + " ГБ";
            double mb = b / 1048576.0;
            if (mb >= 1) return mb.ToString(mb >= 100 ? "0" : "0.0", Ru) + " МБ";
            double kb = b / 1024.0;
            if (kb >= 1) return Math.Round(kb).ToString("0", Ru) + " КБ";
            return b + " Б";
        }
        public static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            string w = (m10 == 1 && m100 != 11) ? one : (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) ? few : many;
            return n.ToString("N0", Ru) + " " + w;
        }
    }

    // ───────────────────────── background size scanner ─────────────────────────
    sealed class Scanner
    {
        public sealed class Entry { public long Size; public int Files; public int Dirs; public DateTime Stamp; public bool Denied; }
        struct Job { public string Path; public bool Force; }

        static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
        readonly ConcurrentDictionary<string, Entry> cache = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly object lk = new object();
        readonly List<Job> queue = new List<Job>();
        int gen;
        volatile string current;
        public long CurrentBytes;
        public event Action Changed;

        public Scanner() : this(true) { }

        public Scanner(bool startThread)
        {
            // Пул потоков по умолчанию добавляет потоки медленно — для блокирующего обхода диска даём запас сразу.
            int w, io;
            ThreadPool.GetMinThreads(out w, out io);
            ThreadPool.SetMinThreads(Math.Max(w, 64), io);
            if (!startThread) return;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Name = "FolderSize scanner";
            t.Start();
        }

        public string Current { get { return current; } }

        public static string Norm(string p)
        {
            if (p == null) return null;
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }

        public Entry Get(string path)
        {
            Entry e;
            return cache.TryGetValue(Norm(path), out e) ? e : null;
        }

        public bool IsQueued(string path)
        {
            lock (lk)
            {
                for (int i = 0; i < queue.Count; i++)
                    if (string.Equals(queue[i].Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool Busy
        {
            get { lock (lk) return queue.Count > 0 || current != null; }
        }

        // Заменяет очередь: считаем только то, что видно сейчас.
        public void Request(IEnumerable<string> dirs, bool force)
        {
            lock (lk)
            {
                gen++;
                queue.Clear();
                foreach (var d in dirs)
                {
                    var e = Get(d);
                    bool stale = e != null && DateTime.UtcNow - e.Stamp > Stale;
                    if (force || e == null || stale) queue.Add(new Job { Path = d, Force = force || stale });
                }
                Monitor.PulseAll(lk);
            }
        }

        public void Cancel()
        {
            lock (lk) { gen++; queue.Clear(); }
        }

        // Сбрасывает путь и всех его предков.
        public void Invalidate(string path)
        {
            string p = Norm(path);
            while (!string.IsNullOrEmpty(p))
            {
                Entry e;
                cache.TryRemove(p, out e);
                string parent;
                try { parent = Path.GetDirectoryName(p); } catch { break; }
                if (parent == null || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) break;
                p = Norm(parent);
            }
        }

        public void Clear() { cache.Clear(); }

        void Loop()
        {
            while (true)
            {
                Job job;
                int myGen;
                lock (lk)
                {
                    while (queue.Count == 0) Monitor.Wait(lk);
                    job = queue[0];
                    queue.RemoveAt(0);
                    myGen = gen;
                    current = job.Path;
                }
                Interlocked.Exchange(ref CurrentBytes, 0);
                var ctx = new ScanCtx { Gen = myGen, Force = job.Force, Degree = DegreeFor(job.Path) };
                try { ScanDir(job.Path, ctx); } catch { }
                current = null;
                var h = Changed;
                if (h != null) h();
            }
        }

        sealed class ScanCtx { public int Gen, Degree; public bool Force; public volatile bool Cancelled; }

        // Сколько потоков пускать на диск: SSD/NVMe любят параллельные запросы, HDD — нет.
        static readonly ConcurrentDictionary<string, int> degrees = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public static int DegreeFor(string path)
        {
            string root;
            try { root = Path.GetPathRoot(path) ?? ""; } catch { root = ""; }
            return degrees.GetOrAdd(root, r =>
            {
                int ssd = Math.Min(16, Math.Max(4, Environment.ProcessorCount));
                if (r.StartsWith("\\\\")) return 8; // сеть: задержки, параллельность помогает
                bool? seek = Native.HasSeekPenalty(r);
                return seek == true ? 1 : ssd;
            });
        }

        // Для режима замера: посчитать путь целиком, не трогая кэш.
        public Entry ScanNow(string path, int degree)
        {
            cache.Clear();
            return ScanDir(path, new ScanCtx { Gen = Volatile.Read(ref gen), Force = true, Degree = degree });
        }

        unsafe Entry ScanDir(string dir, ScanCtx ctx)
        {
            if (!ctx.Force)
            {
                var c = Get(dir);
                if (c != null) { Interlocked.Add(ref CurrentBytes, c.Size); return c; }
            }
            var e = new Entry();
            List<string> subs = null;
            long size = 0;
            int files = 0, n = 0;
            Native.FIND_DATA_RAW fd;
            IntPtr h = Native.FindFirstFileRaw(Native.LongPath(dir) + "\\*", 1, out fd, 0, IntPtr.Zero, 2);
            if (h == Native.INVALID_HANDLE)
            {
                e.Denied = Marshal.GetLastWin32Error() == 5;
                e.Stamp = DateTime.UtcNow;
                cache[Norm(dir)] = e;
                return e;
            }
            try
            {
                do
                {
                    char* name = fd.cFileName;
                    if (name[0] == '.' && (name[1] == '\0' || (name[1] == '.' && name[2] == '\0'))) continue;
                    if ((fd.dwFileAttributes & 0x10) != 0)
                    {
                        if (Native.IsLink(fd.dwFileAttributes, fd.dwReserved0)) continue;
                        if (subs == null) subs = new List<string>();
                        subs.Add(new string(name)); // строку делаем только для папок
                    }
                    else
                    {
                        size += ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        files++;
                    }
                    if ((++n & 1023) == 0 && ctx.Gen != Volatile.Read(ref gen)) { ctx.Cancelled = true; return null; }
                } while (Native.FindNextFileRaw(h, out fd));
            }
            finally { Native.FindClose(h); }
            Interlocked.Add(ref CurrentBytes, size);
            if (ctx.Gen != Volatile.Read(ref gen)) { ctx.Cancelled = true; return null; }

            int dirs = 0;
            if (subs != null)
            {
                string prefix = dir.EndsWith("\\") ? dir : dir + "\\";
                if (ctx.Degree > 1 && subs.Count > 1)
                {
                    long accSize = 0;
                    int accFiles = 0, accDirs = 0;
                    Parallel.ForEach(subs, new ParallelOptions { MaxDegreeOfParallelism = ctx.Degree }, (sn, st) =>
                    {
                        if (ctx.Cancelled) { st.Stop(); return; }
                        var se = ScanDir(prefix + sn, ctx);
                        if (se == null) { st.Stop(); return; }
                        Interlocked.Add(ref accSize, se.Size);
                        Interlocked.Add(ref accFiles, se.Files);
                        Interlocked.Add(ref accDirs, se.Dirs + 1);
                    });
                    if (ctx.Cancelled) return null;
                    size += accSize; files += accFiles; dirs += accDirs;
                }
                else
                {
                    foreach (var sn in subs)
                    {
                        var se = ScanDir(prefix + sn, ctx);
                        if (se == null) return null;
                        size += se.Size; files += se.Files; dirs += se.Dirs + 1;
                    }
                }
            }
            e.Size = size; e.Files = files; e.Dirs = dirs;
            e.Stamp = DateTime.UtcNow;
            cache[Norm(dir)] = e;
            return e;
        }
    }

    // ───────────────────────── list item ─────────────────────────
    sealed class Item
    {
        public string Name, FullPath, Ext;
        public bool IsDir, IsDrive, Link, Denied, Busy, IsUp;
        public long Size = -1, DriveTotal, Shown = -1, Live = -1;
        public float Bar;
        public string SizeText, PctText;
    }

    sealed class Theme
    {
        public Color Bg, Strip, StripLine, Border, Line, Text, Sub, Hover, Sel, Accent, Track, SegBg, SegOn, BtnHover;
        public static readonly Theme Light = new Theme
        {
            Bg = C(0xF9F9F9), Strip = C(0xE9EDF1), StripLine = C(0xDDE2E7), Border = C(0xD4D4D4), Line = C(0xE8E8E8),
            Text = C(0x1A1A1A), Sub = C(0x5F5F5F), Hover = C(0xEEF4FB), Sel = C(0xCCE8FF), Accent = C(0x005FB8),
            Track = C(0xE3E3E3), SegBg = C(0xE9E9E9), SegOn = C(0xFFFFFF), BtnHover = C(0xDCE1E6)
        };
        public static readonly Theme Dark = new Theme
        {
            Bg = C(0x202020), Strip = C(0x2B2B2B), StripLine = C(0x3A3A3A), Border = C(0x3A3A3A), Line = C(0x333333),
            Text = C(0xFFFFFF), Sub = C(0xC5C5C5), Hover = C(0x2A2F35), Sel = C(0x2D3B48), Accent = C(0x4CC2FF),
            Track = C(0x3A3A3A), SegBg = C(0x2E2E2E), SegOn = C(0x454545), BtnHover = C(0x3A3A3A)
        };
        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
    }

    // ───────────────────────── the panel ─────────────────────────
    sealed class PanelForm : Form
    {
        const string ThisPC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        const int RowH = 30, StripH = 36, FooterH = 48, CollapsedW = 26, MinW = 220, MaxW = 720;
        const TextFormatFlags TF = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        readonly Scanner scanner = new Scanner();
        IntPtr target;
        uint targetPid;
        object shellApp, shellWin;
        string curPath, message;
        List<Item> items = new List<Item>();
        List<Item> view = new List<Item>();
        Item selItem;
        int hoverIdx = -1, scrollY, nDirs, nFiles, pendingDirs;
        long totalShown;
        bool sortByName, collapsed, placedRight = true, overlay, draggingThumb, isThisPC;
        public bool ShowPanel = true, PreferRight = true;
        int panelW = 320, dragStartY, dragStartScroll, spin, lastRebuild;
        readonly List<Rectangle> spinnerRects = new List<Rectangle>();
        float s = 1f;
        Theme th = Theme.Light;
        Font fText, fSmall, fBig, fBold;
        Rectangle rRefresh, rCollapse, rSortSize, rSortName, rList, rThumb;
        string hoverBtn;
        long diskFree = -1, diskTotal = -1;
        string diskLabel;

        readonly Native.WinEventDelegate hookProc;
        IntPtr hookFg, hookLoc;
        readonly System.Windows.Forms.Timer poll, anim;
        FileSystemWatcher watcher;
        readonly object fsLock = new object();
        readonly HashSet<string> fsPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fsLastTick;
        readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        IContextMenu2 activeCm2;
        IContextMenu3 activeCm3;

        public PanelForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "FolderSize";
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Bounds = new Rectangle(-32000, -32000, 320, 400);

            PreferRight = Settings.Get("Right", 1) == 1;
            panelW = Math.Max(MinW, Math.Min(MaxW, Settings.Get("Width", 320)));
            collapsed = Settings.Get("Collapsed", 0) == 1;
            sortByName = Settings.Get("SortName", 0) == 1;
            ShowPanel = Settings.Get("Show", 1) == 1;

            scanner.Changed += delegate
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke(new Action(delegate { RebuildView(); Invalidate(); })); } catch { }
            };
            hookProc = OnWinEvent;
            poll = new System.Windows.Forms.Timer { Interval = 300 };
            poll.Tick += delegate { Poll(); };
            anim = new System.Windows.Forms.Timer { Interval = 16 };
            anim.Tick += delegate { AnimTick(); };
            SystemEvents.UserPreferenceChanged += OnUserPrefChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateScale();
            LoadTheme();
            hookFg = Native.SetWinEventHook(3, 3, IntPtr.Zero, hookProc, 0, 0, 2);
            poll.Start();
        }

        public void Shutdown()
        {
            poll.Stop(); anim.Stop();
            if (hookFg != IntPtr.Zero) Native.UnhookWinEvent(hookFg);
            if (hookLoc != IntPtr.Zero) Native.UnhookWinEvent(hookLoc);
            SystemEvents.UserPreferenceChanged -= OnUserPrefChanged;
            if (watcher != null) watcher.Dispose();
            scanner.Cancel();
            Close();
        }

        void OnUserPrefChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            LoadTheme();
            Invalidate();
        }

        public void SetShowPanel(bool v)
        {
            ShowPanel = v;
            Settings.Set("Show", v ? 1 : 0);
            if (!v) Hide(); else Poll();
        }

        public void SetPreferRight(bool v)
        {
            PreferRight = v;
            Settings.Set("Right", v ? 1 : 0);
            Reposition();
        }

        public void ResetCache()
        {
            scanner.Clear();
            RequestScan(false);
            RebuildView();
            Invalidate();
        }

        void LoadTheme()
        {
            int light = 1;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (k != null && k.GetValue("AppsUseLightTheme") is int) light = (int)k.GetValue("AppsUseLightTheme");
            }
            catch { }
            th = light == 0 ? Theme.Dark : Theme.Light;
            Native.SetMenuTheme(light == 0);
        }

        void UpdateScale()
        {
            try { s = Native.GetDpiForWindow(Handle) / 96f; } catch { s = 1f; }
            if (s <= 0) s = 1f;
            if (fText != null) { fText.Dispose(); fSmall.Dispose(); fBig.Dispose(); fBold.Dispose(); }
            fText = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
            fSmall = new Font("Segoe UI", 12f * s, GraphicsUnit.Pixel);
            fBold = new Font("Segoe UI Semibold", 13f * s, GraphicsUnit.Pixel);
            fBig = new Font("Segoe UI Semibold", 24f * s, GraphicsUnit.Pixel);
            foreach (var b in icons.Values) b.Dispose();
            icons.Clear();
        }

        int S(float v) { return (int)Math.Round(v * s); }

        // ───────── tracking Explorer ─────────
        void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (ev == 3)
            {
                if (hwnd != IntPtr.Zero && IsExplorer(hwnd))
                {
                    if (hwnd != target) Attach(hwnd);
                    else { Reposition(); BringToTop(); }
                }
            }
            else if (ev == 0x800B && hwnd == target && idObject == 0)
            {
                Reposition();
            }
        }

        static bool IsExplorer(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(64);
            Native.GetClassName(h, sb, sb.Capacity);
            return sb.ToString() == "CabinetWClass";
        }

        void Attach(IntPtr h)
        {
            target = h;
            shellWin = null;
            curPath = null;
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid || hookLoc == IntPtr.Zero)
            {
                if (hookLoc != IntPtr.Zero) Native.UnhookWinEvent(hookLoc);
                targetPid = pid;
                hookLoc = Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, hookProc, pid, 0, 2);
            }
            Poll();
            BringToTop();
        }

        void Poll()
        {
            if (target != IntPtr.Zero && !Native.IsWindow(target))
            {
                target = IntPtr.Zero;
                shellWin = null;
                Hide();
            }
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != target && IsExplorer(fg)) { Attach(fg); return; }
            if (target == IntPtr.Zero) return;

            bool vis = Native.IsWindowVisible(target) && !Native.IsIconic(target) && !Native.IsCloaked(target);
            if (!vis || !ShowPanel) { if (Visible) Hide(); return; }

            UpdatePath();
            FlushFsChanges();
            if (!Visible)
            {
                Reposition();
                Show();
                BringToTop();
            }
            else if (fg == target && IsCovered()) BringToTop();
        }

        // Фоновому процессу Windows не даёт поднять окно через HWND_TOP,
        // поэтому на миг делаем его topmost и сразу снимаем флаг — оно встаёт над всеми обычными окнами.
        void BringToTop()
        {
            if (!Visible) return;
            uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
            Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, f);
            Native.SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, f);
        }

        // Есть ли выше нас обычное (не topmost) видимое окно, перекрывающее панель.
        bool IsCovered()
        {
            var me = Bounds;
            int guard = 0;
            for (IntPtr h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && h != Handle && guard < 2000; h = Native.GetWindow(h, 2), guard++)
            {
                if (h == target || !Native.IsWindowVisible(h) || Native.IsIconic(h)) continue;
                if ((Native.GetWindowLong(h, -20) & 0x8) != 0) continue; // WS_EX_TOPMOST
                if (Native.IsCloaked(h)) continue;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) continue;
                if (Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom).IntersectsWith(me)) return true;
            }
            return false;
        }

        void Reposition()
        {
            if (target == IntPtr.Zero) return;
            Native.RECT r;
            if (Native.DwmGetWindowAttribute(target, 9, out r, Marshal.SizeOf(typeof(Native.RECT))) != 0)
                Native.GetWindowRect(target, out r);
            Rectangle work = Screen.FromHandle(target).WorkingArea;
            int w = collapsed ? S(CollapsedW) : S(panelW);
            bool fitsR = r.Right + w <= work.Right;
            bool fitsL = r.Left - w >= work.Left;
            int x, top = r.Top, h = r.Bottom - r.Top;
            overlay = false;
            if (PreferRight && fitsR) { x = r.Right; placedRight = true; }
            else if (!PreferRight && fitsL) { x = r.Left - w; placedRight = false; }
            else if (fitsR) { x = r.Right; placedRight = true; }
            else if (fitsL) { x = r.Left - w; placedRight = false; }
            else
            {
                // окно развёрнуто — кладём панель поверх правого края, ниже панели команд
                overlay = true;
                placedRight = PreferRight;
                x = PreferRight ? r.Right - w : r.Left;
                int skip = S(150);
                top = r.Top + skip;
                h = Math.Max(S(200), h - skip);
            }
            if (Bounds != new Rectangle(x, top, w, h))
                Native.SetWindowPos(Handle, IntPtr.Zero, x, top, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        void UpdatePath()
        {
            if (shellApp == null)
            {
                try { shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }
                catch { return; }
            }
            IntPtr tab = Native.FindWindowEx(target, IntPtr.Zero, "ShellTabWindowClass", null);
            object found = null;
            try
            {
                dynamic wins = ((dynamic)shellApp).Windows();
                int n = wins.Count;
                for (int i = 0; i < n; i++)
                {
                    object w = null;
                    try { w = wins.Item(i); } catch { }
                    if (w == null) continue;
                    long hw;
                    try { hw = Convert.ToInt64(((dynamic)w).HWND); } catch { continue; }
                    if (new IntPtr(hw) != target) continue;
                    if (found == null) found = w;
                    if (tab != IntPtr.Zero && Native.TabWindowOf(w) == tab) { found = w; break; }
                }
            }
            catch { shellApp = null; return; }
            if (found == null) return;
            shellWin = found;
            string p = null;
            try { p = (string)((dynamic)found).Document.Folder.Self.Path; } catch { }
            if (p == null) return;
            if (!string.Equals(p, curPath, StringComparison.OrdinalIgnoreCase)) LoadFolder(p, false);
        }

        void NavigateExplorer(string path)
        {
            bool ok = false;
            if (shellWin != null)
            {
                try { ((dynamic)shellWin).Navigate2(path); ok = true; } catch { }
            }
            if (!ok) StartShell("explorer.exe", "\"" + path + "\"");
            LoadFolder(path, false);
        }

        static void StartShell(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file);
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        // ───────── folder model ─────────
        void LoadFolder(string path, bool keepState)
        {
            string prevSel = selItem != null ? selItem.Name : null;
            curPath = path;
            message = null;
            isThisPC = string.Equals(path, ThisPC, StringComparison.OrdinalIgnoreCase);
            if (!keepState) { scrollY = 0; prevSel = null; }
            var list = new List<Item>();

            if (isThisPC)
            {
                SetupWatcher(null);
                scanner.Cancel();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        string root = d.RootDirectory.FullName;
                        string label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Network ? "Сетевой диск" : "Локальный диск") : d.VolumeLabel;
                        list.Add(new Item
                        {
                            Name = label + " (" + root.TrimEnd('\\') + ")", FullPath = root, IsDir = true, IsDrive = true,
                            Size = d.TotalSize - d.TotalFreeSpace, DriveTotal = d.TotalSize, Ext = ""
                        });
                    }
                    catch { }
                }
                diskTotal = -1;
            }
            else if (path.StartsWith("::") || !Directory.Exists(path))
            {
                SetupWatcher(null);
                scanner.Cancel();
                message = "Для этого места размеры не считаются";
                diskTotal = -1;
            }
            else
            {
                bool showHidden = ExplorerFlag("Hidden") == 1, showSuper = ExplorerFlag("ShowSuperHidden") == 1;
                Native.WIN32_FIND_DATA fd;
                IntPtr h = Native.FindFirstFileExW(Native.LongPath(path) + "\\*", 1, out fd, 0, IntPtr.Zero, 2);
                if (h == Native.INVALID_HANDLE)
                {
                    message = Marshal.GetLastWin32Error() == 5 ? "Нет доступа к этой папке" : "Не удалось прочитать папку";
                }
                else
                {
                    try
                    {
                        do
                        {
                            string name = fd.cFileName;
                            if (name == "." || name == "..") continue;
                            uint a = fd.dwFileAttributes;
                            bool hidden = (a & 0x2) != 0, system = (a & 0x4) != 0;
                            if (hidden && system && !showSuper) continue;
                            if (hidden && !showHidden) continue;
                            string full = path.EndsWith("\\") ? path + name : path + "\\" + name;
                            var it = new Item { Name = name, FullPath = full, IsDir = (a & 0x10) != 0 };
                            if (it.IsDir) { it.Link = Native.IsLink(a, fd.dwReserved0); it.Ext = ""; }
                            else
                            {
                                it.Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                                it.Ext = Path.GetExtension(name);
                            }
                            list.Add(it);
                        } while (Native.FindNextFileW(h, out fd));
                    }
                    finally { Native.FindClose(h); }
                }
                SetupWatcher(path);
                UpdateDisk(path);
                string parent = null;
                try { parent = Path.GetDirectoryName(path); } catch { }
                if (parent == null && path.Length <= 3 && path.Length >= 2 && path[1] == ':') parent = ThisPC; // корень диска → «Этот компьютер»
                if (parent != null) list.Insert(0, new Item { Name = "..", FullPath = parent, IsDir = true, IsUp = true, Ext = "" });
            }

            items = list;
            selItem = prevSel == null ? null : items.FirstOrDefault(i => i.Name == prevSel);
            if (!isThisPC) RequestScan(false);
            RebuildView();
            Invalidate();
        }

        static int ExplorerFlag(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    if (k != null && k.GetValue(name) is int) return (int)k.GetValue(name);
            }
            catch { }
            return 0;
        }

        void UpdateDisk(string path)
        {
            diskTotal = -1;
            try
            {
                string root = Path.GetPathRoot(path);
                ulong free, total, totalFree;
                if (Native.GetDiskFreeSpaceEx(root, out free, out total, out totalFree))
                {
                    diskFree = (long)free; diskTotal = (long)total;
                    diskLabel = root.StartsWith("\\\\") ? root : "Диск " + root.TrimEnd('\\');
                    if (!root.StartsWith("\\\\"))
                    {
                        var di = new DriveInfo(root);
                        string lbl = string.IsNullOrEmpty(di.VolumeLabel) ? "Локальный диск" : di.VolumeLabel;
                        diskLabel = lbl + " (" + root.TrimEnd('\\') + ")";
                    }
                }
            }
            catch { }
        }

        void RequestScan(bool force)
        {
            if (isThisPC || items == null) return;
            scanner.Request(items.Where(i => i.IsDir && !i.Link && !i.IsUp).Select(i => i.FullPath).ToList(), force);
            if (!anim.Enabled) anim.Start();
        }

        void RebuildView()
        {
            string cur = scanner.Current;
            long curBytes = Interlocked.Read(ref scanner.CurrentBytes);
            long total = 0, max = 1;
            int unknown = 0, dirs = 0, files = 0;
            Item up = null;
            foreach (var it in items)
            {
                it.Busy = false; it.Live = -1; it.Denied = false;
                if (it.IsUp) { up = it; it.Shown = -1; it.Bar = 0; it.SizeText = ""; it.PctText = ""; continue; }
                if (it.IsDrive) { it.Shown = it.Size; }
                else if (!it.IsDir) { it.Shown = it.Size; files++; }
                else
                {
                    dirs++;
                    if (it.Link) { it.Shown = -1; }
                    else
                    {
                        var en = scanner.Get(it.FullPath);
                        bool running = cur != null && string.Equals(cur, it.FullPath, StringComparison.OrdinalIgnoreCase);
                        it.Busy = running || scanner.IsQueued(it.FullPath);
                        it.Shown = en == null ? -1 : en.Size;
                        it.Denied = en != null && en.Denied;
                        if (running && en == null) it.Live = curBytes;
                        if (en == null) unknown++;
                    }
                }
                if (it.Shown >= 0) { total += it.Shown; if (it.Shown > max) max = it.Shown; }
            }
            foreach (var it in items)
            {
                if (it.IsUp) continue;
                if (it.IsDrive)
                {
                    it.Bar = it.DriveTotal > 0 ? (float)it.Size / it.DriveTotal : 0;
                    it.SizeText = Fmt.Size(it.Size);
                    it.PctText = "из " + Fmt.Size(it.DriveTotal);
                    continue;
                }
                if (it.Link) { it.SizeText = "ссылка"; it.PctText = ""; it.Bar = 0; continue; }
                if (it.Denied) { it.SizeText = "нет доступа"; it.PctText = ""; it.Bar = 0; continue; }
                if (it.Shown < 0)
                {
                    it.SizeText = it.Live > 0 ? Fmt.Size(it.Live) + "…" : "…";
                    it.PctText = ""; it.Bar = 0;
                    continue;
                }
                it.SizeText = Fmt.Size(it.Shown);
                it.Bar = (float)it.Shown / max;
                double p = total > 0 ? it.Shown * 100.0 / total : 0;
                it.PctText = p < 1 ? "<1%" : Math.Round(p) + "%";
            }
            var v = items.Where(i => !i.IsUp).ToList();
            if (sortByName || isThisPC)
                v.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : Native.StrCmpLogicalW(a.Name, b.Name));
            else
                v.Sort((a, b) =>
                {
                    bool ua = a.Shown < 0, ub = b.Shown < 0;
                    if (ua != ub) return ua ? 1 : -1;
                    if (ua) return Native.StrCmpLogicalW(a.Name, b.Name);
                    return b.Shown.CompareTo(a.Shown);
                });
            if (up != null) v.Insert(0, up);
            view = v;
            totalShown = total;
            pendingDirs = unknown;
            nDirs = dirs; nFiles = files;
            if (isThisPC) totalShown = items.Sum(i => i.Size);
        }

        void AnimTick()
        {
            spin = (int)((Environment.TickCount & 0x7FFFFFFF) % 900 * 0.4); // один оборот за 0,9 с
            if (!scanner.Busy) { anim.Stop(); RebuildView(); Invalidate(); return; }
            if (Environment.TickCount - lastRebuild > 250)
            {
                lastRebuild = Environment.TickCount;
                RebuildView();
                Invalidate();
            }
            else foreach (var r in spinnerRects) Invalidate(Rectangle.Inflate(r, S(2), S(2)));
        }

        // ───────── file system watching ─────────
        void SetupWatcher(string path)
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            if (path == null) return;
            try
            {
                var w = new FileSystemWatcher(path);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size;
                w.InternalBufferSize = 65536;
                FileSystemEventHandler h = (o, e) => QueueFs(e.FullPath);
                w.Changed += h; w.Created += h; w.Deleted += h;
                w.Renamed += (o, e) => { QueueFs(e.OldFullPath); QueueFs(e.FullPath); };
                w.Error += (o, e) => QueueFs(path + "\\*");
                w.EnableRaisingEvents = true;
                watcher = w;
            }
            catch { watcher = null; }
        }

        void QueueFs(string full)
        {
            lock (fsLock) { fsPending.Add(full); fsLastTick = Environment.TickCount; }
        }

        void FlushFsChanges()
        {
            List<string> batch;
            lock (fsLock)
            {
                if (fsPending.Count == 0 || Environment.TickCount - fsLastTick < 1200) return;
                batch = fsPending.ToList();
                fsPending.Clear();
            }
            if (curPath == null || isThisPC) return;
            foreach (var p in batch)
            {
                if (p.EndsWith("\\*")) { scanner.Clear(); break; }
                scanner.Invalidate(p);
            }
            LoadFolder(curPath, true);
        }

        // ───────── painting ─────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int W = ClientSize.Width, H = ClientSize.Height;
            spinnerRects.Clear();
            g.Clear(th.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (collapsed) { PaintCollapsed(g, W, H); return; }

            int sh = S(StripH);
            using (var b = new SolidBrush(th.Strip)) g.FillRectangle(b, 0, 0, W, sh);
            HLine(g, th.StripLine, 0, W, sh - 1);
            DrawLogo(g, S(12), (sh - S(16)) / 2, S(16));
            TextRenderer.DrawText(g, "FolderSize", fBold, new Rectangle(S(36), 0, W - S(110), sh), th.Text, th.Strip, TF);
            rCollapse = new Rectangle(W - S(34), (sh - S(28)) / 2, S(28), S(28));
            rRefresh = new Rectangle(rCollapse.X - S(30), rCollapse.Y, S(28), S(28));
            DrawButtonBg(g, rRefresh, "refresh");
            DrawButtonBg(g, rCollapse, "collapse");
            DrawRefresh(g, rRefresh);
            DrawChevron(g, rCollapse, placedRight);

            int pad = S(16), y = sh + S(12);
            string shown = isThisPC ? "Этот компьютер" : (curPath ?? "Откройте окно Проводника");
            if (shown.StartsWith("::")) shown = "Особая папка";
            TextRenderer.DrawText(g, shown, fSmall, new Rectangle(pad, y, W - 2 * pad, S(18)), th.Sub, th.Bg, TF | TextFormatFlags.PathEllipsis);
            y += S(20);

            if (message != null)
            {
                TextRenderer.DrawText(g, message, fText, new Rectangle(pad, y, W - 2 * pad, S(60)), th.Sub, th.Bg,
                    TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
                PaintFooter(g, W, H);
                return;
            }

            string total = Fmt.Size(totalShown) + (pendingDirs > 0 ? "+" : "");
            if (curPath == null) total = "—";
            var tsz = TextRenderer.MeasureText(g, total, fBig, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, total, fBig, new Point(pad, y), th.Text, th.Bg, TextFormatFlags.NoPadding);
            if (pendingDirs > 0 && scanner.Busy)
            {
                int sx = pad + tsz.Width + S(10), cy = y + tsz.Height / 2 + S(2);
                DrawSpinner(g, sx, cy - S(6), S(12));
                TextRenderer.DrawText(g, "ещё " + Fmt.Plural(pendingDirs, "папка", "папки", "папок"), fSmall,
                    new Rectangle(sx + S(18), cy - S(9), W - sx - S(18) - pad, S(18)), th.Sub, th.Bg, TF | TextFormatFlags.EndEllipsis);
            }
            y += S(34);
            string counts = isThisPC ? Fmt.Plural(items.Count, "диск", "диска", "дисков")
                : Fmt.Plural(nDirs, "папка", "папки", "папок") + ", " + Fmt.Plural(nFiles, "файл", "файла", "файлов");
            TextRenderer.DrawText(g, counts, fSmall, new Rectangle(pad, y, W - 2 * pad, S(18)), th.Sub, th.Bg, TF);
            y += S(26);

            if (!isThisPC)
            {
                int w1 = TextRenderer.MeasureText("По размеру", fSmall).Width + S(20);
                int w2 = TextRenderer.MeasureText("По имени", fSmall).Width + S(20);
                var seg = new Rectangle(pad, y, w1 + w2 + S(6), S(28));
                FillRound(g, th.SegBg, seg, S(6));
                rSortSize = new Rectangle(seg.X + S(2), seg.Y + S(2), w1, seg.Height - S(4));
                rSortName = new Rectangle(rSortSize.Right + S(2), rSortSize.Y, w2, rSortSize.Height);
                FillRound(g, th.SegOn, sortByName ? rSortName : rSortSize, S(4));
                TextRenderer.DrawText(g, "По размеру", fSmall, rSortSize, sortByName ? th.Sub : th.Text, TF | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, "По имени", fSmall, rSortName, sortByName ? th.Text : th.Sub, TF | TextFormatFlags.HorizontalCenter);
                y += S(40);
            }
            else { rSortSize = rSortName = Rectangle.Empty; y += S(6); }
            HLine(g, th.Line, 0, W, y);

            int footer = S(FooterH);
            rList = new Rectangle(0, y + 1, W, Math.Max(0, H - y - 1 - (diskTotal > 0 ? footer : 0)));
            PaintList(g, W);
            if (diskTotal > 0) PaintFooter(g, W, H);
            using (var pen = new Pen(th.Border)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        void PaintList(Graphics g, int W)
        {
            int rh = S(RowH), contentH = view.Count * rh + S(8);
            int maxScroll = Math.Max(0, contentH - rList.Height);
            scrollY = Math.Max(0, Math.Min(scrollY, maxScroll));
            var state = g.Save();
            g.SetClip(rList);
            bool hasBar = maxScroll > 0;
            int right = W - S(hasBar ? 14 : 8);
            int first = Math.Max(0, (scrollY - S(4)) / rh);
            for (int i = first; i < view.Count; i++)
            {
                var it = view[i];
                int ry = rList.Y + S(4) + i * rh - scrollY;
                if (ry > rList.Bottom) break;
                var row = new Rectangle(S(4), ry, right - S(4), rh - S(2));
                if (it == selItem) FillRound(g, th.Sel, row, S(4));
                else if (i == hoverIdx) FillRound(g, th.Hover, row, S(4));
                if (it.Bar > 0)
                {
                    int bw = Math.Max(S(2), (int)(row.Width * it.Bar));
                    using (var b = new SolidBrush(Color.FromArgb(150, th.Accent)))
                        g.FillRectangle(b, row.X + S(6), row.Bottom - S(4), Math.Max(S(2), bw - S(12)), S(3));
                }
                var ico = it.IsUp ? null : GetIcon(it);
                if (it.IsUp) DrawUpArrow(g, new Rectangle(row.X + S(8), ry + (rh - S(2) - S(16)) / 2, S(16), S(16)));
                else if (ico != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(ico, new Rectangle(row.X + S(8), ry + (rh - S(2) - S(16)) / 2, S(16), S(16)));
                }
                int pctW = it.IsDrive ? S(78) : S(34);
                int sizeW = S(76);
                var rPct = new Rectangle(row.Right - S(6) - pctW, ry, pctW, rh - S(2));
                var rSize = new Rectangle(rPct.X - S(6) - sizeW, ry, sizeW, rh - S(2));
                Color bg = it == selItem ? th.Sel : (i == hoverIdx ? th.Hover : th.Bg);
                TextRenderer.DrawText(g, it.PctText, fSmall, rPct, th.Sub, bg, TF | TextFormatFlags.Right);
                bool dim = it.Shown < 0 || it.Link || it.Denied;
                TextRenderer.DrawText(g, it.SizeText, dim ? fText : fBold, rSize, dim ? th.Sub : th.Text, bg, TF | TextFormatFlags.Right);
                int nameX = row.X + S(32), nameR = rSize.X - S(6);
                if (it.Busy && it.Shown < 0 && it.Live < 0)
                {
                    DrawSpinner(g, nameR - S(12), ry + (rh - S(2)) / 2 - S(6), S(12));
                    nameR -= S(18);
                }
                else if (it.Busy)
                {
                    DrawSpinner(g, nameR - S(12), ry + (rh - S(2)) / 2 - S(6), S(12));
                    nameR -= S(18);
                }
                TextRenderer.DrawText(g, it.Name, fText, new Rectangle(nameX, ry, Math.Max(0, nameR - nameX), rh - S(2)), th.Text, bg, TF | TextFormatFlags.EndEllipsis);
            }
            if (view.Count == 0 && curPath != null)
                TextRenderer.DrawText(g, "Папка пуста", fText, new Rectangle(0, rList.Y + S(24), W, S(24)), th.Sub, th.Bg, TF | TextFormatFlags.HorizontalCenter);
            g.Restore(state);

            if (hasBar)
            {
                int trackH = rList.Height - S(8);
                int thumbH = Math.Max(S(30), trackH * rList.Height / contentH);
                int thumbY = rList.Y + S(4) + (trackH - thumbH) * scrollY / maxScroll;
                rThumb = new Rectangle(W - S(10), thumbY, S(6), thumbH);
                FillRound(g, Color.FromArgb(draggingThumb ? 170 : 110, th.Sub), rThumb, S(3));
            }
            else rThumb = Rectangle.Empty;
        }

        void PaintFooter(Graphics g, int W, int H)
        {
            if (diskTotal <= 0) return;
            int fh = S(FooterH), y = H - fh;
            HLine(g, th.Line, 0, W, y);
            int pad = S(16);
            string right = "свободно " + Fmt.Size(diskFree) + " из " + Fmt.Size(diskTotal);
            int rw = TextRenderer.MeasureText(right, fSmall).Width + S(2);
            TextRenderer.DrawText(g, diskLabel ?? "", fSmall, new Rectangle(pad, y + S(8), W - 2 * pad - rw - S(8), S(18)), th.Sub, th.Bg, TF | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, right, fSmall, new Rectangle(W - pad - rw, y + S(8), rw, S(18)), th.Sub, th.Bg, TF | TextFormatFlags.Right);
            var track = new Rectangle(pad, y + S(30), W - 2 * pad, S(6));
            FillRound(g, th.Track, track, S(3));
            float used = 1f - (float)diskFree / diskTotal;
            if (used > 0) FillRound(g, th.Accent, new Rectangle(track.X, track.Y, Math.Max(S(6), (int)(track.Width * used)), track.Height), S(3));
            using (var pen = new Pen(th.Border)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        void PaintCollapsed(Graphics g, int W, int H)
        {
            using (var b = new SolidBrush(th.Strip)) g.FillRectangle(b, 0, 0, W, H);
            rCollapse = new Rectangle(0, 0, W, S(36));
            DrawButtonBg(g, rCollapse, "collapse");
            DrawChevron(g, rCollapse, !placedRight);
            DrawLogo(g, (W - S(14)) / 2, S(48), S(14));
            using (var pen = new Pen(th.Border)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        }

        void DrawButtonBg(Graphics g, Rectangle r, string id)
        {
            if (hoverBtn == id) FillRound(g, th.BtnHover, r, S(4));
        }

        void DrawLogo(Graphics g, int x, int y, int size)
        {
            float u = size / 16f;
            using (var b = new SolidBrush(th.Accent))
            {
                g.FillRectangle(b, x + 1.5f * u, y + 9 * u, 3 * u, 5.5f * u);
                g.FillRectangle(b, x + 6.5f * u, y + 5.5f * u, 3 * u, 9 * u);
                g.FillRectangle(b, x + 11.5f * u, y + 1.5f * u, 3 * u, 13 * u);
            }
        }

        void DrawRefresh(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, rad = 5f * s;
            using (var pen = new Pen(th.Text, 1.3f * s))
            {
                g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, -60, 300);
                float ax = cx + rad * (float)Math.Cos(-60 * Math.PI / 180), ay = cy + rad * (float)Math.Sin(-60 * Math.PI / 180);
                g.DrawLine(pen, ax, ay, ax, ay - 3.5f * s);
                g.DrawLine(pen, ax, ay, ax - 3.5f * s, ay);
            }
        }

        void DrawChevron(Graphics g, Rectangle r, bool pointRight)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, d = 3.5f * s;
            using (var pen = new Pen(th.Text, 1.3f * s))
            {
                if (pointRight) { g.DrawLine(pen, cx - d / 2, cy - d * 1.4f, cx + d, cy); g.DrawLine(pen, cx + d, cy, cx - d / 2, cy + d * 1.4f); }
                else { g.DrawLine(pen, cx + d / 2, cy - d * 1.4f, cx - d, cy); g.DrawLine(pen, cx - d, cy, cx + d / 2, cy + d * 1.4f); }
            }
        }

        void DrawUpArrow(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f, top = r.Y + 2.5f * s, bot = r.Bottom - 2.5f * s, d = 4.5f * s;
            using (var pen = new Pen(th.Sub, 1.4f * s))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx, top, cx, bot);
                g.DrawLine(pen, cx - d, top + d, cx, top);
                g.DrawLine(pen, cx + d, top + d, cx, top);
            }
        }

        void DrawSpinner(Graphics g, int x, int y, int size)
        {
            spinnerRects.Add(new Rectangle(x, y, size, size));
            using (var p1 = new Pen(th.Track, 1.5f * s))
            using (var p2 = new Pen(th.Accent, 1.5f * s))
            {
                g.DrawEllipse(p1, x, y, size, size);
                g.DrawArc(p2, x, y, size, size, spin, 100);
            }
        }

        static void HLine(Graphics g, Color c, int x1, int x2, int y)
        {
            using (var pen = new Pen(c)) g.DrawLine(pen, x1, y, x2, y);
        }

        static void FillRound(Graphics g, Color c, Rectangle r, int rad)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(c))
            {
                if (rad <= 0) { g.FillRectangle(b, r); return; }
                int d = rad * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        Bitmap GetIcon(Item it)
        {
            string key;
            string query;
            uint attr;
            bool useAttr = true;
            if (it.IsDrive) { key = "drive:" + it.FullPath; query = it.FullPath; attr = 0; useAttr = false; }
            else if (it.IsDir) { key = "dir"; query = "folder"; attr = 0x10; }
            else
            {
                string ext = (it.Ext ?? "").ToLowerInvariant();
                if (ext == ".exe" || ext == ".lnk" || ext == ".ico" || ext == ".url") { key = it.FullPath; query = it.FullPath; attr = 0x80; useAttr = false; }
                else { key = "ext:" + ext; query = "x" + ext; attr = 0x80; }
            }
            Bitmap bmp;
            if (icons.TryGetValue(key, out bmp)) return bmp;
            bmp = null;
            try
            {
                var info = new Native.SHFILEINFO();
                uint flags = 0x100 /*ICON*/ | (s > 1.25f ? 0u : 0x1u) /*SMALLICON at 100%*/ | (useAttr ? 0x10u : 0u);
                IntPtr r = Native.SHGetFileInfo(query, attr, ref info, (uint)Marshal.SizeOf(info), flags);
                if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    using (var ic = Icon.FromHandle(info.hIcon)) bmp = ic.ToBitmap();
                    Native.DestroyIcon(info.hIcon);
                }
            }
            catch { }
            if (icons.Count > 2000) { foreach (var b in icons.Values) if (b != null) b.Dispose(); icons.Clear(); }
            icons[key] = bmp;
            return bmp;
        }

        // ───────── input ─────────
        int RowAt(Point p)
        {
            if (collapsed || !rList.Contains(p) || rThumb.Contains(p)) return -1;
            int idx = (p.Y - rList.Y - S(4) + scrollY) / S(RowH);
            return idx >= 0 && idx < view.Count ? idx : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (draggingThumb)
            {
                int rh = S(RowH), contentH = view.Count * rh + S(8);
                int maxScroll = Math.Max(1, contentH - rList.Height);
                int trackH = rList.Height - S(8) - rThumb.Height;
                if (trackH > 0) scrollY = dragStartScroll + (e.Y - dragStartY) * maxScroll / trackH;
                Invalidate();
                return;
            }
            string hb = rCollapse.Contains(e.Location) ? "collapse" : (!collapsed && rRefresh.Contains(e.Location) ? "refresh" : null);
            int hi = RowAt(e.Location);
            if (hi != hoverIdx || hb != hoverBtn) { hoverIdx = hi; hoverBtn = hb; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx != -1 || hoverBtn != null) { hoverIdx = -1; hoverBtn = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (rCollapse.Contains(e.Location) && e.Button == MouseButtons.Left)
            {
                collapsed = !collapsed;
                Settings.Set("Collapsed", collapsed ? 1 : 0);
                hoverBtn = null;
                Reposition();
                Invalidate();
                return;
            }
            if (collapsed) return;
            if (e.Button == MouseButtons.Left && rRefresh.Contains(e.Location))
            {
                if (curPath != null) { if (isThisPC) LoadFolder(curPath, true); else { RequestScan(true); UpdateDisk(curPath); } }
                return;
            }
            if (e.Button == MouseButtons.Left && rSortSize.Contains(e.Location)) { SetSort(false); return; }
            if (e.Button == MouseButtons.Left && rSortName.Contains(e.Location)) { SetSort(true); return; }
            if (e.Button == MouseButtons.Left && rThumb.Contains(e.Location))
            {
                draggingThumb = true; dragStartY = e.Y; dragStartScroll = scrollY;
                Capture = true;
                return;
            }
            int i = RowAt(e.Location);
            if (i >= 0)
            {
                selItem = view[i];
                Invalidate();
                if (e.Button == MouseButtons.Right && !selItem.IsUp) ShowShellMenu(selItem, PointToScreen(e.Location));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (draggingThumb) { draggingThumb = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i >= 0) Open(view[i]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scrollY -= e.Delta / 120 * S(RowH) * 3;
            Invalidate();
        }

        void SetSort(bool byName)
        {
            sortByName = byName;
            Settings.Set("SortName", byName ? 1 : 0);
            RebuildView();
            Invalidate();
        }

        void Open(Item it)
        {
            if (it.IsDir) NavigateExplorer(it.FullPath);
            else StartShell(it.FullPath, null);
        }

        // Настоящее контекстное меню Проводника для файла/папки + наши пункты сверху.
        void ShowShellMenu(Item it, Point screenPt)
        {
            const int CmdRescan = 1, CmdCopy = 2, CmdNewWindow = 3, ShellFirst = 100;
            IntPtr pidl = IntPtr.Zero, parentPtr = IntPtr.Zero, cmPtr = IntPtr.Zero, hMenu = IntPtr.Zero;
            IShellFolder parent = null;
            IContextMenu cm = null;
            try
            {
                hMenu = Native.CreatePopupMenu();
                if (it.IsDir)
                {
                    Native.AppendMenu(hMenu, 0, (IntPtr)CmdNewWindow, "Открыть в новом окне");
                    if (!it.IsDrive && !it.Link) Native.AppendMenu(hMenu, 0, (IntPtr)CmdRescan, "Пересчитать размер");
                }
                Native.AppendMenu(hMenu, 0, (IntPtr)CmdCopy, "Копировать путь");
                Native.AppendMenu(hMenu, Native.MF_SEPARATOR, IntPtr.Zero, null);

                uint sfgao;
                if (Native.SHParseDisplayName(it.FullPath, IntPtr.Zero, out pidl, 0, out sfgao) == 0)
                {
                    Guid iidSF = typeof(IShellFolder).GUID, iidCM = typeof(IContextMenu).GUID;
                    IntPtr child;
                    if (Native.SHBindToParent(pidl, ref iidSF, out parentPtr, out child) == 0)
                    {
                        parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                        if (parent.GetUIObjectOf(Handle, 1, new[] { child }, ref iidCM, IntPtr.Zero, out cmPtr) == 0)
                        {
                            cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
                            uint flags = 0; // CMF_NORMAL
                            if ((ModifierKeys & Keys.Shift) != 0) flags |= 0x100; // CMF_EXTENDEDVERBS
                            cm.QueryContextMenu(hMenu, (uint)Native.GetMenuItemCount(hMenu), ShellFirst, 0x7FFF, flags);
                            activeCm3 = cm as IContextMenu3;
                            activeCm2 = activeCm3 == null ? cm as IContextMenu2 : null;
                        }
                    }
                }

                IntPtr fgBefore = Native.GetForegroundWindow();
                Native.SetForegroundWindow(Handle);
                int cmd = Native.TrackPopupMenuEx(hMenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, screenPt.X, screenPt.Y, Handle, IntPtr.Zero);
                Native.PostMessage(Handle, 0, IntPtr.Zero, IntPtr.Zero);
                activeCm2 = null; activeCm3 = null;

                if (cmd >= ShellFirst && cm != null)
                {
                    var info = new CMINVOKECOMMANDINFOEX();
                    info.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX));
                    info.fMask = 0x4000 /*UNICODE*/ | 0x20000000 /*PTINVOKE*/;
                    info.hwnd = Handle;
                    info.lpVerb = (IntPtr)(cmd - ShellFirst);
                    info.lpVerbW = (IntPtr)(cmd - ShellFirst);
                    string dir = null;
                    try { dir = Path.GetDirectoryName(it.FullPath); } catch { }
                    info.lpDirectoryW = dir;
                    info.nShow = 1;
                    info.ptInvoke = new Native.POINT { X = screenPt.X, Y = screenPt.Y };
                    cm.InvokeCommand(ref info);
                }
                else if (cmd == CmdNewWindow) StartShell("explorer.exe", "\"" + it.FullPath + "\"");
                else if (cmd == CmdRescan) { scanner.Invalidate(it.FullPath); RequestScan(false); RebuildView(); Invalidate(); }
                else if (cmd == CmdCopy) { try { Clipboard.SetText(it.FullPath); } catch { } }
                else if (cmd == 0 && target != IntPtr.Zero && fgBefore == target) Native.SetForegroundWindow(target);
            }
            catch { }
            finally
            {
                activeCm2 = null; activeCm3 = null;
                if (hMenu != IntPtr.Zero) Native.DestroyMenu(hMenu);
                if (cm != null) Marshal.ReleaseComObject(cm);
                if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
                if (parent != null) Marshal.ReleaseComObject(parent);
                if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21, WM_EXITSIZEMOVE = 0x232, WM_DPICHANGED = 0x02E0;
            // Подменю «Отправить», «Открыть с помощью» и т. п. наполняются через эти сообщения.
            if (m.Msg == 0x117 || m.Msg == 0x2B || m.Msg == 0x2C || m.Msg == 0x120)
            {
                if (activeCm3 != null)
                {
                    IntPtr res;
                    if (activeCm3.HandleMenuMsg2((uint)m.Msg, m.WParam, m.LParam, out res) == 0) { m.Result = res; return; }
                }
                else if (activeCm2 != null && m.Msg != 0x120)
                {
                    if (activeCm2.HandleMenuMsg((uint)m.Msg, m.WParam, m.LParam) == 0) { m.Result = IntPtr.Zero; return; }
                }
            }
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)3; // MA_NOACTIVATE
                    return;
                case WM_DPICHANGED:
                    UpdateScale();
                    Reposition();
                    Invalidate();
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    if (!collapsed && !overlay)
                    {
                        long lp = m.LParam.ToInt64();
                        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                        int grip = S(6);
                        if (placedRight && p.X >= ClientSize.Width - grip) m.Result = (IntPtr)11; // HTRIGHT
                        else if (!placedRight && p.X < grip) m.Result = (IntPtr)10;              // HTLEFT
                    }
                    return;
                case WM_EXITSIZEMOVE:
                    base.WndProc(ref m);
                    panelW = Math.Max(MinW, Math.Min(MaxW, (int)Math.Round(Width / s)));
                    Settings.Set("Width", panelW);
                    Reposition();
                    return;
            }
            base.WndProc(ref m);
        }
    }

    // ───────────────────────── interop ─────────────────────────
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowserWindow
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pchEaten, out IntPtr ppidl, IntPtr attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr ppenum);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public Native.POINT ptInvoke;
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr name, int cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr name, int cch);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr reserved, IntPtr name, int cch);
        [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    static class Native
    {
        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        public const uint MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        public const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        // Та же WIN32_FIND_DATAW, но blittable: без копирования имени в string на каждый файл.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public unsafe struct FIND_DATA_RAW
        {
            public uint dwFileAttributes;
            public uint ftC1, ftC2, ftA1, ftA2, ftW1, ftW2;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            public fixed char cFileName[260];
            public fixed char cAlternateFileName[14];
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
        public static extern IntPtr FindFirstFileRaw(string name, int infoLevel, out FIND_DATA_RAW data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
        public static extern bool FindNextFileRaw(IntPtr h, out FIND_DATA_RAW data);

        [StructLayout(LayoutKind.Sequential)]
        struct STORAGE_PROPERTY_QUERY { public int PropertyId, QueryType, Additional; }
        [StructLayout(LayoutKind.Sequential)]
        struct DEVICE_SEEK_PENALTY_DESCRIPTOR { public uint Version, Size; public byte IncursSeekPenalty; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, ref STORAGE_PROPERTY_QUERY inBuf, int inSize, out DEVICE_SEEK_PENALTY_DESCRIPTOR outBuf, int outSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // true — HDD (есть задержка позиционирования головки), false — SSD, null — не удалось узнать.
        public static bool? HasSeekPenalty(string root)
        {
            if (root == null || root.Length < 2 || root[1] != ':') return null;
            IntPtr h = CreateFile(@"\\.\" + root.Substring(0, 2), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == INVALID_HANDLE) return null;
            try
            {
                var q = new STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 }; // StorageDeviceSeekPenaltyProperty
                DEVICE_SEEK_PENALTY_DESCRIPTOR d;
                int ret;
                if (!DeviceIoControl(h, 0x2D1400, ref q, Marshal.SizeOf(q), out d, Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)), out ret, IntPtr.Zero)) return null;
                return d.IncursSeekPenalty != 0;
            }
            finally { CloseHandle(h); }
        }

        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr hMenu, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr hMenu);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
        [DllImport("shell32.dll")] public static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);
        [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();

        // Win32-меню по умолчанию всегда светлые; так они следуют теме Windows.
        public static void SetMenuTheme(bool dark)
        {
            try { SetPreferredAppMode(dark ? 2 : 3); FlushMenuThemes(); } catch { }
        }

        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] public static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);

        public static bool IsCloaked(IntPtr h)
        {
            int v;
            return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0;
        }

        // Не заходим в junction/symlink, чтобы не считать одно и то же дважды.
        public static bool IsLink(uint attr, uint reparseTag)
        {
            return (attr & 0x400) != 0 && (reparseTag == 0xA0000003 || reparseTag == 0xA000000C);
        }

        public static string LongPath(string p)
        {
            if (p.StartsWith(@"\\?\")) return p.TrimEnd('\\');
            if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2).TrimEnd('\\');
            return @"\\?\" + p.TrimEnd('\\');
        }

        static Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");

        // HWND вкладки (ShellTabWindowClass) для объекта из ShellWindows — так находим активную вкладку Win11.
        public static IntPtr TabWindowOf(object shellWindow)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                var sp = shellWindow as IComServiceProvider;
                if (sp == null) return IntPtr.Zero;
                Guid sid = SID_STopLevelBrowser, iid = IID_IShellBrowser;
                if (sp.QueryService(ref sid, ref iid, out ptr) != 0 || ptr == IntPtr.Zero) return IntPtr.Zero;
                var sb = (IShellBrowserWindow)Marshal.GetObjectForIUnknown(ptr);
                IntPtr h;
                sb.GetWindow(out h);
                Marshal.ReleaseComObject(sb);
                return h;
            }
            catch { return IntPtr.Zero; }
            finally { if (ptr != IntPtr.Zero) Marshal.Release(ptr); }
        }
    }
}
