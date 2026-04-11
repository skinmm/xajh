using System;

namespace xajh
{
    class PlayerReader
    {
        // X/Y from a global position mirror found via the [O] scanner.
        // The game keeps the player position duplicated in ~17 places; any
        // one of them is fine to read. Update if game version changes.
        // Z (height) still from the matrix-block translation chain.
        const long MgrOffset = 0x9D4518;
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        public (float x, float y, float z) Get()
        {
            try
            {
                float x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
                float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));

                float z = _cz;
                int mr = MemoryHelper.ReadInt32(_h, IntPtr.Add(_m, (int)MgrOffset));
                if (mr != 0)
                {
                    int fr = MemoryHelper.ReadInt32(_h, new IntPtr((uint)(mr + 8)));
                    if (fr != 0)
                    {
                        int nr = MemoryHelper.ReadInt32(_h, IntPtr.Add(new IntPtr((uint)fr), 0x4C));
                        if (nr != 0)
                            z = MemoryHelper.ReadFloat(_h, IntPtr.Add(new IntPtr((uint)nr), 0x9C));
                    }
                }

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
