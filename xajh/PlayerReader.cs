using System;

namespace xajh
{
    class PlayerReader
    {
        // Chain: [moduleBase+0x9D4518] -> mgr+8 -> node+0x4C -> obj+0x94 = X
        // Confirmed from /LOC handler disasm at 0x842DAB + 0x675784 chain.
        // Caches last valid position so zone transitions don't flash (0,0,0).
        const long MgrOffset = 0x9D4518;

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                int mr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
                if (mr == 0) return Cached();
                int fr = MemoryHelper.ReadInt32(_h, new IntPtr((uint)(mr + 8)));
                if (fr == 0) return Cached();
                int nr = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)fr), 0x4C));
                if (nr == 0) return Cached();

                var o = new IntPtr((uint)nr);
                float x = MemoryHelper.ReadFloat(_h, IntPtr.Add(o, 0x94));
                float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(o, 0x98));
                float z = MemoryHelper.ReadFloat(_h, IntPtr.Add(o, 0x9C));

                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return Cached();
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }

}