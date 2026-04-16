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
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        static void Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            static IntPtr Ptr32(int value) => new IntPtr(value);
            static IntPtr Ptr32Add(int baseValue, int offset) => new IntPtr(unchecked(baseValue + offset));
            static string LinkCodeToTag(int linkCode)
            {
                if (linkCode == -1) return "root";
                if ((linkCode & 0x10000) == 0) return $"sub+0x{(linkCode & 0xFF):X2}";
                int l1 = (linkCode >> 8) & 0xFF;
                int l2 = linkCode & 0xFF;
                return $"sub+0x{l1:X2}>+0x{l2:X2}";
            }
            int[] directMgrOffsets = { 0x9D4520, 0x9D4518, 0x9D4514, 0x9D4510, 0x9D4524, 0x9D450C,
                                        0x978AE0, 0x9DD6C4, 0x9CA8A0 };  // added from XajhSmileDll
            int[] directListOffsets = { 0x08, 0x0C, 0x04, 0x10, 0x14, 0x58 };  // 0x58 added from XajhSmileDll
            int[] directObjOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C };
            int[] directPosOffsets = { 0x94, 0x34, 0x64, 0xC4, 0xA4, 0xB4, 0xD4, 0xE4, 0x104, 0x114, 0x4C, 0x7C, 0x84, 0x8C };
            int[] directPtrOffsets = { 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C, 0x80, 0x84, 0x88, 0x8C, 0x90, 0x94, 0x98, 0x9C, 0xA0 };
            int[] directPtrOffsetsL2 = { 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C };
            int[] directSubPosOffsets = { 0x384, 0x230, 0x190, 0x160, 0x94, 0x34, 0x64, 0xC4, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x4C, 0x7C, 0x84, 0x8C };
            int preferredDirectMgr = 0x9D4518;
            int preferredDirectList = 0x08;
            int preferredDirectObj = 0x4C;
            int preferredDirectLink = -1; // -1 = root object, otherwise pointer field offset
            int preferredDirectPos = 0x94;
            bool hasDirectCache = false;
            float directCx = 0f, directCy = 0f, directCz = 0f;
            // Chain D persistent cache — survives temporary null pointer
            float chainDx = 0f, chainDy = 0f, chainDz = 0f;
            bool hasChainD = false;
            int chainDFrozenCount = 0;      // how many consecutive identical reads
            const int ChainDFreezeLimit = 5; // invalidate after this many frozen reads
            long lastReattachMs = 0;         // throttle auto-reattach to once per 5s
            int preferredDirectStaticReads = 0;
            var directLastByKey = new Dictionary<(int mgr, int list, int obj, int link, int pos), (float x, float y)>();
            bool directCalibrating = false;
            long directCalibEndTicks = 0;
            var directCalibStartByKey = new Dictionary<(int mgr, int list, int obj, int link, int pos), (float x, float y)>();
            (int mgr, int list, int obj, int link, int pos)? directCalibLock = null;
            var directMotionByKey = new Dictionary<(int mgr, int list, int obj, int link, int pos), float>();
            IntPtr directGlobalXYLock = IntPtr.Zero;
            int fallbackStaticReads = 0;
            (int mgr, int list, int obj, int link, int pos)? lastAutoLock = null;
            int autoLockCooldown = 0;
            Process game = null;
            IntPtr moduleBase = IntPtr.Zero;
            IntPtr hProcess = IntPtr.Zero;
            int simpleStaticReads = 0;
            float simpleLastX = float.NaN, simpleLastY = float.NaN;
            IntPtr zxxyModuleBase = IntPtr.Zero;
            int zxxyModuleSize = 0;
            long nextZxxyModuleRefreshTicks = 0;
            long nextZxxyRescanTicks = 0;
            int preferredZxxyMgrOffset = -1;
            int preferredZxxyListOffset = 0x08;
            int preferredZxxyObjOffset = 0x4C;
            int preferredZxxyPosOffset = 0x94;
            var zxxyMgrCandidates = new List<(int mgrOff, int listOff, int objOff, int posOff)>();

            // zxxy.dll direct float scan state: scan the DLL's data for raw XYZ triplets
            // that look like world coordinates, tracking which ones move over time.
            long nextZxxyDirectScanTicks = 0;
            var zxxyDirectCandidates = new List<(long addr, float lastX, float lastY, float lastZ, float motion)>();
            long zxxyDirectLockedAddr = 0;
            float zxxyDirectLockedLastX = float.NaN, zxxyDirectLockedLastY = float.NaN;

            double lastNpcCloudCenterX = 0d, lastNpcCloudCenterY = 0d;
            bool hasLastNpcCloudCenter = false;

            (bool hasPlayerMgr, bool hasNpcMgr) ProbeStatics(IntPtr hProcess, IntPtr moduleBase)
            {
                int playerMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                int npcMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D451C));
                return (playerMgr != 0, npcMgr != 0);
            }

            bool TryGetGameModule(string moduleName, out IntPtr baseAddr, out int moduleSize)
            {
                baseAddr = IntPtr.Zero;
                moduleSize = 0;
                if (game == null) return false;
                try
                {
                    game.Refresh();
                    foreach (ProcessModule mod in game.Modules)
                    {
                        if (!string.Equals(mod.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                            continue;
                        baseAddr = mod.BaseAddress;
                        moduleSize = mod.ModuleMemorySize;
                        return baseAddr != IntPtr.Zero && moduleSize > 0;
                    }
                }
                catch
                {
                }
                return false;
            }

            bool TryRefreshZxxyModule(bool force = false)
            {
                long now = Environment.TickCount64;
                if (!force &&
                    now < nextZxxyModuleRefreshTicks &&
                    zxxyModuleBase != IntPtr.Zero &&
                    zxxyModuleSize > 0)
                    return true;

                nextZxxyModuleRefreshTicks = now + 2000;
                if (TryGetGameModule("zxxy.dll", out IntPtr mb, out int ms))
                {
                    bool changed = mb != zxxyModuleBase || ms != zxxyModuleSize;
                    zxxyModuleBase = mb;
                    zxxyModuleSize = ms;
                    if (changed)
                    {
                        zxxyMgrCandidates.Clear();
                        preferredZxxyMgrOffset = -1;
                    }
                    return true;
                }

                zxxyModuleBase = IntPtr.Zero;
                zxxyModuleSize = 0;
                zxxyMgrCandidates.Clear();
                preferredZxxyMgrOffset = -1;
                return false;
            }

            bool TryProbeManagerChain(
                int mgr,
                out int listOff,
                out int objOff,
                out int posOff,
                out float x,
                out float y,
                out float z)
            {
                listOff = 0;
                objOff = 0;
                posOff = 0;
                x = 0f;
                y = 0f;
                z = 0f;

                if (mgr < 0x00100000) return false;
                foreach (int lo in directListOffsets)
                {
                    int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, lo));
                    if (list < 0x00100000) continue;

                    foreach (int oo in directObjOffsets)
                    {
                        int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, oo));
                        if (raw < 0x00100000) continue;

                        foreach (int po in directPosOffsets)
                        {
                            if (!TryReadStablePos(hProcess, Ptr32(raw), po, out x, out y, out z))
                                continue;
                            listOff = lo;
                            objOff = oo;
                            posOff = po;
                            return true;
                        }
                    }
                }
                return false;
            }

            void RescanZxxyManagerCandidates()
            {
                zxxyMgrCandidates.Clear();
                if (zxxyModuleBase == IntPtr.Zero || zxxyModuleSize <= 0)
                    return;

                var seenMgrOffset = new HashSet<int>();
                var scored = new List<(float score, int mgrOff, int listOff, int objOff, int posOff)>();
                long modBase = zxxyModuleBase.ToInt64();
                long modEnd = modBase + zxxyModuleSize;
                IntPtr cursor = zxxyModuleBase;

                while (cursor.ToInt64() < modEnd)
                {
                    if (!MemoryHelper.VirtualQueryEx(
                        hProcess,
                        cursor,
                        out var mbi,
                        (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>()))
                        break;

                    long regionBase = mbi.BaseAddress.ToInt64();
                    long regionSize = mbi.RegionSize.ToInt64();
                    long regionEnd = regionBase + regionSize;
                    bool intersects = regionEnd > modBase && regionBase < modEnd;
                    bool writable = mbi.State == MemoryHelper.MEM_COMMIT &&
                        (mbi.Protect == MemoryHelper.PAGE_READWRITE ||
                         mbi.Protect == MemoryHelper.PAGE_WRITECOPY ||
                         mbi.Protect == MemoryHelper.PAGE_EXECUTE_READWRITE ||
                         mbi.Protect == MemoryHelper.PAGE_EXECUTE_WRITECOPY);

                    if (intersects && writable)
                    {
                        long scanStart = Math.Max(regionBase, modBase);
                        long scanEnd = Math.Min(regionEnd, modEnd);
                        int scanSize = (int)(scanEnd - scanStart);
                        if (scanSize >= 4)
                        {
                            var buf = new byte[scanSize];
                            if (MemoryHelper.ReadProcessMemory(hProcess, new IntPtr(scanStart), buf, scanSize, out int read) && read >= 4)
                            {
                                for (int i = 0; i + 4 <= read; i += 4)
                                {
                                    int mgr = BitConverter.ToInt32(buf, i);
                                    if (mgr < 0x00100000 || mgr > 0x7FFFFFFF) continue;
                                    int mgrOff = (int)(scanStart - modBase + i);
                                    if (!seenMgrOffset.Add(mgrOff)) continue;
                                    if (!TryProbeManagerChain(mgr, out int lo, out int oo, out int po, out float x, out float y, out _))
                                        continue;

                                    float score = 0f;
                                    if (mgrOff == preferredZxxyMgrOffset) score += 3f;
                                    if (lo == preferredZxxyListOffset) score += 1.5f;
                                    if (oo == preferredZxxyObjOffset) score += 1.5f;
                                    if (po == preferredZxxyPosOffset) score += 2f;
                                    if (hasDirectCache)
                                    {
                                        double d = Math.Sqrt(Math.Pow(x - directCx, 2) + Math.Pow(y - directCy, 2));
                                        if (d <= 40f) score += 4f;
                                        else if (d <= 400f) score += 2f;
                                        else if (d > 4000f) score -= 3f;
                                    }
                                    scored.Add((score, mgrOff, lo, oo, po));
                                }
                            }
                        }
                    }

                    long next = regionBase + regionSize;
                    if (next <= cursor.ToInt64() || next >= long.MaxValue) break;
                    cursor = new IntPtr(next);
                }

                scored.Sort((a, b) => b.score.CompareTo(a.score));
                int keep = Math.Min(24, scored.Count);
                for (int i = 0; i < keep; i++)
                {
                    var c = scored[i];
                    zxxyMgrCandidates.Add((c.mgrOff, c.listOff, c.objOff, c.posOff));
                }

                if (zxxyMgrCandidates.Count > 0)
                {
                    preferredZxxyMgrOffset = zxxyMgrCandidates[0].mgrOff;
                    preferredZxxyListOffset = zxxyMgrCandidates[0].listOff;
                    preferredZxxyObjOffset = zxxyMgrCandidates[0].objOff;
                    preferredZxxyPosOffset = zxxyMgrCandidates[0].posOff;
                }
            }

            bool TryReadPlayerPosViaZxxy(out float x, out float y, out float z, out string source)
            {
                x = 0f;
                y = 0f;
                z = 0f;
                source = "zxxy:none";

                if (!TryRefreshZxxyModule()) return false;
                long now = Environment.TickCount64;
                if (zxxyMgrCandidates.Count == 0 || now >= nextZxxyRescanTicks)
                {
                    nextZxxyRescanTicks = now + 2000;
                    RescanZxxyManagerCandidates();
                }
                if (zxxyMgrCandidates.Count == 0) return false;

                float bestScore = float.MinValue;
                int bestMgrOff = 0, bestListOff = 0, bestObjOff = 0, bestPosOff = 0;
                int valid = 0;

                foreach (var c in zxxyMgrCandidates)
                {
                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(zxxyModuleBase, c.mgrOff));
                    if (mgr < 0x00100000 || mgr > 0x7FFFFFFF) continue;
                    int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c.listOff));
                    if (list < 0x00100000 || list > 0x7FFFFFFF) continue;
                    int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c.objOff));
                    if (raw < 0x00100000 || raw > 0x7FFFFFFF) continue;
                    if (!TryReadStablePos(hProcess, Ptr32(raw), c.posOff, out float tx, out float ty, out float tz))
                        continue;

                    valid++;
                    float score = 0f;
                    if (c.mgrOff == preferredZxxyMgrOffset) score += 3f;
                    if (c.listOff == preferredZxxyListOffset) score += 1.5f;
                    if (c.objOff == preferredZxxyObjOffset) score += 1.5f;
                    if (c.posOff == preferredZxxyPosOffset) score += 2f;
                    if (hasDirectCache)
                    {
                        double d = Math.Sqrt(Math.Pow(tx - directCx, 2) + Math.Pow(ty - directCy, 2));
                        if (d <= 25f) score += 4f;
                        else if (d <= 400f) score += 2f;
                        else if (d > 4000f) score -= 3f;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        x = tx;
                        y = ty;
                        z = tz;
                        bestMgrOff = c.mgrOff;
                        bestListOff = c.listOff;
                        bestObjOff = c.objOff;
                        bestPosOff = c.posOff;
                    }
                }

                if (bestScore == float.MinValue) return false;

                preferredZxxyMgrOffset = bestMgrOff;
                preferredZxxyListOffset = bestListOff;
                preferredZxxyObjOffset = bestObjOff;
                preferredZxxyPosOffset = bestPosOff;
                // Do NOT update directCache here — let ReadPlayerPos() validate first.
                source = $"zxxy(base=0x{zxxyModuleBase.ToInt64():X8},mgr=0x{bestMgrOff:X},list=0x{bestListOff:X2},obj=0x{bestObjOff:X2},pos=0x{bestPosOff:X2},cand={valid})";
                return true;
            }

            int zxxyDirectStaleReads = 0;

            bool TryReadPlayerPosViaZxxyDirect(out float x, out float y, out float z, out string source)
            {
                x = 0f; y = 0f; z = 0f;
                source = "zxxy-direct:none";
                if (!TryRefreshZxxyModule()) return false;

                // If we have a locked address, read it — but unlock if stale.
                if (zxxyDirectLockedAddr != 0)
                {
                    var addr = new IntPtr(zxxyDirectLockedAddr);
                    float rx = MemoryHelper.ReadFloat(hProcess, addr);
                    float ry = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(addr, 4));
                    float rz = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(addr, 8));
                    int rSig = (Math.Abs(rx) >= 1f ? 1 : 0) + (Math.Abs(ry) >= 1f ? 1 : 0) + (Math.Abs(rz) >= 1f ? 1 : 0);
                    if (!float.IsNaN(rx) && !float.IsNaN(ry) && !float.IsNaN(rz) &&
                        !float.IsInfinity(rx) && !float.IsInfinity(ry) && !float.IsInfinity(rz) &&
                        Math.Abs(rx) < 1_000_000f && Math.Abs(ry) < 1_000_000f && Math.Abs(rz) < 1_000_000f &&
                        rSig >= 2)
                    {
                        bool moved = !float.IsNaN(zxxyDirectLockedLastX) &&
                            (Math.Abs(rx - zxxyDirectLockedLastX) > 0.01f || Math.Abs(ry - zxxyDirectLockedLastY) > 0.01f);
                        zxxyDirectLockedLastX = rx;
                        zxxyDirectLockedLastY = ry;

                        if (!moved)
                        {
                            zxxyDirectStaleReads++;
                            if (zxxyDirectStaleReads >= 4)
                            {
                                zxxyDirectLockedAddr = 0;
                                zxxyDirectStaleReads = 0;
                                zxxyDirectCandidates.Clear();
                                nextZxxyDirectScanTicks = 0;
                                source = "zxxy-direct:unlocked-stale";
                                return false;
                            }
                        }
                        else
                        {
                            zxxyDirectStaleReads = 0;
                        }

                        x = rx; y = ry; z = rz;
                        source = $"zxxy-direct(addr=0x{zxxyDirectLockedAddr:X8},moved={moved},stale={zxxyDirectStaleReads})";
                        return true;
                    }
                    zxxyDirectLockedAddr = 0;
                    zxxyDirectStaleReads = 0;
                }

                long now = Environment.TickCount64;
                if (now < nextZxxyDirectScanTicks && zxxyDirectCandidates.Count == 0)
                    return false;

                // Always rescan when not yet locked — we need consecutive scans
                // to detect motion. Use short interval (500ms) to accumulate motion quickly.
                bool shouldRescan = zxxyDirectCandidates.Count == 0 ||
                    now >= nextZxxyDirectScanTicks;
                if (shouldRescan)
                {
                    nextZxxyDirectScanTicks = now + 500;
                    var newCandidates = new List<(long addr, float lastX, float lastY, float lastZ, float motion)>();
                    long modBase = zxxyModuleBase.ToInt64();
                    long modEnd = modBase + zxxyModuleSize;
                    IntPtr cursor = zxxyModuleBase;

                    while (cursor.ToInt64() < modEnd)
                    {
                        if (!MemoryHelper.VirtualQueryEx(
                            hProcess, cursor, out var mbi,
                            (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>()))
                            break;

                        long regionBase = mbi.BaseAddress.ToInt64();
                        long regionSize = mbi.RegionSize.ToInt64();
                        long regionEnd = regionBase + regionSize;
                        bool intersects = regionEnd > modBase && regionBase < modEnd;
                        bool readable = mbi.State == MemoryHelper.MEM_COMMIT &&
                            (mbi.Protect == MemoryHelper.PAGE_READWRITE ||
                             mbi.Protect == MemoryHelper.PAGE_WRITECOPY ||
                             mbi.Protect == MemoryHelper.PAGE_EXECUTE_READWRITE ||
                             mbi.Protect == MemoryHelper.PAGE_EXECUTE_WRITECOPY);

                        if (intersects && readable)
                        {
                            long scanStart = Math.Max(regionBase, modBase);
                            long scanEnd = Math.Min(regionEnd, modEnd);
                            int scanSize = (int)(scanEnd - scanStart);
                            if (scanSize >= 12)
                            {
                                var buf = new byte[scanSize];
                                if (MemoryHelper.ReadProcessMemory(hProcess, new IntPtr(scanStart), buf, scanSize, out int read) && read >= 12)
                                {
                                    for (int i = 0; i + 12 <= read; i += 4)
                                    {
                                        float fx = BitConverter.ToSingle(buf, i);
                                        float fy = BitConverter.ToSingle(buf, i + 4);
                                        float fz = BitConverter.ToSingle(buf, i + 8);
                                        if (float.IsNaN(fx) || float.IsNaN(fy) || float.IsNaN(fz)) continue;
                                        if (float.IsInfinity(fx) || float.IsInfinity(fy) || float.IsInfinity(fz)) continue;
                                        if (Math.Abs(fx) > 1_000_000f || Math.Abs(fy) > 1_000_000f || Math.Abs(fz) > 1_000_000f) continue;
                                        // At least 2 of 3 floats must have magnitude > 1
                                        // (allows one axis to be near zero, e.g. height on flat ground)
                                        int sigCount = (Math.Abs(fx) >= 1f ? 1 : 0) +
                                                       (Math.Abs(fy) >= 1f ? 1 : 0) +
                                                       (Math.Abs(fz) >= 1f ? 1 : 0);
                                        if (sigCount < 2) continue;

                                        long absAddr = scanStart + i;
                                        float prevMotion = 0f;
                                        foreach (var old in zxxyDirectCandidates)
                                        {
                                            if (old.addr == absAddr)
                                            {
                                                double d = Math.Sqrt(Math.Pow(fx - old.lastX, 2) + Math.Pow(fy - old.lastY, 2) + Math.Pow(fz - old.lastZ, 2));
                                                float impulse = d > 0.05 ? (float)Math.Min(d, 50.0) : 0f;
                                                prevMotion = old.motion * 0.7f + impulse;
                                                break;
                                            }
                                        }
                                        newCandidates.Add((absAddr, fx, fy, fz, prevMotion));
                                    }
                                }
                            }
                        }
                        long next = regionBase + regionSize;
                        if (next <= cursor.ToInt64() || next >= long.MaxValue) break;
                        cursor = new IntPtr(next);
                    }
                    zxxyDirectCandidates = newCandidates;
                }

                if (zxxyDirectCandidates.Count == 0) return false;

                // Only consider candidates with confirmed motion (motion > 0).
                // Without motion we cannot distinguish real position from random data.
                float bestScore = float.MinValue;
                long bestAddr = 0;
                float bestX = 0f, bestY = 0f, bestZ = 0f;
                foreach (var c in zxxyDirectCandidates)
                {
                    if (c.motion < 0.1f) continue;
                    float score = c.motion * 3f;
                    if (hasLastNpcCloudCenter)
                    {
                        double d = Math.Sqrt(Math.Pow(c.lastX - lastNpcCloudCenterX, 2) + Math.Pow(c.lastY - lastNpcCloudCenterY, 2));
                        if (d <= 5000.0) score += 2f;
                        else if (d > 20000.0) score -= 3f;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAddr = c.addr;
                        bestX = c.lastX; bestY = c.lastY; bestZ = c.lastZ;
                    }
                }

                if (bestAddr == 0)
                {
                    source = $"zxxy-direct:no-motion(cand={zxxyDirectCandidates.Count})";
                    return false;
                }

                zxxyDirectLockedAddr = bestAddr;
                zxxyDirectLockedLastX = bestX;
                zxxyDirectLockedLastY = bestY;
                zxxyDirectStaleReads = 0;

                x = bestX; y = bestY; z = bestZ;
                source = $"zxxy-direct(addr=0x{bestAddr:X8},score={bestScore:F1},cand={zxxyDirectCandidates.Count})";
                return true;
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

            bool TryReadTripletFromBuffer(byte[] buf, int read, int off, out float x, out float y, out float z)
            {
                x = 0f; y = 0f; z = 0f;
                if (off < 0 || off + 12 > read) return false;
                x = BitConverter.ToSingle(buf, off);
                y = BitConverter.ToSingle(buf, off + 4);
                z = BitConverter.ToSingle(buf, off + 8);
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
                if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f || Math.Abs(z) > 1_000_000f) return false;
                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f) return false;
                return true;
            }

            bool TryReadGlobalXYLocked(out float x, out float y)
            {
                x = 0f; y = 0f;
                if (directGlobalXYLock == IntPtr.Zero) return false;
                x = MemoryHelper.ReadFloat(hProcess, directGlobalXYLock);
                y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(directGlobalXYLock, 4));
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) return false;
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f) return false;
                return true;
            }

            bool TryReadGlobalXYModule(out float x, out float y)
            {
                x = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(moduleBase, 0x0B201ABC));
                y = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(moduleBase, 0x0B201ABC + 4));
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) return false;
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f) return false;
                return true;
            }

            /// <summary>
            /// Reads player position from the confirmed sub-object chain:
            ///   playerObj + 0xBC  →  sub-object ptr
            ///   sub + 0x020       →  float X
            ///   sub + 0x024       →  float Z (height)
            ///   sub + 0x028       →  float Y
            ///
            /// Memory layout is (game_X, game_Z_ground, game_Y_height).
            /// Returned as C# (x=X_ground, y=Z_ground, z=Y_height) matching all other position code.
            /// Accuracy: ~10 units vs /loc (visual interpolation lag); far better than
            /// the ~30-unit error from a wrong direct-offset read.
            /// </summary>
            bool TryReadSubPtrPos(IntPtr playerObj, out float x, out float y, out float z)
            {
                x = 0f; y = 0f; z = 0f;
                try
                {
                    const int SubPtrOffset = 0xBC;
                    const int PosInSub = 0x020;

                    int subRaw = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(playerObj, SubPtrOffset));
                    if (subRaw == 0 || subRaw < 0x00100000) return false;

                    var sub = new IntPtr(unchecked((uint)subRaw));

                    // Storage order in memory: (X, Z/height, Y) — swap back to (X, Y, Z)
                    float rx = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(sub, PosInSub));
                    float rz = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(sub, PosInSub + 4));  // height
                    float ry = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(sub, PosInSub + 8));

                    if (float.IsNaN(rx) || float.IsNaN(ry) || float.IsNaN(rz)) return false;
                    if (float.IsInfinity(rx) || float.IsInfinity(ry) || float.IsInfinity(rz)) return false;
                    if (Math.Abs(rx) > 1_000_000f || Math.Abs(ry) > 1_000_000f || Math.Abs(rz) > 1_000_000f) return false;
                    if (Math.Abs(rx) < 1f && Math.Abs(ry) < 1f) return false;

                    x = rx; y = ry; z = rz;
                    return true;
                }
                catch { return false; }
            }

            bool TryAutoLockGlobalXY(float currentX, float currentY)
            {
                try
                {
                    if (autoLockCooldown > 0)
                    {
                        autoLockCooldown--;
                        return false;
                    }

                    var hitsX = MemoryHelper.ScanForFloat(hProcess, currentX, 0.2f);
                    if (hitsX.Count == 0) { autoLockCooldown = 8; return false; }

                    IntPtr best = IntPtr.Zero;
                    int bestScore = int.MinValue;
                    foreach (var a in hitsX)
                    {
                        float ny = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(a, 4));
                        if (float.IsNaN(ny) || float.IsInfinity(ny)) continue;
                        if (Math.Abs(ny - currentY) > 0.2f) continue;

                        int score = 0;
                        if (a == directGlobalXYLock) score += 4;
                        long addr = a.ToInt64();
                        if (addr >= 0x01000000 && addr <= 0x7FFFFFFF) score += 1;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = a;
                        }
                    }

                    autoLockCooldown = 8;
                    if (best == IntPtr.Zero) return false;
                    directGlobalXYLock = best;
                    return true;
                }
                catch
                {
                    autoLockCooldown = 8;
                    return false;
                }
            }

            void CollectPosCandidatesFromAddress(
                IntPtr hProcess, IntPtr objAddr, int scanLength, int[] seedOffsets,
                List<(int po, float x, float y, float z, bool seeded)> outList,
                bool includeWideScan = true)
            {
                var buf = new byte[scanLength];
                if (!MemoryHelper.ReadProcessMemory(hProcess, objAddr, buf, scanLength, out int read) || read < 0x40)
                    return;

                var seen = new HashSet<int>();
                foreach (int po in seedOffsets)
                {
                    if (!seen.Add(po)) continue;
                    if (TryReadTripletFromBuffer(buf, read, po, out float x, out float y, out float z))
                        outList.Add((po, x, y, z, true));
                }

                if (!includeWideScan) return;

                // Wider scan to discover moving coordinates that are not at known offsets.
                for (int po = 0x20; po + 12 <= read; po += 4)
                {
                    if (!seen.Add(po)) continue;
                    if (!TryReadTripletFromBuffer(buf, read, po, out float x, out float y, out float z))
                        continue;
                    outList.Add((po, x, y, z, false));
                    if (outList.Count >= 180) break;
                }
            }

            bool TryReadDirectPlayerPos(
                IntPtr hProcess, IntPtr moduleBase,
                out float x, out float y, out float z, out string source,
                bool avoidKnownStaticRoot = false,
                float rejectX = float.NaN, float rejectY = float.NaN)
            {
                x = 0f; y = 0f; z = 0f; source = "direct:none";
                bool hasRejectLock = !float.IsNaN(rejectX) && !float.IsNaN(rejectY);

                // Strict lock mode: when calibration selected a chain, never hop to unrelated chains.
                // However if the lock returns the known-wrong coordinates, break out.
                if (directCalibLock.HasValue && !hasRejectLock)
                {
                    var lk = directCalibLock.Value;
                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, lk.mgr));
                    if (mgr != 0)
                    {
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, lk.list));
                        if (list != 0)
                        {
                            int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, lk.obj));
                            if (raw != 0)
                            {
                                int targetRaw = raw;
                                if (lk.link != -1)
                                {
                                    if ((lk.link & 0x10000) == 0)
                                    {
                                        int link1 = lk.link & 0xFF;
                                        int subRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, link1));
                                        targetRaw = (subRaw != 0 && subRaw != raw) ? subRaw : 0;
                                    }
                                    else
                                    {
                                        int link1 = (lk.link >> 8) & 0xFF;
                                        int link2 = lk.link & 0xFF;
                                        int subRaw1 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, link1));
                                        if (subRaw1 != 0 && subRaw1 != raw)
                                        {
                                            int subRaw2 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(subRaw1, link2));
                                            targetRaw = (subRaw2 != 0 && subRaw2 != subRaw1 && subRaw2 != raw) ? subRaw2 : 0;
                                        }
                                        else
                                        {
                                            targetRaw = 0;
                                        }
                                    }
                                }

                                if (targetRaw != 0)
                                {
                                    var targetPtr = Ptr32(targetRaw);

                                    // Exact locked position offset first.
                                    if (TryReadStablePos(hProcess, targetPtr, lk.pos, out x, out y, out z))
                                    {
                                        preferredDirectMgr = lk.mgr;
                                        preferredDirectList = lk.list;
                                        preferredDirectObj = lk.obj;
                                        preferredDirectLink = lk.link;
                                        preferredDirectPos = lk.pos;
                                        string lockTag = LinkCodeToTag(lk.link);
                                        source = $"direct(mgr=0x{lk.mgr:X},list=0x{lk.list:X2},obj=0x{lk.obj:X2},ln={lockTag},pos=0x{lk.pos:X2},pref-lock)";
                                        return true;
                                    }

                                    // Same chain relaxed fallback: allow pos offset switch but keep locked chain.
                                    var lockPosCandidates = new List<(int po, float x, float y, float z, bool seeded)>();
                                    int[] seeds = lk.link == -1 ? directPosOffsets : directSubPosOffsets;
                                    int scanLen = lk.link == -1 ? 0x240 : 0x180;
                                    CollectPosCandidatesFromAddress(hProcess, targetPtr, scanLen, seeds, lockPosCandidates);
                                    if (lockPosCandidates.Count > 0)
                                    {
                                        float best = float.MinValue;
                                        int lockBestPos = lk.pos;
                                        float bx = 0f, by = 0f, bz = 0f;
                                        foreach (var c in lockPosCandidates)
                                        {
                                            float s = 0f;
                                            if (c.po == lk.pos) s += 2f;
                                            if (c.po == preferredDirectPos) s += 1f;
                                            if (hasDirectCache)
                                            {
                                                double d = Math.Sqrt(Math.Pow(c.x - directCx, 2) + Math.Pow(c.y - directCy, 2));
                                                if (d <= 30f) s += 4f;
                                                else if (d <= 300f) s += 2f;
                                                else if (d > 3000f) s -= 3f;
                                            }
                                            if (directMotionByKey.TryGetValue((lk.mgr, lk.list, lk.obj, lk.link, c.po), out float mv))
                                                s += Math.Min(mv, 10f);
                                            if (s > best)
                                            {
                                                best = s;
                                                lockBestPos = c.po;
                                                bx = c.x; by = c.y; bz = c.z;
                                            }
                                        }

                                        x = bx; y = by; z = bz;
                                        preferredDirectMgr = lk.mgr;
                                        preferredDirectList = lk.list;
                                        preferredDirectObj = lk.obj;
                                        preferredDirectLink = lk.link;
                                        preferredDirectPos = lockBestPos;
                                        directCalibLock = (lk.mgr, lk.list, lk.obj, lk.link, lockBestPos);
                                        string lockTag = LinkCodeToTag(lk.link);
                                        source = $"direct(mgr=0x{lk.mgr:X},list=0x{lk.list:X2},obj=0x{lk.obj:X2},ln={lockTag},pos=0x{lockBestPos:X2},pref-lock-relaxed)";
                                        return true;
                                    }
                                }
                            }
                        }
                    }

                    // Final strict lock fallback: keep last locked position instead of hopping chains.
                    if (hasDirectCache)
                    {
                        x = directCx; y = directCy; z = directCz;
                        string lockTag = LinkCodeToTag(lk.link);
                        source = $"direct(mgr=0x{lk.mgr:X},list=0x{lk.list:X2},obj=0x{lk.obj:X2},ln={lockTag},pos=0x{lk.pos:X2},pref-lock-cache)";
                        return true;
                    }
                }

                var samples = new List<(int mo, int lo, int oo, int link, int po, float x, float y, float z, bool seeded)>();

                var mgrOrder = new List<int> { preferredDirectMgr };
                foreach (int mo in directMgrOffsets)
                    if (mo != preferredDirectMgr) mgrOrder.Add(mo);

                void CollectSamples(int[] mgrSet, int[] listSet, int[] objSet)
                {
                    foreach (int mo in mgrSet)
                    {
                        int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, mo));
                        if (mgr == 0) continue;

                        foreach (int lo in listSet)
                        {
                            int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, lo));
                            if (list == 0) continue;

                            foreach (int oo in objSet)
                            {
                                int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, oo));
                                if (raw == 0) continue;
                                var pobj = Ptr32(raw);

                                // Root object position candidates.
                                var rootPosCandidates = new List<(int po, float x, float y, float z, bool seeded)>();
                                CollectPosCandidatesFromAddress(hProcess, pobj, 0x240, directPosOffsets, rootPosCandidates);
                                foreach (var c in rootPosCandidates)
                                    samples.Add((mo, lo, oo, -1, c.po, c.x, c.y, c.z, c.seeded));

                                // One-level pointer-linked sub-objects; many maps keep moving
                                // world coords in a linked child object while root looks static.
                                foreach (int linkOff in directPtrOffsets)
                                {
                                    int subRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, linkOff));
                                    if (subRaw == 0 || subRaw == raw) continue;
                                    if (subRaw < 0x00100000) continue;

                                    var subObj = Ptr32(subRaw);
                                    var subPosCandidates = new List<(int po, float x, float y, float z, bool seeded)>();
                                    CollectPosCandidatesFromAddress(hProcess, subObj, 0x180, directSubPosOffsets, subPosCandidates);
                                    foreach (var c in subPosCandidates)
                                        samples.Add((mo, lo, oo, linkOff, c.po, c.x, c.y, c.z, c.seeded));

                                    // Second-level pointer walk: on some maps the live position
                                    // is at obj→sub→sub2 (e.g. mgr+0x9D4520[+0x0C]+0x4C>+0x0C>+0x4C).
                                    {
                                        foreach (int linkOff2 in directPtrOffsetsL2)
                                        {
                                            int subRaw2 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(subRaw, linkOff2));
                                            if (subRaw2 == 0 || subRaw2 == subRaw || subRaw2 == raw) continue;
                                            if (subRaw2 < 0x00100000) continue;

                                            var subObj2 = Ptr32(subRaw2);
                                            var sub2PosCandidates = new List<(int po, float x, float y, float z, bool seeded)>();
                                            CollectPosCandidatesFromAddress(hProcess, subObj2, 0x180, directSubPosOffsets, sub2PosCandidates);
                                            int linkCode = 0x10000 | ((linkOff & 0xFF) << 8) | (linkOff2 & 0xFF);
                                            foreach (var c in sub2PosCandidates)
                                                samples.Add((mo, lo, oo, linkCode, c.po, c.x, c.y, c.z, c.seeded));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Phase 1: strict player chain family only.
                int[] primaryMgr = { 0x9D4518, 0x9D4514, 0x9D4520 };
                int[] primaryList = { 0x08, 0x0C };
                int[] primaryObj = { 0x4C, 0x48 };
                CollectSamples(primaryMgr, primaryList, primaryObj);

                // Phase 2: broader fallback only if strict chain found nothing.
                if (samples.Count == 0)
                    CollectSamples(mgrOrder.ToArray(), directListOffsets, directObjOffsets);

                // Phase 3: walk linked list nodes (like NpcReader) when rejecting coordinates.
                // The player entity list may be a linked list where node+0x0C = next, node+0x4C = entity.
                if (!float.IsNaN(rejectX) && !float.IsNaN(rejectY))
                {
                    foreach (int mo in new[] { 0x9D4518, 0x9D4514 })
                    {
                        int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, mo));
                        if (mgr == 0) continue;
                        int container = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, 0x04));
                        if (container == 0) continue;
                        int firstNode = MemoryHelper.ReadInt32(hProcess, Ptr32Add(container, 0x04));
                        if (firstNode == 0) continue;

                        uint node = (uint)firstNode;
                        int safety = 0;
                        while (node != 0 && safety++ < 200)
                        {
                            int entRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x4C));
                            if (entRaw != 0 && entRaw > 0x00100000)
                            {
                                var entObj = Ptr32(entRaw);
                                var entCandidates = new List<(int po, float x, float y, float z, bool seeded)>();
                                CollectPosCandidatesFromAddress(hProcess, entObj, 0x240, directPosOffsets, entCandidates);
                                foreach (var c in entCandidates)
                                    samples.Add((mo, 0x08, 0x4C, -1, c.po, c.x, c.y, c.z, c.seeded));

                                foreach (int linkOff in directPtrOffsets)
                                {
                                    int subRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(entRaw, linkOff));
                                    if (subRaw == 0 || subRaw == entRaw || subRaw < 0x00100000) continue;
                                    var subCand = new List<(int po, float x, float y, float z, bool seeded)>();
                                    CollectPosCandidatesFromAddress(hProcess, Ptr32(subRaw), 0x180, directSubPosOffsets, subCand);
                                    foreach (var c in subCand)
                                        samples.Add((mo, 0x08, 0x4C, linkOff, c.po, c.x, c.y, c.z, c.seeded));
                                }
                            }
                            node = (uint)MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x0C));
                        }
                    }
                }

                if (samples.Count == 0) return false;

                bool preferredLooksStatic = false;
                bool anyAltMoved = false;
                foreach (var s in samples)
                {
                    if (avoidKnownStaticRoot &&
                        s.mo == 0x9D4518 && s.lo == 0x08 && s.oo == 0x4C &&
                        s.link == -1 && s.po == 0x94)
                        continue;

                    bool isPref = s.mo == preferredDirectMgr &&
                                  s.lo == preferredDirectList &&
                                  s.oo == preferredDirectObj &&
                                  s.link == preferredDirectLink &&
                                  s.po == preferredDirectPos;

                    if (isPref && hasDirectCache)
                    {
                        double d = Math.Sqrt(Math.Pow(s.x - directCx, 2) + Math.Pow(s.y - directCy, 2));
                        if (d < 0.01) preferredLooksStatic = true;
                    }

                    if (directLastByKey.TryGetValue((s.mo, s.lo, s.oo, s.link, s.po), out var last))
                    {
                        double dSelf = Math.Sqrt(Math.Pow(s.x - last.x, 2) + Math.Pow(s.y - last.y, 2));
                        if (isPref && dSelf < 0.01) preferredLooksStatic = true;
                        if (!isPref && dSelf > 0.2 && dSelf <= 3000.0) anyAltMoved = true;
                    }
                }

                if (preferredLooksStatic && anyAltMoved)
                    preferredDirectStaticReads++;
                else
                    preferredDirectStaticReads = 0;

                bool hasReject = !float.IsNaN(rejectX) && !float.IsNaN(rejectY);

                float bestScore = float.MinValue;
                int bestMgr = 0, bestList = 0, bestObj = 0, bestLink = -1, bestPos = 0;
                foreach (var s in samples)
                {
                    // Hard reject: skip candidates matching the known-wrong position.
                    if (hasReject &&
                        Math.Abs(s.x - rejectX) < 1f && Math.Abs(s.y - rejectY) < 1f)
                        continue;

                    bool isPref = s.mo == preferredDirectMgr &&
                                  s.lo == preferredDirectList &&
                                  s.oo == preferredDirectObj &&
                                  s.link == preferredDirectLink &&
                                  s.po == preferredDirectPos;
                    float score = 0f;
                    if (!hasReject)
                    {
                        if (s.mo == preferredDirectMgr) score += 2f;
                        if (s.lo == preferredDirectList) score += 1f;
                        if (s.oo == preferredDirectObj) score += 1f;
                        if (s.link == preferredDirectLink) score += 1f;
                        if (s.po == preferredDirectPos) score += 2f;
                    }
                    if (s.lo == 0x08) score += 1f;
                    if (s.oo == 0x4C) score += 1f;
                    if (s.oo == 0x48) score += 0.7f;
                    if (s.link == -1 && s.po == 0x94) score += 0.5f;
                    if (s.link != -1) score += 0.5f;
                    if (!s.seeded) score -= 0.35f;
                    if (avoidKnownStaticRoot && s.link != -1) score += 1.5f;
                    if (avoidKnownStaticRoot && s.link == -1 && s.po == 0x94) score -= 3f;

                    if (hasLastNpcCloudCenter)
                    {
                        double dNpc = Math.Sqrt(Math.Pow(s.x - lastNpcCloudCenterX, 2) + Math.Pow(s.y - lastNpcCloudCenterY, 2));
                        if (dNpc <= 3000.0) score += 3f;
                        else if (dNpc <= 8000.0) score += 1f;
                        else if (dNpc > 15000.0) score -= 5f;
                    }

                    if (hasDirectCache && !hasReject)
                    {
                        double d = Math.Sqrt(Math.Pow(s.x - directCx, 2) + Math.Pow(s.y - directCy, 2));
                        if (d <= 20f) score += 4f;
                        else if (d <= 300f) score += 2f;
                        else if (d <= 3000f) score -= 1f;
                        else score -= 4f;
                    }

                    if (directLastByKey.TryGetValue((s.mo, s.lo, s.oo, s.link, s.po), out var last))
                    {
                        double dSelf = Math.Sqrt(Math.Pow(s.x - last.x, 2) + Math.Pow(s.y - last.y, 2));
                        if (dSelf > 0.2 && dSelf <= 3000f) score += 3.5f;
                        else if (dSelf < 0.01) score -= 1f;
                    }
                    if (directMotionByKey.TryGetValue((s.mo, s.lo, s.oo, s.link, s.po), out float mv))
                        score += Math.Min(mv, 10f);

                    if (isPref && preferredDirectStaticReads >= 2)
                        score -= 10f;
                    if (!isPref && preferredDirectStaticReads >= 2)
                        score += 2f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        x = s.x; y = s.y; z = s.z;
                        bestMgr = s.mo; bestList = s.lo; bestObj = s.oo; bestLink = s.link; bestPos = s.po;
                    }
                }

                foreach (var s in samples)
                {
                    var k = (s.mo, s.lo, s.oo, s.link, s.po);
                    if (directLastByKey.TryGetValue(k, out var last))
                    {
                        float old = directMotionByKey.TryGetValue(k, out float om) ? om : 0f;
                        double dSelf = Math.Sqrt(Math.Pow(s.x - last.x, 2) + Math.Pow(s.y - last.y, 2));
                        float impulse = dSelf > 0.05 ? (float)Math.Min(dSelf, 25.0) : 0f;
                        directMotionByKey[k] = old * 0.85f + impulse;
                    }
                    else
                    {
                        if (!directMotionByKey.ContainsKey(k))
                            directMotionByKey[k] = 0f;
                    }
                    directLastByKey[k] = (s.x, s.y);
                }

                preferredDirectMgr = bestMgr;
                preferredDirectList = bestList;
                preferredDirectObj = bestObj;
                preferredDirectLink = bestLink;
                preferredDirectPos = bestPos;
                string linkTag = LinkCodeToTag(bestLink);
                source = $"direct(mgr=0x{bestMgr:X},list=0x{bestList:X2},obj=0x{bestObj:X2},ln={linkTag},pos=0x{bestPos:X2},st={preferredDirectStaticReads},cand={samples.Count},lock={(directCalibLock.HasValue ? 1 : 0)})";
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
            // Search for zxxy.dll: check if already loaded, then try multiple paths.
            bool zxxyAlreadyLoaded = TryGetGameModule("zxxy.dll", out _, out _);
            if (zxxyAlreadyLoaded)
            {
                TryRefreshZxxyModule(force: true);
                Console.WriteLine("[*] zxxy.dll already loaded in game process (e.g. by xajhtoy).");
            }
            else
            {
                // Search multiple locations for zxxy.dll
                var zxxySearchPaths = new List<string>();
                string appBase = AppDomain.CurrentDomain.BaseDirectory;
                zxxySearchPaths.Add(Path.Combine(appBase, "zxxy.dll"));
                try
                {
                    // Game directory (where vrchat1.exe lives)
                    game.Refresh();
                    string gameDir = Path.GetDirectoryName(game.MainModule.FileName);
                    if (!string.IsNullOrEmpty(gameDir))
                    {
                        zxxySearchPaths.Add(Path.Combine(gameDir, "zxxy.dll"));
                        // Check parent and sibling directories
                        string parentDir = Path.GetDirectoryName(gameDir);
                        if (!string.IsNullOrEmpty(parentDir))
                        {
                            zxxySearchPaths.Add(Path.Combine(parentDir, "zxxy.dll"));
                            foreach (var sub in Directory.GetDirectories(parentDir))
                                zxxySearchPaths.Add(Path.Combine(sub, "zxxy.dll"));
                        }
                    }
                }
                catch { }
                // Parent of our own directory
                try
                {
                    string appParent = Path.GetDirectoryName(appBase.TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(appParent))
                    {
                        zxxySearchPaths.Add(Path.Combine(appParent, "zxxy.dll"));
                        foreach (var sub in Directory.GetDirectories(appParent))
                            zxxySearchPaths.Add(Path.Combine(sub, "zxxy.dll"));
                    }
                }
                catch { }

                string zxxyPath = null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in zxxySearchPaths)
                {
                    if (!seen.Add(candidate)) continue;
                    if (File.Exists(candidate))
                    {
                        zxxyPath = candidate;
                        break;
                    }
                }

                if (zxxyPath != null)
                {
                    Console.WriteLine($"[*] Found zxxy.dll at: {zxxyPath}");
                    if (!DllInjector.Inject(hProcess, zxxyPath))
                    {
                        Console.WriteLine("[*] Optional zxxy.dll injection failed; continuing with existing resolvers.");
                    }
                    else
                    {
                        TryRefreshZxxyModule(force: true);
                        Console.WriteLine("[*] zxxy.dll injected for map-specific position fallback.");
                    }
                }
                else
                {
                    Console.WriteLine("[*] zxxy.dll not found. Searched:");
                    foreach (var sp in seen)
                        Console.WriteLine($"      {sp}");
                    Console.WriteLine("    Place zxxy.dll next to xajh.exe or in the game directory for correct position on all maps.");
                }
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
            var locCmd = new LocCommand(hProcess, moduleBase, GetGameHwnd);
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
                        locCmd = new LocCommand(hProcess, moduleBase, GetGameHwnd);
                        TryRefreshZxxyModule(force: true);
                        zxxyDirectCandidates.Clear();
                        zxxyDirectLockedAddr = 0;
                        simpleStaticReads = 0;
                        simpleLastX = float.NaN; simpleLastY = float.NaN;

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
                    locCmd = new LocCommand(hProcess, moduleBase, GetGameHwnd);
                    TryRefreshZxxyModule(force: true);
                    zxxyDirectCandidates.Clear();
                    zxxyDirectLockedAddr = 0;
                    simpleStaticReads = 0;
                    simpleLastX = float.NaN; simpleLastY = float.NaN;

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
            Console.WriteLine("[R] Set radius  [L] List nearby NPCs  [M] Calibrate fallback  [P] Dump player obj");
            Console.WriteLine("[D] Deep scan  [F] Focused dump  [G] /loc  [W] Refresh window  [End] Exit");
            Console.WriteLine();

            bool autoFace = false;
            float aimRadius = 300f;
            bool lastPosReliable = false;  // updated by ReadPlayerPos; accessible by AimNearest/GetNearbyNpcs
            Console.WriteLine($"[*] Aim radius: {aimRadius:F0}");
            string lastDirectSource = "";

            void UpdateNpcCloudCenter()
            {
                var npcs = GetTrackedNpcs();
                if (npcs.Count == 0) return;
                double sx = 0d, sy = 0d;
                int count = 0;
                foreach (var n in npcs)
                {
                    if (float.IsNaN(n.X) || float.IsNaN(n.Y) || float.IsInfinity(n.X) || float.IsInfinity(n.Y))
                        continue;
                    sx += n.X;
                    sy += n.Y;
                    count++;
                }
                if (count <= 0) return;
                lastNpcCloudCenterX = sx / count;
                lastNpcCloudCenterY = sy / count;
                hasLastNpcCloudCenter = true;
            }

            bool IsDirectFallbackLikelyWrong(float px, float py)
            {
                if (!hasLastNpcCloudCenter) return false;
                double d = Math.Sqrt(Math.Pow(px - lastNpcCloudCenterX, 2) + Math.Pow(py - lastNpcCloudCenterY, 2));
                // If player estimate is implausibly far from current NPC cloud center, it is usually a stale map anchor.
                return d > 15000.0;
            }

            bool IsUnresolvedSource(string src)
                => src == "none" || src == "mgr=0" || src == "list/obj=0" || src == "obj-ex";

            bool IsPlausibleWorldPos(float x, float y)
            {
                if (float.IsNaN(x) || float.IsNaN(y)) return false;
                if (Math.Abs(x) < 1f || Math.Abs(y) < 1f) return false;
                if (IsDirectFallbackLikelyWrong(x, y)) return false;
                return true;
            }

            // Stricter plausibility check for fallback phases: in addition to
            // the basic IsPlausibleWorldPos, reject candidates with both X and Y
            // near zero — real world positions have significant coordinate values
            // (|X| > 10 AND |Y| > 10), while garbage like (1,1,255) or (2,2,0)
            // from 255-filled entities has near-zero world coords.
            // Coordinate convention used throughout: ReadPlayerPos returns (x, y, z) where
            //   x = game X  (ground plane, typically 200–30000)
            //   y = game Z  (ground plane, typically 200–30000)  ← the tuple's "y" IS game Z
            //   z = game Y  (height, typically -100–+200)        ← the tuple's "z" IS game Y
            // Memory at confirmed position offsets stores (X, Z_ground, Y_height) consecutively.
            // TryReadStablePos reads them raw; TryReadSubPtrPos explicitly remaps the sub+0x020 layout.
            bool IsStrictPlausiblePos(float x, float y)
            {
                if (!IsPlausibleWorldPos(x, y)) return false;
                // Real positions on this server are in the 1000–20000 range.
                // Raise the floor well above the garbage values seen in phase3/4
                // (e.g. 43.5, 0.0, 2832) that previously slipped through.
                if (Math.Abs(x) < 200f || Math.Abs(y) < 200f) return false;
                return true;
            }

            bool TryReadPlayerDirect(out float x, out float y, out float z, out string source)
            {
                return TryReadDirectPlayerPos(hProcess, moduleBase, out x, out y, out z, out source);
            }

            bool TryReadPlayerDirectRejectCoords(float rjX, float rjY, out float x, out float y, out float z, out string source)
            {
                return TryReadDirectPlayerPos(hProcess, moduleBase, out x, out y, out z, out source,
                    avoidKnownStaticRoot: true, rejectX: rjX, rejectY: rjY);
            }

            (float x, float y, float z) ReadPlayerPos()
            {
                var result = ReadPlayerPosInner();
                lastPosReliable = IsStrictPlausiblePos(result.x, result.y)
                    && !IsDirectFallbackLikelyWrong(result.x, result.y)
                    && !string.IsNullOrEmpty(lastDirectSource)
                    && !lastDirectSource.StartsWith("cache-last")
                    && !lastDirectSource.StartsWith("simple-static");
                return result;
            }

            (float x, float y, float z) ReadPlayerPosInner()
            {
                UpdateNpcCloudCenter();

                // Auto-reattach if simple chain base (0x9D4518) is null — process handle stale
                {
                    int mgrCheck = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                    if (mgrCheck == 0)
                    {
                        long nowMs = Environment.TickCount64;
                        if (nowMs - lastReattachMs > 5000)
                        {
                            lastReattachMs = nowMs;
                            TryReattachGame("mgr=0x0", forceRefreshSameProcess: true);
                        }
                    }
                }

                // --- Phase 0: /loc command result (authoritative server-side position) ---
                if (locCmd.HasLoc && locCmd.LocAge < 10000 &&
                    IsPlausibleWorldPos(locCmd.LocX, locCmd.LocY))
                {
                    directCx = locCmd.LocX;
                    directCy = locCmd.LocY;
                    directCz = locCmd.LocZ;
                    hasDirectCache = true;
                    lastDirectSource = $"loc(age={locCmd.LocAge}ms)";
                    return (locCmd.LocX, locCmd.LocY, locCmd.LocZ);
                }

                // --- Phase 0.1: Chain D (game+0x9CA8A0) — highest priority confirmed chain ---
                // Runs every call regardless of simpleStatic. Direct pointer to player object.
                // Confirmed by [F] dump: +0x034=X, +0x038=Y. No NPC cloud needed.
                // Phase 0.1: Chain D (game+0x9CA8A0) — highest priority confirmed chain.
                // Runs every call regardless of simpleStatic.
                // If the value is moving → return immediately (best source).
                // If frozen (player still or snapshot) → record but let later phases compete;
                //   use as final fallback at end if everything else fails.
                {
                    bool chainDLive = false;
                    int mD = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9CA8A0));
                    if (mD != 0 && mD >= 0x00100000)
                    {
                        float d4x = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x034));
                        float d4y = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x038));
                        float d4z = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x03C));
                        if (!IsStrictPlausiblePos(d4x, d4y))
                        {
                            d4x = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x094));
                            d4y = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x098));
                            d4z = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mD, 0x09C));
                        }
                        if (IsStrictPlausiblePos(d4x, d4y))
                        {
                            bool moved = !hasChainD
                                || Math.Abs(d4x - chainDx) >= 0.5f
                                || Math.Abs(d4y - chainDy) >= 0.5f;
                            chainDFrozenCount = moved ? 0 : chainDFrozenCount + 1;
                            chainDx = d4x; chainDy = d4y; chainDz = d4z;
                            hasChainD = true; chainDLive = true;
                            turn.SetPlayerObjectHint(mD);

                            if (moved || chainDFrozenCount <= 3)
                            {
                                directCx = d4x; directCy = d4y; directCz = d4z; hasDirectCache = true;
                                lastDirectSource = $"xajh-D(obj=0x{mD:X8},f={chainDFrozenCount})";
                                return (d4x, d4y, d4z);
                            }
                            // Frozen for >3 reads — fall through to other phases
                        }
                    }
                    else if (hasChainD)
                    {
                        // Pointer temporarily null — keep hasChainD so last-resort can use it
                        chainDFrozenCount++;
                    }
                }

                // --- Phase 0.5: sub-object at playerObj+0xBC (confirmed by dump, ~10u accuracy) ---
                // Probe both list offsets (+0x08 and +0x0C) and both obj offsets (+0x4C and +0x50).
                // Deep scan confirmed the LIVE player object is on list+0x0C/obj+0x50, while
                // list+0x08/obj+0x4C can be stale when the simple chain freezes.
                {
                    int sub05Mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                    if (sub05Mgr != 0)
                    {
                        // Probe order: try +0x0C first (confirmed live), then +0x08 (standard)
                        int[] sub05ListOffs = { 0x0C, 0x08 };
                        int[] sub05ObjOffs = { 0x50, 0x4C };
                        foreach (int sub05Lo in sub05ListOffs)
                        {
                            int sub05List = MemoryHelper.ReadInt32(hProcess, Ptr32Add(sub05Mgr, sub05Lo));
                            if (sub05List == 0 || sub05List < 0x00100000) continue;
                            foreach (int sub05Oo in sub05ObjOffs)
                            {
                                int sub05ObjRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(sub05List, sub05Oo));
                                if (sub05ObjRaw == 0 || sub05ObjRaw < 0x00100000) continue;
                                var sub05Obj = Ptr32(sub05ObjRaw);
                                if (TryReadSubPtrPos(sub05Obj, out float spx, out float spy, out float spz) &&
                                    IsPlausibleWorldPos(spx, spy) &&
                                    !IsDirectFallbackLikelyWrong(spx, spy))
                                {
                                    directCx = spx; directCy = spy; directCz = spz; hasDirectCache = true;
                                    lastDirectSource = $"sub-0xBC(list=0x{sub05Lo:X2},obj=0x{sub05Oo:X2},ptr=0x{sub05ObjRaw:X8})";
                                    return (spx, spy, spz);
                                }
                            }
                        }
                    }
                }

                // --- Pre-evaluate simple chain for reference position & static detection ---
                // Knowing whether simple is static informs all subsequent phases.
                var p = playerReader.Get();
                var dbg = playerReader.GetDebugSnapshot();

                bool simpleResolved = !IsUnresolvedSource(dbg.Source);
                bool simplePlausible = simpleResolved && IsPlausibleWorldPos(p.x, p.y);

                // Track frozen state. Use dbg.SimpleChainStaticReads which tracks
                // the actual output of TryReadSimpleChainPos (won't be fooled by
                // the value changing between calls due to candidate switching).
                if (simpleResolved)
                {
                    if (float.IsNaN(simpleLastX))
                    {
                        simpleLastX = p.x; simpleLastY = p.y;
                    }
                    bool unchanged = Math.Abs(p.x - simpleLastX) < 0.01f &&
                        Math.Abs(p.y - simpleLastY) < 0.01f;
                    if (unchanged)
                        simpleStaticReads++;
                    else
                    {
                        simpleStaticReads = 0;
                        simpleLastX = p.x; simpleLastY = p.y;
                    }
                }

                bool simpleStatic = simpleStaticReads >= 2 || dbg.SimpleChainStaticReads >= 2;

                // If simple chain is resolved, plausible, moving, and not implausibly far
                // from NPC cloud, trust it immediately.
                if (simplePlausible && !simpleStatic && !IsDirectFallbackLikelyWrong(p.x, p.y))
                {
                    directCx = p.x;
                    directCy = p.y;
                    directCz = p.z;
                    hasDirectCache = true;
                    lastDirectSource = "";
                    return p;
                }

                // Note: do NOT seed directCache from frozen simple chain — the spawn
                // position can be thousands of units from the real position.

                // --- Phase 0.6: XajhSmileDll.dll confirmed chains (reverse-engineered) ---
                // These three chains were extracted from XajhSmileDll.dll (Cloud_xajhfuzhu's
                // injected DLL). It reads player position via direct pointer access inside the
                // game process with no VMProtect.
                //
                // Chain A: game+0x9D4514 is a DIRECT pointer to playerObj  → pos at +0x94/+0x98/+0x9C
                // Chain B: game+0x978AE0 → [+0x58] → [+0x0C] → pos at +0x94
                // Chain C: game+0x9DD6C4 → [+0x0C] → pos at +0x94
                //
                // Memory layout at pos offsets: +0x94 = X_ground, +0x98 = Z_height, +0x9C = Y_ground
                {
                    bool p6ok = false;
                    int p6Obj = 0;
                    float p6x = 0, p6y = 0, p6z = 0;
                    string p6src = "";

                    // Chain A: direct playerObj pointer at game+0x9D4514
                    // Scan all known position offsets — game version may have shifted from +0x94
                    {
                        int pObjA = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4514));
                        if (pObjA != 0 && pObjA >= 0x00100000)
                        {
                            foreach (int pOff in directPosOffsets)
                            {
                                float ax = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, pOff));
                                float ay = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, pOff + 8));  // +8 = Y in x,z,y layout
                                if (!IsStrictPlausiblePos(ax, ay) || IsDirectFallbackLikelyWrong(ax, ay)) continue;
                                float az = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, pOff + 4));
                                p6ok = true; p6Obj = pObjA; p6x = ax; p6y = ay; p6z = az;
                                p6src = $"xajh-A(obj=0x{pObjA:X8},po=0x{pOff:X2})";
                                break;
                            }
                        }
                    }

                    // Chain B: game+0x978AE0 → [+0x58] → [+0x0C] → pos
                    if (!p6ok)
                    {
                        int mB = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x978AE0));
                        if (mB != 0 && mB >= 0x00100000)
                        {
                            int lB = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mB, 0x58));
                            if (lB != 0 && lB != mB && lB >= 0x00100000)
                            {
                                int pObjB = MemoryHelper.ReadInt32(hProcess, Ptr32Add(lB, 0x0C));
                                if (pObjB != 0 && pObjB != lB && pObjB >= 0x00100000)
                                {
                                    foreach (int pOff in directPosOffsets)
                                    {
                                        float bx = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, pOff));
                                        float by = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, pOff + 8));
                                        if (!IsStrictPlausiblePos(bx, by) || IsDirectFallbackLikelyWrong(bx, by)) continue;
                                        float bz = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, pOff + 4));
                                        p6ok = true; p6Obj = pObjB; p6x = bx; p6y = by; p6z = bz;
                                        p6src = $"xajh-B(obj=0x{pObjB:X8},po=0x{pOff:X2})";
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Chain C: game+0x9DD6C4 → [+0x0C] → pos
                    if (!p6ok)
                    {
                        int mC = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9DD6C4));
                        if (mC != 0 && mC >= 0x00100000)
                        {
                            int pObjC = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mC, 0x0C));
                            if (pObjC != 0 && pObjC != mC && pObjC >= 0x00100000)
                            {
                                foreach (int pOff in directPosOffsets)
                                {
                                    float cx = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, pOff));
                                    float cy = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, pOff + 8));
                                    if (!IsStrictPlausiblePos(cx, cy) || IsDirectFallbackLikelyWrong(cx, cy)) continue;
                                    float cz = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, pOff + 4));
                                    p6ok = true; p6Obj = pObjC; p6x = cx; p6y = cy; p6z = cz;
                                    p6src = $"xajh-C(obj=0x{pObjC:X8},po=0x{pOff:X2})";
                                    break;
                                }
                            }
                        }
                    }

                    if (p6ok)
                    {
                        turn.SetPlayerObjectHint(p6Obj);
                        directCx = p6x; directCy = p6y; directCz = p6z; hasDirectCache = true;
                        lastDirectSource = p6src;
                        return (p6x, p6y, p6z);
                    }
                }

                // --- Phase 0.7: motion-detection + NPC-cloud brute-force object search ---
                // Skip Phase 0.7 entirely if Chain D has a frozen-but-valid value.
                // Phase 0.7's NPC anchor can pick wrong objects; frozen Chain D is safer.
                if (hasChainD && chainDFrozenCount > 3 && IsStrictPlausiblePos(chainDx, chainDy))
                {
                    directCx = chainDx; directCy = chainDy; directCz = chainDz; hasDirectCache = true;
                    lastDirectSource = $"xajh-D-frozen({chainDx:F0},{chainDy:F0},f={chainDFrozenCount})";
                    return (chainDx, chainDy, chainDz);
                }
                // Two-stage: collect all plausible candidates (excluding the known-frozen simple
                // chain output), wait 180ms, return whichever moved most.  If nothing moved
                // (player standing still), fall back to NPC-cloud proximity — but only if the
                // NPC cloud itself is far from the frozen position (otherwise it is also stale).
                if (simpleStatic)
                {
                    var snap07 = new List<(IntPtr obj07, int po, bool isSub, float x, float y, float z, string tag)>();
                    int[] ptrHops07 = { 0x04, 0x08, 0x0C, 0x10, 0x40, 0x44, 0x48, 0x50, 0x58, 0x70, 0x90, 0xA0, 0xB8, 0xBC };

                    foreach (int mo in directMgrOffsets)
                    {
                        int mgr07 = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, mo));
                        if (mgr07 == 0) continue;
                        foreach (int lo in directListOffsets)
                        {
                            int list07 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr07, lo));
                            if (list07 == 0 || list07 < 0x00100000) continue;
                            foreach (int oo in directObjOffsets)
                            {
                                int raw07 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list07, oo));
                                if (raw07 == 0 || raw07 < 0x00100000) continue;
                                var obj07 = Ptr32(raw07);

                                // Direct pos offsets
                                foreach (int po in directPosOffsets)
                                {
                                    if (!TryReadStablePos(hProcess, obj07, po, out float ex, out float ey, out float ez)) continue;
                                    if (!IsStrictPlausiblePos(ex, ey)) continue;
                                    // Exclude the known-frozen simple chain output
                                    if (simpleResolved && Math.Abs(ex - p.x) < 50f && Math.Abs(ey - p.y) < 50f) continue;
                                    snap07.Add((obj07, po, false, ex, ey, ez, $"mo=0x{mo:X},lo=0x{lo:X2},oo=0x{oo:X2},po=0x{po:X2}"));
                                }

                                // Sub-0xBC
                                if (TryReadSubPtrPos(obj07, out float spx, out float spy, out float spz) &&
                                    IsStrictPlausiblePos(spx, spy) &&
                                    !(simpleResolved && Math.Abs(spx - p.x) < 50f && Math.Abs(spy - p.y) < 50f))
                                {
                                    snap07.Add((obj07, -1, true, spx, spy, spz, $"mo=0x{mo:X},lo=0x{lo:X2},oo=0x{oo:X2},sub0xBC"));
                                }

                                // One-hop sub-objects
                                foreach (int ptrOff in ptrHops07)
                                {
                                    int subRaw07 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw07, ptrOff));
                                    if (subRaw07 == 0 || subRaw07 == raw07 || subRaw07 < 0x00100000) continue;
                                    var sub07 = Ptr32(subRaw07);
                                    foreach (int po in directSubPosOffsets)
                                    {
                                        if (!TryReadStablePos(hProcess, sub07, po, out float ex, out float ey, out float ez)) continue;
                                        if (!IsStrictPlausiblePos(ex, ey)) continue;
                                        if (simpleResolved && Math.Abs(ex - p.x) < 50f && Math.Abs(ey - p.y) < 50f) continue;
                                        snap07.Add((sub07, po, false, ex, ey, ez, $"mo=0x{mo:X},lo=0x{lo:X2},oo=0x{oo:X2},ptr=0x{ptrOff:X2},po=0x{po:X2}"));
                                    }

                                    // Two-hop: follow a second pointer from the sub-object.
                                    // Required to reach deep chains like +0x24>+0x70>+0x28 at +0x384.
                                    foreach (int ptrOff2 in new[] { 0x04, 0x08, 0x0C, 0x10, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68, 0x70, 0x80, 0x90, 0xA0, 0xB4, 0xB8, 0xBC, 0xC0 })
                                    {
                                        int subRaw072 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(subRaw07, ptrOff2));
                                        if (subRaw072 == 0 || subRaw072 == subRaw07 || subRaw072 == raw07 || subRaw072 < 0x00100000) continue;
                                        var sub072 = Ptr32(subRaw072);
                                        foreach (int po in directSubPosOffsets)
                                        {
                                            if (!TryReadStablePos(hProcess, sub072, po, out float ex, out float ey, out float ez)) continue;
                                            if (!IsStrictPlausiblePos(ex, ey)) continue;
                                            if (simpleResolved && Math.Abs(ex - p.x) < 50f && Math.Abs(ey - p.y) < 50f) continue;
                                            snap07.Add((sub072, po, false, ex, ey, ez, $"mo=0x{mo:X},lo=0x{lo:X2},oo=0x{oo:X2},ptr=0x{ptrOff:X2}>0x{ptrOff2:X2},po=0x{po:X2}"));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (snap07.Count > 0)
                    {
                        // Stage 1: motion detection — wait for player movement
                        Thread.Sleep(180);

                        float bestMotion = 0.4f;  // min threshold: must move > 0.4 units
                        float motX = 0, motY = 0, motZ = 0;
                        string motSrc = "";

                        bool hasLocRef = locCmd.HasLoc && locCmd.LocAge < 15000 &&
                            IsPlausibleWorldPos(locCmd.LocX, locCmd.LocY);
                        // /loc is stale if it matches the frozen simple-chain position —
                        // ForceRead fired but the game returned old-zone data.
                        bool locMatchesFrozen = hasLocRef && simpleResolved &&
                            Math.Abs(locCmd.LocX - p.x) < 100f &&
                            Math.Abs(locCmd.LocY - p.y) < 100f;
                        bool locUsable = hasLocRef && !locMatchesFrozen;

                        foreach (var s in snap07)
                        {
                            float nx, ny, nz;
                            if (s.isSub)
                            {
                                if (!TryReadSubPtrPos(s.obj07, out nx, out ny, out nz)) continue;
                            }
                            else
                            {
                                if (!TryReadStablePos(hProcess, s.obj07, s.po, out nx, out ny, out nz)) continue;
                            }
                            if (!IsStrictPlausiblePos(nx, ny)) continue;
                            float mv = (float)Math.Sqrt(Math.Pow(nx - s.x, 2) + Math.Pow(ny - s.y, 2));
                            if (mv > bestMotion)
                            {
                                // Cross-validate against /loc only when it is NOT stale zone data.
                                if (locUsable)
                                {
                                    double dLoc = Math.Sqrt(Math.Pow(nx - locCmd.LocX, 2) + Math.Pow(ny - locCmd.LocY, 2));
                                    if (dLoc > 2000.0) continue;
                                }
                                bestMotion = mv;
                                motX = nx; motY = ny; motZ = nz;
                                motSrc = $"motion({s.tag},mv={mv:F1})";
                            }
                        }

                        if (!string.IsNullOrEmpty(motSrc))
                        {
                            directCx = motX; directCy = motY; directCz = motZ; hasDirectCache = true;
                            lastDirectSource = motSrc;
                            return (motX, motY, motZ);
                        }

                        // Stage 2: player standing still.
                        // Reference priority:
                        //   1. /loc — only if NOT matching the frozen zone (stale /loc is worse than nothing)
                        //   2. directCache — if it's far from the frozen position (previous correct read)
                        //   3. NPC cloud — if far from frozen
                        //   4. No reference: pick snap07 candidate farthest from frozen (best-effort)
                        float refX07 = float.NaN, refY07 = float.NaN;
                        string refSrc07 = "";

                        if (locUsable)
                        {
                            refX07 = locCmd.LocX; refY07 = locCmd.LocY;
                            refSrc07 = $"loc(age={locCmd.LocAge}ms)";
                        }
                        else if (hasDirectCache && IsStrictPlausiblePos(directCx, directCy))
                        {
                            double cacheDistFromFrozen = Math.Sqrt(Math.Pow(directCx - p.x, 2) + Math.Pow(directCy - p.y, 2));
                            if (cacheDistFromFrozen > 500.0)
                            {
                                refX07 = directCx; refY07 = directCy;
                                refSrc07 = $"cache({directCx:F0},{directCy:F0})";
                            }
                        }

                        if (float.IsNaN(refX07) && hasLastNpcCloudCenter)
                        {
                            float ncx = (float)lastNpcCloudCenterX;
                            float ncy = (float)lastNpcCloudCenterY;
                            float frozenDistToCloud = (float)Math.Sqrt(Math.Pow(p.x - ncx, 2) + Math.Pow(p.y - ncy, 2));
                            if (frozenDistToCloud > 500f)
                            {
                                refX07 = ncx; refY07 = ncy;
                                refSrc07 = "npc-cloud";
                            }
                        }

                        if (!float.IsNaN(refX07))
                        {
                            float bestRefDist = 300f;  // tight: real player is within 300u of NPC cloud
                            float bestRefX = 0, bestRefY = 0, bestRefZ = 0;
                            string bestRefSrc = "";
                            foreach (var s in snap07)
                            {
                                float d = (float)Math.Sqrt(Math.Pow(s.x - refX07, 2) + Math.Pow(s.y - refY07, 2));
                                if (d < bestRefDist)
                                {
                                    bestRefDist = d;
                                    bestRefX = s.x; bestRefY = s.y; bestRefZ = s.z;
                                    bestRefSrc = $"anchor({refSrc07},{s.tag},d={d:F0})";
                                }
                            }
                            if (!string.IsNullOrEmpty(bestRefSrc))
                            {
                                directCx = bestRefX; directCy = bestRefY; directCz = bestRefZ; hasDirectCache = true;
                                lastDirectSource = bestRefSrc;
                                return (bestRefX, bestRefY, bestRefZ);
                            }
                        }

                        // No reference available at all — pick the snap07 candidate farthest from
                        // the frozen position.  All candidates in snap07 already passed
                        // IsStrictPlausiblePos and the frozen-exclusion filter, so the farthest one
                        // is the least likely to be a stale anchor object.
                        if (snap07.Count > 0)
                        {
                            float bestFrozenDist = 500f;  // must be at least 500u from frozen
                            float bfX = 0, bfY = 0, bfZ = 0;
                            string bfSrc = "";
                            foreach (var s in snap07)
                            {
                                float d = (float)Math.Sqrt(Math.Pow(s.x - p.x, 2) + Math.Pow(s.y - p.y, 2));
                                if (d > bestFrozenDist)
                                {
                                    bestFrozenDist = d;
                                    bfX = s.x; bfY = s.y; bfZ = s.z;
                                    bfSrc = $"farthest-from-frozen({s.tag},d={d:F0})";
                                }
                            }
                            if (!string.IsNullOrEmpty(bfSrc))
                            {
                                directCx = bfX; directCy = bfY; directCz = bfZ; hasDirectCache = true;
                                lastDirectSource = bfSrc;
                                return (bfX, bfY, bfZ);
                            }
                        }
                    }
                }

                // --- Phase 1: zxxy.dll direct float scan (authoritative, like xajhtoy.exe) ---
                if (TryReadPlayerPosViaZxxyDirect(out float zdx, out float zdy, out float zdz, out string zdsrc) &&
                    IsPlausibleWorldPos(zdx, zdy))
                {
                    lastDirectSource = zdsrc;
                    directCx = zdx; directCy = zdy; directCz = zdz; hasDirectCache = true;
                    return (zdx, zdy, zdz);
                }

                // --- Phase 1b: zxxy-direct proximity fallback ---
                // When motion detection fails (all candidates motion=0), pick the candidate
                // closest to a known reference (simple chain or NPC cloud center).
                if (zxxyDirectLockedAddr == 0 && zxxyDirectCandidates.Count > 0)
                {
                    float refX = float.NaN, refY = float.NaN;
                    if (simplePlausible)
                    {
                        refX = p.x; refY = p.y;
                    }
                    else if (hasLastNpcCloudCenter)
                    {
                        refX = (float)lastNpcCloudCenterX;
                        refY = (float)lastNpcCloudCenterY;
                    }
                    else if (hasDirectCache)
                    {
                        refX = directCx; refY = directCy;
                    }

                    if (!float.IsNaN(refX))
                    {
                        float bestProxScore = float.MaxValue;
                        long bestProxAddr = 0;
                        float bestProxX = 0f, bestProxY = 0f, bestProxZ = 0f;
                        foreach (var c in zxxyDirectCandidates)
                        {
                            double d = Math.Sqrt(Math.Pow(c.lastX - refX, 2) + Math.Pow(c.lastY - refY, 2));
                            if (d > 500.0) continue;
                            if (d < bestProxScore)
                            {
                                bestProxScore = (float)d;
                                bestProxAddr = c.addr;
                                bestProxX = c.lastX; bestProxY = c.lastY; bestProxZ = c.lastZ;
                            }
                        }

                        if (bestProxAddr != 0 && IsPlausibleWorldPos(bestProxX, bestProxY))
                        {
                            zxxyDirectLockedAddr = bestProxAddr;
                            zxxyDirectLockedLastX = bestProxX;
                            zxxyDirectLockedLastY = bestProxY;
                            zxxyDirectStaleReads = 0;
                            lastDirectSource = $"zxxy-direct-prox(addr=0x{bestProxAddr:X8},dist={bestProxScore:F0},ref=simple)";
                            directCx = bestProxX; directCy = bestProxY; directCz = bestProxZ; hasDirectCache = true;
                            return (bestProxX, bestProxY, bestProxZ);
                        }
                    }
                }

                // --- Phase 2: zxxy.dll pointer chain scan (existing approach) ---
                if (TryReadPlayerPosViaZxxy(out float zx, out float zy, out float zz, out string zxsrc) &&
                    IsStrictPlausiblePos(zx, zy))
                {
                    directCx = zx; directCy = zy; directCz = zz; hasDirectCache = true;
                    lastDirectSource = zxsrc;
                    return (zx, zy, zz);
                }

                // --- Phase 2b: probe zxxy chain entities at expanded offsets ---
                // On some maps, zxxy chain entities store positions at offsets like
                // +0x4C, +0x7C, +0x84, +0x8C, +0x94 that the chain scanner misses
                // because the entity's standard chain resolution fails.
                if (zxxyModuleBase != IntPtr.Zero && zxxyMgrCandidates.Count > 0 &&
                    (simpleStatic || !simplePlausible))
                {
                    float bestZxxyEntityScore = float.MinValue;
                    float bestZxxyEntityX = 0f, bestZxxyEntityY = 0f, bestZxxyEntityZ = 0f;
                    string bestZxxyEntitySrc = "";
                    int[] expandedPosOffsets = { 0x94, 0x34, 0x64, 0xC4, 0x4C, 0x7C, 0x84, 0x8C, 0x60, 0xA4 };

                    foreach (var c in zxxyMgrCandidates)
                    {
                        int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(zxxyModuleBase, c.mgrOff));
                        if (mgr < 0x00100000) continue;
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c.listOff));
                        if (list < 0x00100000) continue;
                        int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c.objOff));
                        if (raw < 0x00100000) continue;
                        var objPtr = Ptr32(raw);

                        foreach (int po in expandedPosOffsets)
                        {
                            if (!TryReadStablePos(hProcess, objPtr, po, out float ex, out float ey, out float ez))
                                continue;
                            if (!IsPlausibleWorldPos(ex, ey)) continue;

                            float score = 0f;
                            if (simplePlausible)
                            {
                                double d = Math.Sqrt(Math.Pow(ex - p.x, 2) + Math.Pow(ey - p.y, 2));
                                if (d <= 50f) score += 6f;
                                else if (d <= 300f) score += 3f;
                                else if (d > 5000f) score -= 5f;
                            }
                            if (hasLastNpcCloudCenter)
                            {
                                double d = Math.Sqrt(Math.Pow(ex - lastNpcCloudCenterX, 2) + Math.Pow(ey - lastNpcCloudCenterY, 2));
                                if (d <= 3000.0) score += 2f;
                                else if (d > 15000.0) score -= 5f;
                            }
                            if (hasDirectCache)
                            {
                                double d = Math.Sqrt(Math.Pow(ex - directCx, 2) + Math.Pow(ey - directCy, 2));
                                if (d <= 50f) score += 4f;
                                else if (d <= 500f) score += 1f;
                            }
                            if (po == 0x94) score += 1f;

                            if (score > bestZxxyEntityScore)
                            {
                                bestZxxyEntityScore = score;
                                bestZxxyEntityX = ex; bestZxxyEntityY = ey; bestZxxyEntityZ = ez;
                                bestZxxyEntitySrc = $"zxxy-entity(mgr=0x{c.mgrOff:X},obj=0x{raw:X8},pos=0x{po:X2},score={score:F1})";
                            }
                        }

                        // Also follow pointer fields from the entity to sub-objects
                        foreach (int ptrOff in directPtrOffsets)
                        {
                            int subRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, ptrOff));
                            if (subRaw == 0 || subRaw == raw || subRaw < 0x00100000) continue;
                            var subPtr = Ptr32(subRaw);

                            foreach (int po in expandedPosOffsets)
                            {
                                if (!TryReadStablePos(hProcess, subPtr, po, out float ex, out float ey, out float ez))
                                    continue;
                                if (!IsPlausibleWorldPos(ex, ey)) continue;

                                float score = 0f;
                                if (simplePlausible)
                                {
                                    double d = Math.Sqrt(Math.Pow(ex - p.x, 2) + Math.Pow(ey - p.y, 2));
                                    if (d <= 50f) score += 6f;
                                    else if (d <= 300f) score += 3f;
                                    else if (d > 5000f) score -= 5f;
                                }
                                if (hasLastNpcCloudCenter)
                                {
                                    double d = Math.Sqrt(Math.Pow(ex - lastNpcCloudCenterX, 2) + Math.Pow(ey - lastNpcCloudCenterY, 2));
                                    if (d <= 3000.0) score += 2f;
                                    else if (d > 15000.0) score -= 5f;
                                }
                                if (hasDirectCache)
                                {
                                    double d = Math.Sqrt(Math.Pow(ex - directCx, 2) + Math.Pow(ey - directCy, 2));
                                    if (d <= 50f) score += 4f;
                                    else if (d <= 500f) score += 1f;
                                }
                                if (po == 0x94) score += 1f;
                                score += 0.5f;

                                if (score > bestZxxyEntityScore)
                                {
                                    bestZxxyEntityScore = score;
                                    bestZxxyEntityX = ex; bestZxxyEntityY = ey; bestZxxyEntityZ = ez;
                                    bestZxxyEntitySrc = $"zxxy-entity-sub(mgr=0x{c.mgrOff:X},obj=0x{raw:X8},ptr=0x{ptrOff:X2},sub=0x{subRaw:X8},pos=0x{po:X2},score={score:F1})";
                                }
                            }
                        }
                    }

                    if (bestZxxyEntityScore > 8f && IsStrictPlausiblePos(bestZxxyEntityX, bestZxxyEntityY))
                    {
                        lastDirectSource = bestZxxyEntitySrc;
                        directCx = bestZxxyEntityX; directCy = bestZxxyEntityY; directCz = bestZxxyEntityZ; hasDirectCache = true;
                        return (bestZxxyEntityX, bestZxxyEntityY, bestZxxyEntityZ);
                    }
                }

                // --- Phase 2c: targeted probe for known deep chains ---
                // Deep scan revealed live coords at mgr+0x9D4520[+0x0C]+0x4C>+0x0C>+0x4C
                // at position offsets +0x064 and +0x094. Probe this specific chain
                // with a double-read to detect movement, bypassing generic scoring.
                // --- Phase 2c: targeted probe for known deep chains ---
                // Deep scan revealed moving coords at multiple chain paths under
                // mgr+0x9D4520. Try 2-link and 3-link chains with movement detection.
                if (simpleStatic || !simplePlausible)
                {
                    // 2-link chains: mgr→list→obj→sub1→pos
                    int[][] chains2 = {
                        new[] { 0x9D4518, 0x08, 0x4C, 0xBC },   // standard player chain → sub-0xBC (confirmed)
                        new[] { 0x9D4520, 0x0C, 0x4C, 0x0C },
                        new[] { 0x9D4520, 0x0C, 0x48, 0x0C },
                        new[] { 0x9D4520, 0x0C, 0x58, 0x04 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xB8 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xBC },
                        new[] { 0x9D4520, 0x08, 0x4C, 0x0C },
                        new[] { 0x9D4520, 0x08, 0x48, 0x0C },
                    };
                    // 3-link chains: mgr→list→obj→sub1→sub2→pos
                    int[][] chains3 = {
                        new[] { 0x9D4518, 0x0C, 0x50, 0x04, 0x90 },   // CONFIRMED: deep scan live pos at +0x230
                        new[] { 0x9D4520, 0x0C, 0x4C, 0x0C, 0x4C },
                        new[] { 0x9D4520, 0x0C, 0x48, 0x0C, 0x4C },
                        new[] { 0x9D4520, 0x0C, 0x58, 0x04, 0x70 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xB8, 0x54 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xB8, 0x58 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xBC, 0x38 },
                        new[] { 0x9D4520, 0x0C, 0x58, 0xBC, 0x70 },
                        new[] { 0x9D4520, 0x0C, 0x40, 0xB8, 0x24 },
                    };
                    // 0x230/0x190 confirmed by deep scan; 0x020 confirmed by focused dump
                    int[] probePosOffsets = { 0x384, 0x230, 0x190, 0x160, 0x020, 0x94, 0x64, 0x34, 0xC4, 0x24, 0x28, 0x4C, 0x68 };

                    var endpoints = new List<(IntPtr ptr, string tag, bool is2link)>();

                    foreach (var c in chains2)
                    {
                        int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, c[0]));
                        if (mgr == 0) continue;
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c[1]));
                        if (list == 0) continue;
                        int obj = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c[2]));
                        if (obj == 0 || obj < 0x00100000) continue;
                        int sub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(obj, c[3]));
                        if (sub == 0 || sub == obj || sub < 0x00100000) continue;
                        endpoints.Add((Ptr32(sub), $"mgr+0x{c[0]:X}[+0x{c[1]:X2}]+0x{c[2]:X2}>+0x{c[3]:X2}", true));
                    }
                    foreach (var c in chains3)
                    {
                        int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, c[0]));
                        if (mgr == 0) continue;
                        int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c[1]));
                        if (list == 0) continue;
                        int obj = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c[2]));
                        if (obj == 0 || obj < 0x00100000) continue;
                        int sub1 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(obj, c[3]));
                        if (sub1 == 0 || sub1 == obj || sub1 < 0x00100000) continue;
                        int sub2 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(sub1, c[4]));
                        if (sub2 == 0 || sub2 == sub1 || sub2 == obj || sub2 < 0x00100000) continue;

                        // For the confirmed +0x04>+0x90 chain, also probe the intermediate
                        // objects (obj and sub1) before the predictive sub2 endpoint.
                        // The sub2 endpoint stores the render-interpolated position (+19u X error
                        // when moving); earlier chain objects may hold the server position.
                        if (c[0] == 0x9D4518 && c[3] == 0x04 && c[4] == 0x90)
                        {
                            endpoints.Add((Ptr32(obj), $"mgr+0x{c[0]:X}[+0x{c[1]:X2}]+0x{c[2]:X2}(obj)", true));
                            endpoints.Add((Ptr32(sub1), $"mgr+0x{c[0]:X}[+0x{c[1]:X2}]+0x{c[2]:X2}>+0x{c[3]:X2}(sub1)", true));
                        }

                        endpoints.Add((Ptr32(sub2), $"mgr+0x{c[0]:X}[+0x{c[1]:X2}]+0x{c[2]:X2}>+0x{c[3]:X2}>+0x{c[4]:X2}", false));
                    }

                    var firstReads = new List<(IntPtr ptr, int po, float x, float y, float z, string tag, bool is2link)>();
                    var seenEndpoints = new HashSet<long>();
                    foreach (var ep in endpoints)
                    {
                        if (!seenEndpoints.Add(ep.ptr.ToInt64())) continue;
                        foreach (int po in probePosOffsets)
                        {
                            if (TryReadStablePos(hProcess, ep.ptr, po, out float x, out float y, out float z) &&
                                IsStrictPlausiblePos(x, y))
                                firstReads.Add((ep.ptr, po, x, y, z, ep.tag, ep.is2link));
                        }
                    }

                    if (firstReads.Count > 0)
                    {
                        Thread.Sleep(200);

                        var probeResults = new List<(float x, float y, float z, double mv, bool is2link, string src)>();
                        foreach (var fr in firstReads)
                        {
                            if (!TryReadStablePos(hProcess, fr.ptr, fr.po, out float x2, out float y2, out float z2))
                                continue;
                            double mv = Math.Sqrt(Math.Pow(x2 - fr.x, 2) + Math.Pow(y2 - fr.y, 2));
                            float useX = mv > 0.05 ? x2 : fr.x;
                            float useY = mv > 0.05 ? y2 : fr.y;
                            string src = $"targeted({fr.tag},pos=0x{fr.po:X2},v=({useX:F0},{useY:F0}),mv={mv:F1})";
                            probeResults.Add((useX, useY, fr.z, mv, fr.is2link, src));
                        }

                        (float x, float y, float z, double mv, bool is2link, string src)? bestProbe = null;

                        // Priority 1: any candidate with confirmed movement.
                        foreach (var r in probeResults)
                        {
                            if (r.mv > 0.05 && IsStrictPlausiblePos(r.x, r.y))
                            {
                                bestProbe = r;
                                break;
                            }
                        }

                        // Priority 2 (standing still): first 2-link candidate with
                        // coordinate magnitude > 100. 2-link chains are stable direct
                        // sub-objects; 3-link chains traverse linked lists returning
                        // random entities with potentially large but wrong values.
                        if (!bestProbe.HasValue)
                        {
                            foreach (var r in probeResults)
                            {
                                if (!r.is2link) continue;
                                if (!IsStrictPlausiblePos(r.x, r.y)) continue;
                                float mag = Math.Max(Math.Abs(r.x), Math.Max(Math.Abs(r.y), Math.Abs(r.z)));
                                if (mag > 100f)
                                {
                                    bestProbe = r;
                                    break;
                                }
                            }
                        }

                        if (bestProbe.HasValue)
                        {
                            var bp = bestProbe.Value;
                            directCx = bp.x; directCy = bp.y; directCz = bp.z; hasDirectCache = true;
                            lastDirectSource = bp.src;

                            // If /loc already has a fresh result (from manual [G] press), prefer it.
                            if (locCmd.HasLoc && locCmd.LocAge < 4000 &&
                                IsPlausibleWorldPos(locCmd.LocX, locCmd.LocY))
                            {
                                directCx = locCmd.LocX; directCy = locCmd.LocY; directCz = locCmd.LocZ;
                                lastDirectSource = $"loc-manual(age={locCmd.LocAge}ms,chain={bp.src})";
                                return (locCmd.LocX, locCmd.LocY, locCmd.LocZ);
                            }

                            return (bp.x, bp.y, bp.z);
                        }

                        // Log top candidates for debugging.
                        probeResults.Sort((a, b) => b.mv.CompareTo(a.mv));
                        int show = Math.Min(5, probeResults.Count);
                        for (int i = 0; i < show; i++)
                        {
                            var r = probeResults[i];
                            Console.WriteLine($"  [DBG] targeted#{i}: ({r.x:F1},{r.y:F1},{r.z:F1}) mv={r.mv:F2} 2L={r.is2link} {r.src}");
                        }
                    }
                }

                // --- Phase 2d: 4-hop chains confirmed by deep scan ---
                if (simpleStatic || !simplePlausible)
                {
                    // { mgrOff, listOff, objOff, sub1Off, sub2Off, sub3Off }
                    int[][] chains4 = {
                        new[] { 0x9D4524, 0x0C, 0x54, 0x24, 0x70, 0x28 },  // CONFIRMED: pos at +0x384 (deep scan Δ=161/208)
                        new[] { 0x9D4524, 0x0C, 0x50, 0x70, 0xA0, 0x0C },  // previous confirmed: pos at +0x190
                    };
                    // 0x384/0x388 = confirmed XY pair; others kept as fallback
                    int[] p4PosOffs = { 0x384, 0x190, 0x230, 0x94, 0x64, 0x34 };

                    foreach (var c4 in chains4)
                    {
                        int mg4 = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, c4[0]));
                        if (mg4 == 0) continue;
                        int li4 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mg4, c4[1]));
                        if (li4 == 0) continue;
                        int ob4 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(li4, c4[2]));
                        if (ob4 == 0 || ob4 < 0x00100000) continue;
                        int s1 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(ob4, c4[3]));
                        if (s1 == 0 || s1 == ob4 || s1 < 0x00100000) continue;
                        int s2 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(s1, c4[4]));
                        if (s2 == 0 || s2 == s1 || s2 < 0x00100000) continue;
                        int s3 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(s2, c4[5]));
                        if (s3 == 0 || s3 == s2 || s3 < 0x00100000) continue;
                        var ep4 = Ptr32(s3);
                        foreach (int p4po in p4PosOffs)
                        {
                            if (!TryReadStablePos(hProcess, ep4, p4po, out float e4x, out float e4y, out float e4z)) continue;
                            if (!IsStrictPlausiblePos(e4x, e4y)) continue;
                            // Validate: prefer /loc only when it is NOT stale zone data.
                            bool c4LocStale = simpleResolved && locCmd.HasLoc &&
                                Math.Abs(locCmd.LocX - p.x) < 100f && Math.Abs(locCmd.LocY - p.y) < 100f;
                            bool passesRef = false;
                            if (locCmd.HasLoc && locCmd.LocAge < 15000 && IsPlausibleWorldPos(locCmd.LocX, locCmd.LocY) && !c4LocStale)
                            {
                                double d = Math.Sqrt(Math.Pow(e4x - locCmd.LocX, 2) + Math.Pow(e4y - locCmd.LocY, 2));
                                passesRef = d < 2000.0;
                            }
                            else if (hasLastNpcCloudCenter)
                            {
                                double d = Math.Sqrt(Math.Pow(e4x - lastNpcCloudCenterX, 2) + Math.Pow(e4y - lastNpcCloudCenterY, 2));
                                passesRef = d < 8000.0;
                            }
                            else
                            {
                                passesRef = true;  // no reference available, accept any strictly plausible result
                            }
                            if (!passesRef) continue;
                            directCx = e4x; directCy = e4y; directCz = e4z; hasDirectCache = true;
                            lastDirectSource = $"chain4(mgr=0x{c4[0]:X},ptr=0x{c4[3]:X2}>0x{c4[4]:X2}>0x{c4[5]:X2},pos=0x{p4po:X2},ep=0x{s3:X8})";
                            return (e4x, e4y, e4z);
                        }
                    }
                }

                // --- Phase 3: direct fallback with known-wrong coordinate rejection ---
                float rdx = 0f, rdy = 0f, rdz = 0f;
                string rdsrc = "";
                bool phase3ok = simpleStatic && simplePlausible &&
                    TryReadPlayerDirectRejectCoords(p.x, p.y, out rdx, out rdy, out rdz, out rdsrc);
                if (phase3ok && IsStrictPlausiblePos(rdx, rdy))
                {
                    directCx = rdx; directCy = rdy; directCz = rdz; hasDirectCache = true;
                    lastDirectSource = $"reject-static({p.x:F0},{p.y:F0})->{rdsrc}";
                    return (rdx, rdy, rdz);
                }
                else if (phase3ok)
                {
                    // Phase 3 found something but it failed strict plausibility.
                    Console.WriteLine($"  [DBG] phase3-rejected: ({rdx:F1},{rdy:F1},{rdz:F1}) {rdsrc}");
                }

                float dx = 0f, dy = 0f, dz = 0f;
                string dsrc = "";
                bool phase4ok = (!simplePlausible || simpleStatic) &&
                    TryReadPlayerDirect(out dx, out dy, out dz, out dsrc);
                if (phase4ok && IsStrictPlausiblePos(dx, dy))
                {
                    bool directAlsoStatic = simpleStatic && simplePlausible &&
                        Math.Abs(dx - p.x) < 1f && Math.Abs(dy - p.y) < 1f;
                    if (directAlsoStatic)
                    {
                        fallbackStaticReads++;
                        lastDirectSource = dsrc;
                    }
                    else
                    {
                        if (hasDirectCache)
                        {
                            double dd = Math.Sqrt(Math.Pow(dx - directCx, 2) + Math.Pow(dy - directCy, 2));
                            if (dd < 0.01) fallbackStaticReads++;
                            else fallbackStaticReads = 0;
                        }

                        if (fallbackStaticReads >= 2 && TryAutoLockGlobalXY(dx, dy))
                            lastDirectSource = $"{dsrc},glob=lock";
                        else
                            lastDirectSource = dsrc;

                        if (fallbackStaticReads >= 2 && TryReadGlobalXYLocked(out float gx, out float gy) &&
                            IsPlausibleWorldPos(gx, gy))
                        {
                            directCx = gx; directCy = gy; directCz = dz; hasDirectCache = true;
                            lastDirectSource = $"{lastDirectSource},glob=use";
                            return (gx, gy, dz);
                        }

                        if (fallbackStaticReads >= 2 && TryReadGlobalXYModule(out float mx, out float my) &&
                            IsPlausibleWorldPos(mx, my))
                        {
                            directCx = mx; directCy = my; directCz = dz; hasDirectCache = true;
                            lastDirectSource = $"{lastDirectSource},glob=module";
                            return (mx, my, dz);
                        }

                        if (!directAlsoStatic)
                        {
                            directCx = dx; directCy = dy; directCz = dz; hasDirectCache = true;
                            lastDirectSource = dsrc;
                            return (dx, dy, dz);
                        }
                    }
                }

                if (phase4ok && !IsStrictPlausiblePos(dx, dy))
                    Console.WriteLine($"  [DBG] phase4-rejected: ({dx:F1},{dy:F1},{dz:F1}) {dsrc}");

                lastDirectSource = simpleStatic ? "simple-static(no-alt-found)" : "";
                // Chain D frozen value beats all stale caches — even if not moving,
                // the last known position is more reliable than an arbitrary cache entry.
                if (hasChainD && IsStrictPlausiblePos(chainDx, chainDy))
                {
                    lastDirectSource = $"xajh-D-frozen({chainDx:F0},{chainDy:F0},f={chainDFrozenCount})";
                    return (chainDx, chainDy, chainDz);
                }
                // If simple chain is frozen, prefer last good direct cache over it.
                if (simpleStatic && hasDirectCache && IsStrictPlausiblePos(directCx, directCy))
                {
                    lastDirectSource = $"cache-last-resort({directCx:F0},{directCy:F0})";
                    return (directCx, directCy, directCz);
                }
                return p;
            }

            void RunDirectMovementCalibration()
            {
                Console.WriteLine("[M] Calibrating fallback source. Move your character for ~3 seconds...");
                IntPtr calibHwnd = GetGameHwnd();
                if (calibHwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(calibHwnd);
                    Thread.Sleep(120);
                }
                directCalibrating = true;
                directCalibEndTicks = Environment.TickCount64 + 4200;
                directCalibStartByKey.Clear();
                directMotionByKey.Clear();
                directCalibLock = null;
                directLastByKey.Clear();
                hasDirectCache = false;
                preferredDirectStaticReads = 0;

                while (Environment.TickCount64 < directCalibEndTicks)
                {
                    TryReadDirectPlayerPos(hProcess, moduleBase, out _, out _, out _, out _, avoidKnownStaticRoot: true);
                    foreach (var kv in directLastByKey)
                        if (!directCalibStartByKey.ContainsKey(kv.Key))
                            directCalibStartByKey[kv.Key] = kv.Value;
                    Thread.Sleep(120);
                }

                directCalibrating = false;
                (int mgr, int list, int obj, int link, int pos)? bestKey = null;
                float bestMotion = 0f;

                // Primary metric: start-vs-end displacement during calibration window.
                foreach (var kv in directLastByKey)
                {
                    if (!directCalibStartByKey.TryGetValue(kv.Key, out var start))
                        continue;
                    double d = Math.Sqrt(Math.Pow(kv.Value.x - start.x, 2) + Math.Pow(kv.Value.y - start.y, 2));
                    float motion = (float)Math.Min(d, 500f);
                    if (motion > bestMotion)
                    {
                        bestMotion = motion;
                        bestKey = kv.Key;
                    }
                }

                // Secondary metric: accumulated per-read motion (kept for backup).
                foreach (var kv in directMotionByKey)
                {
                    float motion = kv.Value;
                    if (motion > bestMotion)
                    {
                        bestMotion = motion;
                        bestKey = kv.Key;
                    }
                }

                if (!bestKey.HasValue || bestMotion < 0.25f)
                {
                    // If movement is weak, choose candidate by motion-first score and
                    // discourage known static anchors (root +0x94 / +0x4C).
                    float fallbackScore = float.MinValue;
                    (int mgr, int list, int obj, int link, int pos)? fallbackKey = null;
                    foreach (var kv in directLastByKey)
                    {
                        float s = 0f;

                        if (directCalibStartByKey.TryGetValue(kv.Key, out var start))
                        {
                            double dStartEnd = Math.Sqrt(
                                Math.Pow(kv.Value.x - start.x, 2) +
                                Math.Pow(kv.Value.y - start.y, 2));
                            s += (float)Math.Min(dStartEnd, 80.0) * 4.0f;
                        }

                        if (directMotionByKey.TryGetValue(kv.Key, out float mv))
                            s += Math.Min(mv, 40f) * 2.0f;

                        // Prefer linked sub-objects and non-default triplets when motion is weak.
                        if (kv.Key.link != -1) s += 2.0f;
                        if (kv.Key.pos != 0x94) s += 1.5f;
                        if (kv.Key.obj != 0x4C) s += 1.0f;

                        // Penalize the historical static anchor shape.
                        if (kv.Key.link == -1 && kv.Key.obj == 0x4C && kv.Key.pos == 0x94)
                            s -= 6.0f;

                        if (hasDirectCache)
                        {
                            double d = Math.Sqrt(Math.Pow(kv.Value.x - directCx, 2) + Math.Pow(kv.Value.y - directCy, 2));
                            if (d <= 300f) s += 1.0f;
                            else if (d > 5000f) s -= 2.0f;
                        }

                        if (s > fallbackScore)
                        {
                            fallbackScore = s;
                            fallbackKey = kv.Key;
                        }
                    }

                    if (!fallbackKey.HasValue)
                    {
                        Console.WriteLine("[M] Calibration failed: no candidate available.");
                        return;
                    }

                    bestKey = fallbackKey.Value;
                    bestMotion = 0f;
                    Console.WriteLine("[M] No strong movement detected; locking best motion-biased fallback candidate.");
                }

                var bk = bestKey.Value;
                directCalibLock = bk;
                lastAutoLock = bk;
                preferredDirectMgr = bk.mgr;
                preferredDirectList = bk.list;
                preferredDirectObj = bk.obj;
                preferredDirectLink = bk.link;
                preferredDirectPos = bk.pos;
                preferredDirectStaticReads = 0;
                fallbackStaticReads = 0;
                if (directLastByKey.TryGetValue(bk, out var last))
                {
                    directCx = last.x;
                    directCy = last.y;
                    hasDirectCache = true;
                }
                string linkTag = bk.link == -1 ? "root" : $"sub+0x{bk.link:X2}";
                Console.WriteLine($"[M] Locked fallback: mgr=0x{bk.mgr:X} list=0x{bk.list:X2} obj=0x{bk.obj:X2} ln={linkTag} pos=0x{bk.pos:X2} motion={bestMotion:F1}");
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
                    // When position is unreliable, include all NPCs (ignore radius filter)
                    if (!lastPosReliable || dxy <= aimRadius)
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
                    Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  (x, z, y)  radius={aimRadius:F0}  pos={(lastPosReliable ? "OK" : "UNRELIABLE")}");
                    Console.WriteLine($"  {"#",2}  {"Name",-20} {"xz",7} {"3d",7}  {"NpcX",9} {"NpcZ",9} {"NpcY",9}   {"dX",8} {"dZ",8} {"dY",8}");
                    for (int i = 0; i < nearby.Count; i++)
                    {
                        var (n, dxy, d3d) = nearby[i];
                        Console.WriteLine($"  {i + 1,2}. {n.Name,-20} {dxy,7:F0} {d3d,7:F0}  {n.X,9:F1} {n.Y,9:F1} {n.Z,9:F1}   {n.X - px,8:F1} {n.Y - py,8:F1} {n.Z - pz,8:F1}");
                    }
                    if (nearby.Count == 0) { return "[!] No NPCs found"; }
                    Console.WriteLine();
                }
                else if (nearby.Count == 0)
                    return "[!] No NPCs found";

                var (target, distXY, _) = nearby[0];

                if (lastPosReliable)
                {
                    // Full turn + fight when we have a reliable position
                    string r = turn.FaceTarget(() => ReadPlayerPos(), target.X, target.Y);
                    bool fightTriggered = false;
                    if (!r.StartsWith("[!]"))
                        fightTriggered = turn.TriggerTargetAndFight();
                    string fightStatus = fightTriggered ? "target=X fight=F" : "target/fight=!";
                    return $"→ {target.Name} d={distXY:F0}  {r}  {fightStatus}  ({nearby.Count} npcs)";
                }
                else
                {
                    // Position unreliable — skip turn, just send X (game auto-selects nearest) + F
                    bool ok = turn.TriggerTargetAndFight();
                    return $"→ {target.Name} [no-pos: X+F only]  {(ok ? "sent" : "!")}  ({nearby.Count} npcs)";
                }
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
                            // First call seeds caches (NPC cloud, directCache, Phase 0.6 chains).
                            // Second call gets the correct result using those caches.
                            ReadPlayerPos();
                            var (px, py, pz) = ReadPlayerPos();
                            var dbg = playerReader.GetDebugSnapshot();
                            bool unresolved = IsUnresolvedSource(dbg.Source) && string.IsNullOrEmpty(lastDirectSource);
                            if (unresolved && TryReattachGame($"player-source={dbg.Source}", forceRefreshSameProcess: true))
                            {
                                Thread.Sleep(150);
                                ReadPlayerPos();
                                (px, py, pz) = ReadPlayerPos();
                                dbg = playerReader.GetDebugSnapshot();
                            }
                            else if (unresolved)
                            {
                                Console.WriteLine($"[REATTACH] failed (source={dbg.Source})");
                            }

                            Console.WriteLine($"\n  Player: ({px:F1}, {py:F1}, {pz:F1})  (x, z, y)  radius={aimRadius:F0}");
                            Console.WriteLine(
                                $"  [DBG] src={dbg.Source} mgr=0x{dbg.MgrOffset:X} off=0x{dbg.ObjOffset:X2} pos=0x{dbg.PosOffset:X2} obj=0x{dbg.PlayerObj.ToInt64():X8} " +
                                $"raw=({dbg.RawX:F1},{dbg.RawY:F1},{dbg.RawZ:F1}) simpleStatic={simpleStaticReads} chainStatic={dbg.SimpleChainStaticReads}");
                            if (!string.IsNullOrEmpty(lastDirectSource))
                                Console.WriteLine($"  [DBG] fallback={lastDirectSource}");
                            if (locCmd.HasLoc)
                                Console.WriteLine($"  [DBG] /loc=({locCmd.LocX:F1},{locCmd.LocY:F1},{locCmd.LocZ:F1}) age={locCmd.LocAge}ms");
                            if (zxxyDirectLockedAddr != 0)
                                Console.WriteLine($"  [DBG] zxxy-direct locked=0x{zxxyDirectLockedAddr:X8} cand={zxxyDirectCandidates.Count}");
                            else if (zxxyDirectCandidates.Count > 0)
                            {
                                Console.WriteLine($"  [DBG] zxxy-direct cand={zxxyDirectCandidates.Count} (no lock yet)");
                                var topCand = zxxyDirectCandidates
                                    .OrderByDescending(c => c.motion)
                                    .Take(8);
                                foreach (var c in topCand)
                                    Console.WriteLine($"    0x{c.addr:X8} ({c.lastX:F1},{c.lastY:F1},{c.lastZ:F1}) motion={c.motion:F2}");
                            }

                            // Dump zxxy pointer chain entity if available
                            if (zxxyModuleBase != IntPtr.Zero && zxxyMgrCandidates.Count > 0)
                            {
                                Console.WriteLine($"\n── zxxy chain entity dump ──");
                                int shown = 0;
                                foreach (var c in zxxyMgrCandidates)
                                {
                                    if (shown >= 3) break;
                                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(zxxyModuleBase, c.mgrOff));
                                    if (mgr < 0x00100000) continue;
                                    int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c.listOff));
                                    if (list < 0x00100000) continue;
                                    int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c.objOff));
                                    if (raw < 0x00100000) continue;
                                    var objPtr = Ptr32(raw);
                                    Console.WriteLine($"  chain mgr=0x{c.mgrOff:X} obj=0x{raw:X8}:");
                                    for (int off = 0; off <= 0x120; off += 4)
                                    {
                                        float fv = MemoryHelper.ReadFloat(hProcess, IntPtr.Add(objPtr, off));
                                        if (float.IsNaN(fv) || float.IsInfinity(fv)) continue;
                                        if (Math.Abs(fv) > 100f && Math.Abs(fv) < 100000f)
                                            Console.WriteLine($"    +0x{off:X3} = {fv,12:F1}");
                                    }
                                    shown++;
                                }
                            }

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
                        else if (key == ConsoleKey.M)
                        {
                            RunDirectMovementCalibration();
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
                        else if (key == ConsoleKey.G)
                        {
                            Console.WriteLine("[G] Sending /loc command to game...");
                            bool ok = locCmd.SendAndRead();
                            if (ok)
                                Console.WriteLine($"[G] /loc result: ({locCmd.LocX:F1}, {locCmd.LocY:F1}, {locCmd.LocZ:F1})");
                            else
                                Console.WriteLine("[G] /loc: no valid position found in response");
                        }
                        else if (key == ConsoleKey.F)
                        {
                            // Focused dump: read the exact +0xBC sub-object from the
                            // known chain and print ALL float triplets for offset comparison.
                            // sub+0x020 is the confirmed position triplet (X, Z/height, Y).
                            Console.WriteLine("[F] Focused dump of targeted chain endpoint...");
                            int fMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4520));
                            int fList = fMgr != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(fMgr, 0x0C)) : 0;
                            int fObj = fList != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(fList, 0x58)) : 0;
                            if (fObj == 0 || fObj < 0x00100000)
                            {
                                // Try obj offset 0x4C as fallback
                                fObj = fList != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(fList, 0x4C)) : 0;
                            }

                            // Also resolve the primary standard-chain sub-0xBC and print it first.
                            {
                                int sMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4518));
                                int sList = sMgr != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(sMgr, 0x08)) : 0;
                                int sObj = sList != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(sList, 0x4C)) : 0;
                                if (sObj != 0 && sObj >= 0x00100000)
                                {
                                    var sObjPtr = Ptr32(sObj);
                                    if (TryReadSubPtrPos(sObjPtr, out float cpx, out float cpy, out float cpz))
                                    {
                                        Console.WriteLine($"\n[F] ★ Standard chain sub-0xBC position:");
                                        Console.WriteLine($"    playerObj=0x{sObj:X8}  sub+0x020 → X={cpx:F2}  Y={cpy:F2}  Z(h)={cpz:F2}");
                                        Console.WriteLine($"    (memory order X,Z,Y; ~10u from /loc — render interpolation lag)");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"\n[F] Standard chain sub-0xBC: playerObj=0x{sObj:X8} → sub ptr null/invalid");
                                    }
                                }
                            }

                            // XajhSmileDll chain diagnostics
                            Console.WriteLine("\n[F] XajhSmileDll chain diagnostics:");
                            {
                                // Chain A: direct playerObj at game+0x9D4514
                                int pObjA = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4514));
                                if (pObjA != 0 && pObjA >= 0x00100000)
                                {
                                    float ax = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, 0x94));
                                    float az = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, 0x98));
                                    float ay = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjA, 0x9C));
                                    Console.WriteLine($"  Chain A (game+0x9D4514): ptr=0x{pObjA:X8}  pos=({ax:F1},{ay:F1},{az:F1})  valid={IsStrictPlausiblePos(ax, ay)}");
                                }
                                else Console.WriteLine($"  Chain A (game+0x9D4514): ptr=0x{pObjA:X8}  NULL/invalid");

                                // Chain B: game+0x978AE0 → [+0x58] → [+0x0C]
                                int mB = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x978AE0));
                                int lB = mB != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(mB, 0x58)) : 0;
                                int pObjB = lB != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(lB, 0x0C)) : 0;
                                if (pObjB != 0 && pObjB >= 0x00100000)
                                {
                                    float bx = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, 0x94));
                                    float bz = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, 0x98));
                                    float by = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjB, 0x9C));
                                    Console.WriteLine($"  Chain B (0x978AE0→+0x58→+0x0C): mgr=0x{mB:X8} obj=0x{pObjB:X8}  pos=({bx:F1},{by:F1},{bz:F1})  valid={IsStrictPlausiblePos(bx, by)}");
                                }
                                else Console.WriteLine($"  Chain B (0x978AE0→+0x58→+0x0C): mgr=0x{mB:X8} list=0x{lB:X8} obj=0x{pObjB:X8}  NULL");

                                // Chain C: game+0x9DD6C4 → [+0x0C]
                                int mC = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9DD6C4));
                                int pObjC = mC != 0 ? MemoryHelper.ReadInt32(hProcess, Ptr32Add(mC, 0x0C)) : 0;
                                if (pObjC != 0 && pObjC >= 0x00100000)
                                {
                                    float cx = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, 0x94));
                                    float cz = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, 0x98));
                                    float cy = MemoryHelper.ReadFloat(hProcess, Ptr32Add(pObjC, 0x9C));
                                    Console.WriteLine($"  Chain C (0x9DD6C4→+0x0C): mgr=0x{mC:X8} obj=0x{pObjC:X8}  pos=({cx:F1},{cy:F1},{cz:F1})  valid={IsStrictPlausiblePos(cx, cy)}");
                                }
                                else Console.WriteLine($"  Chain C (0x9DD6C4→+0x0C): mgr=0x{mC:X8} obj=0x{pObjC:X8}  NULL");

                                // Also show game+0x9CA8A0 content — confirmed position object
                                int mX = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9CA8A0));
                                if (mX != 0 && mX >= 0x00100000)
                                {
                                    float dx = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mX, 0x034));
                                    float dy = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mX, 0x038));
                                    float dz = MemoryHelper.ReadFloat(hProcess, Ptr32Add(mX, 0x03C));
                                    Console.WriteLine($"  Chain D (game+0x9CA8A0): obj=0x{mX:X8}  +0x034=({dx:F1},{dy:F1},{dz:F1})  valid={IsStrictPlausiblePos(dx, dy)}");
                                }
                                else Console.WriteLine($"  Chain D (game+0x9CA8A0): = 0x{mX:X8}  NULL");

                                // Dump the shared target object (0x0BBE08D8 style) to find where
                                // position is actually stored in this game version.
                                int dumpTarget = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D4514));
                                if (dumpTarget != 0 && dumpTarget >= 0x00100000)
                                {
                                    Console.WriteLine($"\n  Dumping object 0x{dumpTarget:X8} (0x200 bytes) — looking for world coordinates:");
                                    var dBuf = new byte[0x200];
                                    MemoryHelper.ReadProcessMemory(hProcess, Ptr32(dumpTarget), dBuf, 0x200, out int dRead);
                                    Console.WriteLine($"  {"Off",-6} {"float":>12}  note");
                                    for (int di = 0; di + 4 <= dRead; di += 4)
                                    {
                                        float fv = BitConverter.ToSingle(dBuf, di);
                                        if (float.IsNaN(fv) || float.IsInfinity(fv)) continue;
                                        float af = Math.Abs(fv);
                                        // World coordinates: 200–30000 (our observed range)
                                        if (af >= 200f && af <= 30000f)
                                            Console.WriteLine($"  +0x{di:X3}  {fv,12:F1}  ← COORD?");
                                    }

                                    // Also dump 9CA8A0 target
                                    if (mX != 0 && mX >= 0x00100000)
                                    {
                                        Console.WriteLine($"\n  Dumping game+0x9CA8A0 target 0x{mX:X8}:");
                                        var xBuf = new byte[0x100];
                                        MemoryHelper.ReadProcessMemory(hProcess, Ptr32(mX), xBuf, 0x100, out int xRead);
                                        for (int di = 0; di + 4 <= xRead; di += 4)
                                        {
                                            float fv = BitConverter.ToSingle(xBuf, di);
                                            if (float.IsNaN(fv) || float.IsInfinity(fv)) continue;
                                            float af = Math.Abs(fv);
                                            if (af >= 200f && af <= 30000f)
                                                Console.WriteLine($"  +0x{di:X3}  {fv,12:F1}  ← COORD?");
                                        }
                                    }
                                }
                            }
                            if (fObj != 0 && fObj >= 0x00100000)
                            {
                                // Try multiple sub-offsets that lead to the position sub-object
                                int[] subOffsets = { 0xBC, 0xB8, 0x04, 0x70, 0x0C };
                                foreach (int sOff in subOffsets)
                                {
                                    int fSub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(fObj, sOff));
                                    if (fSub == 0 || fSub == fObj || fSub < 0x00100000) continue;
                                    Console.WriteLine($"\n  obj=0x{fObj:X8}>+0x{sOff:X2} → sub=0x{fSub:X8}:");
                                    var buf = new byte[0x400];
                                    if (!MemoryHelper.ReadProcessMemory(hProcess, Ptr32(fSub), buf, 0x400, out int rd) || rd < 0x40)
                                        continue;
                                    for (int off = 0; off + 12 <= rd; off += 4)
                                    {
                                        float fx = BitConverter.ToSingle(buf, off);
                                        float fy = BitConverter.ToSingle(buf, off + 4);
                                        float fz = BitConverter.ToSingle(buf, off + 8);
                                        if (float.IsNaN(fx) || float.IsNaN(fy) || float.IsNaN(fz)) continue;
                                        if (float.IsInfinity(fx) || float.IsInfinity(fy) || float.IsInfinity(fz)) continue;
                                        bool anyBig = Math.Abs(fx) > 10f || Math.Abs(fy) > 10f || Math.Abs(fz) > 10f;
                                        if (!anyBig) continue;
                                        if (Math.Abs(fx) > 1_000_000f || Math.Abs(fy) > 1_000_000f || Math.Abs(fz) > 1_000_000f) continue;
                                        Console.WriteLine($"    +0x{off:X3}: ({fx,10:F1}, {fy,10:F1}, {fz,10:F1})");
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine("[F] Chain not resolvable");
                            }
                            Console.WriteLine();
                        }
                        else if (key == ConsoleKey.D)
                        {
                            Console.WriteLine("[D] Deep object scan — move your character NOW...");
                            IntPtr dsHwnd = GetGameHwnd();
                            if (dsHwnd != IntPtr.Zero)
                            {
                                SetForegroundWindow(dsHwnd);
                                Thread.Sleep(80);
                            }

                            var scanAddrs = new List<(string tag, int addr)>();
                            int[] mgrOffsets = { 0x9D4518, 0x9D4514, 0x9D4510, 0x9D4520, 0x9D451C, 0x9D4524, 0x9D450C };
                            int[] listOffsets = { 0x08, 0x0C, 0x04, 0x10, 0x14 };
                            int[] objOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C };
                            int[] ptrRange = { 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30,
                                               0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60,
                                               0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C, 0x80, 0x84, 0x88, 0x8C, 0x90,
                                               0x94, 0x98, 0x9C, 0xA0, 0xA4, 0xA8, 0xAC, 0xB0, 0xB4, 0xB8, 0xBC, 0xC0 };

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
                                        int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, oo));
                                        if (raw == 0 || raw < 0x00100000) continue;
                                        string rootTag = $"mgr+0x{mo:X}[+0x{lo:X2}]+0x{oo:X2}";
                                        scanAddrs.Add((rootTag, raw));

                                        foreach (int pOff in ptrRange)
                                        {
                                            int sub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, pOff));
                                            if (sub == 0 || sub == raw || sub < 0x00100000) continue;
                                            scanAddrs.Add(($"{rootTag}>+0x{pOff:X2}", sub));

                                            foreach (int pOff2 in ptrRange)
                                            {
                                                int sub2 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(sub, pOff2));
                                                if (sub2 == 0 || sub2 == sub || sub2 == raw || sub2 < 0x00100000) continue;
                                                scanAddrs.Add(($"{rootTag}>+0x{pOff:X2}>+0x{pOff2:X2}", sub2));

                                                foreach (int pOff3 in new[] { 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20,
                                                                              0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40 })
                                                {
                                                    int sub3 = MemoryHelper.ReadInt32(hProcess, Ptr32Add(sub2, pOff3));
                                                    if (sub3 == 0 || sub3 == sub2 || sub3 == sub || sub3 == raw || sub3 < 0x00100000) continue;
                                                    scanAddrs.Add(($"{rootTag}>+0x{pOff:X2}>+0x{pOff2:X2}>+0x{pOff3:X2}", sub3));
                                                }
                                            }
                                        }
                                    }

                                    // Also walk the linked list: container at list+0x04,
                                    // then node chain via node+0x0C, entity at node+0x4C.
                                    int container = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, 0x04));
                                    if (container != 0 && container > 0x00100000)
                                    {
                                        int firstNode = MemoryHelper.ReadInt32(hProcess, Ptr32Add(container, 0x04));
                                        uint node = (uint)firstNode;
                                        int safety = 0;
                                        while (node != 0 && node > 0x00100000 && safety++ < 50)
                                        {
                                            int entRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x4C));
                                            if (entRaw != 0 && entRaw > 0x00100000)
                                            {
                                                string eTag = $"mgr+0x{mo:X}[+0x{lo:X2}]list-node#{safety}";
                                                scanAddrs.Add((eTag, entRaw));
                                                foreach (int pOff in ptrRange)
                                                {
                                                    int sub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(entRaw, pOff));
                                                    if (sub == 0 || sub == entRaw || sub < 0x00100000) continue;
                                                    scanAddrs.Add(($"{eTag}>+0x{pOff:X2}", sub));
                                                }
                                            }
                                            node = (uint)MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x0C));
                                        }
                                    }
                                }
                            }

                            // Also scan the NPC manager chain entities.
                            {
                                int npcMgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(moduleBase, 0x9D451C));
                                if (npcMgr != 0)
                                {
                                    int firstNode = MemoryHelper.ReadInt32(hProcess, Ptr32Add(npcMgr, 8));
                                    uint node = (uint)firstNode;
                                    int safety = 0;
                                    while (node != 0 && node > 0x00100000 && safety++ < 50)
                                    {
                                        int entRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x4C));
                                        if (entRaw != 0 && entRaw > 0x00100000)
                                        {
                                            scanAddrs.Add(($"npc-node#{safety}", entRaw));
                                            foreach (int pOff in ptrRange)
                                            {
                                                int sub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(entRaw, pOff));
                                                if (sub == 0 || sub == entRaw || sub < 0x00100000) continue;
                                                scanAddrs.Add(($"npc-node#{safety}>+0x{pOff:X2}", sub));
                                            }
                                        }
                                        node = (uint)MemoryHelper.ReadInt32(hProcess, Ptr32Add((int)node, 0x0C));
                                    }
                                }
                            }

                            if (zxxyModuleBase != IntPtr.Zero)
                            {
                                foreach (var c in zxxyMgrCandidates)
                                {
                                    int mgr = MemoryHelper.ReadInt32(hProcess, IntPtr.Add(zxxyModuleBase, c.mgrOff));
                                    if (mgr < 0x00100000) continue;
                                    int list = MemoryHelper.ReadInt32(hProcess, Ptr32Add(mgr, c.listOff));
                                    if (list < 0x00100000) continue;
                                    int raw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(list, c.objOff));
                                    if (raw < 0x00100000) continue;
                                    scanAddrs.Add(($"zxxy:mgr+0x{c.mgrOff:X}", raw));
                                    foreach (int pOff in ptrRange)
                                    {
                                        int sub = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, pOff));
                                        if (sub == 0 || sub == raw || sub < 0x00100000) continue;
                                        scanAddrs.Add(($"zxxy:mgr+0x{c.mgrOff:X}>+0x{pOff:X2}", sub));
                                    }
                                }
                            }

                            var seen = new HashSet<int>();
                            var uniqueAddrs = new List<(string tag, int addr)>();
                            foreach (var a in scanAddrs)
                            {
                                if (seen.Add(a.addr))
                                    uniqueAddrs.Add(a);
                            }

                            Console.WriteLine($"[D] Scanning {uniqueAddrs.Count} objects (0x400 bytes each)...");
                            const int ScanLen = 0x400;

                            var snap1 = new Dictionary<int, byte[]>();
                            foreach (var a in uniqueAddrs)
                            {
                                var buf = new byte[ScanLen];
                                if (MemoryHelper.ReadProcessMemory(hProcess, Ptr32(a.addr), buf, ScanLen, out int rd) && rd >= 0x40)
                                    snap1[a.addr] = buf;
                            }

                            Console.WriteLine("[D] Waiting 2s — keep moving...");
                            Thread.Sleep(2000);

                            var snap2 = new Dictionary<int, byte[]>();
                            foreach (var a in uniqueAddrs)
                            {
                                var buf = new byte[ScanLen];
                                if (MemoryHelper.ReadProcessMemory(hProcess, Ptr32(a.addr), buf, ScanLen, out int rd) && rd >= 0x40)
                                    snap2[a.addr] = buf;
                            }

                            Console.WriteLine("\n── Deep scan: CHANGED float fields ──");
                            int totalChanged = 0;
                            foreach (var a in uniqueAddrs)
                            {
                                if (!snap1.ContainsKey(a.addr) || !snap2.ContainsKey(a.addr)) continue;
                                var b1 = snap1[a.addr];
                                var b2 = snap2[a.addr];
                                int len = Math.Min(b1.Length, b2.Length);
                                var changes = new List<string>();
                                for (int off = 0; off + 4 <= len; off += 4)
                                {
                                    float f1 = BitConverter.ToSingle(b1, off);
                                    float f2 = BitConverter.ToSingle(b2, off);
                                    if (float.IsNaN(f1) || float.IsNaN(f2)) continue;
                                    if (float.IsInfinity(f1) || float.IsInfinity(f2)) continue;
                                    if (Math.Abs(f1) > 1_000_000f || Math.Abs(f2) > 1_000_000f) continue;
                                    double delta = Math.Abs(f2 - f1);
                                    if (delta > 0.05 && delta < 50000)
                                    {
                                        // Flag values in plausible world coordinate range.
                                        bool looksLikeCoord = Math.Abs(f1) > 10f && Math.Abs(f2) > 10f &&
                                            Math.Abs(f1) < 100000f && Math.Abs(f2) < 100000f;
                                        string flag = looksLikeCoord ? " ★" : "";
                                        changes.Add($"    +0x{off:X3}: {f1,12:F1} → {f2,12:F1}  (Δ={delta:F1}){flag}");
                                        totalChanged++;
                                    }
                                }
                                if (changes.Count > 0)
                                {
                                    Console.WriteLine($"  {a.tag} obj=0x{a.addr:X8}:");
                                    foreach (var c in changes)
                                        Console.WriteLine(c);
                                }
                            }
                            if (totalChanged == 0)
                                Console.WriteLine("  (no float changes detected — were you moving?)");
                            Console.WriteLine($"[D] Total changed fields: {totalChanged}\n");
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