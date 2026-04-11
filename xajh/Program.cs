using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using xajh;

namespace Xajh
{
    class Program
    {
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            (bool hasPlayerMgr, bool hasNpcMgr) ProbeStatics(IntPtr hProcess, IntPtr moduleBase)
            {
                int playerMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                int npcMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D451C));
                return (playerMgr != 0, npcMgr != 0);
            }

            (Process game, IntPtr moduleBase, IntPtr hProcess) SelectGameProcess(Process[] procs)
            {
                Process best = null;
                IntPtr bestModuleBase = IntPtr.Zero;
                IntPtr bestHandle = IntPtr.Zero;
                int bestScore = int.MinValue;

                foreach (var p in procs)
                {
                    IntPtr candidateBase = IntPtr.Zero;
                    IntPtr candidateHandle = IntPtr.Zero;
                    try
                    {
                        if (p.HasExited) continue;
                        p.Refresh();
                        candidateBase = p.MainModule.BaseAddress;
                        candidateHandle = MemoryHelper.OpenProcess(
                            MemoryHelper.PROCESS_ALL_ACCESS, false, p.Id);
                        if (candidateHandle == IntPtr.Zero) continue;

                        int score = 0;
                        var (hasPlayerMgr, hasNpcMgr) = ProbeStatics(candidateHandle, candidateBase);
                        if (hasPlayerMgr) score += 6;
                        if (hasNpcMgr) score += 2;
                        if (p.MainWindowHandle != IntPtr.Zero) score += 2;
                        if (p.WorkingSet64 > 100L * 1024L * 1024L) score += 1;

                        if (score > bestScore)
                        {
                            if (bestHandle != IntPtr.Zero) MemoryHelper.CloseHandle(bestHandle);
                            best = p;
                            bestModuleBase = candidateBase;
                            bestHandle = candidateHandle;
                            bestScore = score;
                            candidateHandle = IntPtr.Zero;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        if (candidateHandle != IntPtr.Zero)
                            MemoryHelper.CloseHandle(candidateHandle);
                    }
                }

                return (best, bestModuleBase, bestHandle);
            }

            Console.WriteLine("[*] Waiting for game process (vrchat1) ...");
            Process game = null;
            IntPtr moduleBase = IntPtr.Zero;
            IntPtr hProcess = IntPtr.Zero;
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName("vrchat1");
                if (procs.Length > 0)
                {
                    var picked = SelectGameProcess(procs);
                    if (picked.game != null)
                    {
                        game = picked.game;
                        moduleBase = picked.moduleBase;
                        hProcess = picked.hProcess;
                        Console.WriteLine($"[+] Attached PID={game.Id} Base=0x{moduleBase.ToInt64():X8}");
                        break;
                    }
                }
                Thread.Sleep(500);
            }

            if (game == null) { Console.WriteLine("[!] Game not found."); Console.ReadKey(); return; }

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

            // --- Step 3: Combat overlay control ---
            var npcReader = new NpcReader(hProcess, moduleBase);
            var playerReader = new PlayerReader(hProcess, moduleBase);
            var combat = new CombatOverlay(hProcess, moduleBase);

            // Re-acquire window handle (game may create a new window after login)
            IntPtr GetGameHwnd()
            {
                try
                {
                    game.Refresh();
                    return game.MainWindowHandle;
                }
                catch { return IntPtr.Zero; }
            }

            var turn = new TurnHelper(hProcess, moduleBase, GetGameHwnd());

            Console.WriteLine("=== XAJH Combat Overlay ===");
            Console.WriteLine("[X] Aim nearest NPC    [A] Auto-aim toggle    [C] Reset calibration");
            Console.WriteLine("[R] Set radius         [L] List nearby NPCs   [P] Dump player obj");
            Console.WriteLine("[W] Refresh window     [End] Exit");
            Console.WriteLine();

            bool autoFace = false;
            float aimRadius = 300f;
            var posCandidates = new List<IntPtr>();
            Console.WriteLine($"[*] Aim radius: {aimRadius:F0}");

            int GetPlayerObj()
            {
                int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                if (mgr == 0) return 0;
                int list = MemoryHelper.ReadInt32(hProcess, new IntPtr((uint)(mgr + 8)));
                if (list == 0) return 0;
                return MemoryHelper.ReadInt32(hProcess, new IntPtr((uint)(list + 0x4C)));
            }

            // Get NPCs within radius, sorted by HORIZONTAL (XY) distance
            // In this game: X,Y = ground plane, Z = height
            List<(Npc npc, double distXY, double dist3D)> GetNearbyNpcs()
            {
                var (px, py, pz) = playerReader.Get();
                var npcs = npcReader.GetAllNpcs();
                var nearby = new List<(Npc, double, double)>();
                foreach (var n in npcs)
                {
                    double dxy = Math.Sqrt(
                        Math.Pow(n.X - px, 2) +
                        Math.Pow(n.Y - py, 2));
                    double d3d = Math.Sqrt(
                        Math.Pow(n.X - px, 2) +
                        Math.Pow(n.Y - py, 2) +
                        Math.Pow(n.Z - pz, 2));
                    if (dxy <= aimRadius)
                        nearby.Add((n, dxy, d3d));
                }
                nearby.Sort((a, b) => a.Item2.CompareTo(b.Item2));
                return nearby;
            }

            string AimNearest(bool verbose = true)
            {
                var (px, py, pz) = playerReader.Get();
                var nearby = GetNearbyNpcs();

                if (verbose)
                {
                    // Full list for debugging when pressing [X]
                    Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  radius={aimRadius:F0}");
                    Console.WriteLine($"  {"#",2}  {"Name",-20} {"xy",7} {"3d",7}  {"NpcX",9} {"NpcY",9} {"NpcZ",9}   {"dX",8} {"dY",8} {"dZ",8}");
                    for (int i = 0; i < nearby.Count; i++)
                    {
                        var (n, dxy, d3d) = nearby[i];
                        Console.WriteLine($"  {i + 1,2}. {n.Name,-20} {dxy,7:F0} {d3d,7:F0}  {n.X,9:F1} {n.Y,9:F1} {n.Z,9:F1}   {n.X - px,8:F1} {n.Y - py,8:F1} {n.Z - pz,8:F1}");
                    }
                    if (nearby.Count == 0) { return $"[!] No NPC within {aimRadius:F0}"; }
                    Console.WriteLine();
                }
                else if (nearby.Count == 0)
                    return $"[!] No NPC within {aimRadius:F0}";

                var (target, distXY, _) = nearby[0];
                string r = turn.FaceTarget(() => playerReader.Get(), target.X, target.Y);
                return $"→ {target.Name} d={distXY:F0}  {r}  ({nearby.Count} in range)";
            }

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.End) break;

                    if (key == ConsoleKey.X)
                    {
                        Console.WriteLine($"[X] {AimNearest()}");
                    }
                    else if (key == ConsoleKey.L)
                    {
                        var (px, py, pz) = playerReader.Get();
                        Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  radius={aimRadius:F0}");

                        // NPC list
                        var allNpcs = npcReader.GetAllNpcs();
                        Console.WriteLine($"\n── All NPCs from NpcReader ({allNpcs.Count} total) ──");
                        Console.WriteLine($"  {"#",3}  {"Name",-20} {"X",9} {"Y",9} {"Z",9}  {"xzDist",8}  in?");
                        for (int i = 0; i < allNpcs.Count && i < 50; i++)
                        {
                            var n = allNpcs[i];
                            double dxy = Math.Sqrt(Math.Pow(n.X - px, 2) + Math.Pow(n.Y - py, 2));
                            string inRange = dxy <= aimRadius ? "  ✓" : "";
                            Console.WriteLine($"  {i + 1,3}. {n.Name,-20} {n.X,9:F1} {n.Y,9:F1} {n.Z,9:F1}  {dxy,8:F0}{inRange}");
                        }

                        // Entity list (monsters with HP)
                        var entityMgr = new EntityManager(hProcess, moduleBase);
                        var enemies = entityMgr.GetEnemies();
                        Console.WriteLine($"\n── Enemies from EntityManager ({enemies.Count} total) ──");
                        Console.WriteLine($"  {"#",3}  {"OID",8} {"HP",6}/{"Max",6}  {"Addr",12}");
                        for (int i = 0; i < enemies.Count && i < 30; i++)
                        {
                            var e = enemies[i];
                            Console.WriteLine($"  {i + 1,3}. {e.OID,8}  {e.HP,6}/{e.MaxHP,6}  0x{e.BaseAddress.ToInt64():X8}  ({e.PosX:F0},{e.PosY:F0},{e.PosZ:F0})");
                        }
                        Console.WriteLine();
                    }
                    else if (key == ConsoleKey.R)
                    {
                        Console.Write("New radius> ");
                        string line = Console.ReadLine()?.Trim() ?? "";
                        if (float.TryParse(line, out float r) && r > 0)
                        {
                            aimRadius = r;
                            Console.WriteLine($"[R] Radius set to {aimRadius:F0}");
                        }
                        else
                        {
                            Console.WriteLine($"[R] Invalid — keeping {aimRadius:F0}");
                        }
                    }
                    else if (key == ConsoleKey.C)
                    {
                        turn.ResetCalibration();
                        Console.WriteLine("[C] Calibration reset");
                    }
                    else if (key == ConsoleKey.W)
                    {
                        // Enumerate all top-level windows owned by the game process,
                        // pick the largest visible one (the main viewport).
                        IntPtr best = IntPtr.Zero;
                        int bestArea = 0;
                        foreach (ProcessThread t in game.Threads)
                        {
                            // no per-thread enum without P/Invoke; fall back below
                        }
                        // Use EnumWindows via Process.MainWindowHandle refresh +
                        // walk all process windows
                        game.Refresh();
                        var handles = new List<IntPtr>();
                        EnumWindows((h, l) =>
                        {
                            GetWindowThreadProcessId(h, out uint pid);
                            if (pid == (uint)game.Id && IsWindowVisible(h))
                            {
                                GetClientRect(h, out RECT rc);
                                int area = (rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                                if (area > bestArea) { bestArea = area; best = h; }
                                handles.Add(h);
                            }
                            return true;
                        }, IntPtr.Zero);

                        Console.WriteLine($"[W] Found {handles.Count} visible windows in process:");
                        foreach (var h in handles)
                        {
                            GetClientRect(h, out RECT rc);
                            var sb = new System.Text.StringBuilder(256);
                            GetClassName(h, sb, 256);
                            Console.WriteLine($"    0x{h.ToInt64():X}  {rc.Right - rc.Left}x{rc.Bottom - rc.Top}  class={sb}");
                        }
                        Console.WriteLine($"[W] Picked largest: 0x{best.ToInt64():X} ({bestArea}px²)");
                        turn = new TurnHelper(hProcess, moduleBase, best);
                    }
                    else if (key == ConsoleKey.P)
                    {
                        combat.DumpPlayerObject();
                    }
                    else if (key == ConsoleKey.O)
                    {
                        Console.Write("Enter your /loc X> ");
                        if (!float.TryParse(Console.ReadLine()?.Trim(), out float locX)) { Console.WriteLine("[!] bad"); continue; }
                        Console.Write("Enter your /loc Y> ");
                        if (!float.TryParse(Console.ReadLine()?.Trim(), out float locY)) { Console.WriteLine("[!] bad"); continue; }

                        if (posCandidates.Count == 0)
                        {
                            // First pass: full scan
                            Console.WriteLine($"\n[*] Pass 1: global scan for ({locX:F0}, {locY:F0}) ...");
                            var hits = MemoryHelper.ScanForFloat(hProcess, locX, 5f);
                            foreach (var addr in hits)
                            {
                                float y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(addr, 4));
                                if (Math.Abs(y - locY) < 5f) posCandidates.Add(addr);
                            }
                            Console.WriteLine($"[*] {posCandidates.Count} candidates. Walk to NEW spot and [O] again to narrow,");
                            Console.WriteLine($"[*] or just use the first one now: 0x{(posCandidates.Count > 0 ? posCandidates[0].ToInt64() : 0):X8}");
                            if (posCandidates.Count > 0)
                            {
                                PlayerReader.GlobalPosAddr = posCandidates[0];
                                Console.WriteLine($"[+] PlayerReader.GlobalPosAddr = 0x{posCandidates[0].ToInt64():X8}");
                            }
                        }
                        else
                        {
                            // Pass 2+: filter existing candidates
                            Console.WriteLine($"\n[*] Pass 2: filtering {posCandidates.Count} candidates against ({locX:F0}, {locY:F0}) ...");
                            var keep = new List<IntPtr>();
                            foreach (var addr in posCandidates)
                            {
                                float x = MemoryHelper.ReadFloat(hProcess, addr);
                                float y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(addr, 4));
                                if (Math.Abs(x - locX) < 5f && Math.Abs(y - locY) < 5f)
                                    keep.Add(addr);
                            }
                            posCandidates = keep;
                            Console.WriteLine($"[*] {posCandidates.Count} survived");
                            foreach (var addr in posCandidates)
                            {
                                float x = MemoryHelper.ReadFloat(hProcess, addr);
                                float y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(addr, 4));
                                Console.WriteLine($"  ✓ 0x{addr.ToInt64():X8}  ({x:F1}, {y:F1})");
                            }
                            if (posCandidates.Count == 1)
                            {
                                PlayerReader.GlobalPosAddr = posCandidates[0];
                                Console.WriteLine($"\n[+] LOCKED player position @ 0x{posCandidates[0].ToInt64():X8}");
                                Console.WriteLine("[+] PlayerReader.GlobalPosAddr updated live");
                            }
                            else if (posCandidates.Count > 1)
                            {
                                // All surviving addresses mirror the same value — just pick first
                                PlayerReader.GlobalPosAddr = posCandidates[0];
                                Console.WriteLine($"\n[+] {posCandidates.Count} aliases, using first: 0x{posCandidates[0].ToInt64():X8}");
                                Console.WriteLine("[+] PlayerReader.GlobalPosAddr updated live");
                            }
                            else
                                Console.WriteLine("[!] All filtered out — press [K] to reset, try again.");
                        }
                    }
                    else if (key == ConsoleKey.K)
                    {
                        posCandidates.Clear();
                        Console.WriteLine("[K] Position candidates cleared");
                    }
                    else if (key == ConsoleKey.A)
                    {
                        autoFace = !autoFace;
                        Console.WriteLine(autoFace ? $"[+] Auto-aim ON (radius={aimRadius:F0})" : "[-] Auto-aim OFF");
                    }
                }

                if (autoFace)
                {
                    Console.WriteLine($"[AUTO] {AimNearest(verbose: false)}");
                    Thread.Sleep(800);   // throttle auto-aim
                }
                else
                {
                    Thread.Sleep(50);
                }
            }

        }
    }
}