// Diagnose.cs - generic console diagnostic for Cowork Context Meter.
// Replaces the machine-specific TestScanner in the portable package: it makes
// NO assumptions about which sessions exist on this computer. Run it when the
// GUI shows an empty or incomplete list to see what was found and where.
// Compiled with .NET Framework 4 csc.exe -- C# 5 syntax only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace CoworkContextMeter
{
    public static class DiagnoseProgram
    {
        public static int Main()
        {
            // MUST run before ANY System.IO type is touched: the switch values
            // are cached on first read, and Cowork transcript paths (270-470
            // chars) need modern path handling + \\?\ to work on machines
            // where LongPathsEnabled is off (the Windows default).
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            int exitCode = 0;
            Scanner scanner = new Scanner();

            Console.WriteLine("Cowork Context Meter - diagnostics");
            Console.WriteLine("==================================");
            Console.WriteLine();
            Console.WriteLine("Data locations on THIS computer:");

            bool codeRoot = ReportRoot("Claude Code sessions   ", scanner.ProjectsDir);
            bool coworkRootOld = ReportRoot("Cowork sessions (old)  ", scanner.CoworkSessionsDir);
            bool coworkRootNew = ReportRoot("Cowork sessions (new)  ", scanner.CoworkSessionsDirNew);
            bool coworkRoot = coworkRootOld || coworkRootNew;
            Console.WriteLine();

            if (!codeRoot && !coworkRoot)
            {
                Console.WriteLine("WARNING: neither data folder exists. Claude Code / Cowork has");
                Console.WriteLine("not stored any sessions under this Windows user account yet.");
                exitCode = 2;
            }

            List<SessionInfo> sessions;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                sessions = scanner.Scan();
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: scan failed: " + ex);
                Pause();
                return 1;
            }
            sw.Stop();

            int code = 0, cowork = 0, live = 0, withUsage = 0;
            foreach (SessionInfo s in sessions)
            {
                if (s.Source == "Cowork") cowork++; else code++;
                if (s.IsLive) live++;
                if (s.HasUsage) withUsage++;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Scan completed in {0} ms: {1} session(s) total ({2} Claude Code, {3} Cowork), {4} with usage data, {5} live.",
                sw.ElapsedMilliseconds, sessions.Count, code, cowork, withUsage, live));
            Console.WriteLine();

            if (sessions.Count == 0)
            {
                Console.WriteLine("No sessions found. If you HAVE used Claude Code or Cowork on this");
                Console.WriteLine("computer, check that you are logged in as the same Windows user.");
                if (exitCode == 0) exitCode = 2;
            }
            else
            {
                sessions.Sort(delegate(SessionInfo a, SessionInfo b)
                {
                    return b.LastActivityUtc.CompareTo(a.LastActivityUtc);
                });

                int shown = Math.Min(10, sessions.Count);
                Console.WriteLine("Most recent " + shown + " session(s):");
                Console.WriteLine(string.Format("{0} {1} {2} {3}  {4,-22} {5}",
                    "TITLE".PadRight(40), "SOURCE".PadRight(6), "TOKENS".PadLeft(12),
                    "  PCT".PadLeft(7), "MODEL", "LIVE"));
                Console.WriteLine(new string('-', 99));

                for (int i = 0; i < shown; i++)
                {
                    SessionInfo s = sessions[i];
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
            }

            Console.WriteLine();
            Console.WriteLine(exitCode == 0 ? "DIAGNOSTICS OK" : "DIAGNOSTICS COMPLETED WITH WARNINGS (exit " + exitCode + ")");
            Pause();
            return exitCode;
        }

        private static bool ReportRoot(string label, string path)
        {
            bool exists = false;
            try { exists = Directory.Exists(path); } catch { }
            Console.WriteLine("  " + label + " : " + path + (exists ? "  [found]" : "  [NOT FOUND]"));
            return exists;
        }

        private static void Pause()
        {
            // Keep the window open when launched by double-click; skip when
            // output/input is redirected (scripted runs).
            try
            {
                if (!Console.IsInputRedirected)
                {
                    Console.WriteLine();
                    Console.WriteLine("Press any key to close...");
                    Console.ReadKey(true);
                }
            }
            catch { }
        }
    }
}
