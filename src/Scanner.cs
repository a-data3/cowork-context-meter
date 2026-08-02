// Scanner.cs - pure logic for Cowork Context Meter. No UI code here.
// Compiled with .NET Framework 4 csc.exe -- C# 5 syntax only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CoworkContextMeter
{
    /// <summary>Everything the UI needs to know about one session transcript.</summary>
    public class SessionInfo
    {
        public string SessionId { get; set; }
        public string FilePath { get; set; }
        public string Title { get; set; }
        public string ProjectName { get; set; }
        public string Cwd { get; set; }
        public string Model { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public long InputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalContextTokens { get; set; }
        public bool HasUsage { get; set; }
        public bool IsLive { get; set; }
        public long FileSizeBytes { get; set; }
        /// <summary>"Code" (Claude Code, ~/.claude/projects) or "Cowork" (desktop app local agent mode).</summary>
        public string Source { get; set; }
        /// <summary>Exact window size when known (1,000,000 for a "[1m]" model in the Cowork state file); 0 = unknown.</summary>
        public long WindowOverride { get; set; }
        public bool IsArchived { get; set; }
    }

    /// <summary>One point in the context-growth history of a session.</summary>
    public class GrowthPoint
    {
        public DateTime TimestampUtc { get; set; }
        public long Total { get; set; }
    }

    /// <summary>
    /// Serializable snapshot of one Cowork-root row, persisted to a small local
    /// cache file so a fresh launch can show the last known sessions when the
    /// live folders are briefly unreadable (just after a Desktop update / PC
    /// restart). Times are stored as UTC ticks to avoid time-zone drift.
    /// </summary>
    public class CachedRow
    {
        public string Root { get; set; }
        public string SessionId { get; set; }
        public string FilePath { get; set; }
        public string Title { get; set; }
        public string ProjectName { get; set; }
        public string Cwd { get; set; }
        public string Model { get; set; }
        public long LastActivityTicks { get; set; }
        public long InputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalContextTokens { get; set; }
        public bool HasUsage { get; set; }
        public long FileSizeBytes { get; set; }
        public string Source { get; set; }
        public long WindowOverride { get; set; }
        public bool IsArchived { get; set; }
    }

    public class Scanner
    {
        private class CacheEntry
        {
            public DateTime LastWriteTimeUtc;
            public long Length;
            public SessionInfo Info;
            public string FallbackTitle;
        }

        // path -> (lastWriteTimeUtc + length -> parsed info), so auto-refresh
        // only re-parses files that actually changed.
        private readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        // history.jsonl titles cache, keyed by the file's (mtime, length) so
        // the (unbounded, append-only) file is only re-parsed when it changed.
        private Dictionary<string, string> _historyCache;
        private DateTime _historyWriteUtc;
        private long _historyLength;

        /// <summary>Parsed summary of one Cowork state file (local_*.json).</summary>
        private class CoworkState
        {
            public DateTime LastWriteTimeUtc;
            public long Length;
            public bool Valid;            // has a cliSessionId -> is a real session state
            public string CoworkSessionId; // "local_<id>"
            public string CliSessionId;    // transcript uuid
            public string Title;
            public string InitialMessage;
            public string Model;           // verbatim, may carry the "[1m]" suffix
            public bool IsArchived;
            public DateTime LastActivityUtc;
            public string ProjectFolder;   // NEW: cwd; OLD: userSelectedFolders[0]
        }

        // state-file path -> parsed summary, keyed by (mtime, length): the
        // ~94 x ~200 KB state files must not be re-parsed every 5-s tick.
        private readonly Dictionary<string, CoworkState> _coworkCache =
            new Dictionary<string, CoworkState>(StringComparer.OrdinalIgnoreCase);

        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public virtual string ClaudeDir
        {
            get
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(profile, ".claude");
            }
        }

        public string ProjectsDir
        {
            get { return Path.Combine(ClaudeDir, "projects"); }
        }

        /// <summary>%APPDATA%\Claude\local-agent-mode-sessions (OLD Cowork session storage; sandboxed sessions).</summary>
        public virtual string CoworkSessionsDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "Claude", "local-agent-mode-sessions");
            }
        }

        /// <summary>%APPDATA%\Claude\claude-code-sessions (NEW Cowork session storage; no sandbox, transcripts in shared projects).</summary>
        public virtual string CoworkSessionsDirNew
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, "Claude", "claude-code-sessions");
            }
        }

        /// <summary>
        /// Claude Desktop ships as an MSIX (packaged) app, so it stores its data
        /// INSIDE its package container rather than in the real %APPDATA%:
        ///   %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\&lt;leaf&gt;
        /// A process running OUTSIDE that container -- which is the normal case
        /// for this app when the user launches it from Explorer -- sees an empty
        /// real %APPDATA% and would find ZERO Cowork sessions. So we must scan
        /// the package location as well. (Wildcarded: the package family name
        /// changes when Claude Desktop is reinstalled/updated.)
        /// </summary>
        /// <summary>
        /// Context window for a Cowork session, derived from the model string
        /// in its state file. Returns 0 when unknown (the UI's Auto mode then
        /// decides).
        ///
        /// Why this is not simply "[1m] -> 1M, else 200k": the Claude Code CLI
        /// carries a per-model window table keyed by SURFACE, and the Cowork
        /// surfaces ("local-agent" / "remote_cowork") get a SMALLER window
        /// than everywhere else:
        ///     claude-sonnet-5: local-agent / remote_cowork -> 500,000
        ///                      default (other surfaces)    -> 967,000
        /// Assuming 200k for a Cowork sonnet-5 session makes the meter read
        /// over 100% the moment it passes 200,000 tokens -- exactly when the
        /// number starts to matter.
        ///
        /// Auto-compaction then fires at (window - 33,000): a 20,000 output
        /// reserve plus 13,000 headroom. That rule is verified three ways --
        /// the CLI bundle, live client telemetry (1,000,000 -> 967,000 and
        /// 967,000 -> 934,000), and 9 measured compaction events on a 200k
        /// model (167,287-171,567). The 500,000 Cowork figure itself comes
        /// from the bundle only, so treat it as best-known, not certain.
        /// </summary>
        private static long CoworkWindowFor(string model)
        {
            if (string.IsNullOrEmpty(model)) return 0L;
            if (model.IndexOf("[1m]", StringComparison.Ordinal) >= 0) return 1000000L;
            if (model.IndexOf("sonnet-5", StringComparison.OrdinalIgnoreCase) >= 0) return 500000L;
            return 0L;   // unknown -> let the UI's Auto mode decide
        }

        /// <summary>~/.claude/jobs -- one folder per daemon-run Cowork job.</summary>
        public virtual string JobsDir
        {
            get { return Path.Combine(ClaudeDir, "jobs"); }
        }

        /// <summary>
        /// Current Cowork sessions are run by the Claude daemon as "jobs": each
        /// gets ~/.claude/jobs/&lt;short&gt;/state.json holding its real name and the
        /// sessionId of its transcript (which lives in the normal
        /// ~/.claude/projects tree). They have NO state file under
        /// claude-code-sessions, so without this they'd be mistaken for ordinary
        /// command-line "Code" sessions and titled with their first prompt.
        /// Returns sessionId -> job name (name may be empty).
        /// </summary>
        public Dictionary<string, string> LoadJobSessions()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string root = JobsDir;
                if (!Directory.Exists(Lp(root))) return map;
                string[] dirs;
                try { dirs = Directory.GetDirectories(Lp(root)); }
                catch { return map; }

                JavaScriptSerializer ser = NewSerializer();
                foreach (string dRaw in dirs)
                {
                    try
                    {
                        string state = Path.Combine(StripLp(dRaw), "state.json");
                        if (!File.Exists(Lp(state))) continue;
                        string text;
                        using (FileStream fs = OpenShared(state))
                        using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                            text = r.ReadToEnd();
                        Dictionary<string, object> obj =
                            ser.DeserializeObject(text) as Dictionary<string, object>;
                        if (obj == null) continue;
                        string sid = GetString(obj, "sessionId");
                        if (string.IsNullOrEmpty(sid)) continue;
                        map[sid] = TrimTitle(GetString(obj, "name"));
                    }
                    catch { }
                }
                if (map.Count > 0) ScanLog("jobs: found " + map.Count + " daemon Cowork job(s)");
            }
            catch (Exception ex) { ScanLog("jobs: LOAD FAILED " + ex.GetType().Name + ": " + ex.Message); }
            return map;
        }

        public List<string> PackageCoworkRoots(string leaf)
        {
            List<string> found = new List<string>();
            try
            {
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string pkgs = Path.Combine(lad, "Packages");
                if (!Directory.Exists(pkgs)) return found;
                string[] dirs;
                try { dirs = Directory.GetDirectories(pkgs, "Claude_*"); }
                catch { return found; }
                foreach (string dir in dirs)
                {
                    string p = Path.Combine(dir,
                        Path.Combine("LocalCache", Path.Combine("Roaming",
                        Path.Combine("Claude", leaf))));
                    try { if (Directory.Exists(Lp(p))) found.Add(p); }
                    catch { }
                }
            }
            catch { }
            return found;
        }

        // Live session ids from the MAIN ~/.claude/sessions, computed once per
        // scan. New-style Cowork sessions are no longer sandboxed, so their
        // live state lives here (keyed by cliSessionId), not in a sandbox
        // .claude/sessions dir.
        private HashSet<string> _mainLiveIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Last good per-root Cowork rows. If a root is transiently unreadable
        // (e.g. its folder is locked while Claude Desktop resets), reuse these
        // instead of dropping every Cowork session from the list.
        private readonly Dictionary<string, List<SessionInfo>> _lastCoworkRows =
            new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True if the most recent scan reused stale Cowork rows because a root was busy.</summary>
        public bool StaleCoworkShown;

        // On-disk last-good cache so a FRESH process (e.g. opened right after a
        // Desktop update or PC restart, while the folders are still locked) can
        // fall back to the last known Cowork sessions instead of showing none.
        private bool _diskLoaded;
        private string _lastSavedSig;

        protected virtual string CoworkCachePath
        {
            get
            {
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(lad, Path.Combine("CoworkContextMeter", "cowork-cache.json"));
            }
        }

        // Diagnostic log shared with the UI (%LOCALAPPDATA%\CoworkContextMeter\
        // ui-debug.log). Silent-catch blocks below used to hide WHY Cowork rows
        // disappeared; now they say so. Never throws.
        internal static void ScanLog(string msg)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CoworkContextMeter");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ui-debug.log");
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 262144) File.Delete(path);
                }
                catch { }
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  [scanner] " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Seed _lastCoworkRows from the on-disk cache once per Scanner.</summary>
        private void LoadCoworkCache()
        {
            // Retry the load while we still have NOTHING cached: a single failed
            // (or too-early) first read must not permanently disable the
            // fallback for the whole life of the process.
            if (_diskLoaded && _lastCoworkRows.Count > 0) return;
            _diskLoaded = true;
            try
            {
                string path = CoworkCachePath;
                if (!File.Exists(path)) { ScanLog("cache MISSING at " + path); return; }
                string text;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                {
                    text = r.ReadToEnd();
                }
                JavaScriptSerializer ser = NewSerializer();
                List<CachedRow> cached = ser.Deserialize<List<CachedRow>>(text);
                if (cached == null) return;

                Dictionary<string, List<SessionInfo>> byRoot =
                    new Dictionary<string, List<SessionInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (CachedRow c in cached)
                {
                    if (c == null || string.IsNullOrEmpty(c.Root)) continue;
                    List<SessionInfo> list;
                    if (!byRoot.TryGetValue(c.Root, out list))
                    {
                        list = new List<SessionInfo>();
                        byRoot[c.Root] = list;
                    }
                    list.Add(FromCached(c));
                }
                int seeded = 0;
                foreach (KeyValuePair<string, List<SessionInfo>> kv in byRoot)
                {
                    if (kv.Value.Count > 0 && !_lastCoworkRows.ContainsKey(kv.Key))
                    {
                        _lastCoworkRows[kv.Key] = kv.Value;
                        seeded += kv.Value.Count;
                    }
                }
                ScanLog("cache loaded rows=" + cached.Count + " seeded=" + seeded
                    + " roots=" + byRoot.Count);
            }
            catch (Exception ex)
            {
                ScanLog("cache LOAD FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Persist the current per-root last-good rows; debounced by a cheap signature.</summary>
        private void SaveCoworkCache()
        {
            try
            {
                List<CachedRow> rows = new List<CachedRow>();
                List<string> sigParts = new List<string>();
                foreach (KeyValuePair<string, List<SessionInfo>> kv in _lastCoworkRows)
                {
                    if (kv.Value == null) continue;
                    foreach (SessionInfo s in kv.Value)
                    {
                        rows.Add(ToCached(kv.Key, s));
                        sigParts.Add(kv.Key + "#" + (s.SessionId ?? ""));
                    }
                }
                sigParts.Sort(StringComparer.Ordinal);
                string sig = rows.Count + "|" + string.Join(",", sigParts.ToArray());
                if (sig == _lastSavedSig) return; // unchanged set -> skip write

                JavaScriptSerializer ser = NewSerializer();
                string text = ser.Serialize(rows);
                string path = CoworkCachePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                string tmp = path + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(text);
                }
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
                File.Move(tmp, path);
                _lastSavedSig = sig;
            }
            catch { }
        }

        private static SessionInfo FromCached(CachedRow c)
        {
            SessionInfo s = new SessionInfo();
            s.SessionId = c.SessionId;
            s.FilePath = c.FilePath;
            s.Title = c.Title;
            s.ProjectName = c.ProjectName;
            s.Cwd = c.Cwd;
            s.Model = c.Model;
            long t = c.LastActivityTicks;
            s.LastActivityUtc = (t >= DateTime.MinValue.Ticks && t <= DateTime.MaxValue.Ticks)
                ? new DateTime(t, DateTimeKind.Utc) : DateTime.MinValue;
            s.InputTokens = c.InputTokens;
            s.CacheReadTokens = c.CacheReadTokens;
            s.CacheCreationTokens = c.CacheCreationTokens;
            s.OutputTokens = c.OutputTokens;
            s.TotalContextTokens = c.TotalContextTokens;
            s.HasUsage = c.HasUsage;
            s.IsLive = false; // never claim "live" from cached data
            s.FileSizeBytes = c.FileSizeBytes;
            s.Source = c.Source;
            s.WindowOverride = c.WindowOverride;
            s.IsArchived = c.IsArchived;
            return s;
        }

        private static CachedRow ToCached(string root, SessionInfo s)
        {
            CachedRow c = new CachedRow();
            c.Root = root;
            c.SessionId = s.SessionId;
            c.FilePath = s.FilePath;
            c.Title = s.Title;
            c.ProjectName = s.ProjectName;
            c.Cwd = s.Cwd;
            c.Model = s.Model;
            c.LastActivityTicks = s.LastActivityUtc.Ticks;
            c.InputTokens = s.InputTokens;
            c.CacheReadTokens = s.CacheReadTokens;
            c.CacheCreationTokens = s.CacheCreationTokens;
            c.OutputTokens = s.OutputTokens;
            c.TotalContextTokens = s.TotalContextTokens;
            c.HasUsage = s.HasUsage;
            c.FileSizeBytes = s.FileSizeBytes;
            c.Source = s.Source;
            c.WindowOverride = s.WindowOverride;
            c.IsArchived = s.IsArchived;
            return c;
        }

        /// <summary>
        /// Enumerate %USERPROFILE%\.claude\projects\*\*.jsonl and return one
        /// SessionInfo per transcript. Never throws for a single bad file.
        /// </summary>
        public List<SessionInfo> Scan()
        {
            List<SessionInfo> results = new List<SessionInfo>();
            StaleCoworkShown = false;
            LoadCoworkCache();
            _mainLiveIds = LoadLiveSessionIds();

            // Map cliSessionId -> shared-projects transcript path, built once
            // from the Code enumeration. New-style Cowork state files (which
            // no longer sandbox) resolve their transcript through this map.
            Dictionary<string, string> sharedTranscripts = BuildSharedTranscriptMap();

            // Cowork pass runs FIRST so the set of cliSessionIds it claims is
            // known before Code rows are added. A new-style Cowork transcript
            // also lives in shared projects, so the Code pass must skip any id
            // a Cowork state file already claimed -- otherwise the session
            // would appear twice (one Code row + one Cowork row).
            HashSet<string> coworkClaimed =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanCowork(results, sharedTranscripts, coworkClaimed);
            ScanCode(results, coworkClaimed);
            return results;
        }

        /// <summary>
        /// Like Scan(), but if a Cowork root exists yet no "Cowork" rows came
        /// back (the signature of a folder briefly locked just after a PC
        /// restart or a Claude Desktop launch), retry up to maxTries times,
        /// sleeping sleepMs between attempts, before settling. Used for the
        /// FIRST scan at startup; auto-refresh keeps using plain Scan().
        /// </summary>
        public List<SessionInfo> ScanResilient(int maxTries, int sleepMs)
        {
            List<SessionInfo> list = Scan();
            int tries = 1;
            while (tries < maxTries && CoworkRootPresentButEmpty(list))
            {
                System.Threading.Thread.Sleep(sleepMs);
                list = Scan();
                tries++;
            }
            return list;
        }

        /// <summary>
        /// True if the legacy Cowork root directory exists but the scan produced
        /// no "Cowork"-source rows -- the signature of a transient folder lock,
        /// not a genuinely Cowork-free machine (where the root would be absent).
        /// </summary>
        private bool CoworkRootPresentButEmpty(List<SessionInfo> list)
        {
            bool oldExists = false;
            try { oldExists = Directory.Exists(Lp(CoworkSessionsDir)); }
            catch { }
            if (!oldExists) return false;
            if (list == null) return true;
            foreach (SessionInfo s in list)
            {
                if (s.Source == "Cowork") return false;
            }
            return true;
        }

        /// <summary>
        /// Enumerate every transcript under %USERPROFILE%\.claude\projects and
        /// map its cliSessionId (= file name without ".jsonl") to its path.
        /// Used to resolve new-style Cowork sessions, whose transcripts live in
        /// this shared location rather than inside a sandbox home.
        /// </summary>
        private Dictionary<string, string> BuildSharedTranscriptMap()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string projectsDir = ProjectsDir;
            if (!Directory.Exists(projectsDir))
                return map;

            string[] dirs;
            try { dirs = Directory.GetDirectories(projectsDir); }
            catch { return map; }

            foreach (string dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); }
                catch { continue; }

                foreach (string file in files)
                {
                    try
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                            map[id] = file;
                    }
                    catch { }
                }
            }
            return map;
        }

        private void ScanCode(List<SessionInfo> results, HashSet<string> coworkClaimed)
        {
            Dictionary<string, string> historyTitles = LoadHistoryTitles();
            HashSet<string> liveIds = _mainLiveIds;
            // Daemon-run Cowork jobs: their transcripts sit in the normal
            // projects tree, so they arrive here looking like plain Code rows.
            Dictionary<string, string> jobs = LoadJobSessions();

            string projectsDir = ProjectsDir;
            if (!Directory.Exists(projectsDir))
                return;

            string[] dirs;
            try { dirs = Directory.GetDirectories(projectsDir); }
            catch { return; }

            foreach (string dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.jsonl"); }
                catch { continue; }

                foreach (string file in files)
                {
                    try
                    {
                        // DEDUP: a transcript already claimed by a Cowork state
                        // file is shown as a Cowork row, never also as Code.
                        string idDedup = Path.GetFileNameWithoutExtension(file);
                        if (idDedup != null && coworkClaimed.Contains(idDedup)) continue;

                        CacheEntry entry = GetOrParse(file);
                        if (entry == null || entry.Info == null) continue;
                        // Publish a per-scan copy: on a cache hit the cached
                        // instance is still referenced by the UI thread from
                        // the previous scan (_all list and row Tags), so it
                        // must never be mutated here on the worker thread.
                        SessionInfo info = CloneInfo(entry.Info);

                        // Title: earliest history.jsonl display wins, then the
                        // transcript's first user prompt, then the session id.
                        string title = null;
                        if (info.SessionId != null)
                            historyTitles.TryGetValue(info.SessionId, out title);
                        if (string.IsNullOrEmpty(title)) title = entry.FallbackTitle;
                        if (string.IsNullOrEmpty(title)) title = info.SessionId;
                        info.Title = title;

                        // A transcript owned by a daemon job IS a Cowork session:
                        // relabel it and prefer the job's real name over the
                        // first-prompt fallback title.
                        string jobName;
                        if (info.SessionId != null && jobs.TryGetValue(info.SessionId, out jobName))
                        {
                            info.Source = "Cowork";
                            if (!string.IsNullOrEmpty(jobName)) info.Title = jobName;
                        }

                        info.IsLive = info.SessionId != null && liveIds.Contains(info.SessionId);
                        results.Add(info);
                    }
                    catch
                    {
                        // one corrupt file must never kill the scan
                    }
                }
            }
        }

        // ----- Cowork (desktop app local agent mode) ------------------------

        /// <summary>
        /// Scan both Cowork state-file roots for local_&lt;id&gt;.json files under
        /// &lt;workspace&gt;\&lt;container&gt;\. Two storage layouts coexist:
        ///   OLD (local-agent-mode-sessions): sandboxed; the transcript lives
        ///        inside the sandbox home next to the state file, behind a
        ///        270-460 char path that needs \\?\ long-path routing.
        ///   NEW (claude-code-sessions): no sandbox; the transcript lives in
        ///        the shared %USERPROFILE%\.claude\projects folder and is
        ///        resolved through <paramref name="sharedTranscripts"/> by
        ///        cliSessionId.
        /// Every cliSessionId turned into a row is added to
        /// <paramref name="claimed"/> so the Code pass can de-dup against it.
        /// </summary>
        private void ScanCowork(List<SessionInfo> results,
            Dictionary<string, string> sharedTranscripts, HashSet<string> claimed)
        {
            // Label by which storage the session's state file lives in:
            //   local-agent-mode-sessions = Cowork (local agent mode) sessions.
            //       These carry legacy-only fields (sessionType, hostLoopMode,
            //       vmProcessName) and are sandboxed.
            //   claude-code-sessions      = Claude CODE sessions run from the
            //       desktop app (this is literally what the folder holds; every
            //       file in it is structurally identical, so there is NO field
            //       that separates a "Cowork" one from a "Code" one here).
            // NOTE: do NOT blanket-label everything with a desktop state file as
            // Cowork -- that wrongly tags ordinary Claude Code sessions (v1.8 bug).
            ScanCoworkRoot(CoworkSessionsDir, "Cowork", results, sharedTranscripts, claimed);
            ScanCoworkRoot(CoworkSessionsDirNew, "Code", results, sharedTranscripts, claimed);

            // Claude Desktop is an MSIX app: its real data lives in the package
            // container, which the plain %APPDATA% paths above CANNOT see when
            // this app runs outside that container (the normal case).
            foreach (string r in PackageCoworkRoots("local-agent-mode-sessions"))
                ScanCoworkRoot(r, "Cowork", results, sharedTranscripts, claimed);
            foreach (string r in PackageCoworkRoots("claude-code-sessions"))
                ScanCoworkRoot(r, "Code", results, sharedTranscripts, claimed);
        }

        /// <summary>
        /// Walk one root's tree, labeling rows <paramref name="source"/>. If the
        /// enumeration fails transiently (folder busy/locked, e.g. during a
        /// Claude Desktop reset), reuse the previous good scan's rows for this
        /// root rather than dropping every Cowork session from the list.
        /// </summary>
        private void ScanCoworkRoot(string root, string source, List<SessionInfo> results,
            Dictionary<string, string> sharedTranscripts, HashSet<string> claimed)
        {
            List<SessionInfo> rows = new List<SessionInfo>();
            bool ok = TryScanCoworkRoot(root, source, sharedTranscripts, rows);
            int built = rows.Count; // capture before `rows` may be swapped for the cache

            List<SessionInfo> prev;
            bool havePrev = _lastCoworkRows.TryGetValue(root, out prev)
                && prev != null && prev.Count > 0;

            if (ok && rows.Count > 0)
            {
                _lastCoworkRows[root] = rows;
                SaveCoworkCache();           // remember across restarts
            }
            else if (ok && havePrev)
            {
                // A clean enumeration that suddenly yields nothing, while we had
                // rows moments ago, is almost always a folder still settling
                // (e.g. just after a Desktop update) -- keep the last known set.
                rows = prev;
                StaleCoworkShown = true;
            }
            else if (ok)
            {
                // Genuinely empty and nothing known before. Do NOT store the empty
                // list: that would make havePrev false forever and permanently
                // poison the fallback for this process. Leave the key absent so a
                // later cache load or a good scan can still populate it.
            }
            else if (havePrev)
            {
                rows = prev;                 // enumeration failed -> stale-beats-empty
                StaleCoworkShown = true;
            }
            else
            {
                rows = new List<SessionInfo>(); // failed and nothing known -> honest empty
            }

            // Only log when something is off, so the normal 5 s cadence stays quiet.
            if (!ok || rows.Count == 0 || StaleCoworkShown)
            {
                bool exists = false;
                try { exists = Directory.Exists(Lp(root)); } catch { }
                ScanLog("root=" + LeafName(root) + " exists=" + exists + " enumOk=" + ok
                    + " built=" + built + " prevCached=" + (havePrev ? prev.Count : 0)
                    + " used=" + rows.Count + " stale=" + StaleCoworkShown);
            }

            foreach (SessionInfo info in rows)
            {
                // The same session can surface from two roots (e.g. the plain
                // %APPDATA% path and the MSIX package path resolve to the same
                // data when running inside the container) -- never list it twice.
                if (!string.IsNullOrEmpty(info.SessionId) && claimed.Contains(info.SessionId))
                    continue;
                results.Add(info);
                if (!string.IsNullOrEmpty(info.SessionId))
                    claimed.Add(info.SessionId);
            }
        }

        /// <summary>
        /// Enumerate one root into <paramref name="rows"/>. Returns false if any
        /// directory enumeration failed (root missing or threw) so the caller
        /// can fall back to the last good rows. A single corrupt state file is
        /// skipped and never fails the whole root.
        /// </summary>
        private bool TryScanCoworkRoot(string root, string source,
            Dictionary<string, string> sharedTranscripts, List<SessionInfo> rows)
        {
            try { if (!Directory.Exists(Lp(root))) { ScanLog("enum: root not found " + root); return false; } }
            catch (Exception ex) { ScanLog("enum: Exists threw " + ex.GetType().Name + ": " + ex.Message); return false; }

            string[] wsDirs;
            try { wsDirs = Directory.GetDirectories(Lp(root)); }
            catch (Exception ex) { ScanLog("enum: workspaces threw " + ex.GetType().Name + ": " + ex.Message); return false; }

            foreach (string wsRaw in wsDirs)
            {
                string ws = StripLp(wsRaw);
                string[] containers;
                try { containers = Directory.GetDirectories(Lp(ws)); }
                catch (Exception ex) { ScanLog("enum: containers threw " + ex.GetType().Name + ": " + ex.Message); return false; }

                foreach (string contRaw in containers)
                {
                    string cont = StripLp(contRaw);
                    string[] stateFiles;
                    try { stateFiles = Directory.GetFiles(Lp(cont), "local_*.json"); }
                    catch (Exception ex) { ScanLog("enum: stateFiles threw " + ex.GetType().Name + ": " + ex.Message); return false; }

                    foreach (string sfRaw in stateFiles)
                    {
                        try
                        {
                            SessionInfo info =
                                BuildCoworkSession(StripLp(sfRaw), source, sharedTranscripts);
                            if (info != null) rows.Add(info);
                        }
                        catch
                        {
                            // one bad session must never kill the scan
                        }
                    }
                }
            }
            return true;
        }

        /// <summary>One SessionInfo per valid state file, labeled <paramref name="source"/>; transcript-less sessions still get a row.</summary>
        private SessionInfo BuildCoworkSession(string stateFile, string source,
            Dictionary<string, string> sharedTranscripts)
        {
            CoworkState st = GetOrParseCoworkState(stateFile);
            if (st == null || !st.Valid) return null;

            // sandbox home dir sits next to the state file: local_<id>.json -> local_<id>\
            string sandboxDir = stateFile.Substring(0, stateFile.Length - 5);

            SessionInfo info = null;
            string fallbackTitle = null;
            // OLD layout: transcript inside the sandbox home (when it exists).
            // NEW layout: no sandbox -> resolve via the shared-projects map by
            // cliSessionId. Try the sandbox first for back-compat, then fall
            // back to the shared map.
            string transcript = FindCoworkTranscript(sandboxDir, st.CliSessionId);
            if (transcript == null && sharedTranscripts != null &&
                !string.IsNullOrEmpty(st.CliSessionId))
            {
                string shared;
                if (sharedTranscripts.TryGetValue(st.CliSessionId, out shared))
                    transcript = shared;
            }
            if (transcript != null)
            {
                CacheEntry entry = GetOrParse(transcript);
                if (entry != null && entry.Info != null)
                {
                    info = CloneInfo(entry.Info);
                    fallbackTitle = entry.FallbackTitle;
                }
            }
            if (info == null)
            {
                // transcript missing or transiently unreadable: row from state only
                info = new SessionInfo();
                info.SessionId = st.CliSessionId;
                info.FilePath = stateFile;
                info.HasUsage = false;
                info.Model = "";
                info.LastActivityUtc = st.LastActivityUtc;
            }

            info.Source = source;
            info.IsArchived = st.IsArchived;

            // the state file records the [1m] 1M-window marker that transcripts lack
            if (!string.IsNullOrEmpty(st.Model)) info.Model = st.Model; // verbatim, keep "[1m]"
            info.WindowOverride = CoworkWindowFor(info.Model);

            string title = TrimTitle(st.Title);
            if (string.IsNullOrEmpty(title)) title = TrimTitle(st.InitialMessage);
            if (string.IsNullOrEmpty(title)) title = fallbackTitle;
            if (string.IsNullOrEmpty(title)) title = info.SessionId;
            info.Title = title;

            string leaf = LeafName(st.ProjectFolder);
            if (!string.IsNullOrEmpty(leaf))
                info.ProjectName = leaf;
            else if (IsSandboxPath(info.Cwd))
                // the transcript-derived project name came from a sandbox cwd
                // (e.g. "outputs") -> not a real project, so blank it.
                info.ProjectName = "";
            if (!string.IsNullOrEmpty(st.ProjectFolder)) info.Cwd = st.ProjectFolder;

            if (st.LastActivityUtc > info.LastActivityUtc)
                info.LastActivityUtc = st.LastActivityUtc;

            // each sandbox home has its own .claude\sessions\<pid>.json dir
            HashSet<string> live = LoadLiveSessionIdsFrom(
                Path.Combine(sandboxDir, Path.Combine(".claude", "sessions")));
            // OLD-style sessions are live via their sandbox .claude/sessions;
            // NEW-style sessions (no sandbox) are live via the main
            // ~/.claude/sessions, keyed by cliSessionId. Check both.
            info.IsLive =
                (st.CliSessionId != null && live.Contains(st.CliSessionId)) ||
                (st.CoworkSessionId != null && live.Contains(st.CoworkSessionId)) ||
                (st.CliSessionId != null && _mainLiveIds.Contains(st.CliSessionId)) ||
                (st.CoworkSessionId != null && _mainLiveIds.Contains(st.CoworkSessionId));
            return info;
        }

        /// <summary>
        /// Resolve &lt;sandbox&gt;\.claude\projects\&lt;slug&gt;\&lt;cliSessionId&gt;.jsonl by
        /// enumerating the (usually single) slug dir and probing for the exact
        /// file name. Never globs, so audit.jsonl (sandbox root) and
        /// */subagents/* files can never be picked up.
        /// </summary>
        private string FindCoworkTranscript(string sandboxDir, string cliSessionId)
        {
            if (string.IsNullOrEmpty(cliSessionId)) return null;
            string projects = Path.Combine(sandboxDir, Path.Combine(".claude", "projects"));
            try { if (!Directory.Exists(Lp(projects))) return null; }
            catch { return null; }

            string[] slugs;
            try { slugs = Directory.GetDirectories(Lp(projects)); }
            catch { return null; }

            foreach (string slugRaw in slugs)
            {
                string slug = StripLp(slugRaw);
                string candidate = slug + "\\" + cliSessionId + ".jsonl";
                try { if (File.Exists(Lp(candidate))) return candidate; }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Parse (or serve cached) summary of one local_*.json state file. The
        /// file is mostly a large embedded conversation, so the parsed summary
        /// is cached by (mtime, length) exactly like the transcript cache.
        /// </summary>
        private CoworkState GetOrParseCoworkState(string file)
        {
            FileInfo fi = new FileInfo(Lp(file));
            if (!fi.Exists) return null;

            CoworkState prev;
            if (_coworkCache.TryGetValue(file, out prev) && prev != null &&
                prev.LastWriteTimeUtc == fi.LastWriteTimeUtc && prev.Length == fi.Length)
            {
                return prev;
            }

            string text;
            try
            {
                using (FileStream fs = OpenShared(file))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }
            }
            catch
            {
                // transient read failure: serve the previous summary (may be
                // null -> session skipped for one tick); never cache a failure
                return prev;
            }

            CoworkState st = new CoworkState();
            st.LastWriteTimeUtc = fi.LastWriteTimeUtc;
            st.Length = fi.Length;
            try
            {
                JavaScriptSerializer ser = NewSerializer();
                Dictionary<string, object> obj =
                    ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj != null)
                {
                    st.CoworkSessionId = GetString(obj, "sessionId");
                    st.CliSessionId = GetString(obj, "cliSessionId");
                    st.Title = GetString(obj, "title");
                    st.InitialMessage = GetString(obj, "initialMessage");
                    st.Model = GetString(obj, "model");
                    object arch = GetField(obj, "isArchived");
                    st.IsArchived = arch is bool && (bool)arch;
                    long ms = ToLong(GetField(obj, "lastActivityAt"));
                    if (ms > 0) st.LastActivityUtc = Epoch.AddMilliseconds((double)ms);
                    // OLD schema's userSelectedFolders[0] is the REAL project
                    // folder; its "cwd" points at the sandbox
                    // "...\local_<id>\outputs" dir (which would wrongly show
                    // "outputs" as the project). NEW schema has no
                    // userSelectedFolders and its "cwd" IS the real folder.
                    // So prefer userSelectedFolders, fall back to cwd.
                    object[] folders = GetField(obj, "userSelectedFolders") as object[];
                    if (folders != null && folders.Length > 0)
                        st.ProjectFolder = folders[0] as string;
                    if (string.IsNullOrEmpty(st.ProjectFolder))
                    {
                        string cwdVal = GetString(obj, "cwd");
                        // A sandbox cwd (".../local-agent-mode-sessions/.../outputs")
                        // is never a real project folder -> never show "outputs".
                        if (!IsSandboxPath(cwdVal)) st.ProjectFolder = cwdVal;
                    }
                    // only files that carry a cliSessionId are sessions (the
                    // skills-plugin dir and other JSON never qualify)
                    st.Valid = !string.IsNullOrEmpty(st.CliSessionId);
                }
            }
            catch
            {
                st.Valid = false; // malformed JSON: not a session (re-checked on change)
            }
            _coworkCache[file] = st;
            return st;
        }

        /// <summary>Normalize a title: single line, collapsed spaces, ~80 chars.</summary>
        private static string TrimTitle(string text)
        {
            if (text == null) return null;
            text = text.Trim();
            if (text.Length == 0) return null;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            if (text.Length > 80) text = text.Substring(0, 80).TrimEnd() + "...";
            return text;
        }

        /// <summary>
        /// True if a path lives inside a Cowork sandbox tree (its leaf is
        /// typically "outputs"); such a path is never a real user project
        /// folder, so it must not become a ProjectName.
        /// </summary>
        private static bool IsSandboxPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            return path.IndexOf("\\local-agent-mode-sessions\\", StringComparison.OrdinalIgnoreCase) >= 0
                || path.IndexOf("\\claude-code-sessions\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string t = path.Trim().TrimEnd('\\', '/');
            int idx = t.LastIndexOfAny(new char[] { '\\', '/' });
            string leaf = idx >= 0 ? t.Substring(idx + 1) : t;
            return leaf.Length > 0 ? leaf : null;
        }

        private CacheEntry GetOrParse(string file)
        {
            FileInfo fi = new FileInfo(Lp(file));
            if (!fi.Exists) return null;

            CacheEntry prev;
            if (_cache.TryGetValue(file, out prev) && prev.Info != null &&
                prev.LastWriteTimeUtc == fi.LastWriteTimeUtc && prev.Length == fi.Length)
            {
                return prev;
            }

            bool parseFailed;
            bool titleFailed;
            CacheEntry entry = new CacheEntry();
            entry.LastWriteTimeUtc = fi.LastWriteTimeUtc;
            entry.Length = fi.Length;
            entry.Info = ParseTranscript(file, fi, out parseFailed);
            entry.FallbackTitle = FindFallbackTitle(file, out titleFailed);

            if (parseFailed)
            {
                // Transient read failure (antivirus/indexer holding the file,
                // momentary I/O error): do NOT cache the empty result -- for a
                // completed session the file never changes again, so a cached
                // failure would be permanent. Serve the previous entry (stale
                // data beats a false "no usage"); the next scan retries.
                return prev; // may be null -> session skipped for one tick
            }
            if (titleFailed)
            {
                // Usage parsed fine but the title read failed: show the fresh
                // data without caching it, so the next scan retries the title.
                return entry;
            }
            _cache[file] = entry;
            return entry;
        }

        /// <summary>
        /// Tail-read the transcript and find the LAST non-sidechain assistant
        /// line whose usage fields sum above zero. Widens the tail window
        /// (256 KB -> 1 MB -> 4 MB -> whole file) until one is found.
        /// </summary>
        private SessionInfo ParseTranscript(string file, FileInfo fi, out bool readFailed)
        {
            readFailed = false;
            JavaScriptSerializer ser = NewSerializer();

            SessionInfo info = new SessionInfo();
            info.SessionId = Path.GetFileNameWithoutExtension(file);
            info.FilePath = file;
            info.FileSizeBytes = fi.Length;
            info.LastActivityUtc = fi.LastWriteTimeUtc;
            info.Model = "";
            info.Source = "Code"; // the Cowork pass overrides this on its clone

            string cwd = null;
            string tsStr = null;
            string model = null;
            bool foundUsage = false;

            long[] passes = new long[] { 262144L, 1048576L, 4194304L, long.MaxValue };
            for (int p = 0; p < passes.Length && !foundUsage; p++)
            {
                long want = passes[p];
                bool wholeFile = want >= fi.Length;

                string[] lines;
                try { lines = ReadTailLines(file, want); }
                catch { readFailed = true; break; }
                if (lines == null) { readFailed = true; break; }

                for (int i = lines.Length - 1; i >= 0 && !foundUsage; i--)
                {
                    string line = lines[i];
                    if (line == null) continue;
                    line = line.Trim(' ', '\t', '\r', '\uFEFF');
                    if (line.Length < 2 || line[0] != '{') continue;

                    // cheap substring pre-checks before paying for JSON parsing
                    bool maybeAssistant =
                        line.IndexOf("\"assistant\"", StringComparison.Ordinal) >= 0 &&
                        line.IndexOf("\"usage\"", StringComparison.Ordinal) >= 0;
                    bool wantMeta =
                        tsStr == null ||
                        (cwd == null && line.IndexOf("\"cwd\"", StringComparison.Ordinal) >= 0);
                    if (!maybeAssistant && !wantMeta) continue;

                    Dictionary<string, object> obj;
                    try { obj = ser.DeserializeObject(line) as Dictionary<string, object>; }
                    catch { continue; }
                    if (obj == null) continue;

                    // harvest the latest cwd/timestamp (we walk backwards, so
                    // the first one seen is the latest in the file)
                    if (cwd == null)
                    {
                        string c = GetString(obj, "cwd");
                        if (!string.IsNullOrEmpty(c)) cwd = c;
                    }
                    if (tsStr == null)
                    {
                        string t = GetString(obj, "timestamp");
                        if (!string.IsNullOrEmpty(t)) tsStr = t;
                    }

                    if (!maybeAssistant) continue;
                    if (GetString(obj, "type") != "assistant") continue;
                    object side = GetField(obj, "isSidechain");
                    if (side is bool && (bool)side) continue;

                    Dictionary<string, object> msg = GetField(obj, "message") as Dictionary<string, object>;
                    if (msg == null) continue;
                    Dictionary<string, object> usage = GetField(msg, "usage") as Dictionary<string, object>;
                    if (usage == null) continue;

                    long input = ToLong(GetField(usage, "input_tokens"));
                    long cacheCreate = ToLong(GetField(usage, "cache_creation_input_tokens"));
                    long cacheRead = ToLong(GetField(usage, "cache_read_input_tokens"));
                    long output = ToLong(GetField(usage, "output_tokens"));
                    long sum = input + cacheCreate + cacheRead + output;
                    if (sum <= 0) continue; // all-zero usage line: keep scanning backwards

                    info.InputTokens = input;
                    info.CacheCreationTokens = cacheCreate;
                    info.CacheReadTokens = cacheRead;
                    info.OutputTokens = output;
                    info.TotalContextTokens = sum;
                    info.HasUsage = true;
                    string m = GetString(msg, "model");
                    if (!string.IsNullOrEmpty(m)) model = m;
                    foundUsage = true;
                }

                if (wholeFile) break;
            }

            if (!string.IsNullOrEmpty(tsStr))
            {
                DateTime dt;
                if (DateTime.TryParse(tsStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                {
                    info.LastActivityUtc = dt;
                }
            }
            info.Cwd = cwd;
            info.ProjectName = ResolveProjectName(cwd, file);
            if (model != null) info.Model = model;
            return info;
        }

        /// <summary>
        /// Read the first 64 KB and return the first non-sidechain user
        /// prompt as a fallback title. Skips injected "&lt;...&gt;" messages.
        /// </summary>
        private string FindFallbackTitle(string file, out bool readFailed)
        {
            readFailed = false;
            try
            {
                JavaScriptSerializer ser = NewSerializer();
                string[] lines = ReadHeadLines(file, 65536);
                if (lines == null) return null;

                foreach (string raw in lines)
                {
                    if (raw == null) continue;
                    string line = raw.Trim(' ', '\t', '\r', '\uFEFF');
                    if (line.Length < 2 || line[0] != '{') continue;
                    if (line.IndexOf("\"user\"", StringComparison.Ordinal) < 0) continue;

                    Dictionary<string, object> obj;
                    try { obj = ser.DeserializeObject(line) as Dictionary<string, object>; }
                    catch { continue; }
                    if (obj == null) continue;
                    if (GetString(obj, "type") != "user") continue;
                    object side = GetField(obj, "isSidechain");
                    if (side is bool && (bool)side) continue;

                    Dictionary<string, object> msg = GetField(obj, "message") as Dictionary<string, object>;
                    if (msg == null) continue;
                    object content = GetField(msg, "content");

                    string text = null;
                    if (content is string)
                    {
                        text = (string)content;
                    }
                    else
                    {
                        object[] blocks = content as object[];
                        if (blocks != null)
                        {
                            foreach (object b in blocks)
                            {
                                Dictionary<string, object> block = b as Dictionary<string, object>;
                                if (block == null) continue;
                                if (GetString(block, "type") != "text") continue;
                                string bt = GetString(block, "text");
                                if (!string.IsNullOrEmpty(bt)) { text = bt; break; }
                            }
                        }
                    }

                    if (text == null) continue;
                    text = text.Trim();
                    if (text.Length == 0) continue;
                    if (text[0] == '<') continue; // injected reminder / command message

                    text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                    while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                        text = text.Replace("  ", " ");
                    if (text.Length > 80) text = text.Substring(0, 80).TrimEnd() + "...";
                    return text;
                }
            }
            catch { readFailed = true; }
            return null;
        }

        /// <summary>
        /// Earliest "display" per sessionId from ~/.claude/history.jsonl.
        /// </summary>
        private Dictionary<string, string> LoadHistoryTitles()
        {
            Dictionary<string, string> titles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> earliest =
                new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            string path = Path.Combine(ClaudeDir, "history.jsonl");
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists) return titles;

            // serve the cached dictionary while the file is unchanged
            if (_historyCache != null &&
                _historyWriteUtc == fi.LastWriteTimeUtc && _historyLength == fi.Length)
            {
                return _historyCache;
            }

            bool readFailed = false;
            JavaScriptSerializer ser = NewSerializer();
            try
            {
                using (FileStream fs = OpenShared(path))
                using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        try
                        {
                            string trimmed = line.Trim(' ', '\t', '\r', '\uFEFF');
                            if (trimmed.Length < 2 || trimmed[0] != '{') continue;

                            Dictionary<string, object> obj =
                                ser.DeserializeObject(trimmed) as Dictionary<string, object>;
                            if (obj == null) continue;

                            string sid = GetString(obj, "sessionId");
                            string display = GetString(obj, "display");
                            if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(display)) continue;

                            long ts = ToLong(GetField(obj, "timestamp"));
                            long prev;
                            if (!earliest.TryGetValue(sid, out prev) || ts < prev)
                            {
                                earliest[sid] = ts;
                                string t = display.Trim()
                                    .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
                                if (t.Length > 80) t = t.Substring(0, 80).TrimEnd() + "...";
                                titles[sid] = t;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { readFailed = true; }

            if (!readFailed)
            {
                // cache only complete reads: a transient I/O failure must not
                // freeze a partial title set for this (mtime, length)
                _historyCache = titles;
                _historyWriteUtc = fi.LastWriteTimeUtc;
                _historyLength = fi.Length;
            }
            return titles;
        }

        /// <summary>
        /// Session ids of currently running sessions, from
        /// ~/.claude/sessions/&lt;pid&gt;.json. A stale file whose pid is no
        /// longer running is ignored.
        /// </summary>
        private HashSet<string> LoadLiveSessionIds()
        {
            return LoadLiveSessionIdsFrom(Path.Combine(ClaudeDir, "sessions"));
        }

        /// <summary>
        /// Same live-id logic pointed at any sessions dir (the main
        /// ~/.claude/sessions or a Cowork sandbox's .claude/sessions).
        /// </summary>
        private HashSet<string> LoadLiveSessionIdsFrom(string dir)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (!Directory.Exists(Lp(dir))) return ids; }
            catch { return ids; }

            JavaScriptSerializer ser = NewSerializer();
            string[] files;
            try { files = Directory.GetFiles(Lp(dir), "*.json"); }
            catch { return ids; }

            foreach (string file in files)
            {
                try
                {
                    string text;
                    using (FileStream fs = OpenShared(file))
                    using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        text = reader.ReadToEnd();
                    }
                    Dictionary<string, object> obj =
                        ser.DeserializeObject(text) as Dictionary<string, object>;
                    if (obj == null) continue;
                    string sid = GetString(obj, "sessionId");
                    if (string.IsNullOrEmpty(sid)) continue;

                    long pid = ToLong(GetField(obj, "pid"));
                    // malformed/partially-written file with no usable pid:
                    // never badge live without a process to validate against
                    if (pid <= 0) continue;
                    try
                    {
                        Process proc = Process.GetProcessById((int)pid);
                        proc.Dispose();
                    }
                    catch
                    {
                        continue; // pid not running -> stale file
                    }
                    ids.Add(sid);
                }
                catch { }
            }
            return ids;
        }

        /// <summary>
        /// Full parse of one transcript: every point where the running total
        /// (non-sidechain assistant usage sum) changed. Returns the last
        /// maxPoints entries, oldest first.
        /// </summary>
        public List<GrowthPoint> ReadGrowth(string path, int maxPoints)
        {
            List<GrowthPoint> points = new List<GrowthPoint>();
            JavaScriptSerializer ser = NewSerializer();
            long lastTotal = -1;

            using (FileStream fs = OpenShared(path))
            using (StreamReader reader = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        if (line.Length < 2) continue;
                        if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                        if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;

                        Dictionary<string, object> obj =
                            ser.DeserializeObject(line.Trim(' ', '\t', '\r', '\uFEFF'))
                            as Dictionary<string, object>;
                        if (obj == null) continue;
                        if (GetString(obj, "type") != "assistant") continue;
                        object side = GetField(obj, "isSidechain");
                        if (side is bool && (bool)side) continue;

                        Dictionary<string, object> msg = GetField(obj, "message") as Dictionary<string, object>;
                        if (msg == null) continue;
                        Dictionary<string, object> usage = GetField(msg, "usage") as Dictionary<string, object>;
                        if (usage == null) continue;

                        long sum = ToLong(GetField(usage, "input_tokens"))
                                 + ToLong(GetField(usage, "cache_creation_input_tokens"))
                                 + ToLong(GetField(usage, "cache_read_input_tokens"))
                                 + ToLong(GetField(usage, "output_tokens"));
                        if (sum <= 0) continue;
                        if (sum == lastTotal) continue; // streamed duplicates of one API call
                        lastTotal = sum;

                        DateTime ts = DateTime.MinValue;
                        string t = GetString(obj, "timestamp");
                        if (!string.IsNullOrEmpty(t))
                        {
                            DateTime parsed;
                            if (DateTime.TryParse(t, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                out parsed))
                            {
                                ts = parsed;
                            }
                        }

                        GrowthPoint gp = new GrowthPoint();
                        gp.TimestampUtc = ts;
                        gp.Total = sum;
                        points.Add(gp);
                    }
                    catch { }
                }
            }

            if (maxPoints > 0 && points.Count > maxPoints)
                points = points.GetRange(points.Count - maxPoints, maxPoints);
            return points;
        }

        // ----- low-level helpers -------------------------------------------

        /// <summary>Shallow copy so cached instances stay immutable after publication.</summary>
        private static SessionInfo CloneInfo(SessionInfo src)
        {
            SessionInfo c = new SessionInfo();
            c.SessionId = src.SessionId;
            c.FilePath = src.FilePath;
            c.Title = src.Title;
            c.ProjectName = src.ProjectName;
            c.Cwd = src.Cwd;
            c.Model = src.Model;
            c.LastActivityUtc = src.LastActivityUtc;
            c.InputTokens = src.InputTokens;
            c.CacheReadTokens = src.CacheReadTokens;
            c.CacheCreationTokens = src.CacheCreationTokens;
            c.OutputTokens = src.OutputTokens;
            c.TotalContextTokens = src.TotalContextTokens;
            c.HasUsage = src.HasUsage;
            c.IsLive = src.IsLive;
            c.FileSizeBytes = src.FileSizeBytes;
            c.Source = src.Source;
            c.WindowOverride = src.WindowOverride;
            c.IsArchived = src.IsArchived;
            return c;
        }

        private static JavaScriptSerializer NewSerializer()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            // single transcript lines can exceed the 2M-char default
            ser.MaxJsonLength = int.MaxValue;
            return ser;
        }

        /// <summary>
        /// Open for reading while another process appends. ReadWrite+Delete
        /// sharing is required or live transcripts fail to open. Routed
        /// through Lp() because Cowork transcript paths exceed MAX_PATH.
        /// </summary>
        private static FileStream OpenShared(string path)
        {
            return new FileStream(Lp(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        /// <summary>
        /// Long-path guard: prefix with \\?\ so Win32 accepts paths beyond
        /// MAX_PATH even with HKLM FileSystem LongPathsEnabled = 0. Requires
        /// the two AppContext switches set at the very start of Main
        /// (UseLegacyPathHandling=false, BlockLongPaths=false).
        /// </summary>
        internal static string Lp(string p)
        {
            if (p == null) return null;
            if (p.Length >= 240 && !p.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return "\\\\?\\" + p;
            return p;
        }

        /// <summary>
        /// Enumerating from an Lp()-prefixed root returns \\?\-prefixed
        /// results; strip before storing/displaying (re-apply Lp() at IO time).
        /// </summary>
        internal static string StripLp(string p)
        {
            if (p != null && p.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return p.Substring(4);
            return p;
        }

        /// <summary>
        /// Read the last maxBytes of the file as lines. If we seeked into the
        /// middle, the first (possibly partial) line is discarded.
        /// </summary>
        private static string[] ReadTailLines(string path, long maxBytes)
        {
            using (FileStream fs = OpenShared(path))
            {
                long len = fs.Length;
                long start = 0;
                if (maxBytes < len) start = len - maxBytes;
                long count = len - start;
                if (count > int.MaxValue) return null;
                if (count <= 0) return new string[0];

                fs.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[(int)count];
                int total = ReadFully(fs, buf);
                string text = Encoding.UTF8.GetString(buf, 0, total);
                string[] lines = text.Split('\n');
                if (start > 0 && lines.Length > 0)
                {
                    string[] t = new string[lines.Length - 1];
                    Array.Copy(lines, 1, t, 0, lines.Length - 1);
                    lines = t;
                }
                return lines;
            }
        }

        /// <summary>
        /// Read the first maxBytes of the file as lines. The trailing
        /// (possibly partial) line is discarded when the file is longer.
        /// </summary>
        private static string[] ReadHeadLines(string path, int maxBytes)
        {
            using (FileStream fs = OpenShared(path))
            {
                long len = fs.Length;
                int take = (int)Math.Min((long)maxBytes, len);
                if (take <= 0) return new string[0];

                byte[] buf = new byte[take];
                int total = ReadFully(fs, buf);
                string text = Encoding.UTF8.GetString(buf, 0, total);
                string[] lines = text.Split('\n');
                if (len > take && lines.Length > 0)
                {
                    string[] t = new string[lines.Length - 1];
                    Array.Copy(lines, 0, t, 0, lines.Length - 1);
                    lines = t;
                }
                return lines;
            }
        }

        private static int ReadFully(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static string ResolveProjectName(string cwd, string transcriptPath)
        {
            if (!string.IsNullOrEmpty(cwd))
            {
                string t = cwd.Trim().TrimEnd('\\', '/');
                int idx = t.LastIndexOfAny(new char[] { '\\', '/' });
                string leaf = idx >= 0 ? t.Substring(idx + 1) : t;
                if (leaf.Length > 0) return leaf;
            }
            try
            {
                string dir = Path.GetDirectoryName(transcriptPath);
                if (dir != null) return Path.GetFileName(dir);
            }
            catch { }
            return "";
        }

        internal static object GetField(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (d.TryGetValue(key, out v)) return v;
            return null;
        }

        internal static string GetString(Dictionary<string, object> d, string key)
        {
            object v = GetField(d, key);
            return v as string;
        }

        internal static long ToLong(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }
}
