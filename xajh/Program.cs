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
            int[] directMgrOffsets = { 0x9D4518, 0x9D4514, 0x9D4510, 0x9D4520, 0x9D4524, 0x9D450C };
            int[] directListOffsets = { 0x08, 0x0C, 0x04, 0x10, 0x14 };
            int[] directObjOffsets = { 0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C };
            int[] directPosOffsets = { 0x94, 0x34, 0x64, 0xC4, 0xA4, 0xB4, 0xD4, 0xE4, 0x104, 0x114 };
            int[] directPtrOffsets = { 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64, 0x68, 0x6C, 0x70, 0x74, 0x78, 0x7C };
            int[] directPtrOffsetsL2 = { 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C, 0x30, 0x34, 0x38, 0x3C, 0x40 };
            int[] directSubPosOffsets = { 0x94, 0x34, 0x64, 0xC4, 0x20, 0x24, 0x28, 0x2C, 0x30 };
            int preferredDirectMgr = 0x9D4518;
            int preferredDirectList = 0x08;
            int preferredDirectObj = 0x4C;
            int preferredDirectLink = -1; // -1 = root object, otherwise pointer field offset
            int preferredDirectPos = 0x94;
            bool hasDirectCache = false;
            float directCx = 0f, directCy = 0f, directCz = 0f;
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
            IntPtr zxxyModuleBase = IntPtr.Zero;
            int zxxyModuleSize = 0;
            long nextZxxyModuleRefreshTicks = 0;
            long nextZxxyRescanTicks = 0;
            int preferredZxxyMgrOffset = -1;
            int preferredZxxyListOffset = 0x08;
            int preferredZxxyObjOffset = 0x4C;
            int preferredZxxyPosOffset = 0x94;
            var zxxyMgrCandidates = new List<(int mgrOff, int listOff, int objOff, int posOff)>();

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
                directCx = x;
                directCy = y;
                directCz = z;
                hasDirectCache = true;
                source = $"zxxy(base=0x{zxxyModuleBase.ToInt64():X8},mgr=0x{bestMgrOff:X},list=0x{bestListOff:X2},obj=0x{bestObjOff:X2},pos=0x{bestPosOff:X2},cand={valid})";
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
                out float x, out float y, out float z, out string source)
            {
                x = 0f; y = 0f; z = 0f; source = "direct:none";

                // Strict lock mode: when calibration selected a chain, never hop to unrelated chains.
                if (directCalibLock.HasValue)
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
                                    int subRaw = MemoryHelper.ReadInt32(hProcess, Ptr32Add(raw, lk.link));
                                    targetRaw = (subRaw != 0 && subRaw != raw) ? subRaw : 0;
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
                                        directCx = x; directCy = y; directCz = z; hasDirectCache = true;
                                        string lockTag = lk.link == -1 ? "root" : $"sub+0x{lk.link:X2}";
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
                                        directCx = x; directCy = y; directCz = z; hasDirectCache = true;
                                        string lockTag = lk.link == -1 ? "root" : $"sub+0x{lk.link:X2}";
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
                        string lockTag = lk.link == -1 ? "root" : $"sub+0x{lk.link:X2}";
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
                                }
                            }
                        }
                    }
                }

                // Phase 1: strict player chain family only.
                int[] primaryMgr = { 0x9D4518, 0x9D4514 };
                int[] primaryList = { 0x08 };
                int[] primaryObj = { 0x4C, 0x48 };
                CollectSamples(primaryMgr, primaryList, primaryObj);

                // Phase 2: broader fallback only if strict chain found nothing.
                if (samples.Count == 0)
                    CollectSamples(mgrOrder.ToArray(), directListOffsets, directObjOffsets);

                if (samples.Count == 0) return false;

                bool preferredLooksStatic = false;
                bool anyAltMoved = false;
                foreach (var s in samples)
                {
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

                float bestScore = float.MinValue;
                int bestMgr = 0, bestList = 0, bestObj = 0, bestLink = -1, bestPos = 0;
                foreach (var s in samples)
                {
                    bool isPref = s.mo == preferredDirectMgr &&
                                  s.lo == preferredDirectList &&
                                  s.oo == preferredDirectObj &&
                                  s.link == preferredDirectLink &&
                                  s.po == preferredDirectPos;
                    float score = 0f;
                    if (s.mo == preferredDirectMgr) score += 2f;
                    if (s.lo == preferredDirectList) score += 1f;
                    if (s.oo == preferredDirectObj) score += 1f;
                    if (s.link == preferredDirectLink) score += 1f;
                    if (s.po == preferredDirectPos) score += 2f;
                    if (s.lo == 0x08) score += 1f;
                    if (s.oo == 0x4C) score += 1f;
                    if (s.oo == 0x48) score += 0.7f;
                    if (s.link == -1 && s.po == 0x94) score += 0.5f;
                    if (s.link != -1) score += 0.5f;
                    if (!s.seeded) score -= 0.35f;

                    if (hasDirectCache)
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
                directCx = x; directCy = y; directCz = z; hasDirectCache = true;
                string linkTag = bestLink == -1 ? "root" : $"sub+0x{bestLink:X2}";
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
            Console.WriteLine("[R] Set radius  [L] List nearby NPCs  [M] Calibrate fallback  [P] Dump player obj");
            Console.WriteLine("[W] Refresh window     [End] Exit");
            Console.WriteLine();

            bool autoFace = false;
            float aimRadius = 300f;
            Console.WriteLine($"[*] Aim radius: {aimRadius:F0}");
            string lastDirectSource = "";
            double lastNpcCloudCenterX = 0d, lastNpcCloudCenterY = 0d;
            bool hasLastNpcCloudCenter = false;

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

            bool TryReadPlayerDirect(out float x, out float y, out float z, out string source)
            {
                return TryReadDirectPlayerPos(hProcess, moduleBase, out x, out y, out z, out source);
            }

            (float x, float y, float z) ReadPlayerPos()
            {
                UpdateNpcCloudCenter();
                var p = playerReader.Get();
                var dbg = playerReader.GetDebugSnapshot();
                bool simpleStatic =
                    dbg.Source == "simple" &&
                    hasDirectCache &&
                    Math.Sqrt(Math.Pow(p.x - directCx, 2) + Math.Pow(p.y - directCy, 2)) < 0.01;

                if (!IsUnresolvedSource(dbg.Source) && !simpleStatic)
                {
                    directCx = p.x;
                    directCy = p.y;
                    directCz = p.z;
                    hasDirectCache = true;
                    lastDirectSource = "";
                    return p;
                }

                if (IsUnresolvedSource(dbg.Source) &&
                    TryReadPlayerPosViaZxxy(out float zx, out float zy, out float zz, out string zxsrc))
                {
                    lastDirectSource = zxsrc;
                    return (zx, zy, zz);
                }

                if (IsUnresolvedSource(dbg.Source) &&
                    TryReadPlayerDirect(out float dx, out float dy, out float dz, out string dsrc))
                {
                    if (IsDirectFallbackLikelyWrong(dx, dy))
                    {
                        if (directCalibLock.HasValue)
                        {
                            directCalibLock = null;
                            preferredDirectStaticReads = 0;
                            lastDirectSource = "direct(lock-cleared:npc-cloud)";
                        }
                        if (TryReadPlayerDirect(out dx, out dy, out dz, out dsrc) && !IsDirectFallbackLikelyWrong(dx, dy))
                        {
                            lastDirectSource = dsrc;
                            return (dx, dy, dz);
                        }
                    }
                    // Detect static fallback and escalate to global XY lock.
                    if (hasDirectCache)
                    {
                        double dd = Math.Sqrt(Math.Pow(dx - directCx, 2) + Math.Pow(dy - directCy, 2));
                        if (dd < 0.01) fallbackStaticReads++;
                        else fallbackStaticReads = 0;
                    }

                    if (fallbackStaticReads >= 2 && TryAutoLockGlobalXY(dx, dy))
                    {
                        lastDirectSource = $"{dsrc},glob=lock";
                    }
                    else
                    {
                        lastDirectSource = dsrc;
                    }

                    if (fallbackStaticReads >= 2 && TryReadGlobalXYLocked(out float gx, out float gy))
                    {
                        // Keep Z from fallback candidate; override XY with locked global world coords.
                        directCx = gx; directCy = gy; directCz = dz; hasDirectCache = true;
                        lastDirectSource = $"{lastDirectSource},glob=use";
                        return (gx, gy, dz);
                    }

                    // Final fallback: known global mirror static from module base.
                    if (fallbackStaticReads >= 2 && TryReadGlobalXYModule(out float mx, out float my))
                    {
                        directCx = mx; directCy = my; directCz = dz; hasDirectCache = true;
                        lastDirectSource = $"{lastDirectSource},glob=module";
                        return (mx, my, dz);
                    }

                    if (fallbackStaticReads >= 1 &&
                        TryReadPlayerPosViaZxxy(out float zfx, out float zfy, out float zfz, out string zfsrc))
                    {
                        lastDirectSource = $"{lastDirectSource},{zfsrc}";
                        return (zfx, zfy, zfz);
                    }

                    return (dx, dy, dz);
                }

                // If simple source exists but appears numerically frozen, still route
                // through direct fallback path to escape static map-anchor coordinates.
                if (simpleStatic &&
                    TryReadPlayerPosViaZxxy(out float ssx, out float ssy, out float ssz, out string sssrc))
                {
                    lastDirectSource = $"simple-static,{sssrc}";
                    return (ssx, ssy, ssz);
                }

                if (simpleStatic &&
                    TryReadPlayerDirect(out float sx, out float sy, out float sz, out string ssrc))
                {
                    lastDirectSource = $"simple-static,{ssrc}";
                    return (sx, sy, sz);
                }
                lastDirectSource = "";
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
                    TryReadDirectPlayerPos(hProcess, moduleBase, out _, out _, out _, out _);
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