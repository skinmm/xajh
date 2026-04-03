using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace xajh
{
    /// <summary>
    /// Interactive HP scanner.  Works like a mini Cheat Engine:
    ///   1. Set HP to known value (e.g. 100), run First Scan
    ///   2. Take damage, run Next Scan with new value
    ///   3. Repeat until 1–5 addresses remain → those are HP addresses
    /// </summary>
    public class HpScanner
    {
        private readonly IntPtr _hProcess;
        private List<IntPtr> _candidates = new List<IntPtr>();
        private bool _firstScan = true;

        public HpScanner(IntPtr hProcess)
        {
            _hProcess = hProcess;
        }

        public void Run()
        {
            Console.WriteLine("\n╔══════════════════════════════╗");
            Console.WriteLine("║       HP ADDRESS FINDER      ║");
            Console.WriteLine("╚══════════════════════════════╝");
            Console.WriteLine("Commands: [s]can <value>  [f]ilter <value>  [r]eset  [q]uit\n");

            while (true)
            {
                Console.Write("Scanner> ");
                string input = Console.ReadLine()?.Trim().ToLower() ?? "";
                string[] parts = input.Split(' ');

                if (parts[0] == "q") break;

                if (parts[0] == "r")
                {
                    _candidates.Clear();
                    _firstScan = true;
                    Console.WriteLine("Reset. Ready for first scan.");
                    continue;
                }

                if ((parts[0] == "s" || parts[0] == "f") && parts.Length == 2 && int.TryParse(parts[1], out int val))
                {
                    if (_firstScan || parts[0] == "s")
                    {
                        Console.WriteLine($"Scanning all memory for value {val}...");
                        var sw = Stopwatch.StartNew();
                        _candidates = MemoryHelper.ScanForValue(_hProcess, val);
                        sw.Stop();
                        _firstScan = false;
                        Console.WriteLine($"Found {_candidates.Count} addresses in {sw.ElapsedMilliseconds}ms.");
                    }
                    else
                    {
                        Console.WriteLine($"Filtering {_candidates.Count} candidates for value {val}...");
                        _candidates = MemoryHelper.FilterByValue(_hProcess, _candidates, val);
                        Console.WriteLine($"Remaining: {_candidates.Count} addresses.");
                    }

                    if (_candidates.Count <= 20)
                    {
                        Console.WriteLine("\n── Candidate addresses ──");
                        foreach (var addr in _candidates)
                            Console.WriteLine($"  0x{addr.ToInt64():X16}  →  {MemoryHelper.ReadInt32(_hProcess, addr)}");
                        Console.WriteLine();
                    }
                    continue;
                }

                Console.WriteLine("Usage:  s <value>   – first/new scan");
                Console.WriteLine("        f <value>   – filter existing results");
                Console.WriteLine("        r           – reset");
                Console.WriteLine("        q           – back to main menu");
            }
        }
    }
}
