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
            static IntPtr Ptr32(int value) => new IntPtr(value);
            static IntPtr Ptr32Add(int baseValue, int offset) => new IntPtr(unchecked(baseValue + offset));
            int[] directMgrOffsets = { 0x9D4518, 0x9D4514, 0x9D4510, 0x9D4520, 0x9D451C, 0x9D4524, 0x9D450C };
            int[] directListOffsets = { 0x08, 0x0C, 0x04, 0x10, 0x14 };
            int[] directObjOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C };
            int[] directPosOffsets = { 0x94, 0x34, 0x64, 0xC4 };
            int preferredDirectMgr = 0x9D4518;
            int preferredDirectObj = 0x4C;
            int preferredDirectPos = 0x94;
            bool hasDirectCache = false;
            float directCx = 0f, directCy = 0f;

            (bool hasPlayerMgr, bool hasNpcMgr) ProbeStatics(IntPtr hProcess, IntPtr moduleBase)
            {
                int playerMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                int npcMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D451C));
                return (playerMgr != 0, npcMgr != 0);
            }

            bool TryReadStablePos(IntPtr hProcess, IntPtr playerObj, int posOff, out float x, out float y, out float z)
            {
                x = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff));
                y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff + 4));
                z = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff + 8));
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
                if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f || Math.Abs(z) > 1_000_000f) return false;
                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f) return false;

                // Read twice to reject clearly volatile/garbage pointers.
                float x2 = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff));
                float y2 = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff + 4));
                float z2 = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(playerObj, posOff + 8));
                if (Math.Abs(x2 - x) > 200f || Math.Abs(y2 - y) > 200f || Math.Abs(z2 - z) > 200f)
                    return false;
                return true;
            }

            bool TryReadDirectPlayerPos(
                IntPtr hProcess, IntPtr moduleBase,
                out float x, out float y, out float z, out string source)
            {
                x = 0f; y = 0f; z = 0f; source = "direct:none";
                float bestScore = float.MinValue;
                int bestMgr = 0, bestObj = 0, bestPos = 0;

                var mgrOrder = new List<int> { preferredDirectMgr };
                foreach (int mo in directMgrOffsets)
                    if (mo != preferredDirectMgr) mgrOrder.Add(mo);

                foreach (int mo in mgrOrder)
                {
                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, mo));
                    if (mgr == 0) continue;

                    foreach (int lo in directListOffsets)
                    {
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, lo));
                        if (list == 0) continue;

                        foreach (int oo in directObjOffsets)
                        {
                            int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, oo));
                            if (raw == 0) continue;
                            var pobj = Ptr32(raw);

                            foreach (int po in directPosOffsets)
                            {
                                if (!TryReadStablePos(hProcess, pobj, po, out float px, out float py, out float pz))
                                    continue;

                                float score = 0f;
                                if (mo == preferredDirectMgr) score += 2f;
                                if (oo == preferredDirectObj) score += 1f;
                                if (po == preferredDirectPos) score += 2f;
                                if (oo == 0x4C) score += 1f;
                                if (po == 0x94) score += 1f;

                                if (hasDirectCache)
                                {
                                    double d = Math.Sqrt(Math.Pow(px - directCx, 2) + Math.Pow(py - directCy, 2));
                                    if (d <= 20f) score += 4f;
                                    else if (d <= 300f) score += 2f;
                                    else if (d <= 3000f) score -= 1f;
                                    else score -= 4f;
                                }

                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    x = px; y = py; z = pz;
                                    bestMgr = mo; bestObj = oo; bestPos = po;
                                }
                            }
                        }
                    }
                }

                if (bestMgr == 0) return false;
                preferredDirectMgr = bestMgr;
                preferredDirectObj = bestObj;
                preferredDirectPos = bestPos;
                directCx = x; directCy = y; hasDirectCache = true;
                source = $"direct(m=0x{bestMgr:X},o=0x{bestObj:X2},p=0x{bestPos:X2})";
                return true;
            }

            (int chainScore, string chainSummary) ProbeGameChains(IntPtr hProcess, IntPtr moduleBase)
            {
                int score = 0;

                int playerMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                bool hasPlayerMgr = playerMgr != 0;
                if (hasPlayerMgr) score += 4;

                int playerList = 0;
                if (hasPlayerMgr)
                {
                    playerList = MemoryHelper.ReadInt32(hProcess, Ptr32Add(playerMgr, 0x08));
                    if (playerList != 0) score += 5;
                }

                int[] playerObjOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40 };
                int playerObj = 0;
                int playerObjOff = 0;
                if (playerList != 0)
                {
                    foreach (int off in playerObjOffsets)
                    {
                        int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(playerList, off));
                        if (raw == 0) continue;
                        playerObj = raw;
                        playerObjOff = off;
                        break;
                    }
                    if (playerObj != 0) score += 6;
                }

                bool hasPlayerPos = false;
                int playerPosOff = 0;
                if (playerObj != 0)
                {
                    int[] posOffsets = { 0x94, 0x34, 0x64, 0xC4 };
                    foreach (int poff in posOffsets)
                    {
                        if (!TryReadStablePos(hProcess, Ptr32(playerObj), poff, out _, out _, out _))
                            continue;
                        hasPlayerPos = true;
                        playerPosOff = poff;
                        break;
                    }
                    if (hasPlayerPos) score += 10;
                }

                int npcMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D451C));
                bool hasNpcMgr = npcMgr != 0;
                if (hasNpcMgr) score += 2;

                bool hasNpcFirst = false;
                if (hasNpcMgr)
                {
                    int firstNode = MemoryHelper.ReadInt32(hProcess, Ptr32Add(npcMgr, 8));
                    hasNpcFirst = firstNode != 0;
                    if (hasNpcFirst) score += 3;
                }

                string summary =
                    $"pmgr={(hasPlayerMgr ? 1 : 0)} plist={(playerList != 0 ? 1 : 0)} pobj={(playerObj != 0 ? 1 : 0)}@0x{playerObjOff:X2} " +
                    $"ppos={(hasPlayerPos ? 1 : 0)}@0x{playerPosOff:X2} nmgr={(hasNpcMgr ? 1 : 0)} nfirst={(hasNpcFirst ? 1 : 0)}";
                return (score, summary);
            }

            void PrintAttachDiagnostics(string tag, IntPtr handle, IntPtr baseAddr)
            {
                try
                {
                    var (sc, summary) = ProbeGameChains(handle, baseAddr);
                    Console.WriteLine($"[{tag}] score={sc} base=0x{baseAddr.ToInt64():X8} {summary}");
                }
                catch
                {
                    Console.WriteLine($"[{tag}] failed to probe");
                }
            }

            (Process game, IntPtr moduleBase, IntPtr hProcess, List<string> logs) SelectGameProcess(Process[] procs, bool collectLogs = false)
            {
                Process best = null;
                IntPtr bestModuleBase = IntPtr.Zero;
                IntPtr bestHandle = IntPtr.Zero;
                int bestScore = int.MinValue;
                var logs = new List<string>();

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
                        var (chainScore, chainSummary) = ProbeGameChains(candidateHandle, candidateBase);
                        score += chainScore;
                        if (p.MainWindowHandle != IntPtr.Zero) score += 2;
                        else score -= 2;
                        if (p.WorkingSet64 > 100L * 1024L * 1024L) score += 1;
                        if (collectLogs)
                        {
                            long mb = p.WorkingSet64 / (1024L * 1024L);
                            logs.Add($"PID={p.Id} score={score,2} base=0x{candidateBase.ToInt64():X8} win={(p.MainWindowHandle != IntPtr.Zero ? 1 : 0)} ws={mb}MB {chainSummary}");
                        }

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

                return (best, bestModuleBase, bestHandle, logs);
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

            bool TryReattachGame(string reason, bool forceRefreshSameProcess = false)
            {
                try
                {
                    var procs = Process.GetProcessesByName("vrchat1");
                    if (procs.Length == 0) return false;

                    var picked = SelectGameProcess(procs, collectLogs: true);
                    if (picked.game == null || picked.hProcess == IntPtr.Zero) return false;
                    foreach (var line in picked.logs)
                        Console.WriteLine($"[PICK] {line}");

                    bool sameProcess = game != null &&
                        !game.HasExited &&
                        picked.game.Id == game.Id &&
                        picked.moduleBase == moduleBase;

                    if (sameProcess)
                    {
                        if (!forceRefreshSameProcess)
                        {
                            // SelectGameProcess opened a handle for this candidate.
                            // Keep the current one and close the duplicate.
                            MemoryHelper.CloseHandle(picked.hProcess);
                            return false;
                        }

                        // In unresolved mode, force-refresh readers/handle even if PID/base match.
                        if (hProcess != IntPtr.Zero)
                            MemoryHelper.CloseHandle(hProcess);

                        hProcess = picked.hProcess;
                        npcReader = new NpcReader(hProcess, moduleBase);
                        playerReader = new PlayerReader(hProcess, moduleBase);
                        combat = new CombatOverlay(hProcess, moduleBase);
                        turn = new TurnHelper(hProcess, moduleBase, GetGameHwnd());

                        Console.WriteLine($"[REATTACH] {reason} -> PID={game.Id} Base=0x{moduleBase.ToInt64():X8} (refresh)");
                        return true;
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
            bool directPosHasCache = false;
            float directPosCacheX = 0f, directPosCacheY = 0f;
            string lastDirectSource = "";

            bool IsUnresolvedSource(string src)
                => src == "none" || src == "mgr=0" || src == "list/obj=0" || src == "obj-ex";

            bool TryReadPlayerDirect(out float x, out float y, out float z, out string source)
            {
                x = y = z = 0f;
                source = "";
                int[] mgrOffsets = { 0x9D4518, 0x9D4514, 0x9D4510, 0x9D4520, 0x9D451C, 0x9D4524, 0x9D450C };
                int[] listOffsets = { 0x08, 0x0C, 0x04, 0x10, 0x14 };
                int[] objOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C };
                int[] posOffsets = { 0x94, 0x34, 0x64, 0xC4 };

                float bestScore = float.MinValue;
                bool found = false;
                int bestMgr = 0, bestObj = 0, bestPos = 0;

                foreach (int mo in mgrOffsets)
                {
                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, mo));
                    if (mgr == 0) continue;
                    foreach (int lo in listOffsets)
                    {
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, lo));
                        if (list == 0) continue;
                        foreach (int oo in objOffsets)
                        {
                            int rawObj = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, oo));
                            if (rawObj == 0) continue;
                            var pobj = Ptr32(rawObj);
                            foreach (int po in posOffsets)
                            {
                                float tx = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(pobj, po));
                                float ty = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(pobj, po + 4));
                                float tz = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(pobj, po + 8));
                                bool plausible = !float.IsNaN(tx) && !float.IsNaN(ty) && !float.IsNaN(tz) &&
                                                 !float.IsInfinity(tx) && !float.IsInfinity(ty) && !float.IsInfinity(tz) &&
                                                 Math.Abs(tx) < 1_000_000f && Math.Abs(ty) < 1_000_000f && Math.Abs(tz) < 1_000_000f &&
                                                 !(Math.Abs(tx) < 0.001f && Math.Abs(ty) < 0.001f && Math.Abs(tz) < 0.001f);
                                if (!plausible) continue;

                                float score = 0f;
                                if (mo == 0x9D4518) score += 2f;
                                if (oo == 0x4C) score += 1f;
                                if (po == 0x94) score += 1f;
                                if (directPosHasCache)
                                {
                                    double dxy = Math.Sqrt(Math.Pow(tx - directPosCacheX, 2) + Math.Pow(ty - directPosCacheY, 2));
                                    if (dxy <= 20f) score += 4f;
                                    else if (dxy <= 300f) score += 2f;
                                    else if (dxy > 3000f) score -= 3f;
                                }

                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    x = tx; y = ty; z = tz;
                                    bestMgr = mo; bestObj = oo; bestPos = po;
                                    found = true;
                                }
                            }
                        }
                    }
                }

                if (!found) return false;

                directPosCacheX = x;
                directPosCacheY = y;
                directPosHasCache = true;
                source = $"direct(mgr=0x{bestMgr:X},obj=0x{bestObj:X2},pos=0x{bestPos:X2})";
                return true;
            }

            (float x, float y, float z) ReadPlayerPos()
            {
                var p = playerReader.Get();
                var dbg = playerReader.GetDebugSnapshot();
                if (IsUnresolvedSource(dbg.Source) &&
                    TryReadPlayerDirect(out float dx, out float dy, out float dz, out string dsrc))
                {
                    lastDirectSource = dsrc;
                    return (dx, dy, dz);
                }
                lastDirectSource = "";
                return p;
            }

            // Get NPCs within radius, sorted by HORIZONTAL (XY) distance
            // In this game: X,Y = ground plane, Z = height
            List<Npc> GetTrackedNpcs()
            {
                lock (npcSnapshotLock)
                    return new List<Npc>(npcSnapshot);
            }

            List<(Npc npc, double distXY, double dist3D)> GetNearbyNpcs()
            {
                var (px, py, pz) = ReadPlayerPos();
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
                var (px, py, pz) = ReadPlayerPos();
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
                string r = turn.FaceTarget(() => ReadPlayerPos(), target.X, target.Y);
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
                            var (px, py, pz) = ReadPlayerPos();
                            var dbg = playerReader.GetDebugSnapshot();
                            bool unresolved = IsUnresolvedSource(dbg.Source) && string.IsNullOrEmpty(lastDirectSource);
                            if (unresolved && TryReattachGame($"player-source={dbg.Source}", forceRefreshSameProcess: true))
                            {
                                Thread.Sleep(150);
                                (px, py, pz) = ReadPlayerPos();
                                dbg = playerReader.GetDebugSnapshot();
                            }
                            else if (unresolved)
                            {
                                Console.WriteLine($"[REATTACH] failed (source={dbg.Source})");
                            }

                            Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  radius={aimRadius:F0}");
                            Console.WriteLine(
                                $"  [DBG] src={dbg.Source} mgr=0x{dbg.MgrOffset:X} off=0x{dbg.ObjOffset:X2} pos=0x{dbg.PosOffset:X2} obj=0x{dbg.PlayerObj.ToInt64():X8} " +
                                $"raw=({dbg.RawX:F1},{dbg.RawY:F1},{dbg.RawZ:F1})");
                            if (!string.IsNullOrEmpty(lastDirectSource))
                                Console.WriteLine($"  [DBG] fallback={lastDirectSource}");

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