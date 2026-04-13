using System;
using System.Collections.Generic;

namespace xajh
{
    class PlayerReader
    {
        static IntPtr Ptr32(int value) => new IntPtr(value);
        static IntPtr Ptr32Add(int baseValue, int offset) => new IntPtr(unchecked(baseValue + offset));

        readonly struct PosSample
        {
            public readonly int Offset;
            public readonly float X;
            public readonly float Y;
            public readonly float Z;

            public PosSample(int offset, float x, float y, float z)
            {
                Offset = offset;
                X = x;
                Y = y;
                Z = z;
            }
        }

        public readonly struct DebugSnapshot
        {
            public readonly int MgrOffset;     // module offset used for player manager*
            public readonly IntPtr PlayerObj;
            public readonly int ObjOffset;     // offset in player list node -> playerObj*
            public readonly int PosOffset;     // offset in playerObj -> XYZ block
            public readonly float RawX;
            public readonly float RawY;
            public readonly float RawZ;
            public readonly string Source;

            public DebugSnapshot(int mgrOffset, IntPtr playerObj, int objOffset, int posOffset, float rawX, float rawY, float rawZ, string source)
            {
                MgrOffset = mgrOffset;
                PlayerObj = playerObj;
                ObjOffset = objOffset;
                PosOffset = posOffset;
                RawX = rawX;
                RawY = rawY;
                RawZ = rawZ;
                Source = source;
            }
        }

        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int DefaultPlayerObjOffset = 0x4C;
        const int DefaultPosOffset = 0x94;
        readonly int[] _candidateMgrOffsets =
        {
            0x9D4518, 0x9D4514, 0x9D4510, 0x9D4520, 0x9D451C, 0x9D4524, 0x9D450C
        };
        readonly int[] _candidateListOffsets =
        {
            0x08, 0x0C, 0x04, 0x10, 0x14
        };
        readonly int[] _candidateObjOffsets =
        {
            0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C, 0x5C, 0x38, 0x60
        };
        readonly int[] _candidatePosOffsets =
        {
            // Translation triplets from four 3x3 matrix blocks inside player object.
            0x94, 0x34, 0x64, 0xC4
        };

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;
        int _preferredObjOffset = DefaultPlayerObjOffset;
        int _preferredPosOffset = DefaultPosOffset;
        readonly Dictionary<int, (float x, float y, float z)> _lastPosByOffset = new Dictionary<int, (float x, float y, float z)>();
        int _preferredPosStaticReads = 0;
        readonly Dictionary<int, (float x, float y, float z)> _lastObjPosByOffset = new Dictionary<int, (float x, float y, float z)>();
        int _preferredObjStaticReads = 0;
        int _preferredMgrOffset = (int)MgrOffset;
        long _nextDeepMgrScanAtTicks = 0;
        long _nextSigScanAtTicks = 0;
        IntPtr _signaturePlayerObj = IntPtr.Zero;
        IntPtr _dbgPlayerObj = IntPtr.Zero;
        int _dbgMgrOffset = 0;
        int _dbgObjOffset = 0;
        int _dbgPosOffset = 0;
        float _dbgRawX = float.NaN, _dbgRawY = float.NaN, _dbgRawZ = float.NaN;
        string _dbgSource = "none";

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public DebugSnapshot GetDebugSnapshot()
            => new DebugSnapshot(_dbgMgrOffset, _dbgPlayerObj, _dbgObjOffset, _dbgPosOffset, _dbgRawX, _dbgRawY, _dbgRawZ, _dbgSource);

