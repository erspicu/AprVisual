using AprVisual.Test;

namespace AprVisual
{
    internal static class Program
    {
        // Pure console entry. AprVisual.etc is a headless perf / validation workbench (S1 engine
        // fork) with NO frame output — the screenshot / frame-dump PNG paths were removed; the
        // purpose is measuring engine throughput, incl. on other-CPU netlists (see netlists/).
        //   AprVisual.etc --benchmark <rom> --bench-hc N   throughput + real-time gap
        //   AprVisual.etc --test <rom> / --test-dir <dir>  blargg $6000 PASS/FAIL
        //   AprVisual.etc --trace / --ppu-dump / --dump-* / --selftest ...
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                System.Console.Error.WriteLine("AprVisual.etc (headless perf/validation workbench). Use --help for usage.");
                return 1;
            }
            return TestRunner.Run(args);
        }
    }
}
