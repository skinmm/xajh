using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using xajh;

namespace Xajh
{
    class Program
    {
        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            bool bypassLogin = args.Length == 0 || Array.Exists(args, a =>
                a.Equals("--bypass", StringComparison.OrdinalIgnoreCase));

            // --- Step 1: Bypass Cloud_xajhfuzhu.exe login ---
            var (cloudH, cloudBase) = LoginBypasser.Bypass();
            if (cloudH == IntPtr.Zero)
            {
                Console.WriteLine("[!] Login bypass could not attach. Continuing anyway ...");
            }

            Console.WriteLine("[*] Waiting for game process (vrchat1) ...");
            Process game = null;
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName("vrchat1");
                if (procs.Length > 0) { game = procs[0]; break; }
                Thread.Sleep(500);
            }

            if (game == null) { Console.WriteLine("[!] Game not found."); Console.ReadKey(); return; }

            IntPtr moduleBase = game.MainModule.BaseAddress;
            IntPtr hProcess = MemoryHelper.OpenProcess(
                MemoryHelper.PROCESS_ALL_ACCESS, false, game.Id);

            if (hProcess == IntPtr.Zero)
            {
                Console.WriteLine("[!] Failed to open process. Run as Administrator.");
                Console.ReadKey(); return;
            }

            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XajhSmileDll.dll");
            if (!DllInjector.Inject(hProcess, dllPath))
            {
                Console.WriteLine("[!] Injection failed.");
                Console.ReadKey(); return;
            }

            // --- Step 3: List NPCs, press [F] to face nearest ---
            var npcReader = new NpcReader(hProcess, moduleBase);
            var playerReader = new PlayerReader(hProcess, moduleBase);
            var combat = new CombatOverlay(hProcess, moduleBase, game.Id);

            Console.WriteLine("=== XAJH NPC List ===");
            Console.WriteLine("[F] Face Nearest | [D] Dump Floats (pauses list) | [End] Exit\n");

            string lastFaceMsg = "";

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.End) break;

                    if (key == ConsoleKey.F)
                    {
                        var (fx, fy, fz) = playerReader.Get();
                        var faceNpcs = npcReader.GetAllNpcs();
                        string faced = combat.FaceNearest(fx, fy, fz, faceNpcs);
                        lastFaceMsg = faced != null
                            ? $"[+] {faced}"
                            : "[!] No NPC found to face";
                    }
                    else if (key == ConsoleKey.D)
                    {
                        Console.Clear();
                        Console.WriteLine("[D] Wide dump — stand still, do NOT turn yet.");
                        Console.WriteLine("    Press any key to take snapshot 1 ...");
                        Console.ReadKey(true);
                        var snap1 = combat.DumpPlayerWide();
                        Console.WriteLine("    Now TURN your character in-game.");
                        Console.WriteLine("    Press any key to take snapshot 2 ...");
                        Console.ReadKey(true);
                        var snap2 = combat.DumpPlayerWide();
                        CombatOverlay.CompareDumps(snap1, snap2);
                        Console.WriteLine("    Press any key to resume NPC list ...");
                        Console.ReadKey(true);
                    }
                }

                var (px, py, pz) = playerReader.Get();
                var npcs = npcReader.GetAllNpcs();

                Console.SetCursorPosition(0, 3);
                Console.WriteLine($"Player Position: ({px:F1}, {py:F1}, {pz:F1})          ");
                Console.WriteLine($"NPCs found: {npcs.Count,-6}  {lastFaceMsg,-50}");
                Console.WriteLine(new string('-', 70));
                Console.WriteLine($"{"#",-4} {"Name",-20} {"X",9} {"Y",9} {"Z",9}  {"Dist",8}");
                Console.WriteLine(new string('-', 70));

                for (int i = 0; i < npcs.Count && i < 30; i++)
                {
                    var n = npcs[i];
                    double dist = Math.Sqrt(
                        Math.Pow(n.X - px, 2) +
                        Math.Pow(n.Y - py, 2) +
                        Math.Pow(n.Z - pz, 2));
                    Console.WriteLine(
                        $"{i + 1,-4} {n.Name,-20} {n.X,9:F1} {n.Y,9:F1} {n.Z,9:F1}  {dist,8:F1}");
                }

                for (int i = npcs.Count; i < 30; i++)
                    Console.WriteLine(new string(' ', 70));

                Thread.Sleep(1000);
            }

        }
    }
}