        public (float x, float y, float z) Get()
        {
            try
            {
                if (TryReadSimpleChainPos(out float sx, out float sy, out float sz,
                                         out int smgr, out int sobj, out int spos, out IntPtr sptr))
                {
                    _preferredMgrOffset = smgr;
                    _preferredObjOffset = sobj;
                    _preferredPosOffset = spos;
                    _signaturePlayerObj = IntPtr.Zero;
                    _dbgMgrOffset = smgr;
                    _dbgObjOffset = sobj;
                    _dbgPosOffset = spos;
                    _dbgPlayerObj = sptr;
                    _dbgRawX = sx; _dbgRawY = sy; _dbgRawZ = sz;
                    _dbgSource = "simple";
                    _cx = sx; _cy = sy; _cz = sz; _hasCache = true;
                    return (sx, sy, sz);
                }

                bool haveObj = TryReadPlayerObjectPos(out float ox, out float oy, out float oz);
                bool haveGlobal = TryReadGlobalXY(out float gx, out float gy);

                float x, y, z;
                if (haveObj)
                {
                    // Some wrong object candidates read as exactly 0,0,0.
                    // If that happens, prefer global world-space XY when available.
                    bool objLooksZero = Math.Abs(ox) < 0.001f && Math.Abs(oy) < 0.001f && Math.Abs(oz) < 0.001f;
                    if (objLooksZero && haveGlobal && (Math.Abs(gx) > 1f || Math.Abs(gy) > 1f))
                    {
                        x = gx;
                        y = gy;
                        z = _hasCache ? _cz : 0f;
                    }
                    else
                    {
                        x = ox;
                        y = oy;
                        z = oz;
                    }
                }
                else if (haveGlobal)
                {
                    x = gx;
                    y = gy;
                    z = _hasCache ? _cz : 0f;
                }
                else
                {
                    return Cached();
                }

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        bool TryReadSimpleChainPos(
            out float x, out float y, out float z,
            out int mgrOff, out int objOff, out int posOff, out IntPtr playerObj)
        {
            x = 0f; y = 0f; z = 0f;
            mgrOff = 0; objOff = 0; posOff = 0; playerObj = IntPtr.Zero;
            float bestScore = float.MinValue;

            var mgrOrder = new List<int> { _preferredMgrOffset };
            foreach (int mo in _candidateMgrOffsets)
                if (mo != _preferredMgrOffset) mgrOrder.Add(mo);

            foreach (int mo in mgrOrder)
            {
                int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, mo));
                if (mgr == 0) continue;

                foreach (int lo in _candidateListOffsets)
                {
                    int list = MemoryHelper.ReadInt32(_h, Ptr32Add(mgr, lo));
                    if (list == 0) continue;

                    foreach (int oo in _candidateObjOffsets)
                    {
                        int raw = MemoryHelper.ReadInt32(_h, Ptr32Add(list, oo));
                        if (raw == 0) continue;
                        var p = Ptr32(raw);

                        foreach (int po in _candidatePosOffsets)
                        {
                            if (!TryReadPosAtOffset(p, po, out float tx, out float ty, out float tz))
                                continue;

                            float score = 0f;
                            if (mo == _preferredMgrOffset) score += 2f;
                            if (oo == _preferredObjOffset) score += 1f;
                            if (po == _preferredPosOffset) score += 2f;
                            if (oo == DefaultPlayerObjOffset) score += 1f;
                            if (po == DefaultPosOffset) score += 1f;
                            if (_hasCache)
                            {
                                double d = DistXY(tx, ty, _cx, _cy);
                                if (d <= 20f) score += 4f;
                                else if (d <= 300f) score += 2f;
                                else if (d <= 3000f) score -= 1f;
                                else score -= 4f;
                            }

                            if (score > bestScore)
                            {
                                bestScore = score;
                                x = tx; y = ty; z = tz;
                                mgrOff = mo; objOff = oo; posOff = po;
                                playerObj = p;
                            }
                        }
                    }
                }
            }

            return playerObj != IntPtr.Zero;
        }

        bool TryReadGlobalXY(out float x, out float y)
        {
            x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
            if (float.IsNaN(x) || float.IsNaN(y)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
            if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f) return false;
            return true;
        }

        bool TryReadPlayerObjectPos(out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            if (!TryGetPlayerObject(out IntPtr p)) return false;
            if (TryReadBestPlayerPos(p, out x, out y, out z, out int posOffset))
            {
                _dbgPosOffset = posOffset;
                _dbgRawX = x; _dbgRawY = y; _dbgRawZ = z;
                return true;
            }

            // Last-chance read so we don't regress to src=none/0,0,0 on maps where
            // sampling heuristics cannot score candidates reliably.
            if (TryReadPosAtOffset(p, DefaultPosOffset, out x, out y, out z))
            {
                _dbgPosOffset = DefaultPosOffset;
                _dbgRawX = x; _dbgRawY = y; _dbgRawZ = z;
                if (_dbgSource == "fallback-raw")
                    _dbgSource = "fallback-raw+94";
                return true;
            }

            return false;
        }

