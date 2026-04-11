using System;

namespace xajh
{
    class PlayerReader
    {
        // Optional X/Y from a global mirror — set by the [O] scanner (absolute address
        // in the target process). Default IntPtr.Zero: do not read a bogus hardcoded
        // pointer (ASLR breaks fixed addresses like 0x0B201ABC).
        //
        // World position is read from the live player object: matrix block translations
        // at block+0x24 (CombatOverlay [P]). Blocks at +0x10, +0x40, +0x70, +0xA0
        // → translations +0x34, +0x64, +0x94, +0xC4. +0x94 matches npc_obj when that
        // block holds world coords; we try +0x70 first (often the active root).
        const long MgrOffset = 0x9D4518;
        public static IntPtr GlobalPosAddr = IntPtr.Zero;

        const int OffListPlayer = 0x4C;
        static readonly int[] MatrixBlockStarts = { 0x70, 0x10, 0x40, 0xA0 };
        const int TransFromBlock = 0x24;

        /// <summary>Last matrix block start that produced valid coords (-1 = none).</summary>
        static int s_cachedBlockStart = -1;

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        static int ResolvePlayerObject(IntPtr h, IntPtr moduleBase)
        {
            int mr = MemoryHelper.ReadInt32(h, IntPtr.Add(moduleBase, (int)MgrOffset));
            if (mr == 0) return 0;
            int fr = MemoryHelper.ReadInt32(h, new IntPtr((uint)(mr + 8)));
            if (fr == 0) return 0;
            return MemoryHelper.ReadInt32(h, IntPtr.Add(new IntPtr((uint)fr), OffListPlayer));
        }

        static bool IsSaneWorldTriplet(float x, float y, float z)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            if (Math.Abs(x) > 100_000f || Math.Abs(y) > 100_000f || Math.Abs(z) > 100_000f) return false;
            if (x == 0f && y == 0f && z == 0f) return false;
            return true;
        }

        static bool TryReadTranslationAt(IntPtr h, int obj, int blockStart, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (obj == 0) return false;
            int tOff = blockStart + TransFromBlock;
            if (tOff < 0 || tOff + 12 > 0x800) return false;
            var p = new IntPtr((uint)obj);
            try
            {
                x = MemoryHelper.ReadFloat(h, IntPtr.Add(p, tOff));
                y = MemoryHelper.ReadFloat(h, IntPtr.Add(p, tOff + 4));
                z = MemoryHelper.ReadFloat(h, IntPtr.Add(p, tOff + 8));
            }
            catch { return false; }
            return IsSaneWorldTriplet(x, y, z);
        }

        static bool TryReadWorldFromPlayerMatrices(IntPtr h, int playerObj, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (playerObj == 0) return false;

            if (s_cachedBlockStart >= 0 &&
                TryReadTranslationAt(h, playerObj, s_cachedBlockStart, out x, out y, out z))
                return true;

            s_cachedBlockStart = -1;
            foreach (int block in MatrixBlockStarts)
            {
                if (TryReadTranslationAt(h, playerObj, block, out x, out y, out z))
                {
                    s_cachedBlockStart = block;
                    return true;
                }
            }
            return false;
        }

        static bool GlobalXYLooksWrong(float x, float y)
        {
            if (float.IsNaN(x) || float.IsNaN(y)) return true;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return true;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return true;
            if (Math.Abs(x) < 0.01f && Math.Abs(y) < 0.01f) return true;
            return false;
        }

        public (float x, float y, float z) Get()
        {
            try
            {
                int playerObj = ResolvePlayerObject(_h, _m);
                bool haveMatrix = playerObj != 0 &&
                    TryReadWorldFromPlayerMatrices(_h, playerObj, out float x, out float y, out float z);

                if (!haveMatrix)
                    return Cached();

                if (GlobalPosAddr != IntPtr.Zero)
                {
                    float gx = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
                    float gy = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));
                    if (!GlobalXYLooksWrong(gx, gy))
                    {
                        x = gx;
                        y = gy;
                    }
                }

                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return Cached();
                if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return Cached();
                if (x == 0f && y == 0f && z == 0f) return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
