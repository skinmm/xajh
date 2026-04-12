using System;
using System.Collections.Generic;

namespace xajh
{
    class PlayerReader
    {
        public readonly struct DebugSnapshot
        {
            public readonly IntPtr PlayerObj;
            public readonly int ObjOffset;
            public readonly float RawX;
            public readonly float RawY;
            public readonly float RawZ;
            public readonly string Source;

            public DebugSnapshot(IntPtr playerObj, int objOffset, float rawX, float rawY, float rawZ, string source)
            {
                PlayerObj = playerObj;
                ObjOffset = objOffset;
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
        readonly int[] _candidateObjOffsets =
        {
            0x4C, 0x48, 0x50, 0x44, 0x54, 0x40, 0x58, 0x3C, 0x5C, 0x38, 0x60
        };

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;
        int _preferredObjOffset = DefaultPlayerObjOffset;
        IntPtr _dbgPlayerObj = IntPtr.Zero;
        int _dbgObjOffset = 0;
        float _dbgRawX = float.NaN, _dbgRawY = float.NaN, _dbgRawZ = float.NaN;
        string _dbgSource = "none";

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public DebugSnapshot GetDebugSnapshot()
            => new DebugSnapshot(_dbgPlayerObj, _dbgObjOffset, _dbgRawX, _dbgRawY, _dbgRawZ, _dbgSource);

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

            x = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x94));
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x98));
            z = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x9C));
            if (!IsCoordinatePlausible(x, y, z)) return false;

            // Treat pure zero-vector as invalid player location.
            if (Math.Abs(x) < 0.001f && Math.Abs(y) < 0.001f && Math.Abs(z) < 0.001f)
                return false;

            return true;
        }

        bool TryGetPlayerObject(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            _dbgPlayerObj = IntPtr.Zero;
            _dbgObjOffset = 0;
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

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