        bool TryReadBestPlayerPos(IntPtr playerObj, out float x, out float y, out float z, out int posOffset)
        {
            x = 0f; y = 0f; z = 0f; posOffset = 0;
            var samples = new List<PosSample>(_candidatePosOffsets.Length);
            foreach (int off in _candidatePosOffsets)
            {
                if (!TryReadPosAtOffset(playerObj, off, out float sx, out float sy, out float sz))
                    continue;
                samples.Add(new PosSample(off, sx, sy, sz));
            }

            if (samples.Count == 0)
                return false;

            bool preferredLooksStatic = false;
            bool anyAltMovedFromCache = false;
            if (_hasCache)
            {
                foreach (var s in samples)
                {
                    double dCache = DistXY(s.X, s.Y, _cx, _cy);
                    if (s.Offset == _preferredPosOffset && dCache < 0.01)
                        preferredLooksStatic = true;
                    if (s.Offset != _preferredPosOffset && dCache > 0.20 && dCache <= 300.0)
                        anyAltMovedFromCache = true;
                }
            }

            if (preferredLooksStatic && anyAltMovedFromCache)
                _preferredPosStaticReads++;
            else
                _preferredPosStaticReads = 0;

            float bestScore = float.MinValue;
            PosSample best = samples[0];
            foreach (var s in samples)
            {
                float score = 0f;

                if (s.Offset == _preferredPosOffset) score += 2f;
                if (s.Offset == DefaultPosOffset) score += 1f;

                if (_hasCache)
                {
                    double dCache = DistXY(s.X, s.Y, _cx, _cy);
                    if (dCache <= 10.0) score += 2f;
                    else if (dCache <= 300.0) score += 1f;
                    else score -= 3f;

                    if (dCache > 0.20 && dCache <= 300.0) score += 1f;
                }

                if (_lastPosByOffset.TryGetValue(s.Offset, out var last))
                {
                    double dSelf = DistXY(s.X, s.Y, last.x, last.y);
                    if (dSelf > 0.05 && dSelf <= 300.0) score += 2f;
                    else if (dSelf < 0.005) score -= 0.5f;
                }

                // If current preferred block appears frozen while another block moves,
                // strongly encourage a switch after a couple of consecutive reads.
                if (s.Offset == _preferredPosOffset && _preferredPosStaticReads >= 2)
                    score -= 6f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = s;
                }
            }

            foreach (var s in samples)
                _lastPosByOffset[s.Offset] = (s.X, s.Y, s.Z);

            _preferredPosOffset = best.Offset;
            x = best.X; y = best.Y; z = best.Z;
            posOffset = best.Offset;
            return true;
        }

