using System;

namespace xajh
{
    class PlayerReader
    {
        // Primary path: read player object position at +0x94/+0x98/+0x9C.
        // Fallback path: use global mirror address discovered by [O] scanner.
        const long MgrOffset = 0x9D4518;
        const int ListOffset = 0x08;
        const int PlayerObjOffset = 0x4C;
        const int PosXOffset = 0x94;
        const int PosYOffset = 0x98;
        const int PosZOffset = 0x9C;
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                bool haveObjPos = TryReadPlayerObjectPos(out float x, out float y, out float z);

                // Keep global mirror as a runtime fallback because users can
                // re-lock it with [O] when game updates shuffle structures.
                if (!haveObjPos)
                {
                    x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
                    y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
                    z = _cz;
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
            x = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, PosXOffset));
            y = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, PosYOffset));
            z = MemoryHelper.ReadFloat(_h, IntPtr.Add(p, PosZOffset));
            return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z));
        }

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
