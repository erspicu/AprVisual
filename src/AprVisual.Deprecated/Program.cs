using System;
using System.Windows.Forms;
using AprVisual.Test;

namespace AprVisual
{
    internal static class Program
    {
        // Single exe, args-branching (AprNes style — see MD/struct/09):
        //   AprVisual.exe --rom path\game.nes        → pop up a window, show live 256x240 switch-level sim
        //   AprVisual.exe --test path\test.nes       → headless: run to the $6000 signature, print PASS/FAIL, exit code
        //   AprVisual.exe --test-dir path\roms\      → headless: batch-run a directory
        //   (no args)                                → S1: nothing yet (later: a simple open-file GUI)
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0)
                return TestRunner.Run(args);

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }
    }
}
