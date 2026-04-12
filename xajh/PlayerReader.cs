using System;

namespace xajh
{
    class PlayerReader
    {
        // World-space XY mirror (matches in-game /loc in prior builds).
        // Keep object-chain read for Z fallback.
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);

        // Object-chain path:
        // moduleBase+0x9D4518 -> +0x08 -> +0x4C -> playerObj(+0x94/+0x98/+0x9C)
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                bool haveGlobal = TryReadGlobalXY(out float gx, out float gy);
                bool haveObj = TryReadPlayerObjectPos(out float ox, out float oy, out float oz);

                float x, y, z;
                if (haveGlobal)
                {
                    // Use world-space global XY; object coordinates can be map-local.
                    x = gx;
                    y = gy;
                    z = haveObj ? oz : (_hasCache ? _cz : 0f);
                }
                else if (haveObj)
                {
                    x = ox;
                    y = oy;
                    z = oz;
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

        bool TryReadGlobalXY(out float x, out float y)
        {
            x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
            return IsCoordinatePlausible(x, y, 0f);
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
