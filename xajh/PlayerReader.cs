using System;

namespace xajh
{
    class PlayerReader
    {
        // Primary path: read player object matrix-translation candidates and
        // choose the most plausible world position each tick.
        // Fallback path: use global mirror address discovered by [O] scanner.
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;
        static readonly int[] PosCandidateOffsets = { 0x34, 0x64, 0x94, 0xC4 };
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;
        int _preferredPosOffset = 0x94;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                bool haveObjPos = TryReadPlayerObjectPos(out float objX, out float objY, out float objZ);
                bool haveGlobal = TryReadGlobalMirror(out float gx, out float gy);

                float x, y, z;
                if (haveGlobal && haveObjPos)
                {
                    bool useObjXY = false;
                    if (_hasCache)
                    {
                        double dGlobal = Math.Sqrt(Math.Pow(gx - _cx, 2) + Math.Pow(gy - _cy, 2));
                        double dObj = Math.Sqrt(Math.Pow(objX - _cx, 2) + Math.Pow(objY - _cy, 2));
                        // If global jumps wildly while object position stays smooth,
                        // trust object XY for this tick (global mirror may be stale).
                        if (dGlobal > 300f && dObj < 80f)
                            useObjXY = true;
                    }

                    if (useObjXY)
                    {
                        x = objX;
                        y = objY;
                    }
                    else
                    {
                        // Global mirror XY is the most stable source when valid.
                        x = gx;
                        y = gy;
                    }
                    z = objZ;
                }
                else if (haveGlobal)
                {
                    x = gx;
                    y = gy;
                    z = _hasCache ? _cz : 0f;
                }
                else if (haveObjPos)
                {
                    x = objX;
                    y = objY;
                    z = objZ;
                }
                else
                {
                    return Cached();
                }

                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return Cached();
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        bool TryReadPlayerObjectPos(out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
            if (mgr == 0) return false;

            int list = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)mgr), ListOffset));
            if (list == 0) return false;

            int playerObj = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)list), PlayerObjOffset));
            if (playerObj == 0) return false;

            var p = new IntPtr((uint)playerObj);
            bool haveGlobal = TryReadGlobalMirror(out float gx, out float gy);

            float bestScore = float.MinValue;
            int bestOffset = 0;
            float bx = 0f, by = 0f, bz = 0f;
            bool found = false;

            foreach (int off in PosCandidateOffsets)
            {
                float cx = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off));
                float cy = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off + 4));
                float cz = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, off + 8));
                if (!IsCoordinatePlausible(cx, cy, cz)) continue;

                float score = 0f;
                if ((_hasCache || haveGlobal) && off == _preferredPosOffset) score += 0.7f;

                if (_hasCache)
                {
                    double dxy = Math.Sqrt(Math.Pow(cx - _cx, 2) + Math.Pow(cy - _cy, 2));
                    if (dxy <= 3f) score += 3f;
                    else if (dxy <= 60f) score += 2f;
                    else if (dxy <= 250f) score += 1f;
                    else score -= 3f;

                    float dz = Math.Abs(cz - _cz);
                    if (dz <= 20f) score += 0.5f;
                    else if (dz > 500f) score -= 1f;
                }

                if (haveGlobal)
                {
                    double dg = Math.Sqrt(Math.Pow(cx - gx, 2) + Math.Pow(cy - gy, 2));
                    if (dg <= 3f) score += 4f;
                    else if (dg <= 30f) score += 2f;
                    else if (dg <= 200f) score += 0.5f;
                    else score -= 2f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = off;
                    bx = cx; by = cy; bz = cz;
                    found = true;
                }
            }

            if (!found) return false;
            _preferredPosOffset = bestOffset;
            x = bx; y = by; z = bz;
            return true;
        }

        bool TryReadGlobalMirror(out float x, out float y)
        {
            x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
            if (float.IsNaN(x) || float.IsNaN(y)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return false;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return false;
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
