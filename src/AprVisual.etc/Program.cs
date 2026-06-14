using AprVisual.Test;

namespace AprVisual
{
    internal static class Program
    {
        // Pure console entry. The S1 fork is headless-only (benchmark / test / frame-dump);
        // the live WinForms window was removed — see MD notes on the S1 console conversion.
        //   AprVisual.S1 --benchmark <rom> --bench-hc N   throughput + real-time gap
        //   AprVisual.S1 --frame-dump <rom> [--frame-count N] [--out-dir DIR]
        //   AprVisual.S1 --test <rom> / --test-dir <dir>  blargg $6000 PASS/FAIL
        //   AprVisual.S1 --screenshot / --trace / --dump-* / --selftest ...
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                System.Console.Error.WriteLine("AprVisual.S1 (headless console). Use --help for usage.");
                return 1;
            }
            return TestRunner.Run(args);
        }
    }
}
