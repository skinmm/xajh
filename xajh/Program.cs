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
            var npcSnapshotLock = new object();
            var npcSnapshot = new List<Npc>();
            try
            {
                npcSnapshot = npcReader.GetAllNpcs();
            }
            catch { }

            bool TryReattachGame(string reason)
            {
                try
                {
                    var procs = Process.GetProcessesByName("vrchat1");
                    if (procs.Length == 0) return false;

                    var picked = SelectGameProcess(procs);
                    if (picked.game == null || picked.hProcess == IntPtr.Zero) return false;

                    bool sameProcess = game != null &&
                        !game.HasExited &&
                        picked.game.Id == game.Id &&
                        picked.moduleBase == moduleBase;

                    if (sameProcess)
                    {
                        // SelectGameProcess opened a handle for this candidate.
                        // Keep the current one and close the duplicate.
                        MemoryHelper.CloseHandle(picked.hProcess);
                        return false;
                    }

                    if (hProcess != IntPtr.Zero)
                        MemoryHelper.CloseHandle(hProcess);

                    game = picked.game;
                    moduleBase = picked.moduleBase;
                    hProcess = picked.hProcess;
                    npcReader = new NpcReader(hProcess, moduleBase);
                    playerReader = new PlayerReader(hProcess, moduleBase);
                    combat = new CombatOverlay(hProcess, moduleBase);
                    turn = new TurnHelper(hProcess, moduleBase, GetGameHwnd());

                    Console.WriteLine($"[REATTACH] {reason} -> PID={game.Id} Base=0x{moduleBase.ToInt64():X8}");
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            var npcTrackerCts = new CancellationTokenSource();
            var npcTracker = new Thread(() =>
            {
                while (!npcTrackerCts.IsCancellationRequested)
                {
                    try
                    {
                        var fresh = npcReader.GetAllNpcs();
                        lock (npcSnapshotLock)
                            npcSnapshot = fresh;
                    }
                    catch { }
                    Thread.Sleep(120);
                }
            })
            { IsBackground = true };
            npcTracker.Start();
            Console.WriteLine("[*] NPC tracker thread started");

            Console.WriteLine("=== XAJH Combat Overlay ===");
            Console.WriteLine("[A] Auto-turn+fight toggle    [C] Reset calibration");
            Console.WriteLine("[R] Set radius         [L] List nearby NPCs   [P] Dump player obj");
            Console.WriteLine("[W] Refresh window     [End] Exit");
            Console.WriteLine();

            bool autoFace = false;
            float aimRadius = 300f;
            Console.WriteLine($"[*] Aim radius: {aimRadius:F0}");

            // Get NPCs within radius, sorted by HORIZONTAL (XY) distance
            // In this game: X,Y = ground plane, Z = height
            List<Npc> GetTrackedNpcs()
            {
                lock (npcSnapshotLock)
                    return new List<Npc>(npcSnapshot);
            }

            List<(Npc npc, double distXY, double dist3D)> GetNearbyNpcs()
            {
                var (px, py, pz) = playerReader.Get();
                var npcs = GetTrackedNpcs();
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
                    // Full list for debugging when auto routine runs
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
                bool fightTriggered = false;
                if (!r.StartsWith("[!]"))
                    fightTriggered = turn.TriggerTargetAndFight();
                string fightStatus = fightTriggered ? "target=X fight=F" : "target/fight=!";
                return $"→ {target.Name} d={distXY:F0}  {r}  {fightStatus}  ({nearby.Count} in range)";
            }

            try
            {
                while (true)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true).Key;
                        if (key == ConsoleKey.End) break;

                        if (key == ConsoleKey.L)
                        {
                            var (px, py, pz) = playerReader.Get();
                            var dbg = playerReader.GetDebugSnapshot();
                            bool unresolved = dbg.Source == "none" ||
                                              dbg.Source == "mgr=0" ||
                                              dbg.Source == "list/obj=0" ||
                                              dbg.Source == "obj-ex";
                            if (unresolved && TryReattachGame($"player-source={dbg.Source}"))
                            {
                                Thread.Sleep(150);
                                (px, py, pz) = playerReader.Get();
                                dbg = playerReader.GetDebugSnapshot();
                            }

                            Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  radius={aimRadius:F0}");
                            Console.WriteLine(
                                $"  [DBG] src={dbg.Source} mgr=0x{dbg.MgrOffset:X} off=0x{dbg.ObjOffset:X2} pos=0x{dbg.PosOffset:X2} obj=0x{dbg.PlayerObj.ToInt64():X8} " +
                                $"raw=({dbg.RawX:F1},{dbg.RawY:F1},{dbg.RawZ:F1})");

                            // NPC list
                            var allNpcs = GetTrackedNpcs();
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
                        else if (key == ConsoleKey.A)
                        {
                            autoFace = !autoFace;
                            Console.WriteLine(autoFace ? $"[+] Auto-turn+fight ON (radius={aimRadius:F0})" : "[-] Auto-turn+fight OFF");
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
            finally
            {
                npcTrackerCts.Cancel();
                if (!npcTracker.Join(600))
                    Console.WriteLine("[*] NPC tracker thread stop timeout");
            }

        }
    }
}