        bool TryReadPosAtOffset(IntPtr playerObj, int posOffset, out float x, out float y, out float z)
        {
            x = MemoryHelper.ReadFloat(_h, IntPtr.Add(playerObj, posOffset));
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(playerObj, posOffset + 4));
            z = MemoryHelper.ReadFloat(_h, IntPtr.Add(playerObj, posOffset + 8));
            if (!IsCoordinatePlausible(x, y, z)) return false;
            if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f) return false;
            return true;
        }

        bool TryGetPlayerObject(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            _dbgPlayerObj = IntPtr.Zero;
            _dbgMgrOffset = 0;
            _dbgObjOffset = 0;
            _dbgPosOffset = 0;
            _dbgRawX = float.NaN; _dbgRawY = float.NaN; _dbgRawZ = float.NaN;
            _dbgSource = "none";
            try
            {
                // Fast path: preferred manager offset first.
                int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, _preferredMgrOffset));
                if (mgr != 0)
                {
                    foreach (int listOff in _candidateListOffsets)
                    {
                        int list = MemoryHelper.ReadInt32(_h, Ptr32Add(mgr, listOff));
                        if (list != 0 && TryResolveFromList(list, "main", _preferredMgrOffset, out playerObj))
                            return true;
                    }
                }

                // Hard fallback: map/session specific manager chains can move.
                // Probe nearby static manager/list variants before giving up.
                if (TryResolveFromMgrScans(out playerObj))
                    return true;

                // Reuse previously signature-scanned object if still valid.
                if (TryResolveFromSignatureCache(out playerObj))
                    return true;

                long now = Environment.TickCount64;
                if (now >= _nextDeepMgrScanAtTicks)
                {
                    _nextDeepMgrScanAtTicks = now + 2000;
                    if (TryResolveFromDeepStaticScan(out playerObj))
                        return true;
                }

                if (now >= _nextSigScanAtTicks)
                {
                    _nextSigScanAtTicks = now + 3000;
                    if (TryResolveFromSignatureScan(out playerObj))
                        return true;
                }

                _dbgSource = mgr == 0 ? "mgr=0" : "list/obj=0";
                return false;
            }
            catch
            {
                if (TryResolveFromMgrScans(out playerObj))
                    return true;
                long now = Environment.TickCount64;
                if (now >= _nextDeepMgrScanAtTicks)
                {
                    _nextDeepMgrScanAtTicks = now + 2000;
                    if (TryResolveFromDeepStaticScan(out playerObj))
                        return true;
                }
                if (now >= _nextSigScanAtTicks)
                {
                    _nextSigScanAtTicks = now + 3000;
                    if (TryResolveFromSignatureScan(out playerObj))
                        return true;
                }
                _dbgSource = "obj-ex";
                return false;
            }
        }

        bool TryResolveFromMgrScans(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            var seenLists = new HashSet<int>();

            foreach (int mgrOff in _candidateMgrOffsets)
            {
                int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, mgrOff));
                if (mgr == 0) continue;

                foreach (int listOff in _candidateListOffsets)
                {
                    int list = MemoryHelper.ReadInt32(_h, Ptr32Add(mgr, listOff));
                    if (list == 0 || !seenLists.Add(list)) continue;
                    if (TryResolveFromList(list, $"mgrscan({mgrOff:X},{listOff:X})", mgrOff, out playerObj))
                        return true;
                }
            }

            return false;
        }

        bool TryResolveFromDeepStaticScan(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            var seenLists = new HashSet<int>();
            for (int mgrOff = 0x9D4400; mgrOff <= 0x9D4580; mgrOff += 4)
            {
                int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, mgrOff));
                if (mgr < 0x00100000) continue;

                foreach (int listOff in _candidateListOffsets)
                {
                    int list = MemoryHelper.ReadInt32(_h, Ptr32Add(mgr, listOff));
                    if (list < 0x00100000 || !seenLists.Add(list)) continue;
                    if (TryResolveFromList(list, $"deepscan({mgrOff:X},{listOff:X})", mgrOff, out playerObj))
                        return true;
                }
            }
            return false;
        }

        bool TryResolveFromSignatureCache(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            if (_signaturePlayerObj == IntPtr.Zero)
                return false;
            if (!TrySampleObjectPosition(_signaturePlayerObj, out float x, out float y, out float z, out int posOff))
                return false;

            playerObj = _signaturePlayerObj;
            _dbgPlayerObj = playerObj;
            _dbgMgrOffset = 0;
            _dbgObjOffset = 0;
            _dbgPosOffset = posOff;
            _dbgRawX = x; _dbgRawY = y; _dbgRawZ = z;
            _dbgSource = "sigcache";
            return true;
        }

        bool TryResolveFromSignatureScan(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            float bestScore = float.MinValue;
            IntPtr bestPtr = IntPtr.Zero;
            float bestX = 0f, bestY = 0f, bestZ = 0f;
            int bestPosOff = 0;

            IntPtr address = IntPtr.Zero;
            while (true)
            {
                if (!MemoryHelper.VirtualQueryEx(
                    _h,
                    address,
                    out var mbi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>()))
                    break;

                bool writable = mbi.State == MemoryHelper.MEM_COMMIT &&
                    (mbi.Protect == MemoryHelper.PAGE_READWRITE ||
                     mbi.Protect == MemoryHelper.PAGE_WRITECOPY ||
                     mbi.Protect == MemoryHelper.PAGE_EXECUTE_READWRITE ||
                     mbi.Protect == MemoryHelper.PAGE_EXECUTE_WRITECOPY);

                if (writable)
                {
                    long regionBase = mbi.BaseAddress.ToInt64();
                    long regionSize = mbi.RegionSize.ToInt64();
                    const int ScanChunk = 0x20000;
                    const int Tail = 0x200;
                    const int MinRead = 0x140;

                    for (long off = 0; off < regionSize; off += ScanChunk)
                    {
                        int toRead = (int)Math.Min(ScanChunk + Tail, regionSize - off);
                        if (toRead < MinRead) break;

                        var chunkBase = new IntPtr(regionBase + off);
                        var buf = new byte[toRead];
                        if (!MemoryHelper.ReadProcessMemory(_h, chunkBase, buf, toRead, out int bytesRead) || bytesRead < MinRead)
                            continue;

                        int limit = bytesRead - 0xD0;
                        for (int i = 0; i <= limit; i += 4)
                        {
                            float c0 = BitConverter.ToSingle(buf, i + 0x10);
                            float s0 = BitConverter.ToSingle(buf, i + 0x1C);
                            if (!LooksLikeUnitPair(c0, s0))
                                continue;

                            float c1 = BitConverter.ToSingle(buf, i + 0x40);
                            float s1 = BitConverter.ToSingle(buf, i + 0x4C);
                            int pairCount = 1 + (LooksLikeUnitPair(c1, s1) ? 1 : 0);

                            int validPosCount = 0;
                            float localBest = float.MinValue;
                            int localPosOff = 0;
                            float localX = 0f, localY = 0f, localZ = 0f;
                            foreach (int posOff in _candidatePosOffsets)
                            {
                                int p = i + posOff;
                                if (p + 8 >= bytesRead) continue;
                                float x = BitConverter.ToSingle(buf, p);
                                float y = BitConverter.ToSingle(buf, p + 4);
                                float z = BitConverter.ToSingle(buf, p + 8);
                                if (!IsCoordinatePlausible(x, y, z)) continue;
                                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f) continue;

                                validPosCount++;
                                float s = 0f;
                                if (posOff == _preferredPosOffset) s += 1f;
                                if (posOff == DefaultPosOffset) s += 0.5f;
                                if (_hasCache)
                                {
                                    double d = DistXY(x, y, _cx, _cy);
                                    if (d <= 250f) s += 4f;
                                    else if (d <= 2000f) s += 2f;
                                    else s -= 2f;
                                }
                                if (s > localBest)
                                {
                                    localBest = s;
                                    localPosOff = posOff;
                                    localX = x; localY = y; localZ = z;
                                }
                            }

                            // Strong signature: rotation + at least two plausible translation triplets.
                            if (validPosCount < 2)
                                continue;

                            float score = localBest + (pairCount * 2f) + (validPosCount * 1.5f);
                            int maybeVtable = BitConverter.ToInt32(buf, i);
                            if (maybeVtable > 0x00400000 && maybeVtable < 0x7FFFFFFF)
                                score += 1f;

                            if (score <= bestScore)
                                continue;

                            bestScore = score;
                            bestPtr = IntPtr.Add(chunkBase, i);
                            bestX = localX; bestY = localY; bestZ = localZ;
                            bestPosOff = localPosOff;
                        }
                    }
                }

                long next = address.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= 0 || next >= long.MaxValue) break;
                address = new IntPtr(next);
            }

            if (bestPtr == IntPtr.Zero)
                return false;

            _signaturePlayerObj = bestPtr;
            _preferredPosOffset = bestPosOff == 0 ? _preferredPosOffset : bestPosOff;
            playerObj = bestPtr;
            _dbgPlayerObj = bestPtr;
            _dbgMgrOffset = 0;
            _dbgObjOffset = 0;
            _dbgPosOffset = bestPosOff;
            _dbgRawX = bestX; _dbgRawY = bestY; _dbgRawZ = bestZ;
            _dbgSource = "sigscan";
            return true;
        }

        static bool LooksLikeUnitPair(float c, float s)
        {
            if (float.IsNaN(c) || float.IsNaN(s) || float.IsInfinity(c) || float.IsInfinity(s))
                return false;
            if (Math.Abs(c) > 1.01f || Math.Abs(s) > 1.01f)
                return false;
            if (Math.Abs(c) < 0.01f || Math.Abs(s) < 0.01f)
                return false;
            float mag = c * c + s * s;
            return Math.Abs(mag - 1f) < 0.05f;
        }

        bool TryResolveFromList(int list, string sourceTag, int mgrOff, out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;

            // Probe nearby offsets with continuity and anti-stale scoring.
            var rawCandidates = new List<(int off, IntPtr p)>();
            var candidates = new List<(int off, IntPtr p, float x, float y, float z)>();
            var seen = new HashSet<int>();
            foreach (int off in _candidateObjOffsets)
            {
                int raw = MemoryHelper.ReadInt32(_h, Ptr32Add(list, off));
                if (raw == 0 || !seen.Add(raw)) continue;

                var p = Ptr32(raw);
                rawCandidates.Add((off, p));
                if (!TrySampleObjectPosition(p, out float x, out float y, out float z, out _))
                    continue;

                candidates.Add((off, p, x, y, z));
            }

            if (rawCandidates.Count == 0)
                return false;

            if (candidates.Count == 0)
            {
                // Keep a raw fallback to avoid total loss ("src=none") when
                // all current sampled position blocks look invalid.
                (int off, IntPtr p) rawPick = rawCandidates[0];
                foreach (var rc in rawCandidates)
                {
                    if (rc.off == _preferredObjOffset) { rawPick = rc; break; }
                    if (rc.off == DefaultPlayerObjOffset) rawPick = rc;
                }

                playerObj = rawPick.p;
                _preferredObjOffset = rawPick.off;
                _dbgPlayerObj = rawPick.p;
                _dbgMgrOffset = mgrOff;
                _dbgObjOffset = rawPick.off;
                _dbgSource = $"{sourceTag}-fallback-raw";
                return true;
            }

            bool preferredLooksStatic = false;
            bool anyAltMovedFromCache = false;
            if (_hasCache)
            {
                foreach (var c in candidates)
                {
                    double dCache = DistXY(c.x, c.y, _cx, _cy);
                    if (c.off == _preferredObjOffset && dCache < 0.01)
                        preferredLooksStatic = true;
                    if (c.off != _preferredObjOffset && dCache > 0.20 && dCache <= 300.0)
                        anyAltMovedFromCache = true;
                }
            }
            if (preferredLooksStatic && anyAltMovedFromCache)
                _preferredObjStaticReads++;
            else
                _preferredObjStaticReads = 0;

            float bestScore = float.MinValue;
            IntPtr bestPtr = IntPtr.Zero;
            int bestOffset = _preferredObjOffset;
            float bestX = float.NaN, bestY = float.NaN, bestZ = float.NaN;
            foreach (var c in candidates)
            {
                float score = 0f;
                if (c.off == _preferredObjOffset) score += 2f;
                if (c.off == DefaultPlayerObjOffset) score += 1f;
                if (_hasCache)
                {
                    double dxy = DistXY(c.x, c.y, _cx, _cy);
                    if (dxy <= 10f) score += 2f;
                    else if (dxy <= 300f) score += 1f;
                    else if (dxy > 3000f) score -= 3f;

                    if (dxy > 0.20f && dxy <= 300f) score += 1f;
                }

                if (_lastObjPosByOffset.TryGetValue(c.off, out var lastObj))
                {
                    double dSelf = DistXY(c.x, c.y, lastObj.x, lastObj.y);
                    if (dSelf > 0.05f && dSelf <= 300f) score += 1.5f;
                    else if (dSelf < 0.005f) score -= 0.5f;
                }

                if (c.off == _preferredObjOffset && _preferredObjStaticReads >= 2)
                    score -= 6f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPtr = c.p;
                    bestOffset = c.off;
                    bestX = c.x; bestY = c.y; bestZ = c.z;
                }
            }

            if (bestPtr == IntPtr.Zero)
                return false;

            foreach (var c in candidates)
                _lastObjPosByOffset[c.off] = (c.x, c.y, c.z);

            _preferredObjOffset = bestOffset;
            _preferredMgrOffset = mgrOff;
            _signaturePlayerObj = IntPtr.Zero;
            playerObj = bestPtr;
            _dbgPlayerObj = bestPtr;
            _dbgMgrOffset = mgrOff;
            _dbgObjOffset = bestOffset;
            _dbgRawX = bestX; _dbgRawY = bestY; _dbgRawZ = bestZ;
            _dbgSource = bestOffset == DefaultPlayerObjOffset
                ? $"{sourceTag}-default"
                : $"{sourceTag}-scan";
            return true;
        }

        bool TrySampleObjectPosition(IntPtr playerObj, out float x, out float y, out float z, out int bestPosOffset)
        {
            x = 0f; y = 0f; z = 0f; bestPosOffset = 0;
            float bestScore = float.MinValue;
            bool found = false;
            foreach (int posOff in _candidatePosOffsets)
            {
                if (!TryReadPosAtOffset(playerObj, posOff, out float sx, out float sy, out float sz))
                    continue;
                float score = 0f;
                if (posOff == _preferredPosOffset) score += 2f;
                if (posOff == DefaultPosOffset) score += 1f;
                if (_hasCache)
                {
                    double d = DistXY(sx, sy, _cx, _cy);
                    if (d <= 10f) score += 2f;
                    else if (d <= 300f) score += 1f;
                    else if (d > 3000f) score -= 3f;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    x = sx; y = sy; z = sz;
                    bestPosOffset = posOff;
                    found = true;
                }
            }
            return found;
        }

        static bool IsCoordinatePlausible(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f || Math.Abs(z) > 1_000_000f)
                return false;
            return true;
        }

        static double DistXY(float ax, float ay, float bx, float by)
            => Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2));

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
