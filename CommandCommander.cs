// Command Commander
//
// Saved commands that run, one after another, in a single Administrator
// Command Prompt that lives inside the window.
//
// CommandCommander.bat/.ps1 compile this file into CommandCommander.exe with
// the C# compiler that ships with Windows (.NET Framework 4, C# 5), and again
// whenever this file changes. Keep the code to C# 5 features and to the
// System, System.Drawing and System.Windows.Forms assemblies so it compiles on
// every Windows 10/11 PC without installing anything.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CommandCommander
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // Everything started from an elevated process is elevated too, so make
            // sure we run as Administrator before the Command Prompt starts.
            WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                }
                catch (Win32Exception)
                {
                    // The UAC prompt was declined.
                }
                return;
            }
            App.Run(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "commands.json"));
        }
    }

    public static class App
    {
        public static void Run(string dataFile)
        {
            Application.EnableVisualStyles();
            using (MainForm form = new MainForm(dataFile))
            {
                Application.Run(form);
            }
        }
    }

    // =====================================================================
    // Model
    // =====================================================================

    internal enum StepState { Idle, Queued, Running, Done, Failed }

    // One command. A block with one step is a single command; a block with
    // several steps is a chain that runs them in order.
    internal class Step
    {
        public string Command = "";
        public int Delay;          // extra seconds to wait before the next step
        public StepState State;    // not saved

        public Step(string command) { Command = command ?? ""; }

        public bool IsEmpty { get { return Command.Trim().Length == 0; } }
    }

    internal class Block
    {
        public readonly List<Step> Steps = new List<Step>();

        public bool IsEmpty
        {
            get
            {
                foreach (Step s in Steps) if (!s.IsEmpty) return false;
                return true;
            }
        }

        public bool IsChain { get { return Steps.Count > 1; } }

        public static Block Of(params string[] commands)
        {
            Block b = new Block();
            foreach (string c in commands) b.Steps.Add(new Step(c));
            return b;
        }
    }

    // =====================================================================
    // commands.json
    // =====================================================================

    internal static class Store
    {
        public static List<Block> Load(string path, out string error)
        {
            error = null;
            List<Block> blocks = new List<Block>();

            if (!File.Exists(path))
            {
                // First run: a couple of harmless examples.
                blocks.Add(Block.Of("ipconfig /flushdns"));
                blocks.Add(Block.Of("echo Checking the network...", "ipconfig", "ping -n 2 8.8.8.8"));
                return blocks;
            }

            try
            {
                object root = Json.Parse(File.ReadAllText(path, Encoding.UTF8));
                List<object> items = root as List<object>;
                Dictionary<string, object> obj = root as Dictionary<string, object>;
                if (obj != null) items = Get(obj, "blocks") as List<object>;
                if (items == null) throw new FormatException("No list of commands was found in the file.");

                foreach (object item in items)
                {
                    Dictionary<string, object> d = item as Dictionary<string, object>;
                    if (d == null) continue;
                    Block b = new Block();

                    List<object> steps = Get(d, "steps") as List<object>;
                    if (steps != null)
                    {
                        foreach (object so in steps)
                        {
                            Dictionary<string, object> sd = so as Dictionary<string, object>;
                            if (sd == null) continue;
                            Step s = new Step((Get(sd, "command") as string ?? "").Trim());
                            object delay = Get(sd, "delay");
                            if (delay is double) s.Delay = Math.Max(0, Math.Min(3600, (int)(double)delay));
                            if (!s.IsEmpty) b.Steps.Add(s);
                        }
                    }
                    else
                    {
                        // Format written by the first version: { "Name": "...", "Command": "line 1\r\nline 2" }
                        string text = Get(d, "command") as string;
                        if (text != null)
                        {
                            foreach (string line in Regex.Split(text, "\r\n|\r|\n"))
                                if (line.Trim().Length > 0) b.Steps.Add(new Step(line.Trim()));
                        }
                    }

                    if (b.Steps.Count > 0) blocks.Add(b);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { File.Copy(path, path + ".bak", true); } catch { }
                blocks.Clear();
            }
            return blocks;
        }

        public static void Save(string path, List<Block> blocks)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\r\n  \"version\": 2,\r\n  \"blocks\": [");
            bool first = true;
            foreach (Block b in blocks)
            {
                List<Step> steps = b.Steps.FindAll(delegate(Step s) { return !s.IsEmpty; });
                if (steps.Count == 0) continue;
                sb.Append(first ? "\r\n" : ",\r\n");
                first = false;
                sb.Append("    { \"steps\": [");
                for (int i = 0; i < steps.Count; i++)
                {
                    sb.Append(i == 0 ? "\r\n" : ",\r\n");
                    sb.Append("      { \"command\": ").Append(Json.Quote(steps[i].Command.Trim()));
                    sb.Append(", \"delay\": ").Append(steps[i].Delay.ToString(CultureInfo.InvariantCulture)).Append(" }");
                }
                sb.Append("\r\n    ] }");
            }
            sb.Append(first ? "]\r\n}\r\n" : "\r\n  ]\r\n}\r\n");

            // Write to a temporary file first so a failed save never leaves a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }
    }

    // Just enough JSON for commands.json (objects, arrays, strings, numbers, true/false/null).
    internal static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Skip(s, ref i);
            if (i != s.Length) throw new FormatException("Unexpected text at position " + i + ".");
            return v;
        }

        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static void Expect(string s, ref int i, char c)
        {
            Skip(s, ref i);
            if (i >= s.Length || s[i] != c) throw new FormatException("Expected '" + c + "' at position " + i + ".");
            i++;
        }

        static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of file.");
            char c = s[i];

            if (c == '{')
            {
                i++;
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Skip(s, ref i);
                    string key = Str(s, ref i);
                    Expect(s, ref i, ':');
                    d[key] = Value(s, ref i);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                i++;
                List<object> list = new List<object>();
                Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(Value(s, ref i));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return list;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("Unexpected character at position " + i + ".");
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unfinished text value.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Unfinished text value.");
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Bad \\u escape.");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
        }
    }

    // =====================================================================
    // The Command Prompt behind the console panel
    // =====================================================================

    // Reads cmd.exe's output and spots the hidden marker we print after every
    // command, which tells us the command has finished, its exit code and the
    // current folder.
    internal class MarkerParser
    {
        readonly string marker;
        readonly StringBuilder pending = new StringBuilder();
        bool awaitingFolder;
        int exitCode;

        public event Action<string> Text;               // normal output
        public event Action<int, string> Finished;      // exit code, current folder

        public MarkerParser(string marker) { this.marker = marker; }

        public void Reset()
        {
            pending.Length = 0;
            awaitingFolder = false;
        }

        public void Feed(string chunk)
        {
            pending.Append(chunk);
            while (true)
            {
                string p = pending.ToString();

                if (awaitingFolder)
                {
                    int nl = p.IndexOf('\n');
                    if (nl < 0) return;
                    string folder = p.Substring(0, nl).Trim();
                    pending.Remove(0, nl + 1);
                    awaitingFolder = false;
                    if (Finished != null) Finished(exitCode, folder);
                    continue;
                }

                int at = p.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    int nl = p.IndexOf('\n', at);
                    if (nl < 0)
                    {
                        Emit(p.Substring(0, at));
                        pending.Remove(0, at);
                        return;
                    }
                    string value = p.Substring(at + marker.Length, nl - at - marker.Length).Trim();
                    int code;
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    {
                        exitCode = code;
                    }
                    else if (value == "%errorlevel%" || value == "%^errorlevel%")
                    {
                        exitCode = 0;   // could not read the exit code; treat as success
                    }
                    else
                    {
                        // Not our marker line (for example cmd echoing our own input after "echo on").
                        Emit(p.Substring(0, nl + 1));
                        pending.Remove(0, nl + 1);
                        continue;
                    }
                    Emit(p.Substring(0, at));
                    pending.Remove(0, nl + 1);
                    awaitingFolder = true;
                    continue;
                }

                // Show everything except a tail that might be the start of a marker.
                int keep = 0;
                for (int k = Math.Min(p.Length, marker.Length - 1); k > 0; k--)
                {
                    if (string.CompareOrdinal(p, p.Length - k, marker, 0, k) == 0) { keep = k; break; }
                }
                Emit(p.Substring(0, p.Length - keep));
                pending.Remove(0, p.Length - keep);
                return;
            }
        }

        void Emit(string s)
        {
            if (s.Length > 0 && Text != null) Text(s);
        }
    }

    internal class ConsoleEngine
    {
        readonly Control ui;
        readonly string marker;
        readonly MarkerParser parser;
        Process proc;
        StreamWriter input;
        int generation;

        public string Folder = Environment.SystemDirectory;

        public event Action<string, bool> Output;   // text, is error output
        public event Action<int> Finished;          // exit code of the command that just finished
        public event Action<int> Exited;            // cmd.exe closed by itself (for example "exit")

        public ConsoleEngine(Control ui)
        {
            this.ui = ui;
            marker = "##CCDONE" + Guid.NewGuid().ToString("N").Substring(0, 8) + "#";
            parser = new MarkerParser(marker);
            parser.Text += delegate(string s) { if (Output != null) Output(s, false); };
            parser.Finished += delegate(int code, string folder)
            {
                if (folder.Length > 1 && (folder.IndexOf(":\\", StringComparison.Ordinal) == 1 || folder.StartsWith("\\\\"))) Folder = folder;
                if (Finished != null) Finished(code);
            };
        }

        public bool IsRunning
        {
            get
            {
                try { return proc != null && !proc.HasExited; } catch { return false; }
            }
        }

        static Encoding OemEncoding()
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.Default; }
        }

        public void Start()
        {
            generation++;
            int gen = generation;
            parser.Reset();

            Encoding enc = OemEncoding();
            ProcessStartInfo psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/q");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            if (Directory.Exists(Folder)) psi.WorkingDirectory = Folder;

            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.Exited += delegate { Post(gen, delegate { OnExited(p); }); };
            p.Start();

            proc = p;
            input = new StreamWriter(p.StandardInput.BaseStream, enc);
            input.AutoFlush = true;
            Read(p.StandardOutput, false, gen);
            Read(p.StandardError, true, gen);
        }

        // Runs one command line. Afterwards cmd prints our marker with the exit code,
        // then the current folder. "%^errorlevel%" survives the first expansion of the
        // line and "call" expands it again once the command has finished.
        public bool Execute(string command)
        {
            return Write(command + " & call echo " + marker + "%^errorlevel% & cd");
        }

        // Text typed while a command is running goes to that command (answers to prompts).
        public bool SendInput(string text)
        {
            return Write(text);
        }

        bool Write(string line)
        {
            try
            {
                if (!IsRunning) return false;
                input.Write(line + "\r\n");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Restart()
        {
            KillTree();
            Start();
        }

        public void Shutdown()
        {
            KillTree();
        }

        void KillTree()
        {
            Process p = proc;
            proc = null;
            generation++;
            parser.Reset();
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    try
                    {
                        ProcessStartInfo k = new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id);
                        k.CreateNoWindow = true;
                        k.UseShellExecute = false;
                        using (Process kp = Process.Start(k)) kp.WaitForExit(5000);
                    }
                    catch { }
                    if (!p.HasExited) p.Kill();
                }
            }
            catch { }
        }

        void OnExited(Process p)
        {
            if (p != proc) return;
            int code = 0;
            try { code = p.ExitCode; } catch { }
            proc = null;
            if (Exited != null) Exited(code);
        }

        void Read(StreamReader reader, bool isError, int gen)
        {
            Thread t = new Thread(delegate()
            {
                char[] buf = new char[4096];
                try
                {
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        string s = new string(buf, 0, n);
                        Post(gen, delegate
                        {
                            if (isError) { if (Output != null) Output(s, true); }
                            else parser.Feed(s);
                        });
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Post(int gen, MethodInvoker action)
        {
            try
            {
                if (ui.IsDisposed || !ui.IsHandleCreated) return;
                ui.BeginInvoke((MethodInvoker)delegate { if (gen == generation) action(); });
            }
            catch { }
        }
    }

    // Runs blocks one step at a time and waits for each step to really finish
    // before starting the next one.
    internal class Runner
    {
        class Job
        {
            public List<Step> Steps;
            public int Index;
        }

        static readonly Regex TimeoutRx = new Regex(@"^timeout(?:\.exe)?\s+(?:/nobreak\s+)?/t\s+(\d+)(?:\s+/nobreak)?\s*$", RegexOptions.IgnoreCase);
        static readonly Regex ServiceRx = new Regex(@"^sc(?:\.exe)?\s+(start|stop)\s+""?([^""\s\\][^""\s]*)""?", RegexOptions.IgnoreCase);
        static readonly Regex RobocopyRx = new Regex(@"^robocopy(?:\.exe)?\b", RegexOptions.IgnoreCase);

        readonly MainForm form;
        readonly ConsoleEngine engine;
        readonly Queue<Job> queue = new Queue<Job>();
        readonly System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();
        MethodInvoker afterWait;
        Job current;
        int token;   // bumped by Abort so late callbacks are ignored

        public Runner(MainForm form, ConsoleEngine engine)
        {
            this.form = form;
            this.engine = engine;
            engine.Finished += OnFinished;
            waitTimer.Tick += delegate
            {
                waitTimer.Stop();
                MethodInvoker a = afterWait;
                afterWait = null;
                if (a != null) a();
            };
        }

        public bool Busy { get { return current != null; } }
        public int QueuedJobs { get { return queue.Count; } }

        public string Progress
        {
            get { return current == null ? "" : (current.Index + 1) + " / " + current.Steps.Count; }
        }

        Step Current { get { return current.Steps[current.Index]; } }

        public void Enqueue(List<Step> steps)
        {
            List<Step> list = new List<Step>();
            foreach (Step s in steps)
            {
                if (s.IsEmpty) continue;
                s.State = StepState.Queued;
                list.Add(s);
            }
            if (list.Count == 0) return;
            Job j = new Job();
            j.Steps = list;
            queue.Enqueue(j);
            if (current == null) Next();
            else form.StepsChanged();
        }

        void Next()
        {
            current = queue.Count > 0 ? queue.Dequeue() : null;
            if (current != null) RunCurrent();
            else form.StepsChanged();
        }

        void RunCurrent()
        {
            if (current.Index >= current.Steps.Count)
            {
                if (current.Steps.Count > 1)
                    form.ConsoleUi.Note("DONE", "All " + current.Steps.Count + " commands finished.", Theme.Green);
                current = null;
                Next();
                return;
            }

            Step s = Current;
            s.State = StepState.Running;
            form.StepsChanged();
            string cmd = s.Command.Trim();
            form.ConsoleUi.EchoCommand(engine.Folder, cmd);

            if (cmd.StartsWith("::") || Regex.IsMatch(cmd, @"^rem(\s|$)", RegexOptions.IgnoreCase))
            {
                Succeeded(s);
                return;
            }

            // "timeout" refuses to run when its input is redirected, so do the wait here.
            Match t = TimeoutRx.Match(cmd);
            if (t.Success)
            {
                int secs = Math.Min(86400, int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture));
                form.ConsoleUi.Note("WAIT", "Waiting " + secs + " s.", Theme.Amber);
                Wait(secs, delegate { Succeeded(s); });
                return;
            }

            if (!engine.Execute(cmd))
            {
                form.ConsoleUi.Note("ERROR", "The Command Prompt is not running. Press ABORT to restart it.", Theme.Red);
                Fail(s, -1);
            }
        }

        void OnFinished(int code)
        {
            if (current == null) return;
            Step s = Current;
            bool ok = code == 0 || (RobocopyRx.IsMatch(s.Command.Trim()) && code > 0 && code < 8);
            if (ok) Succeeded(s);
            else Fail(s, code);
        }

        // Called after the console was restarted because cmd.exe closed by itself.
        public void ConsoleExited(int code)
        {
            if (current != null) OnFinished(code);
        }

        void Succeeded(Step s)
        {
            s.State = StepState.Done;
            form.StepsChanged();

            // "sc start/stop" returns before the service has actually started or stopped.
            Match m = ServiceRx.Match(s.Command.Trim());
            if (m.Success)
            {
                bool start = m.Groups[1].Value.Equals("start", StringComparison.OrdinalIgnoreCase);
                WaitForService(m.Groups[2].Value, start ? "RUNNING" : "STOPPED", AfterSettle);
                return;
            }
            AfterSettle();
        }

        void AfterSettle()
        {
            if (current == null) return;
            Step s = Current;
            bool last = current.Index >= current.Steps.Count - 1;
            if (!last && s.Delay > 0)
            {
                form.ConsoleUi.Note("WAIT", "Waiting " + s.Delay + " s before the next command.", Theme.Amber);
                Wait(s.Delay, Advance);
            }
            else
            {
                Advance();
            }
        }

        void Advance()
        {
            if (current == null) return;
            current.Index++;
            RunCurrent();
        }

        void Fail(Step s, int code)
        {
            s.State = StepState.Failed;
            bool more = current.Index < current.Steps.Count - 1;
            for (int i = current.Index + 1; i < current.Steps.Count; i++) current.Steps[i].State = StepState.Idle;
            form.ConsoleUi.Note("STOP", "Exit code " + code + "." + (more ? " The rest of the chain was not run." : ""), Theme.Red);
            current = null;
            Next();
        }

        public void Abort()
        {
            token++;
            waitTimer.Stop();
            afterWait = null;
            if (current != null)
            {
                foreach (Step s in current.Steps)
                    s.State = s.State == StepState.Running ? StepState.Failed : (s.State == StepState.Queued ? StepState.Idle : s.State);
            }
            foreach (Job j in queue)
                foreach (Step s in j.Steps) s.State = StepState.Idle;
            queue.Clear();
            current = null;

            engine.Restart();
            form.ConsoleUi.Note("ABORT", "Stopped. The Command Prompt was restarted.", Theme.Red);
            form.StepsChanged();
        }

        void Wait(int seconds, MethodInvoker then)
        {
            int t = token;
            waitTimer.Stop();
            afterWait = delegate { if (t == token) then(); };
            waitTimer.Interval = Math.Max(1, seconds * 1000);
            waitTimer.Start();
        }

        void WaitForService(string name, string wanted, MethodInvoker then)
        {
            int t = token;
            form.ConsoleUi.Note("WAIT", "Waiting for service \"" + name + "\" to be " + wanted + ".", Theme.Amber);
            Thread th = new Thread(delegate()
            {
                bool reached = false;
                DateTime until = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < until)
                {
                    string state = QueryService(name);
                    if (state == null) break;   // unknown service or sc failed: don't wait
                    if (state == wanted) { reached = true; break; }
                    Thread.Sleep(500);
                }
                form.Post(delegate
                {
                    if (t != token) return;
                    if (!reached) form.ConsoleUi.Note("WAIT", "Could not confirm the service state; continuing.", Theme.Amber);
                    then();
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        static string QueryService(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", "query \"" + name + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    Match m = Regex.Match(o, @"\b\d\s+(STOPPED|START_PENDING|STOP_PENDING|RUNNING|CONTINUE_PENDING|PAUSE_PENDING|PAUSED)\b");
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }

    // =====================================================================
    // Look and feel
    // =====================================================================

    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(3, 7, 13);
        public static readonly Color Panel = Color.FromArgb(10, 22, 36);
        public static readonly Color PanelTop = Color.FromArgb(17, 37, 58);
        public static readonly Color PanelBottom = Color.FromArgb(7, 15, 26);
        public static readonly Color Field = Color.FromArgb(3, 10, 18);
        public static readonly Color ConsoleBg = Color.FromArgb(2, 6, 11);
        public static readonly Color Border = Color.FromArgb(30, 74, 112);
        public static readonly Color Blue = Color.FromArgb(64, 170, 255);
        public static readonly Color Cyan = Color.FromArgb(120, 222, 255);
        public static readonly Color Text = Color.FromArgb(214, 236, 255);
        public static readonly Color TextDim = Color.FromArgb(104, 140, 174);
        public static readonly Color ConsoleText = Color.FromArgb(186, 212, 232);
        public static readonly Color Green = Color.FromArgb(62, 222, 124);
        public static readonly Color Amber = Color.FromArgb(255, 192, 72);
        public static readonly Color Red = Color.FromArgb(255, 78, 64);
        public static readonly Color ErrorText = Color.FromArgb(255, 132, 120);

        public static readonly float S;   // display scale (1.0 at 100%)
        public static readonly Font TitleFont, ButtonFont, ChipFont, LabelFont, UiFont, MonoFont, ConsoleFont;

        static Theme()
        {
            S = 1f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = Math.Max(1f, g.DpiX / 96f);
            }
            catch { }

            string display = PickFamily("Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Bahnschrift",
                                        "Agency FB", "Segoe UI Semibold", "DejaVu Sans Condensed", "Arial");
            FontStyle ds = (display == "Bahnschrift" || display == "Agency FB" || display == "DejaVu Sans Condensed")
                ? FontStyle.Bold : FontStyle.Regular;
            string ui = PickFamily("Segoe UI", "DejaVu Sans", "Arial");
            string mono = PickFamily("Consolas", "Cascadia Mono", "DejaVu Sans Mono", "Courier New");

            TitleFont = new Font(display, 30f * S, ds, GraphicsUnit.Pixel);
            ButtonFont = new Font(display, 20f * S, ds, GraphicsUnit.Pixel);
            ChipFont = new Font(display, 14f * S, ds, GraphicsUnit.Pixel);
            LabelFont = new Font(display, 15f * S, ds, GraphicsUnit.Pixel);
            UiFont = new Font(ui, 13f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            MonoFont = new Font(mono, 16f * S, FontStyle.Regular, GraphicsUnit.Pixel);
            ConsoleFont = new Font(mono, 14f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static int Px(float v) { return (int)Math.Round(v * S); }

        static string PickFamily(params string[] names)
        {
            FontFamily[] installed = FontFamily.Families;
            foreach (string n in names)
                foreach (FontFamily f in installed)
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f.Name;
            return FontFamily.GenericSansSerif.Name;
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B);
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // A rectangle with the top-left and bottom-right corners cut off.
        public static GraphicsPath Chamfer(RectangleF r, float c)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddPolygon(new PointF[]
            {
                new PointF(r.Left + c, r.Top),
                new PointF(r.Right, r.Top),
                new PointF(r.Right, r.Bottom - c),
                new PointF(r.Right - c, r.Bottom),
                new PointF(r.Left, r.Bottom),
                new PointF(r.Left, r.Top + c),
            });
            return p;
        }

        public static void Glow(Graphics g, GraphicsPath path, Color c, float strength)
        {
            for (int i = 3; i >= 1; i--)
            {
                using (Pen p = new Pen(Alpha(c, (int)(26 * (4 - i) * strength)), i * 2.2f * S))
                {
                    p.LineJoin = LineJoin.Miter;
                    g.DrawPath(p, path);
                }
            }
        }

        public static void GlowLine(Graphics g, Color c, float x1, float y, float x2)
        {
            float[] widths = { 9f, 5f, 2f };
            int[] alphas = { 40, 90, 255 };
            for (int i = 0; i < 3; i++)
            {
                using (Pen p = new Pen(Alpha(c, alphas[i]), widths[i] * S))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLine(p, x1, y, x2, y);
                }
            }
        }

        // Letter-spaced text, like the headings in the game menus.
        public static float DrawSpaced(Graphics g, string text, Font font, Color color, Color glow, float x, float y, float spacing)
        {
            StringFormat sf = StringFormat.GenericTypographic;
            float spaceW = font.Size * 0.35f;
            using (SolidBrush gb = new SolidBrush(Alpha(glow, 70)))
            using (SolidBrush tb = new SolidBrush(color))
            {
                foreach (char ch in text)
                {
                    string s = ch.ToString();
                    if (ch == ' ') { x += spaceW + spacing; continue; }
                    if (glow != Color.Empty)
                    {
                        g.DrawString(s, font, gb, x - 1, y, sf);
                        g.DrawString(s, font, gb, x + 1, y, sf);
                        g.DrawString(s, font, gb, x, y - 1, sf);
                        g.DrawString(s, font, gb, x, y + 1, sf);
                    }
                    g.DrawString(s, font, tb, x, y, sf);
                    x += g.MeasureString(s, font, PointF.Empty, sf).Width + spacing;
                }
            }
            return x;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int border = Border.R | (Border.G << 8) | (Border.B << 16);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);   // window border colour (Windows 11)
                int caption = Bg.R | (Bg.G << 8) | (Bg.B << 16);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);  // title bar colour (Windows 11)
            }
            catch { }
        }

        // Grey hint text shown in an empty text box.
        public static void SetCue(TextBox box, string cue)
        {
            try { SendMessage(box.Handle, 0x1501, (IntPtr)1, cue); } catch { }
        }

        public static ContextMenuStrip Menu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.Font = UiFont;
            m.ForeColor = Text;
            m.BackColor = Panel;
            return m;
        }

        public static void StyleToolTip(ToolTip tips)
        {
            tips.OwnerDraw = true;
            tips.Popup += delegate(object o, PopupEventArgs e)
            {
                Size sz = TextRenderer.MeasureText(tips.GetToolTip(e.AssociatedControl), UiFont);
                e.ToolTipSize = new Size(sz.Width + Px(18), sz.Height + Px(12));
            };
            tips.Draw += delegate(object o, DrawToolTipEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(Panel)) e.Graphics.FillRectangle(b, e.Bounds);
                using (Pen p = new Pen(Blue)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, UiFont, e.Bounds, Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
        }
    }

    internal class DarkMenuColors : ProfessionalColorTable
    {
        static Color Hi { get { return Theme.Blend(Theme.Blue, Theme.Panel, 0.65f); } }
        public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
        public override Color MenuBorder { get { return Theme.Blue; } }
        public override Color MenuItemBorder { get { return Theme.Blue; } }
        public override Color MenuItemSelected { get { return Hi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Panel; } }
        public override Color CheckBackground { get { return Theme.Blend(Theme.Green, Theme.Panel, 0.6f); } }
        public override Color CheckSelectedBackground { get { return Theme.Blend(Theme.Green, Theme.Panel, 0.4f); } }
        public override Color CheckPressedBackground { get { return Theme.Blend(Theme.Green, Theme.Panel, 0.4f); } }
    }

    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.TextDim : (e.Item.Selected ? Color.White : Theme.Text);
            base.OnRenderItemText(e);
        }
    }

    // =====================================================================
    // Controls
    // =====================================================================

    // Base for the custom-drawn controls: double buffered, background shows through.
    internal class Drawn : Control
    {
        public Drawn()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
    }

    internal class Sc2Button : Drawn
    {
        Color accent;
        bool hover, down;

        public Sc2Button(string text, Color accent)
        {
            SetStyle(ControlStyles.Selectable, false);
            this.accent = accent;
            Text = text;
            Font = Theme.ButtonFont;
            Cursor = Cursors.Hand;
        }

        public Color Accent
        {
            get { return accent; }
            set { if (accent != value) { accent = value; Invalidate(); } }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            down = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            bool on = Enabled;
            Color a = on ? accent : Theme.Border;
            float m = 3f * Theme.S;
            RectangleF r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            float c = Math.Min(11f * Theme.S, r.Height / 3f);

            using (GraphicsPath path = Theme.Chamfer(r, c))
            {
                if (on && hover) Theme.Glow(g, path, a, 1f);

                float top = down ? 0.80f : (hover ? 0.45f : 0.60f);
                RectangleF gr = new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2);
                using (LinearGradientBrush br = new LinearGradientBrush(gr, Theme.Blend(a, Theme.Bg, top), Theme.Blend(a, Theme.Bg, 0.9f), 90f))
                    g.FillPath(br, path);

                // glassy sheen on the top half
                g.SetClip(path);
                using (SolidBrush sheen = new SolidBrush(Color.FromArgb(on ? 22 : 8, 255, 255, 255)))
                    g.FillRectangle(sheen, r.X, r.Y, r.Width, r.Height * 0.45f);
                g.ResetClip();

                using (Pen p = new Pen(Theme.Alpha(a, on ? (hover ? 255 : 200) : 140), 1.4f * Theme.S))
                    g.DrawPath(p, path);

                // bright ticks on the cut corners
                using (Pen p = new Pen(Theme.Alpha(Theme.Blend(a, Color.White, 0.45f), on ? 235 : 70), 2f * Theme.S))
                {
                    g.DrawLine(p, r.Left, r.Top + c, r.Left + c, r.Top);
                    g.DrawLine(p, r.Right - c, r.Bottom, r.Right, r.Bottom - c);
                }
            }

            string text = Text.ToUpperInvariant();
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                RectangleF tr = new RectangleF(0, down ? 1 : 0, Width, Height);
                if (on)
                {
                    using (SolidBrush gb = new SolidBrush(Theme.Alpha(a, hover ? 120 : 60)))
                    {
                        g.DrawString(text, Font, gb, new RectangleF(tr.X - 1, tr.Y, tr.Width, tr.Height), sf);
                        g.DrawString(text, Font, gb, new RectangleF(tr.X + 1, tr.Y, tr.Width, tr.Height), sf);
                        g.DrawString(text, Font, gb, new RectangleF(tr.X, tr.Y + 1, tr.Width, tr.Height), sf);
                    }
                }
                using (SolidBrush tb = new SolidBrush(on ? Theme.Blend(a, Color.White, 0.75f) : Theme.TextDim))
                    g.DrawString(text, Font, tb, tr, sf);
            }
        }
    }

    // Small square button with an X, used to delete a command.
    internal class GlyphButton : Drawn
    {
        bool hover;

        public GlyphButton()
        {
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            Color c = hover ? Theme.Red : Theme.TextDim;
            float m = 2f * Theme.S;
            RectangleF r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            using (GraphicsPath path = Theme.Chamfer(r, 5f * Theme.S))
            {
                using (SolidBrush b = new SolidBrush(hover ? Theme.Alpha(Theme.Red, 40) : Theme.Alpha(Theme.Bg, 120))) g.FillPath(b, path);
                using (Pen p = new Pen(hover ? Theme.Red : Theme.Border, 1.2f * Theme.S)) g.DrawPath(p, path);
            }
            float i = Width * 0.34f;
            using (Pen p = new Pen(c, 2f * Theme.S))
            {
                g.DrawLine(p, i, i, Width - i, Height - i);
                g.DrawLine(p, Width - i, i, i, Height - i);
            }
        }
    }

    // Single-line text box that turns a multi-line paste into several commands.
    internal class PasteTextBox : TextBox
    {
        public event Action<string[]> MultiLinePaste;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
            {
                if (HandlePaste()) { e.Handled = true; e.SuppressKeyPress = true; return; }
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0302 && HandlePaste()) return;   // WM_PASTE (context menu)
            base.WndProc(ref m);
        }

        bool HandlePaste()
        {
            if (MultiLinePaste == null) return false;
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            if (t == null || (t.IndexOf('\n') < 0 && t.IndexOf('\r') < 0)) return false;

            List<string> lines = new List<string>();
            foreach (string l in Regex.Split(t, "\r\n|\r|\n"))
                if (l.Trim().Length > 0) lines.Add(l.Trim());
            if (lines.Count == 0) return true;
            if (lines.Count == 1) { SelectedText = lines[0]; return true; }
            MultiLinePaste(lines.ToArray());
            return true;
        }
    }

    // The framed text box that holds a command.
    internal class CommandField : Drawn
    {
        public readonly PasteTextBox Box;
        public readonly Step Step;   // null for the console input line
        public float Pulse = 1f;

        public CommandField(Step step, string cue)
        {
            Step = step;
            Cursor = Cursors.IBeam;
            Box = new PasteTextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.MonoFont;
            if (step != null) Box.Text = step.Command;
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.HandleCreated += delegate { Theme.SetCue(Box, cue); };
            Controls.Add(Box);
        }

        Color StateColor()
        {
            if (Step != null)
            {
                switch (Step.State)
                {
                    case StepState.Running: return Theme.Amber;
                    case StepState.Queued: return Theme.Blend(Theme.Amber, Theme.Border, 0.55f);
                    case StepState.Done: return Theme.Green;
                    case StepState.Failed: return Theme.Red;
                }
            }
            return Box.Focused ? Theme.Cyan : Theme.Border;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Theme.Px(18);
            Box.SetBounds(x, (Height - Box.Height) / 2, Math.Max(10, Width - x - Theme.Px(12)), Box.Height);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            Color sc = StateColor();
            bool running = Step != null && Step.State == StepState.Running;
            bool lit = Box.Focused || (Step != null && Step.State != StepState.Idle);
            float m = 3f * Theme.S;
            RectangleF r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            float c = 9f * Theme.S;

            using (GraphicsPath path = Theme.Chamfer(r, c))
            {
                if (lit) Theme.Glow(g, path, sc, running ? 0.35f + 0.65f * Pulse : 0.8f);
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(lit ? sc : Theme.Border, 1.4f * Theme.S)) g.DrawPath(p, path);
            }
            // status bar on the left edge
            using (SolidBrush b = new SolidBrush(lit ? sc : Theme.Alpha(Theme.Blue, 110)))
                g.FillRectangle(b, r.X + 5 * Theme.S, r.Y + c, 3 * Theme.S, r.Height - c - 6 * Theme.S);
        }
    }

    // Numbered handle on each command of a chain; drag it to move that command.
    internal class StepBadge : Drawn
    {
        readonly string label;
        bool hover;

        public StepBadge(MainForm form, Block block, Step step, int number)
        {
            label = number.ToString(CultureInfo.InvariantCulture);
            Font = Theme.LabelFont;
            Cursor = Cursors.SizeAll;
            new DragSource(form, this, null, block, step);
            ContextMenuStrip = form.StepMenu(block, step);
            form.Tips.SetToolTip(this, "Drag to move this command within the chain or out of it.\nRight-click for more options.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ContextMenuStrip != null) ContextMenuStrip.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            float m = 2f * Theme.S;
            RectangleF r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            using (GraphicsPath path = Theme.Chamfer(r, 7f * Theme.S))
            {
                if (hover) Theme.Glow(g, path, Theme.Cyan, 0.7f);
                using (SolidBrush b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Blue, hover ? 90 : 45))) g.FillPath(b, path);
                using (Pen p = new Pen(hover ? Theme.Cyan : Theme.Blue, 1.3f * Theme.S)) g.DrawPath(p, path);
            }
            using (StringFormat sf = new StringFormat())
            using (SolidBrush tb = new SolidBrush(Theme.Text))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(label, Font, tb, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    // Turns mouse presses on a control into drags handled by the main window.
    internal class DragSource
    {
        readonly MainForm form;
        readonly Control control;
        readonly Predicate<Point> area;
        readonly Block block;
        readonly Step step;
        Point start;
        bool pressed, active;

        public DragSource(MainForm form, Control control, Predicate<Point> area, Block block, Step step)
        {
            this.form = form;
            this.control = control;
            this.area = area;
            this.block = block;
            this.step = step;
            control.MouseDown += OnDown;
            control.MouseMove += OnMove;
            control.MouseUp += OnUp;
            control.MouseCaptureChanged += OnCaptureLost;
        }

        void OnDown(object o, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || (area != null && !area(e.Location))) return;
            pressed = true;
            start = e.Location;
        }

        void OnMove(object o, MouseEventArgs e)
        {
            if (!pressed) return;
            if (!active)
            {
                if (Math.Abs(e.X - start.X) + Math.Abs(e.Y - start.Y) < Theme.Px(5)) return;
                form.DragBegin(block, step);
                if (!form.Dragging) { pressed = false; return; }
                active = true;
            }
            form.DragMove(control.PointToScreen(e.Location));
        }

        void OnUp(object o, MouseEventArgs e)
        {
            bool was = active;
            pressed = active = false;
            if (was) form.DragEnd(control.PointToScreen(e.Location), false);
        }

        void OnCaptureLost(object o, EventArgs e)
        {
            if (active && !control.Capture)
            {
                pressed = active = false;
                form.DragEnd(Cursor.Position, true);
            }
        }
    }

    // One command block: grip, RUN button and one row per command.
    internal class BlockView : Drawn
    {
        public class Row
        {
            public Step Step;
            public StepBadge Badge;
            public CommandField Field;
            public Sc2Button Delay;
            public GlyphButton Remove;
            public int Top;
        }

        public readonly Block Block;
        public readonly Sc2Button RunButton;
        public readonly List<Row> Rows = new List<Row>();
        readonly MainForm form;
        int joinPos = -1;
        bool dragging, gripHover;

        public static int Pad { get { return Theme.Px(8); } }
        public static int GripW { get { return Theme.Px(24); } }
        public static int RowH { get { return Theme.Px(48); } }
        public static int RowGap { get { return Theme.Px(8); } }
        static int RunW { get { return Theme.Px(132); } }
        static int BadgeW { get { return Theme.Px(32); } }
        static int ChipW { get { return Theme.Px(70); } }
        static int XW { get { return Theme.Px(28); } }

        public BlockView(MainForm form, Block block, bool trailing)
        {
            this.form = form;
            Block = block;
            bool chain = block.IsChain;

            RunButton = new Sc2Button(chain ? "RUN ALL" : "RUN", Theme.Green);
            RunButton.Click += delegate { form.RunBlock(Block); };
            form.Tips.SetToolTip(RunButton, chain
                ? "Run all " + block.Steps.Count + " commands, one after another"
                : "Run this command (or press Enter in the box)");
            Controls.Add(RunButton);

            for (int i = 0; i < block.Steps.Count; i++)
            {
                Step s = block.Steps[i];
                Row r = new Row();
                r.Step = s;

                CommandField f = new CommandField(s, trailing ? "Type a command here..." : "");
                r.Field = f;
                f.Box.TextChanged += delegate { form.FieldChanged(Block, s, f); };
                f.Box.Leave += delegate { form.FieldLeft(); };
                f.Box.KeyDown += delegate(object o, KeyEventArgs e) { form.FieldKey(Block, s, f, e); };
                f.Box.MultiLinePaste += delegate(string[] lines) { form.MultiPaste(Block, s, f, lines); };
                Controls.Add(f);

                if (chain)
                {
                    r.Badge = new StepBadge(form, block, s, i + 1);
                    Controls.Add(r.Badge);
                    if (i < block.Steps.Count - 1)
                    {
                        Sc2Button d = new Sc2Button(DelayText(s), Theme.Blue);
                        d.Font = Theme.ChipFont;
                        d.Click += delegate { form.ShowDelayMenu(s, d); };
                        form.Tips.SetToolTip(d, "How long to wait before the next command.\nAUTO starts it as soon as this one has finished.");
                        r.Delay = d;
                        Controls.Add(d);
                    }
                }

                if (!trailing)
                {
                    GlyphButton x = new GlyphButton();
                    x.Click += delegate { form.RemoveStep(Block, s); };
                    form.Tips.SetToolTip(x, chain ? "Remove this command from the chain" : "Delete this command");
                    r.Remove = x;
                    Controls.Add(x);
                }
                Rows.Add(r);
            }

            new DragSource(form, this, delegate(Point p) { return p.X < Pad + GripW + Theme.Px(4); }, block, null);
            form.Tips.SetToolTip(this, trailing ? "" : "Drag the grip to move this block.\nDrop it onto another block to chain them.");
            UpdateState();
        }

        public static string DelayText(Step s)
        {
            return s.Delay <= 0 ? "AUTO" : "+" + s.Delay + "S";
        }

        public int JoinPos
        {
            get { return joinPos; }
            set { if (joinPos != value) { joinPos = value; Invalidate(); } }
        }

        public bool Dragging
        {
            get { return dragging; }
            set { if (dragging != value) { dragging = value; Invalidate(); } }
        }

        public void UpdateState()
        {
            bool running = false, queued = false;
            foreach (Step s in Block.Steps)
            {
                if (s.State == StepState.Running) running = true;
                if (s.State == StepState.Queued) queued = true;
            }
            RunButton.Enabled = !Block.IsEmpty;
            RunButton.Accent = running || queued ? Theme.Amber : Theme.Green;
            RunButton.Text = running ? "RUNNING" : (queued ? "QUEUED" : (Block.IsChain ? "RUN ALL" : "RUN"));
            foreach (Row r in Rows) r.Field.Invalidate();
        }

        // Positions the child controls for the given width and returns the block's height.
        public int Arrange(int width)
        {
            int n = Rows.Count;
            int h = Pad * 2 + n * RowH + (n - 1) * RowGap;
            int x0 = Pad + GripW + Theme.Px(6);
            RunButton.SetBounds(x0, Pad, RunW, h - Pad * 2);
            int x1 = x0 + RunW + Theme.Px(12);
            bool chain = n > 1;

            for (int i = 0; i < n; i++)
            {
                Row r = Rows[i];
                r.Top = Pad + i * (RowH + RowGap);
                int right = width - Pad;
                if (r.Remove != null) r.Remove.SetBounds(right - XW, r.Top + (RowH - XW) / 2, XW, XW);
                right -= XW + Theme.Px(8);
                if (chain)
                {
                    if (r.Delay != null) r.Delay.SetBounds(right - ChipW, r.Top + Theme.Px(6), ChipW, RowH - Theme.Px(12));
                    right -= ChipW + Theme.Px(8);
                }
                int fx = x1;
                if (r.Badge != null)
                {
                    r.Badge.SetBounds(x1, r.Top + (RowH - BadgeW) / 2, BadgeW, BadgeW);
                    fx += BadgeW + Theme.Px(10);
                }
                r.Field.SetBounds(fx, r.Top, Math.Max(Theme.Px(80), right - fx), RowH);
            }
            return h;
        }

        // Where a command dropped at this height (relative to the block) would go in the chain.
        public int RowIndexAt(int y)
        {
            for (int i = 0; i < Rows.Count; i++)
                if (y < Rows[i].Top + RowH / 2) return i;
            return Rows.Count;
        }

        bool InGrip(Point p) { return p.X < Pad + GripW + Theme.Px(4); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = InGrip(e.Location) && !Block.IsEmpty;
            if (h != gripHover) { gripHover = h; Invalidate(); }
            Cursor = h || form.Dragging ? Cursors.SizeAll : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (gripHover) { gripHover = false; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            float S = Theme.S;
            float m = 4f * S;
            RectangleF r = new RectangleF(m, m, Width - 2 * m, Height - 2 * m);
            float c = 14f * S;
            bool join = joinPos >= 0;

            using (GraphicsPath path = Theme.Chamfer(r, c))
            {
                if (join) Theme.Glow(g, path, Theme.Cyan, 1f);
                using (LinearGradientBrush br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
                    Theme.PanelTop, Theme.PanelBottom, 90f))
                    g.FillPath(br, path);

                // grip strip
                g.SetClip(path);
                using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Blue, gripHover ? 45 : 16)))
                    g.FillRectangle(b, 0, 0, Pad + GripW, Height);
                g.ResetClip();

                Color bc = join ? Theme.Cyan : (dragging ? Theme.Amber : Theme.Border);
                using (Pen p = new Pen(bc, (join ? 2f : 1.4f) * S))
                {
                    if (dragging) p.DashStyle = DashStyle.Dash;
                    g.DrawPath(p, path);
                }
                using (Pen p = new Pen(Theme.Alpha(join ? Theme.Cyan : Theme.Blue, 220), 2f * S))
                {
                    g.DrawLine(p, r.Left, r.Top + c, r.Left + c, r.Top);
                    g.DrawLine(p, r.Right - c, r.Bottom, r.Right, r.Bottom - c);
                }
            }

            // grip dots
            if (!Block.IsEmpty)
            {
                Color dot = gripHover ? Theme.Cyan : Theme.Alpha(Theme.TextDim, 200);
                float d = 3.2f * S;
                using (SolidBrush b = new SolidBrush(dot))
                {
                    for (float y = Pad + 12 * S; y < Height - Pad - 8 * S; y += 7 * S)
                    {
                        g.FillEllipse(b, Pad + 6 * S, y, d, d);
                        g.FillEllipse(b, Pad + 13 * S, y, d, d);
                    }
                }
            }

            // line that links the commands of a chain
            if (Rows.Count > 1 && Rows[0].Badge != null)
            {
                float cx = Rows[0].Badge.Left + Rows[0].Badge.Width / 2f;
                using (Pen p = new Pen(Theme.Alpha(Theme.Blue, 170), 2f * S))
                    g.DrawLine(p, cx, Rows[0].Top + RowH / 2f, cx, Rows[Rows.Count - 1].Top + RowH / 2f);
            }

            // where a dropped command would join the chain
            if (join)
            {
                float y = joinPos < Rows.Count ? Rows[joinPos].Top - RowGap / 2f : Rows[Rows.Count - 1].Top + RowH + RowGap / 2f;
                float x1 = RunButton.Right + 8 * S;
                Theme.GlowLine(g, Theme.Cyan, x1, y, Width - Pad - 4 * S);
            }
        }
    }

    // Scrolling area that holds the blocks; draws the faint grid and the insert line.
    internal class BlocksPanel : Panel
    {
        int insertY = -1;

        public BlocksPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            AutoScroll = true;
            BackColor = Theme.Bg;
        }

        public int InsertY
        {
            get { return insertY; }
            set { if (insertY != value) { insertY = value; Invalidate(); } }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScrollbars(this);
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Bg);
            int step = Theme.Px(32);
            int off = AutoScrollPosition.Y % step;
            using (Pen p = new Pen(Theme.Alpha(Theme.Border, 34)))
            {
                for (int x = 0; x < Width; x += step) g.DrawLine(p, x, 0, x, Height);
                for (int y = off; y < Height; y += step) g.DrawLine(p, 0, y, Width, y);
            }
            // fade the grid out towards the bottom
            Rectangle fade = new Rectangle(0, Height / 2, Width, Height / 2 + 1);
            if (fade.Height > 1)
            {
                using (LinearGradientBrush b = new LinearGradientBrush(fade, Theme.Alpha(Theme.Bg, 0), Theme.Alpha(Theme.Bg, 200), 90f))
                    g.FillRectangle(b, fade);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (insertY < 0) return;
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            float x1 = MainForm.SidePad, x2 = ClientSize.Width - MainForm.SidePad;
            Theme.GlowLine(g, Theme.Cyan, x1 + 8, insertY, x2 - 8);
            float t = 6f * Theme.S;
            using (SolidBrush b = new SolidBrush(Theme.Cyan))
            {
                g.FillPolygon(b, new PointF[] { new PointF(x1 - 2, insertY - t), new PointF(x1 + t, insertY), new PointF(x1 - 2, insertY + t) });
                g.FillPolygon(b, new PointF[] { new PointF(x2 + 2, insertY - t), new PointF(x2 - t, insertY), new PointF(x2 + 2, insertY + t) });
            }
        }
    }

    internal class Header : Drawn
    {
        public Header()
        {
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            float S = Theme.S;
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, Width, Height + 1), Theme.PanelTop, Theme.Bg, 90f))
                g.FillRectangle(b, 0, 0, Width, Height);

            float x = Theme.DrawSpaced(g, "COMMAND COMMANDER", Theme.TitleFont, Theme.Text, Theme.Blue, 22 * S, 12 * S, 3 * S);
            Theme.DrawSpaced(g, "ADMINISTRATOR", Theme.ChipFont, Theme.Green, Color.Empty, 24 * S, 50 * S, 3 * S);

            // frame line along the bottom, brighter under the title, with an angled step
            float y = Height - 3 * S;
            float notch = x + 30 * S;
            using (Pen p = new Pen(Theme.Border, 1.5f * S))
                g.DrawLine(p, notch + 14 * S, y, Width, y);
            using (Pen p = new Pen(Theme.Blue, 2f * S))
            {
                g.DrawLine(p, 0, y - 6 * S, notch, y - 6 * S);
                g.DrawLine(p, notch, y - 6 * S, notch + 14 * S, y);
            }

            // usage hints on the right
            string[][] hints =
            {
                new string[] { "ENTER", "RUN" },
                new string[] { "DRAG GRIP", "MOVE" },
                new string[] { "DROP ON A BLOCK", "CHAIN" },
            };
            float hx = Width - 20 * S;
            float hy = 26 * S;
            for (int i = hints.Length - 1; i >= 0; i--)
            {
                SizeF vs = g.MeasureString(hints[i][1], Theme.ChipFont);
                hx -= vs.Width;
                using (SolidBrush b = new SolidBrush(Theme.TextDim)) g.DrawString(hints[i][1], Theme.ChipFont, b, hx, hy);
                SizeF ks = g.MeasureString(hints[i][0], Theme.ChipFont);
                hx -= ks.Width + 12 * S;
                RectangleF kr = new RectangleF(hx - 4 * S, hy - 3 * S, ks.Width + 8 * S, ks.Height + 6 * S);
                using (GraphicsPath path = Theme.Chamfer(kr, 5 * S))
                {
                    using (SolidBrush b = new SolidBrush(Theme.Alpha(Theme.Blue, 40))) g.FillPath(b, path);
                    using (Pen p = new Pen(Theme.Alpha(Theme.Blue, 160), 1f * S)) g.DrawPath(p, path);
                }
                using (SolidBrush b = new SolidBrush(Theme.Cyan)) g.DrawString(hints[i][0], Theme.ChipFont, b, hx, hy);
                hx -= 28 * S;
            }
        }
    }

    // The built-in Command Prompt: output, status, and a line for typing.
    internal class ConsolePanel : Drawn
    {
        readonly MainForm form;
        readonly RichTextBox output;
        public readonly CommandField Input;
        readonly Sc2Button send, clear, abort;
        readonly List<string> history = new List<string>();
        int historyPos;
        string status = "STARTING";
        Color statusColor = Theme.Amber;
        string folder = "";
        bool pendingCR;

        static int HeaderH { get { return Theme.Px(48); } }
        static int InputH { get { return Theme.Px(60); } }
        static int Side { get { return Theme.Px(16); } }

        public ConsolePanel(MainForm form)
        {
            this.form = form;
            BackColor = Theme.Bg;

            output = new RichTextBox();
            output.BorderStyle = BorderStyle.None;
            output.ReadOnly = true;
            output.BackColor = Theme.ConsoleBg;
            output.ForeColor = Theme.ConsoleText;
            output.Font = Theme.ConsoleFont;
            output.DetectUrls = false;
            output.WordWrap = true;
            output.ScrollBars = RichTextBoxScrollBars.Vertical;
            output.HandleCreated += delegate { Theme.DarkScrollbars(output); };
            Controls.Add(output);

            Input = new CommandField(null, "Type a command and press Enter.  While a command runs, type your answers to its questions here.");
            Input.Box.KeyDown += InputKey;
            Controls.Add(Input);

            send = new Sc2Button("SEND", Theme.Blue);
            send.Click += delegate { Submit(); };
            form.Tips.SetToolTip(send, "Run the typed command, or send it as input to the command that is running");
            Controls.Add(send);

            clear = new Sc2Button("CLEAR", Theme.Blue);
            clear.Font = Theme.LabelFont;
            clear.Click += delegate { output.Clear(); pendingCR = false; };
            form.Tips.SetToolTip(clear, "Clear the console output");
            Controls.Add(clear);

            abort = new Sc2Button("ABORT", Theme.Red);
            abort.Font = Theme.LabelFont;
            abort.Click += delegate { form.Abort(); };
            form.Tips.SetToolTip(abort, "Stop what is running, cancel queued commands and restart the Command Prompt");
            Controls.Add(abort);
        }

        public void SetStatus(string text, Color color, string currentFolder)
        {
            status = text;
            statusColor = color;
            folder = currentFolder ?? "";
            Invalidate(new Rectangle(0, 0, Width, HeaderH));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (abort == null || clear == null || send == null || Input == null) return;   // still being built
            int w = Width, h = Height, side = Side;
            int bh = Theme.Px(34);
            abort.SetBounds(w - side - Theme.Px(104), (HeaderH - bh) / 2 + Theme.Px(2), Theme.Px(104), bh);
            clear.SetBounds(abort.Left - Theme.Px(8) - Theme.Px(96), abort.Top, Theme.Px(96), bh);

            Rectangle frame = OutputFrame();
            output.SetBounds(frame.X + Theme.Px(14), frame.Y + Theme.Px(10), Math.Max(10, frame.Width - Theme.Px(20)), Math.Max(10, frame.Height - Theme.Px(16)));

            int iy = h - InputH + Theme.Px(6);
            int sw = Theme.Px(116);
            send.SetBounds(w - side - sw, iy, sw, InputH - Theme.Px(14));
            Input.SetBounds(side, iy, Math.Max(10, send.Left - Theme.Px(10) - side), InputH - Theme.Px(14));
        }

        Rectangle OutputFrame()
        {
            return new Rectangle(Side, HeaderH, Math.Max(10, Width - 2 * Side), Math.Max(10, Height - HeaderH - InputH));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            float S = Theme.S;

            float x = Theme.DrawSpaced(g, "CONSOLE", Theme.LabelFont, Theme.Text, Theme.Blue, Side + 2 * S, 15 * S, 3 * S);
            x += 18 * S;
            float cy = 24 * S;
            using (SolidBrush b = new SolidBrush(Theme.Alpha(statusColor, 70))) g.FillEllipse(b, x - 3 * S, cy - 7 * S, 14 * S, 14 * S);
            using (SolidBrush b = new SolidBrush(statusColor)) g.FillEllipse(b, x, cy - 4 * S, 8 * S, 8 * S);
            x += 16 * S;
            x = Theme.DrawSpaced(g, status, Theme.LabelFont, statusColor, Color.Empty, x, 15 * S, 2 * S);
            if (folder.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.TextDim))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Trimming = StringTrimming.EllipsisPath;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    float right = clear.Left - 12 * S;
                    if (right > x + 40 * S)
                        g.DrawString(folder, Theme.UiFont, b, new RectangleF(x + 18 * S, 17 * S, right - x - 18 * S, 20 * S), sf);
                }
            }

            Rectangle f = OutputFrame();
            using (GraphicsPath path = Theme.Chamfer(new RectangleF(f.X + 1, f.Y + 1, f.Width - 2, f.Height - 2), 14 * S))
            {
                using (SolidBrush b = new SolidBrush(Theme.ConsoleBg)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Border, 1.4f * S)) g.DrawPath(p, path);
            }
            using (Pen p = new Pen(Theme.Blue, 2f * S))
            {
                float c = 14 * S;
                g.DrawLine(p, f.X + 1, f.Y + 1 + c, f.X + 1 + c, f.Y + 1);
                g.DrawLine(p, f.Right - 1 - c, f.Bottom - 1, f.Right - 1, f.Bottom - 1 - c);
            }
        }

        void InputKey(object o, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Submit();
            }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                e.Handled = true;
                if (history.Count == 0) return;
                historyPos = Math.Max(0, Math.Min(history.Count, historyPos + (e.KeyCode == Keys.Up ? -1 : 1)));
                Input.Box.Text = historyPos < history.Count ? history[historyPos] : "";
                Input.Box.SelectionStart = Input.Box.TextLength;
            }
        }

        void Submit()
        {
            string t = Input.Box.Text;
            if (t.Trim().Length == 0 && !form.Busy) return;
            if (t.Trim().Length > 0 && (history.Count == 0 || history[history.Count - 1] != t)) history.Add(t);
            historyPos = history.Count;
            Input.Box.Clear();
            form.SubmitInput(t);
        }

        // ---- writing to the console ----

        bool AtLineStart
        {
            get { return output.TextLength == 0 || output.Text[output.TextLength - 1] == '\n'; }
        }

        void Raw(string s, Color c)
        {
            if (s.Length == 0) return;
            output.SelectionStart = output.TextLength;
            output.SelectionLength = 0;
            output.SelectionColor = c;
            output.AppendText(s);
        }

        void EraseCurrentLine()
        {
            int start = output.Text.LastIndexOf('\n') + 1;
            if (start >= output.TextLength) return;
            output.ReadOnly = false;
            output.Select(start, output.TextLength - start);
            output.SelectedText = "";
            output.ReadOnly = true;
        }

        void Finish()
        {
            if (output.TextLength > 400000)
            {
                output.ReadOnly = false;
                output.Select(0, output.TextLength - 300000);
                output.SelectedText = "";
                output.ReadOnly = true;
            }
            output.SelectionStart = output.TextLength;
            output.ScrollToCaret();
        }

        void NewLineIfNeeded()
        {
            if (!AtLineStart) Raw("\n", Theme.ConsoleText);
        }

        // Output from cmd.exe. Handles "\r" progress lines and the form feed that "cls" prints.
        public void AppendOutput(string text, bool isError)
        {
            if (pendingCR)
            {
                pendingCR = false;
                if (!text.StartsWith("\n")) EraseCurrentLine();
            }
            if (text.EndsWith("\r"))
            {
                pendingCR = true;
                text = text.Substring(0, text.Length - 1);
            }
            text = text.Replace("\r\n", "\n");
            int ff = text.LastIndexOf('\f');
            if (ff >= 0)
            {
                output.Clear();
                text = text.Substring(ff + 1);
            }
            string[] parts = text.Split('\r');
            Color c = isError ? Theme.ErrorText : Theme.ConsoleText;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) EraseCurrentLine();
                Raw(parts[i], c);
            }
            Finish();
        }

        public void EchoCommand(string currentFolder, string command)
        {
            NewLineIfNeeded();
            if (output.TextLength > 0 && !output.Text.EndsWith("\n\n")) Raw("\n", Theme.ConsoleText);
            Raw(currentFolder + ">", Theme.Blue);
            Raw(command + "\n", Color.White);
            Finish();
        }

        public void EchoInput(string text)
        {
            Raw(text + "\n", Theme.Cyan);
            Finish();
        }

        public void Note(string tag, string text, Color color)
        {
            NewLineIfNeeded();
            Raw("[ " + tag + " ] ", color);
            Raw(text + "\n", Theme.Blend(color, Theme.ConsoleText, 0.45f));
            Finish();
        }

        public void Banner()
        {
            Raw("COMMAND COMMANDER  //  ADMINISTRATOR COMMAND PROMPT\n", Theme.Cyan);
            Finish();
        }
    }

    // =====================================================================
    // Main window
    // =====================================================================

    internal class MainForm : Form
    {
        class DropTarget
        {
            public bool Join;          // false: insert as its own block before Ref (null = at the end)
            public Block Ref;
            public int Pos;            // position in Ref's chain when joining
        }

        public static int SidePad { get { return Theme.Px(16); } }
        static int BlockGap { get { return Theme.Px(12); } }

        readonly string dataFile;
        readonly List<Block> blocks;
        readonly List<BlockView> views = new List<BlockView>();
        readonly BlocksPanel blocksPanel;
        readonly SplitContainer split;
        readonly ConsoleEngine engine;
        readonly Runner runner;
        readonly System.Windows.Forms.Timer pulseTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer scrollTimer = new System.Windows.Forms.Timer();
        readonly string loadError;
        public readonly ConsolePanel ConsoleUi;
        public readonly ToolTip Tips = new ToolTip();

        bool rebuilding;
        float pulse;
        Block dragBlock;
        Step dragStep;
        DropTarget target;
        Point dragScreen;

        public MainForm(string dataFile)
        {
            this.dataFile = dataFile;
            blocks = Store.Load(dataFile, out loadError);

            AutoScaleMode = AutoScaleMode.None;
            Text = "Command Commander (Administrator)";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UiFont;
            ClientSize = new Size(Theme.Px(1180), Theme.Px(820));
            MinimumSize = new Size(Theme.Px(860), Theme.Px(600));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"); } catch { }

            Theme.StyleToolTip(Tips);
            Tips.AutoPopDelay = 12000;

            Header header = new Header();
            header.Dock = DockStyle.Top;
            header.Height = Theme.Px(80);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.BackColor = Theme.Border;
            split.SplitterWidth = Theme.Px(5);
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            split.TabStop = false;

            blocksPanel = new BlocksPanel();
            blocksPanel.Dock = DockStyle.Fill;
            blocksPanel.Resize += delegate { Arrange(); };
            split.Panel1.Controls.Add(blocksPanel);

            ConsoleUi = new ConsolePanel(this);
            ConsoleUi.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(ConsoleUi);

            Controls.Add(split);
            Controls.Add(header);

            engine = new ConsoleEngine(this);
            engine.Output += delegate(string text, bool isError) { ConsoleUi.AppendOutput(text, isError); };
            engine.Exited += OnConsoleExited;
            runner = new Runner(this, engine);

            pulseTimer.Interval = 60;
            pulseTimer.Tick += delegate { Pulse(); };
            scrollTimer.Interval = 30;
            scrollTimer.Tick += delegate { AutoScrollWhileDragging(); };

            Normalize();
            Rebuild();
        }

        // ---------------------------------------------------------------- lifetime

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Theme.DarkTitleBar(this);
            try
            {
                split.Panel1MinSize = Theme.Px(150);
                split.Panel2MinSize = Theme.Px(190);
                split.SplitterDistance = (int)(split.Height * 0.55);
            }
            catch { }

            ConsoleUi.Banner();
            try
            {
                engine.Start();
            }
            catch (Exception ex)
            {
                ConsoleUi.Note("ERROR", "Could not start the Command Prompt: " + ex.Message, Theme.Red);
            }
            StepsChanged();
            pulseTimer.Start();

            if (loadError != null)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    MessageBox.Show(this,
                        "Could not read " + dataFile + ":\n" + loadError + "\n\nA copy of the file was saved as " + dataFile + ".bak.",
                        "Command Commander", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            try
            {
                Store.Save(dataFile, blocks);
            }
            catch (Exception ex)
            {
                DialogResult r = MessageBox.Show(this,
                    "Your commands could not be saved to " + dataFile + ":\n" + ex.Message + "\n\nClose anyway?",
                    "Command Commander", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            pulseTimer.Stop();
            engine.Shutdown();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && Dragging)
            {
                DragEnd(Cursor.Position, true);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public void Post(MethodInvoker action)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch { }
        }

        void OnConsoleExited(int code)
        {
            ConsoleUi.Note("INFO", "The Command Prompt closed (exit code " + code + "). Starting a new one.", Theme.TextDim);
            try { engine.Start(); }
            catch (Exception ex) { ConsoleUi.Note("ERROR", "Could not restart the Command Prompt: " + ex.Message, Theme.Red); }
            runner.ConsoleExited(code);
        }

        // ---------------------------------------------------------------- running

        public bool Busy { get { return runner.Busy; } }

        public void RunBlock(Block b)
        {
            foreach (Step s in b.Steps)
                if (s.State == StepState.Running || s.State == StepState.Queued) return;   // already on its way
            runner.Enqueue(new List<Step>(b.Steps));
        }

        public void RunSingle(Step s)
        {
            if (s.State == StepState.Running || s.State == StepState.Queued) return;
            runner.Enqueue(new List<Step> { s });
        }

        public void SubmitInput(string text)
        {
            if (runner.Busy)
            {
                ConsoleUi.EchoInput(text);
                engine.SendInput(text);
            }
            else if (text.Trim().Length > 0)
            {
                runner.Enqueue(new List<Step> { new Step(text.Trim()) });
            }
        }

        public void Abort()
        {
            runner.Abort();
        }

        public void StepsChanged()
        {
            foreach (BlockView v in views) v.UpdateState();
            if (runner.Busy)
            {
                string s = "RUNNING " + runner.Progress;
                if (runner.QueuedJobs > 0) s += "   +" + runner.QueuedJobs + " QUEUED";
                ConsoleUi.SetStatus(s, Theme.Amber, engine.Folder);
            }
            else
            {
                ConsoleUi.SetStatus(engine.IsRunning ? "READY" : "OFFLINE", engine.IsRunning ? Theme.Green : Theme.Red, engine.Folder);
            }
        }

        void Pulse()
        {
            if (!runner.Busy) return;
            pulse += 0.18f;
            float v = (float)(0.5 + 0.5 * Math.Sin(pulse));
            foreach (BlockView bv in views)
            {
                foreach (BlockView.Row r in bv.Rows)
                {
                    if (r.Step.State != StepState.Running) continue;
                    r.Field.Pulse = v;
                    r.Field.Invalidate();
                }
            }
        }

        // ---------------------------------------------------------------- blocks

        bool IsTrailing(Block b)
        {
            return blocks.Count > 0 && blocks[blocks.Count - 1] == b && b.Steps.Count == 1 && b.IsEmpty;
        }

        Step FocusedStep()
        {
            CommandField f = FocusedField();
            return f == null ? null : f.Step;
        }

        CommandField FocusedField()
        {
            foreach (BlockView v in views)
                foreach (BlockView.Row r in v.Rows)
                    if (r.Field.Box.Focused) return r.Field;
            return null;
        }

        CommandField FieldOf(Step s)
        {
            foreach (BlockView v in views)
                foreach (BlockView.Row r in v.Rows)
                    if (r.Step == s) return r.Field;
            return null;
        }

        BlockView ViewOf(Block b)
        {
            foreach (BlockView v in views) if (v.Block == b) return v;
            return null;
        }

        // Adds an empty block at the end once every field has text in it.
        bool EnsureTrailing()
        {
            foreach (Block b in blocks)
                foreach (Step s in b.Steps)
                    if (s.IsEmpty) return false;
            blocks.Add(Block.Of(""));
            return true;
        }

        // Removes empty commands and empty blocks (except the one being typed in
        // and the blank block at the end). Returns true if anything changed.
        bool Normalize()
        {
            Step keep = FocusedStep();
            bool changed = false;
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                Block b = blocks[i];
                if (b.Steps.Count > 1 && b.Steps.RemoveAll(delegate(Step s) { return s.IsEmpty && s != keep; }) > 0) changed = true;
                bool trailing = i == blocks.Count - 1 && b.Steps.Count == 1 && b.IsEmpty;
                if (!trailing && (b.Steps.Count == 0 || (b.IsEmpty && !b.Steps.Contains(keep))))
                {
                    blocks.RemoveAt(i);
                    changed = true;
                }
            }
            if (EnsureTrailing()) changed = true;
            return changed;
        }

        void Rebuild()
        {
            rebuilding = true;
            CommandField ff = FocusedField();
            Step focus = ff == null ? null : ff.Step;
            int selStart = ff == null ? 0 : ff.Box.SelectionStart;
            int selLen = ff == null ? 0 : ff.Box.SelectionLength;

            blocksPanel.SuspendLayout();
            foreach (BlockView v in views)
            {
                blocksPanel.Controls.Remove(v);
                v.Dispose();
            }
            views.Clear();
            foreach (Block b in blocks)
            {
                BlockView v = new BlockView(this, b, IsTrailing(b));
                views.Add(v);
                blocksPanel.Controls.Add(v);
            }
            blocksPanel.ResumeLayout(false);
            Arrange();

            if (focus != null)
            {
                CommandField f = FieldOf(focus);
                if (f != null)
                {
                    f.Box.Focus();
                    f.Box.Select(selStart, selLen);
                }
            }
            rebuilding = false;
            StepsChanged();
        }

        void Arrange()
        {
            int w = Math.Max(Theme.Px(420), blocksPanel.ClientSize.Width - SidePad * 2);
            int y = SidePad;
            int scroll = blocksPanel.AutoScrollPosition.Y;
            blocksPanel.SuspendLayout();
            foreach (BlockView v in views)
            {
                int h = v.Arrange(w);
                v.SetBounds(SidePad, y + scroll, w, h);
                y += h + BlockGap;
            }
            blocksPanel.AutoScrollMinSize = new Size(0, y + SidePad);
            blocksPanel.ResumeLayout(false);
            blocksPanel.Invalidate();
        }

        public void FieldChanged(Block b, Step s, CommandField f)
        {
            s.Command = f.Box.Text;
            if (s.State == StepState.Done || s.State == StepState.Failed)
                s.State = StepState.Idle;
            BlockView v = ViewOf(b);
            if (v != null) v.UpdateState();
            if (!rebuilding && EnsureTrailing())
            {
                Block nb = blocks[blocks.Count - 1];
                BlockView nv = new BlockView(this, nb, true);
                views.Add(nv);
                blocksPanel.Controls.Add(nv);
                Arrange();
            }
        }

        public void FieldLeft()
        {
            if (rebuilding) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (!rebuilding && !Dragging && Normalize()) Rebuild();
            });
        }

        public void FieldKey(Block b, Step s, CommandField f, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                RunBlock(b);
            }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                List<CommandField> all = new List<CommandField>();
                foreach (BlockView v in views)
                    foreach (BlockView.Row r in v.Rows) all.Add(r.Field);
                int i = all.IndexOf(f) + (e.KeyCode == Keys.Up ? -1 : 1);
                if (i >= 0 && i < all.Count)
                {
                    all[i].Box.Focus();
                    all[i].Box.SelectionStart = all[i].Box.TextLength;
                }
                e.Handled = true;
            }
        }

        // Pasting several lines into one box makes a chain, one command per line.
        public void MultiPaste(Block b, Step s, CommandField f, string[] lines)
        {
            f.Box.SelectedText = lines[0];
            int at = b.Steps.IndexOf(s);
            Step last = s;
            for (int i = 1; i < lines.Length; i++)
            {
                last = new Step(lines[i]);
                b.Steps.Insert(at + i, last);
            }
            Normalize();
            Rebuild();
            CommandField lf = FieldOf(last);
            if (lf != null)
            {
                lf.Box.Focus();
                lf.Box.SelectionStart = lf.Box.TextLength;
            }
        }

        public void RemoveStep(Block b, Step s)
        {
            if (b.Steps.Count > 1) b.Steps.Remove(s);
            else blocks.Remove(b);
            Normalize();
            Rebuild();
        }

        void TakeOut(Block b, Step s)
        {
            if (b.Steps.Count < 2) return;
            b.Steps.Remove(s);
            Block nb = new Block();
            nb.Steps.Add(s);
            blocks.Insert(blocks.IndexOf(b) + 1, nb);
            Normalize();
            Rebuild();
        }

        public ContextMenuStrip StepMenu(Block b, Step s)
        {
            ContextMenuStrip m = Theme.Menu();
            m.Items.Add("Run only this command", null, delegate { RunSingle(s); });
            m.Items.Add("Take it out of the chain", null, delegate { TakeOut(b, s); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Remove", null, delegate { RemoveStep(b, s); });
            return m;
        }

        public void ShowDelayMenu(Step s, Sc2Button chip)
        {
            ContextMenuStrip m = Theme.Menu();
            int[] options = { 0, 1, 2, 3, 5, 10, 15, 30, 60 };
            foreach (int sec in options)
            {
                int value = sec;
                string label = value == 0 ? "AUTO - start the next command as soon as this one finishes"
                                          : "Wait " + value + (value == 1 ? " second" : " seconds") + " after it finishes";
                ToolStripMenuItem item = new ToolStripMenuItem(label);
                item.Checked = s.Delay == value;
                item.Click += delegate
                {
                    s.Delay = value;
                    chip.Text = BlockView.DelayText(s);
                };
                m.Items.Add(item);
            }
            m.Closed += delegate { BeginInvoke((MethodInvoker)delegate { m.Dispose(); }); };
            m.Show(chip, new Point(0, chip.Height));
        }

        // ---------------------------------------------------------------- drag and drop

        public bool Dragging { get { return dragBlock != null; } }

        public void DragBegin(Block b, Step s)
        {
            if (b.IsEmpty) return;
            if (s != null && !b.IsChain) s = null;
            dragBlock = b;
            dragStep = s;
            target = null;
            BlockView v = ViewOf(b);
            if (v != null) v.Dragging = true;
            scrollTimer.Start();
        }

        public void DragMove(Point screen)
        {
            if (!Dragging) return;
            dragScreen = screen;
            UpdateTarget();
        }

        public void DragEnd(Point screen, bool cancel)
        {
            if (!Dragging) return;
            dragScreen = screen;
            if (!cancel) UpdateTarget();
            DropTarget t = cancel ? null : target;
            Block b = dragBlock;
            Step s = dragStep;
            ShowTarget(null);
            scrollTimer.Stop();
            dragBlock = null;
            dragStep = null;
            target = null;
            BeginInvoke((MethodInvoker)delegate { ApplyDrop(b, s, t); });
        }

        void AutoScrollWhileDragging()
        {
            if (!Dragging) return;
            Point p = blocksPanel.PointToClient(Cursor.Position);
            int edge = Theme.Px(40), delta = 0;
            if (p.Y < edge) delta = -Theme.Px(14);
            else if (p.Y > blocksPanel.ClientSize.Height - edge) delta = Theme.Px(14);
            if (delta == 0) return;
            int current = -blocksPanel.AutoScrollPosition.Y;
            blocksPanel.AutoScrollPosition = new Point(0, Math.Max(0, current + delta));
            dragScreen = Cursor.Position;
            UpdateTarget();
        }

        void UpdateTarget()
        {
            target = Validate(Compute(blocksPanel.PointToClient(dragScreen)));
            ShowTarget(target);
        }

        DropTarget Insert(Block before)
        {
            DropTarget t = new DropTarget();
            t.Ref = before;
            return t;
        }

        // Top or bottom edge of a block: insert above/below it. Middle: join its chain.
        DropTarget Compute(Point p)
        {
            int half = BlockGap / 2;
            for (int i = 0; i < views.Count; i++)
            {
                BlockView v = views[i];
                if (p.Y < v.Top - half) return Insert(v.Block);
                if (p.Y <= v.Bottom + half)
                {
                    int rel = p.Y - v.Top;
                    int edge = Math.Min(Theme.Px(14), v.Height / 4);
                    if (rel < edge) return Insert(v.Block);
                    if (rel > v.Height - edge) return Insert(i + 1 < views.Count ? views[i + 1].Block : null);
                    DropTarget t = new DropTarget();
                    t.Join = true;
                    t.Ref = v.Block;
                    t.Pos = v.RowIndexAt(rel);
                    return t;
                }
            }
            return Insert(null);
        }

        // Drops that would not change anything show no indicator.
        DropTarget Validate(DropTarget t)
        {
            if (t == null) return null;
            if (!t.Join && t.Ref == null && blocks.Count > 0 && IsTrailing(blocks[blocks.Count - 1]))
                t.Ref = blocks[blocks.Count - 1];   // keep the blank block last

            if (dragStep == null)
            {
                if (t.Join && t.Ref == dragBlock) return null;
                int i = blocks.IndexOf(dragBlock);
                Block next = i + 1 < blocks.Count ? blocks[i + 1] : null;
                if (!t.Join && (t.Ref == dragBlock || t.Ref == next)) return null;
            }
            else if (t.Join && t.Ref == dragBlock)
            {
                int i = dragBlock.Steps.IndexOf(dragStep);
                if (t.Pos == i || t.Pos == i + 1) return null;
            }
            return t;
        }

        void ShowTarget(DropTarget t)
        {
            foreach (BlockView v in views) v.JoinPos = (t != null && t.Join && v.Block == t.Ref) ? t.Pos : -1;
            int y = -1;
            if (t != null && !t.Join)
            {
                BlockView rv = t.Ref == null ? null : ViewOf(t.Ref);
                if (rv != null) y = rv.Top - BlockGap / 2;
                else if (views.Count > 0) y = views[views.Count - 1].Bottom + BlockGap / 2;
            }
            blocksPanel.InsertY = y;
        }

        void ApplyDrop(Block b, Step s, DropTarget t)
        {
            BlockView source = ViewOf(b);
            if (source != null) source.Dragging = false;
            if (t == null || !blocks.Contains(b)) return;

            if (s == null)
            {
                blocks.Remove(b);
                if (t.Join)
                {
                    t.Ref.Steps.InsertRange(Math.Min(t.Pos, t.Ref.Steps.Count), b.Steps);
                }
                else
                {
                    int at = t.Ref == null ? blocks.Count : blocks.IndexOf(t.Ref);
                    blocks.Insert(at < 0 ? blocks.Count : at, b);
                }
            }
            else
            {
                int i = b.Steps.IndexOf(s);
                if (i < 0) return;
                if (t.Join && t.Ref == b)
                {
                    int pos = t.Pos;
                    b.Steps.RemoveAt(i);
                    if (pos > i) pos--;
                    b.Steps.Insert(Math.Min(pos, b.Steps.Count), s);
                }
                else
                {
                    b.Steps.RemoveAt(i);
                    if (t.Join)
                    {
                        t.Ref.Steps.Insert(Math.Min(t.Pos, t.Ref.Steps.Count), s);
                    }
                    else
                    {
                        Block nb = new Block();
                        nb.Steps.Add(s);
                        int at = t.Ref == null ? blocks.Count : blocks.IndexOf(t.Ref);
                        blocks.Insert(at < 0 ? blocks.Count : at, nb);
                    }
                    if (b.Steps.Count == 0) blocks.Remove(b);
                }
            }
            Normalize();
            Rebuild();
        }
    }
}
