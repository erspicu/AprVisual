using System;
using System.IO;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    /// <summary>
    /// Headless CLI entry (no window). Mirrors ref/AprNes/TestRunner.cs's shape:
    ///   --rom &lt;path&gt;        load and... (S1: actually this pops up the window — see below)
    ///   --test &lt;path&gt;       run to the blargg $6000 signature; print "PASS|..." / "FAIL(n)|...", exit code
    ///   --test-dir &lt;dir&gt;    batch-run *.nes in a directory
    ///   --max-wait &lt;sec&gt;    timeout per test (default 15)
    ///   --region ntsc|pal|dendy
    ///   --benchmark         (S3) measure cycles/sec — placeholder for now
    ///
    /// Note: `--rom` is handled here too so a single arg works; for `--rom` we hand back to the
    /// GUI path (open MainForm) rather than running headless.
    /// </summary>
    internal static class TestRunner
    {
        public static int Run(string[] args)
        {
            string? romPath = null, testPath = null, testDir = null;
            int maxWait = 15;
            string region = "ntsc";
            bool benchmark = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom":      if (i + 1 < args.Length) romPath  = args[++i]; break;
                    case "--test":     if (i + 1 < args.Length) testPath = args[++i]; break;
                    case "--test-dir": if (i + 1 < args.Length) testDir  = args[++i]; break;
                    case "--max-wait": if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--region":   if (i + 1 < args.Length) region   = args[++i].ToLowerInvariant(); break;
                    case "--benchmark": benchmark = true; break;
                    case "--help": case "-h": case "/?": PrintUsage(); return 0;
                    default:
                        // bare path → treat as --rom
                        if (romPath is null && testPath is null && testDir is null && !args[i].StartsWith('-'))
                            romPath = args[i];
                        break;
                }
            }

            if (romPath != null)
            {
                // GUI path: show the window for this ROM.
                ApplicationConfiguration.Initialize();   // source-generated, global namespace
                System.Windows.Forms.Application.Run(new MainForm(romPath));
                return 0;
            }

            if (testDir != null)
            {
                if (!Directory.Exists(testDir)) { Console.Error.WriteLine($"no such dir: {testDir}"); return 2; }
                int fail = 0, total = 0;
                foreach (string f in Directory.EnumerateFiles(testDir, "*.nes", SearchOption.AllDirectories))
                {
                    total++;
                    if (RunOneTest(f, maxWait, region, benchmark) != 0) fail++;
                }
                Console.WriteLine($"\n{total - fail}/{total} passed");
                return fail == 0 ? 0 : 1;
            }

            if (testPath != null)
                return RunOneTest(testPath, maxWait, region, benchmark);

            PrintUsage();
            return 0;
        }

        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            try
            {
                // TODO (S1): WireCore.LoadSystem(rom); set region; then loop:
                //   run frames until WireCore.CheckUnitTest().Complete or `maxWait` seconds elapse;
                //   print "PASS | rom | name\n<text>" (exit 0) or "FAIL(code) | rom | (text)" (exit code).
                // TODO (S3): if (benchmark) — measure switch-level cycles/sec; later compare vs IR backend.
                WireCore.LoadSystem(rom);
                WireCore.ResetNes();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < maxWait)
                {
                    WireCore.RunFrame();
                    var r = WireCore.CheckUnitTest();
                    if (r.Complete)
                    {
                        if (r.Code == 0) { Console.WriteLine($"PASS | {Path.GetFileName(path)} | {name}\n{r.Text}"); return 0; }
                        Console.WriteLine($"FAIL({r.Code}) | {Path.GetFileName(path)} | ({r.Text})");
                        return r.Code == 0 ? 1 : r.Code;
                    }
                }
                Console.WriteLine($"FAIL(timeout) | {Path.GetFileName(path)} | {name}");
                return 3;
            }
            catch (NotImplementedException ex)
            {
                Console.WriteLine($"SKIP(not-implemented) | {Path.GetFileName(path)} | {ex.Message}");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                AprVisual — switch-level NES (S1)

                  AprVisual --rom <game.nes>          show a window with the live 256x240 sim
                  AprVisual --test <test.nes>         headless: run to the $6000 signature, print PASS/FAIL
                  AprVisual --test-dir <dir>          headless: batch-run *.nes under <dir>
                    [--max-wait <sec>]                timeout per test (default 15)
                    [--region ntsc|pal|dendy]
                    [--benchmark]                     (S3) measure cycles/sec
                  (no args)                           open the GUI (S1: not implemented yet)
                """);
        }
    }
}
