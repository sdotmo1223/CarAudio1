using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DreTunes
{
    /// <summary>Process-wide logging that survives a window that never opened.</summary>
    public static class App
    {
        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DreTunes", "desktop.log");

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>
    /// Entry point, process lifecycle and the one place the app decides to spawn something.
    ///
    ///   DreTunes.exe                          the app
    ///   DreTunes.exe --selftest               the self-test (exit 0)
    ///   DreTunes.exe --selftest --mutant      the negative control (exit 1)
    ///   DreTunes.exe --shot out.png [--surface N] [--step N] [--tab N]
    ///
    /// The window is shown first and the rig is brought up on a background thread, so a cold
    /// start with the bridge down does not wait 25 s on a black screen. What the window says
    /// during those seconds is true: the bridge is down, and it says so.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string root = null;
            string shot = null;
            bool selftest = false;
            bool mutant = false;
            int surface = 0, step = 0, tab = 0;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--selftest") selftest = true;
                else if (a == "--mutant") mutant = true;
                else if (a == "--root" && i + 1 < args.Length) root = args[++i];
                else if (a == "--shot" && i + 1 < args.Length) shot = args[++i];
                else if (a == "--surface" && i + 1 < args.Length) int.TryParse(args[++i], out surface);
                else if (a == "--step" && i + 1 < args.Length) int.TryParse(args[++i], out step);
                else if (a == "--tab" && i + 1 < args.Length) int.TryParse(args[++i], out tab);
            }

            if (root == null) root = FindRoot();
            App.Log("start args=" + string.Join(" ", args) + " root=" + root);

            if (selftest) return SelfTest.Run(root, mutant);

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    App.Log("UI exception: " + e.Exception);
                    MessageBox.Show("DreTunes hit an error:\n\n" + e.Exception.Message +
                                    "\n\nDetails: " + App.LogPath, "DreTunes",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                SetProcessDPIAware();
            }
            catch (Exception ex) { App.Log("startup: " + ex.Message); }

            if (shot != null) return Shot(root, shot, surface, step, tab);

            bool created;
            using (Mutex only = new Mutex(true, "Local\\DreTunesDesktop", out created))
            {
                if (!created)
                {
                    IntPtr w = FindWindow(null, "DreTunes");
                    if (w != IntPtr.Zero) { ShowWindow(w, 9); SetForegroundWindow(w); }
                    return 0;
                }

                AppState state = new AppState(root);
                state.Surface = Math.Max(0, Math.Min(4, surface));

                MainForm form = new MainForm(state);
                form.Shown += delegate
                {
                    state.Start();
                    Thread t = new Thread(delegate()
                    {
                        EnsureRig(root);
                        state.RunDoctor();
                    });
                    t.IsBackground = true;
                    t.Name = "bootstrap";
                    t.Start();
                };
                Application.Run(form);
                state.Rig.Stop();
                App.Log("exit");
                GC.KeepAlive(only);
                return 0;
            }
        }

        /// <summary>Locate ds18-bridge from the exe, wherever the folder is moved to.</summary>
        private static string FindRoot()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            for (int i = 0; i < 6; i++)
            {
                if (File.Exists(Path.Combine(d, "web", "index.html")) &&
                    File.Exists(Path.Combine(d, "src", "BridgeServer.cs")))
                    return d;
                DirectoryInfo parent = Directory.GetParent(d);
                if (parent == null) break;
                d = parent.FullName;
            }
            // Fall back to the conventional layout rather than the current directory, so a
            // shortcut with no "start in" still finds the rig.
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
        }

        // ------------------------------------------------------------------ rig lifecycle

        private static bool Alive(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Proxy = null;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.KeepAlive = false;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                using (Stream s = r.GetResponseStream())
                using (StreamReader rd = new StreamReader(s))
                    return rd.ReadToEnd().Length > 0;
            }
            catch { return false; }
        }

        private static bool WaitUp(string url, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Alive(url, 800)) return true;
                Thread.Sleep(400);
            }
            return false;
        }

        /// <summary>
        /// Start what is down. The bridge is required; the RTA is optional and is allowed to fail
        /// (its failure is a readiness row, not a startup error). Neither is stopped on exit: the
        /// bridge also serves the phone, and killing it would break the surface this app must
        /// not break.
        /// </summary>
        private static void EnsureRig(string root)
        {
            try
            {
                if (!Alive("http://127.0.0.1:8585/api/info", 700))
                {
                    string exe = Path.Combine(root, "bin", "ds18-bridge.exe");
                    if (File.Exists(exe))
                    {
                        App.Log("starting bridge");
                        Spawn(exe, "--serve --port 8585", root);
                        App.Log(WaitUp("http://127.0.0.1:8585/api/info", 25000)
                            ? "bridge up" : "bridge did not answer within 25 s");
                    }
                    else App.Log("bridge missing at " + exe);
                }

                if (!Alive("http://127.0.0.1:8586/rta.json", 700))
                {
                    string exe = Path.Combine(root, "bin", "rta.exe");
                    if (File.Exists(exe))
                    {
                        App.Log("starting rta");
                        Spawn(exe, "", Path.Combine(root, "bin"));
                        App.Log(WaitUp("http://127.0.0.1:8586/rta.json", 12000)
                            ? "rta up" : "rta did not answer within 12 s");
                    }
                }
            }
            catch (Exception ex) { App.Log("EnsureRig: " + ex.Message); }
        }

        private static void Spawn(string exe, string args, string cwd)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }

        // ------------------------------------------------------------------ screenshots

        /// <summary>
        /// Render one frame to a PNG without showing a window. This is how the layout was checked
        /// against the real 1366x768 screen (client 1366x730) during development, and it stays in
        /// the binary because a screenshot is the only honest way to review a custom-painted UI.
        /// </summary>
        private static int Shot(string root, string file, int surface, int step, int tab)
        {
            AppState s = new AppState(root);
            s.Surface = Math.Max(0, Math.Min(4, surface));
            s.ShowStep = Math.Max(0, Math.Min(5, step));
            s.LibraryTab = tab == 1 ? 1 : 0;
            s.RigTab = tab == 1 ? 1 : 0;
            s.LoadLibrary();
            s.LoadMeasures();

            s.Rig.Start();
            Thread.Sleep(2200);
            if (s.Surface == 3)
            {
                s.RunDoctor();
                Thread.Sleep(9000);
            }
            if (s.Surface == 1 || s.Surface == 2) s.SwapGate = new ToolResult();

            MainForm form = new MainForm(s);
            using (Bitmap bmp = form.Shot(new Size(1366, 730), 1f))
            {
                bmp.Save(file, ImageFormat.Png);
            }
            s.Rig.Stop();
            Console.WriteLine("wrote " + file);
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string cls, string title);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    }
}
