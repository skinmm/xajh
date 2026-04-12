using System;
using System.Collections.Generic;

namespace xajh
{
    class PlayerReader
    {
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
            public readonly IntPtr PlayerObj;
            public readonly int ObjOffset;     // offset in player list node -> playerObj*
            public readonly int PosOffset;     // offset in playerObj -> XYZ block
            public readonly float RawX;
            public readonly float RawY;
            public readonly float RawZ;
            public readonly string Source;

            public DebugSnapshot(IntPtr playerObj, int objOffset, int posOffset, float rawX, float rawY, float rawZ, string source)
            {
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
        IntPtr _dbgPlayerObj = IntPtr.Zero;
        int _dbgObjOffset = 0;
        int _dbgPosOffset = 0;
        float _dbgRawX = float.NaN, _dbgRawY = float.NaN, _dbgRawZ = float.NaN;
        string _dbgSource = "none";

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public DebugSnapshot GetDebugSnapshot()
            => new DebugSnapshot(_dbgPlayerObj, _dbgObjOffset, _dbgPosOffset, _dbgRawX, _dbgRawY, _dbgRawZ, _dbgSource);

        public (float x, float y, float z) Get()
        {
            try
            {
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

        bool TryReadGlobalXY(out float x, out float y)
        {
            x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
            if (float.IsNaN(x) || float.IsNaN(y)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
            return true;
        }

        bool TryReadPlayerObjectPos(out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            if (!TryGetPlayerObject(out IntPtr p)) return false;
            if (!TryReadBestPlayerPos(p, out x, out y, out z, out int posOffset)) return false;
            _dbgPosOffset = posOffset;
            _dbgRawX = x; _dbgRawY = y; _dbgRawZ = z;
            return true;
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
            _dbgObjOffset = 0;
            _dbgPosOffset = 0;
            _dbgRawX = float.NaN; _dbgRawY = float.NaN; _dbgRawZ = float.NaN;
            _dbgSource = "none";

            int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
            if (mgr == 0) return false;

            int list = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)mgr), ListOffset));
            if (list == 0) return false;

            // Fast path: historical default (+0x4C) first.
            int rawDefault = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)list), DefaultPlayerObjOffset));
            if (rawDefault != 0)
            {
                var pDefault = new IntPtr((uint)rawDefault);
                float xDef = MemoryHelper.ReadFloat(_h, IntPtr.Add(pDefault, 0x94));
                float yDef = MemoryHelper.ReadFloat(_h, IntPtr.Add(pDefault, 0x98));
                float zDef = MemoryHelper.ReadFloat(_h, IntPtr.Add(pDefault, 0x9C));
                bool defValid = IsCoordinatePlausible(xDef, yDef, zDef) &&
                                !(Math.Abs(xDef) < 0.001f && Math.Abs(yDef) < 0.001f && Math.Abs(zDef) < 0.001f);
                if (defValid)
                {
                    _preferredObjOffset = DefaultPlayerObjOffset;
                    playerObj = pDefault;
                    _dbgPlayerObj = pDefault;
                    _dbgObjOffset = DefaultPlayerObjOffset;
                    _dbgRawX = xDef; _dbgRawY = yDef; _dbgRawZ = zDef;
                    _dbgSource = "default";
                    return true;
                }
            }

            // Fallback: probe nearby offsets with continuity checks.
            float bestScore = float.MinValue;
            IntPtr bestPtr = IntPtr.Zero;
            int bestOffset = _preferredObjOffset;
            float bestX = float.NaN, bestY = float.NaN, bestZ = float.NaN;
            var seen = new HashSet<int>();
            foreach (int off in _candidateObjOffsets)
            {
                int raw = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)list), off));
                if (raw == 0 || !seen.Add(raw)) continue;

                var p = new IntPtr((uint)raw);
                float x = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x94));
                float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x98));
                float z = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x9C));
                if (!IsCoordinatePlausible(x, y, z)) continue;
                if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f) continue;

                float score = off == _preferredObjOffset ? 1.0f : 0f;
                if (_hasCache)
                {
                    double dxy = Math.Sqrt(Math.Pow(x - _cx, 2) + Math.Pow(y - _cy, 2));
                    if (dxy <= 8f) score += 3f;
                    else if (dxy <= 120f) score += 1.5f;
                    else score -= 2f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPtr = p;
                    bestOffset = off;
                    bestX = x; bestY = y; bestZ = z;
                }
            }

            if (bestPtr == IntPtr.Zero)
                return false;

            _preferredObjOffset = bestOffset;
            playerObj = bestPtr;
            _dbgPlayerObj = bestPtr;
            _dbgObjOffset = bestOffset;
            _dbgRawX = bestX; _dbgRawY = bestY; _dbgRawZ = bestZ;
            _dbgSource = "scan";
            return true;
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
