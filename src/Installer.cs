using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("Probably Stolen — Azerbaijani Patch Setup")]
[assembly: AssemblyDescription("Azerbaijani language patch installer for Probably Stolen build 100-REV6")]
[assembly: AssemblyProduct("Probably Stolen Azerbaijani Patch")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AzerbaijaniPatch {
    public sealed class PatchFile {
        public string path { get; set; }
        public string before_sha256 { get; set; }
        public string after_sha256 { get; set; }
        public object[][] operations { get; set; }
    }
    public sealed class Manifest {
        public string game_build { get; set; }
        public PatchFile[] files { get; set; }
    }
    sealed class Plan {
        public PatchFile Entry;
        public string Target, Backup, Staged, Prior;
        public string StartingHash;
        public bool Changed;
    }
    public sealed class PatchEngine {
        public const string Build = "100-REV6";
        public const string BackupName = "_AzerbaijaniPatch_Backup_100-REV6";
        readonly Action<string> log;
        readonly byte[] manifestBytes;
        readonly Manifest manifest;
        public PatchEngine(Action<string> output) {
            log = output ?? delegate { };
            manifestBytes = Resource("manifest.json");
            var serializer = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 256 };
            manifest = serializer.Deserialize<Manifest>(Encoding.UTF8.GetString(manifestBytes));
            if (manifest == null || manifest.game_build != Build || manifest.files == null || manifest.files.Length == 0)
                throw new InvalidDataException("The embedded patch manifest is invalid.");
        }
        static byte[] Resource(string name) {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name + ".gz")) {
                if (resource == null) throw new InvalidDataException("Missing embedded resource: " + name);
                using (var input = new GZipStream(resource, CompressionMode.Decompress))
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
        static string Hash(Stream stream) {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        static string HashFile(string path) {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return Hash(input);
        }
        static string HashText(string value) {
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(value))) return Hash(input);
        }
        static string ExtendedPath(string path) {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }
        static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        static void RequireHash(string file, string expected, string message) {
            if (!File.Exists(file) || !Same(HashFile(file), expected)) throw new InvalidDataException(message);
        }
        static void CheckReparse(string path, string root) {
            string cursor = path;
            while (cursor.Length >= root.Length) {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symbolic links and junctions are not supported inside the selected game folder: " + cursor);
                if (Same(cursor, root)) break;
                cursor = Path.GetDirectoryName(cursor);
                if (String.IsNullOrEmpty(cursor)) break;
            }
        }
        static string SafePath(string root, string relative) {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("Unsafe path in patch manifest.");
            string[] parts = relative.Replace('/', '\\').Split('\\');
            if (parts.Any(x => x == ".." || x == "." || x.Length == 0 || x.EndsWith(".") || x.EndsWith(" ")))
                throw new InvalidDataException("Unsafe path in patch manifest.");
            string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Patch path escapes the selected game folder.");
            CheckReparse(result, root);
            return result;
        }
        static void DurableCopy(string source, string destination) {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                input.CopyTo(output); output.Flush(true);
            }
        }
        static void DurableBytes(byte[] bytes, string destination) {
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)) {
                output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
        }
        static void Replace(string staged, string target) {
            // Both files are on the same volume. File.Replace is atomic on NTFS and preserves the target's ACL.
            // Do not fall back to delete-and-move: a failed replacement must leave the target present.
            File.Replace(staged, target, null, true);
        }
        static void CopyRange(Stream source, Stream output, long offset, long length) {
            if (offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset)
                throw new InvalidDataException("A patch operation is out of bounds.");
            source.Position = offset;
            byte[] buffer = new byte[128 * 1024];
            while (length > 0) {
                int count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (count == 0) throw new EndOfStreamException("Incomplete patch data.");
                output.Write(buffer, 0, count); length -= count;
            }
        }
        void CreatePatched(Plan plan, byte[] payload) {
            if (plan.Entry.operations == null) throw new InvalidDataException("Missing patch operations.");
            using (var original = new FileStream(plan.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var literal = new MemoryStream(payload, false))
            using (var output = new FileStream(plan.Staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                foreach (var operation in plan.Entry.operations) {
                    if (operation == null || operation.Length != 3) throw new InvalidDataException("Invalid patch operation.");
                    string kind = Convert.ToString(operation[0]);
                    long offset = Convert.ToInt64(operation[1]), length = Convert.ToInt64(operation[2]);
                    if (kind != "c" && kind != "a") throw new InvalidDataException("Unknown patch operation.");
                    CopyRange(kind == "c" ? (Stream)original : literal, output, offset, length);
                }
                output.Flush(true);
            }
            RequireHash(plan.Staged, plan.Entry.after_sha256, "Generated patch checksum failed: " + plan.Entry.path);
        }
        public void Run(string action, string gameFolder) {
            if (String.IsNullOrWhiteSpace(gameFolder)) throw new ArgumentException("Select the Probably Stolen game folder.");
            // Extended paths work without changing the machine's LongPathsEnabled policy.
            // Keep all internal game, backup and transaction paths extended, including UNC shares.
            string root = ExtendedPath(gameFolder).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Probably Stolen.exe")))
                throw new DirectoryNotFoundException("Probably Stolen.exe was not found in the selected folder.");
            CheckReparse(root, root);
            if (Process.GetProcessesByName("Probably Stolen").Length != 0)
                throw new IOException("Close Probably Stolen before using this installer.");
            bool restore = action == "restore", verify = action == "verify";
            if (!restore && !verify && action != "install") throw new ArgumentException("Unknown action.");
            string mutexName = "Local\\ProbablyStolenAzerbaijani_" + HashText(root.ToLowerInvariant());
            using (var mutex = new Mutex(false, mutexName)) {
                bool acquired = false;
                try {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("Another installer is working on this game folder.");
                    Execute(action, root, restore, verify);
                } finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        void Execute(string action, string root, bool restore, bool verify) {
            log("Checking Probably Stolen build " + Build + "...");
            string backupRoot = SafePath(root, BackupName);
            var plans = new List<Plan>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Validate every current file and every existing original backup before writing anything.
            foreach (var entry in manifest.files) {
                if (entry == null) throw new InvalidDataException("Invalid manifest entry.");
                var plan = new Plan { Entry = entry, Target = SafePath(root, entry.path), Backup = SafePath(backupRoot, entry.path) };
                if (!paths.Add(plan.Target)) throw new InvalidDataException("Duplicate manifest path.");
                if (!File.Exists(plan.Target)) throw new FileNotFoundException("Required game file is missing: " + entry.path);
                plan.StartingHash = HashFile(plan.Target);
                bool original = Same(plan.StartingHash, entry.before_sha256), installed = Same(plan.StartingHash, entry.after_sha256);
                if (!original && !installed) throw new InvalidDataException("Unsupported build or modified game file: " + entry.path + ". Expected build " + Build + ". No game files changed.");
                if (File.Exists(plan.Backup)) RequireHash(plan.Backup, entry.before_sha256, "Original backup is damaged: " + entry.path);
                if (installed && !File.Exists(plan.Backup)) throw new InvalidDataException("The patch is installed but a verified original backup is missing: " + entry.path + ". Restore is unavailable; restore the original game files first.");
                plan.Changed = restore ? installed : original;
                plans.Add(plan);
                log((original ? "Original: " : "Patched: ") + entry.path);
            }
            if (verify) { log("Verification passed. All " + plans.Count + " files match this patch's supported checksums." + (plans.Any(x => Same(x.StartingHash, x.Entry.after_sha256)) ? " Verified original backups are available." : " The game is unpatched.")); return; }
            if (!plans.Any(x => x.Changed)) { log(restore ? "The original game files are already restored." : "The Azerbaijani patch is already installed. Original backups verified."); return; }
            string transaction = SafePath(root, ".AzerbaijaniPatch_transaction_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(transaction);
            var replaced = new List<Plan>();
            bool rollbackFailed = false;
            try {
                byte[] payload = restore ? null : Resource("payload.bin");
                int index = 0;
                foreach (var plan in plans.Where(x => x.Changed)) {
                    plan.Staged = Path.Combine(transaction, "new_" + index);
                    plan.Prior = Path.Combine(transaction, "prior_" + index++);
                    if (restore) DurableCopy(plan.Backup, plan.Staged); else CreatePatched(plan, payload);
                    RequireHash(plan.Staged, restore ? plan.Entry.before_sha256 : plan.Entry.after_sha256, "Staged file checksum failed: " + plan.Entry.path);
                    DurableCopy(plan.Target, plan.Prior);
                    RequireHash(plan.Prior, plan.StartingHash, "Rollback copy checksum failed: " + plan.Entry.path);
                }
                if (!restore) {
                    Directory.CreateDirectory(backupRoot);
                    foreach (var plan in plans) {
                        if (!File.Exists(plan.Backup)) {
                            string temporary = plan.Backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try { DurableCopy(plan.Target, temporary); RequireHash(temporary, plan.Entry.before_sha256, "Backup checksum failed: " + plan.Entry.path); File.Move(temporary, plan.Backup); }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                        RequireHash(plan.Backup, plan.Entry.before_sha256, "Backup checksum failed: " + plan.Entry.path);
                    }
                    DurableBytes(manifestBytes, SafePath(backupRoot, "manifest.json"));
                }
                // Durable rollback copies and a journal remain available if the process is interrupted.
                DurableBytes(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new {
                    action = action, game_build = Build, started_utc = DateTime.UtcNow.ToString("o"),
                    files = plans.Where(x => x.Changed).Select(x => new { path = x.Entry.path, prior_sha256 = x.StartingHash, prior_file = Path.GetFileName(x.Prior) }).ToArray()
                })), Path.Combine(transaction, "transaction.json"));
                foreach (var plan in plans.Where(x => x.Changed)) {
                    CheckReparse(plan.Target, root);
                    RequireHash(plan.Target, plan.StartingHash, "Game file changed while preparing: " + plan.Entry.path);
                    // Mark before replacement so a post-replacement error also rolls this file back.
                    replaced.Add(plan);
                    Replace(plan.Staged, plan.Target);
                    RequireHash(plan.Target, restore ? plan.Entry.before_sha256 : plan.Entry.after_sha256, "Installed checksum failed: " + plan.Entry.path);
                    log((restore ? "Restored: " : "Installed: ") + plan.Entry.path);
                }
                foreach (var plan in plans) RequireHash(plan.Target, restore ? plan.Entry.before_sha256 : plan.Entry.after_sha256, "Final verification failed: " + plan.Entry.path);
                log(restore ? "Restore complete. Original game files verified. Save files were preserved." : "Installation complete. Select Azərbaycan dili in the game's language menu. Save files were preserved.");
                log("Original backup: " + backupRoot);
            } catch (Exception error) {
                log("Operation failed. Rolling back changed game files...");
                foreach (var plan in Enumerable.Reverse(replaced)) {
                    try {
                        if (File.Exists(plan.Target) && Same(HashFile(plan.Target), plan.StartingHash)) {
                            log("Unchanged or already restored: " + plan.Entry.path);
                            continue;
                        }
                        string recovery = Path.Combine(transaction, "recover_" + Guid.NewGuid().ToString("N"));
                        RequireHash(plan.Prior, plan.StartingHash, "Rollback copy is damaged.");
                        DurableCopy(plan.Prior, recovery); Replace(recovery, plan.Target);
                        RequireHash(plan.Target, plan.StartingHash, "Rollback verification failed.");
                        log("Rolled back: " + plan.Entry.path);
                    } catch (Exception rollbackError) { rollbackFailed = true; log("ROLLBACK FAILED: " + plan.Entry.path + ": " + rollbackError.Message); }
                }
                if (rollbackFailed) throw new IOException(error.Message + "\r\nRecovery files were retained in " + transaction + ". Use the verified original backup to recover the game.", error);
                throw;
            } finally {
                if (!rollbackFailed) { try { Directory.Delete(transaction, true); } catch { log("Temporary files remain: " + transaction); } }
            }
        }
    }
    sealed class SetupForm : Form {
        readonly TextBox folder = new TextBox(), output = new TextBox();
        readonly Button browse = new Button(), install = new Button(), restore = new Button(), verify = new Button();
        readonly Label status = new Label();
        bool working;
        public SetupForm(string initial) {
            Text = "Probably Stolen · Azerbaijani Patch"; ClientSize = new Size(720, 500);
            MinimumSize = new Size(650, 480); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F); BackColor = Color.FromArgb(245, 247, 250);
            var title = new Label { Text = "Probably Stolen", Font = new Font("Segoe UI", 23F, FontStyle.Bold), AutoSize = true, Location = new Point(25, 21) };
            var subtitle = new Label { Text = "Azərbaycan dili  /  Azerbaijani language patch  ·  v1.0.0", AutoSize = true, ForeColor = Color.FromArgb(55, 85, 112), Location = new Point(28, 72) };
            var note = new Label { Text = "For game build 100-REV6. Close the game, then select its installation folder.\r\nOriginal files are backed up automatically. Your saves are preserved.", AutoSize = true, Location = new Point(28, 108) };
            folder.SetBounds(28, 166, 564, 27); folder.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; folder.Text = initial;
            browse.Text = "Browse…"; browse.SetBounds(602, 164, 90, 29); browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            browse.Click += delegate { using (var picker = new FolderBrowserDialog { Description = "Select the folder containing Probably Stolen.exe", SelectedPath = folder.Text }) if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath; };
            install.Text = "Install patch"; install.SetBounds(28, 211, 150, 37); install.BackColor = Color.FromArgb(28, 103, 155); install.ForeColor = Color.White; install.FlatStyle = FlatStyle.Flat;
            restore.Text = "Restore original"; restore.SetBounds(190, 211, 150, 37);
            verify.Text = "Verify files"; verify.SetBounds(352, 211, 120, 37);
            install.Click += delegate { Start("install"); }; restore.Click += delegate { Start("restore"); }; verify.Click += delegate { Start("verify"); };
            status.SetBounds(28, 267, 664, 23); status.Text = "Ready. Select your game folder to begin."; status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            output.SetBounds(28, 297, 664, 176); output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; output.Multiline = true; output.ReadOnly = true; output.ScrollBars = ScrollBars.Vertical; output.BackColor = Color.White; output.Font = new Font("Consolas", 8.5F);
            Controls.AddRange(new Control[] { title, subtitle, note, folder, browse, install, restore, verify, status, output });
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (working) e.Cancel = true; };
        }
        void Start(string action) {
            string selected = folder.Text; working = true;
            foreach (Control control in new Control[] { folder, browse, install, restore, verify }) control.Enabled = false;
            output.Clear(); status.Text = "Working… Please keep this window open.";
            var worker = new BackgroundWorker();
            worker.DoWork += delegate { new PatchEngine(delegate(string line) { BeginInvoke((Action)delegate { output.AppendText(line + Environment.NewLine); }); }).Run(action, selected); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e) {
                working = false; foreach (Control control in new Control[] { folder, browse, install, restore, verify }) control.Enabled = true;
                status.Text = e.Error == null ? "Completed successfully." : "Could not complete the operation.";
                if (e.Error != null) { output.AppendText("ERROR: " + e.Error.Message + Environment.NewLine); MessageBox.Show(this, e.Error.Message, "Patch setup", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }
    }
    static class Program {
        static string DetectGame() {
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Probably Stolen");
            var options = new List<string> { desktop };
            foreach (string baseFolder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
                if (!String.IsNullOrEmpty(baseFolder)) options.Add(Path.Combine(baseFolder, "Steam", "steamapps", "common", "Probably Stolen"));
            return options.FirstOrDefault(x => File.Exists(Path.Combine(x, "Probably Stolen.exe"))) ?? desktop;
        }
        [STAThread]
        static int Main(string[] args) {
            // .NET Framework 4.8: opt in before the first System.IO.Path operation.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            string action = null, game = DetectGame(), logfile = null;
            bool quiet = args.Any(x => String.Equals(x, "/quiet", StringComparison.OrdinalIgnoreCase));
            try {
                for (int i = 0; i < args.Length; i++) {
                    string arg = args[i].ToLowerInvariant();
                    if (arg == "/install" || arg == "/restore" || arg == "/verify") { if (action != null) throw new ArgumentException("Choose only one operation."); action = arg.Substring(1); }
                    else if (arg == "/quiet") quiet = true;
                    else if (arg == "/game" || arg == "/log") { if (++i >= args.Length) throw new ArgumentException("Missing value for " + arg); if (arg == "/game") game = args[i]; else logfile = args[i]; }
                    else throw new ArgumentException("Unknown argument: " + args[i]);
                }
                if (action == null) {
                    if (quiet) throw new ArgumentException("/quiet requires /install, /restore or /verify.");
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new SetupForm(game)); return 0;
                }
                using (var log = logfile == null ? null : new StreamWriter(Path.GetFullPath(logfile), false, new UTF8Encoding(false))) {
                    Action<string> write = delegate(string line) { if (log != null) { log.WriteLine(line); log.Flush(); } if (!quiet) Console.WriteLine(line); };
                    try { new PatchEngine(write).Run(action, game); return 0; }
                    catch (Exception e) { write("ERROR: " + e.Message); return 1; }
                }
            } catch (Exception e) { if (!quiet && args.Length == 0) MessageBox.Show(e.Message, "Patch setup", MessageBoxButtons.OK, MessageBoxIcon.Error); else if (!quiet) Console.Error.WriteLine(e.Message); return 2; }
        }
    }
}
