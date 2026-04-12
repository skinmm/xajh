using System;
using System.Collections.Generic;

namespace xajh
{
    class PlayerReader
    {
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

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                if (!TryReadPlayerObjectPos(out float x, out float y, out float z))
                    return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        bool TryReadPlayerObjectPos(out float x, out float y, out float z)
        {
            x = 0f; y = 0f; z = 0f;
            if (!TryGetPlayerObject(out IntPtr p)) return false;

            x = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x94));
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x98));
            z = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x9C));
            return IsCoordinatePlausible(x, y, z);
        }

        bool TryGetPlayerObject(out IntPtr playerObj)
        {
            playerObj = IntPtr.Zero;
            int mgr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
            if (mgr == 0) return false;

            int list = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)mgr), ListOffset));
            if (list == 0) return false;

            float bestScore = float.MinValue;
            IntPtr bestPtr = IntPtr.Zero;
            int bestOffset = _preferredObjOffset;

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

                float c = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x10));
                float s = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, 0x1C));
                if (float.IsNaN(c) || float.IsNaN(s) || float.IsInfinity(c) || float.IsInfinity(s))
                    continue;
                if (Math.Abs(c) > 1.2f || Math.Abs(s) > 1.2f) continue;

                float score = 0f;
                if (off == _preferredObjOffset) score += 1.5f;
                if (off == DefaultPlayerObjOffset) score += 1.0f;

                float rotMagErr = Math.Abs((c * c + s * s) - 1f);
                score += Math.Max(0f, 1.0f - rotMagErr);

                if (_hasCache)
                {
                    double dxy = Math.Sqrt(Math.Pow(x - _cx, 2) + Math.Pow(y - _cy, 2));
                    if (dxy <= 8f) score += 3f;
                    else if (dxy <= 120f) score += 1.5f;
                    else score -= 2f;

                    float dz = Math.Abs(z - _cz);
                    if (dz <= 40f) score += 0.5f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPtr = p;
                    bestOffset = off;
                }
            }

            if (bestPtr == IntPtr.Zero) return false;
            _preferredObjOffset = bestOffset;
            playerObj = bestPtr;
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
