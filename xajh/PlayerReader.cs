using System;

namespace xajh
{
    class PlayerReader
    {
        // Deterministic path:
        // moduleBase+0x9D4518 -> +0x08 -> +0x4C -> playerObj(+0x94/+0x98/+0x9C)
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;

        readonly IntPtr _h;
        readonly IntPtr _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                if (!TryReadPlayerObjectPos(out float x, out float y, out float z))
                    return Cached();

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

            int raw = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)list), PlayerObjOffset));
            if (raw == 0) return false;

            playerObj = new IntPtr((uint)raw);
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
