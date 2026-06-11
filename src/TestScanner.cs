// TestScanner.cs - console test harness for Scanner.cs.
// Compiled separately with Scanner.cs (NOT part of the GUI build).
// Compiled with .NET Framework 4 csc.exe -- C# 5 syntax only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CoworkContextMeter
{
    public static class TestScannerProgram
    {
        public static int Main()
        {
            // MUST run before ANY System.IO type is touched: the switch values
            // are cached on first read, and Cowork transcript paths (274-463
            // chars) need modern path handling + \\?\ to work with the
            // machine-wide LongPathsEnabled=0.
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            int failures = 0;
            Scanner scanner = new Scanner();
            List<SessionInfo> sessions;

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                sessions = scanner.Scan();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL: Scan() threw: " + ex);
                return 1;
            }
            sw.Stop();

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Scanned {0} sessions in {1} ms from {2}",
                sessions.Count, sw.ElapsedMilliseconds, scanner.ProjectsDir));
            Console.WriteLine();

            // newest first for readability
            sessions.Sort(delegate(SessionInfo a, SessionInfo b)
            {
                return b.LastActivityUtc.CompareTo(a.LastActivityUtc);
            });

            Console.WriteLine(string.Format("{0} {1} {2} {3}  {4,-22} {5}",
                "TITLE".PadRight(40), "SOURCE".PadRight(6), "TOKENS".PadLeft(12),
                "  PCT".PadLeft(7), "MODEL", "LIVE"));
            Console.WriteLine(new string('-', 99));

            foreach (SessionInfo s in sessions)
            {
                string title = s.Title;
                if (string.IsNullOrEmpty(title)) title = s.SessionId ?? "(unknown)";
                if (title.Length > 40) title = title.Substring(0, 40);
                title = title.PadRight(40);

                string source = (s.Source ?? "Code").PadRight(6);

                string total;
                string pct;
                if (s.HasUsage)
                {
                    long window;
                    if (s.WindowOverride > 0)
                        window = s.WindowOverride; // exact (state-file [1m] marker)
                    else if (s.TotalContextTokens > 200000L)
                        window = 1000000L; // Auto bump
                    else
                        window = 200000L;
                    double p = (double)s.TotalContextTokens * 100.0 / (double)window;
                    total = s.TotalContextTokens.ToString("N0", CultureInfo.InvariantCulture).PadLeft(12);
                    pct = string.Format(CultureInfo.InvariantCulture, "{0,6:0.0}%", p);
                }
                else
                {
                    total = "-".PadLeft(12);
                    pct = "     -%";
                }

                Console.WriteLine(string.Format("{0} {1} {2} {3}  {4,-22} {5}",
                    title, source, total, pct, s.Model ?? "", s.IsLive ? "LIVE" : ""));
            }
            Console.WriteLine();

            // --- assertions -------------------------------------------------

            if (sessions.Count >= 3)
            {
                Console.WriteLine("PASS: found at least 3 sessions (" + sessions.Count + ")");
            }
            else
            {
                Console.WriteLine("FAIL: expected at least 3 sessions, found " + sessions.Count);
                failures++;
            }

            int usageCount = 0;
            long maxTotal = 0;
            bool rangeOk = true;
            foreach (SessionInfo s in sessions)
            {
                if (!s.HasUsage) continue;
                usageCount++;
                if (s.TotalContextTokens > maxTotal) maxTotal = s.TotalContextTokens;
                if (s.TotalContextTokens <= 0 || s.TotalContextTokens >= 2000000L)
                {
                    rangeOk = false;
                    failures++;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "FAIL: session {0} total {1:N0} out of range (0, 2,000,000)",
                        s.SessionId, s.TotalContextTokens));
                }
            }
            if (rangeOk)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "PASS: all {0} sessions with usage have 0 < total < 2,000,000", usageCount));
            }

            if (maxTotal > 10000L)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "PASS: at least one session has total > 10,000 (max = {0:N0})", maxTotal));
            }
            else
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "FAIL: no session has total > 10,000 (max = {0:N0})", maxTotal));
                failures++;
            }

            // --- Cowork assertions ------------------------------------------

            int coworkCount = 0;
            SessionInfo target = null;
            string longPathExample = null;
            foreach (SessionInfo s in sessions)
            {
                if (s.Source != "Cowork") continue;
                coworkCount++;
                if (s.SessionId == "7c1ca0f5-28fe-4d35-b54e-64be4634aaec") target = s;
                if (longPathExample == null && s.HasUsage &&
                    s.FilePath != null && s.FilePath.Length >= 270)
                {
                    longPathExample = s.FilePath;
                }
            }

            if (coworkCount >= 1)
            {
                Console.WriteLine("PASS: found at least 1 Cowork-source session ("
                    + coworkCount + ")");
            }
            else
            {
                Console.WriteLine("FAIL: no Cowork-source sessions found");
                failures++;
            }

            if (target == null)
            {
                Console.WriteLine("FAIL: session 7c1ca0f5-28fe-4d35-b54e-64be4634aaec not found");
                failures++;
            }
            else
            {
                Console.WriteLine("PASS: session 7c1ca0f5-28fe-4d35-b54e-64be4634aaec is present");

                if (target.HasUsage && target.TotalContextTokens > 50000L)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "PASS: 7c1ca0f5 total > 50,000 (total = {0:N0})",
                        target.TotalContextTokens));
                }
                else
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "FAIL: 7c1ca0f5 total expected > 50,000, got {0:N0} (HasUsage={1})",
                        target.TotalContextTokens, target.HasUsage));
                    failures++;
                }

                long resolvedWindow;
                if (target.WindowOverride > 0)
                    resolvedWindow = target.WindowOverride;
                else if (target.TotalContextTokens > 200000L)
                    resolvedWindow = 1000000L;
                else
                    resolvedWindow = 200000L;
                if (resolvedWindow == 1000000L && target.WindowOverride == 1000000L)
                {
                    Console.WriteLine(
                        "PASS: 7c1ca0f5 window resolves to 1,000,000 via state-file [1m] marker"
                        + " (model = " + (target.Model ?? "") + ")");
                }
                else
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "FAIL: 7c1ca0f5 window expected 1,000,000 override, got override={0:N0} resolved={1:N0} model={2}",
                        target.WindowOverride, resolvedWindow, target.Model ?? ""));
                    failures++;
                }

                if (target.Title == "SRI document pending issues")
                {
                    Console.WriteLine("PASS: 7c1ca0f5 title == \"SRI document pending issues\"");
                }
                else
                {
                    Console.WriteLine("FAIL: 7c1ca0f5 title expected \"SRI document pending issues\", got \""
                        + (target.Title ?? "(null)") + "\"");
                    failures++;
                }
            }

            if (longPathExample != null)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "PASS: read a Cowork transcript behind a {0}-char path (long-path IO works"
                    + " with LongPathsEnabled=0):", longPathExample.Length));
                Console.WriteLine("      " + longPathExample);
            }
            else
            {
                Console.WriteLine(
                    "FAIL: no successfully-read Cowork transcript path >= 270 chars");
                failures++;
            }

            Console.WriteLine();
            if (failures == 0)
            {
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            Console.WriteLine(failures + " TEST(S) FAILED");
            return 1;
        }
    }
}
