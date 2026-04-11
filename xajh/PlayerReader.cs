using System;

namespace xajh
{
    class PlayerReader
    {
        // X/Y from a global position mirror found via the [O] scanner.
        // The game keeps the player position duplicated in ~17 places; any
        // one of them is fine to read. Update if game version changes.
        // When GlobalPosAddr is wrong for this build, we fall back to the
        // live player object (+0x94/+0x98/+0x9C — same layout as npc_obj).
        const long MgrOffset = 0x9D4518;
        public static IntPtr GlobalPosAddr = new IntPtr(0x0B201ABC);

        const int OffListPlayer = 0x4C;
        const int OffPosX = 0x94;
        const int OffPosY = 0x98;
        const int OffPosZ = 0x9C;

        readonly IntPtr _h, _m;
        float _cx, _cy, _cz;
        bool _hasCache;

        public PlayerReader(IntPtr h, IntPtr m) { _h = h; _m = m; }

        /// <summary>Resolved player entity pointer (same chain as CombatOverlay DumpPlayer).</summary>
        static int ResolvePlayerObject(IntPtr h, IntPtr moduleBase)
        {
            int mr = MemoryHelper.ReadInt32(h, IntPtr.Add(moduleBase, (int)MgrOffset));
            if (mr == 0) return 0;
            int fr = MemoryHelper.ReadInt32(h, new IntPtr((uint)(mr + 8)));
            if (fr == 0) return 0;
            return MemoryHelper.ReadInt32(h, IntPtr.Add(new IntPtr((uint)fr), OffListPlayer));
        }

        static bool TryReadWorldPosFromObject(IntPtr h, int obj, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (obj == 0) return false;
            var p = new IntPtr((uint)obj);
            try
            {
                x = MemoryHelper.ReadFloat(h, IntPtr.Add(p, OffPosX));
                y = MemoryHelper.ReadFloat(h, IntPtr.Add(p, OffPosY));
                z = MemoryHelper.ReadFloat(h, IntPtr.Add(p, OffPosZ));
            }
            catch { return false; }

            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) return false;
            if (Math.Abs(x) > 100_000f || Math.Abs(y) > 100_000f || Math.Abs(z) > 100_000f) return false;
            // (0,0,0) = uninitialized / same sentinel as NpcReader
            if (x == 0f && y == 0f && z == 0f) return false;
            return true;
        }

        static bool GlobalXYLooksWrong(float x, float y)
        {
            if (float.IsNaN(x) || float.IsNaN(y)) return true;
            if (float.IsInfinity(x) || float.IsInfinity(y)) return true;
            if (Math.Abs(x) > 1_000_000f || Math.Abs(y) > 1_000_000f) return true;
            // Stale RVA default or failed read often sits at 0 — NPC list still works via NpcReader.
            if (Math.Abs(x) < 0.01f && Math.Abs(y) < 0.01f) return true;
            return false;
        }

        public (float x, float y, float z) Get()
        {
            try
            {
                float x = MemoryHelper.ReadFloat(_h, GlobalPosAddr);
                float y = MemoryHelper.ReadFloat(_h, IntPtr.Add(GlobalPosAddr, 4));

                float z = _cz;
                int playerObj = ResolvePlayerObject(_h, _m);
                if (playerObj != 0)
                {
                    var pObj = new IntPtr((uint)playerObj);
                    float zFromObj = MemoryHelper.ReadFloat(_h, IntPtr.Add(pObj, OffPosZ));
                    if (!float.IsNaN(zFromObj) && !float.IsInfinity(zFromObj) && Math.Abs(zFromObj) <= 100_000f)
                        z = zFromObj;
                }

                if (playerObj != 0 &&
                    TryReadWorldPosFromObject(_h, playerObj, out float ox, out float oy, out float oz) &&
                    GlobalXYLooksWrong(x, y))
                {
                    x = ox;
                    y = oy;
                    z = oz;
                }

                if (GlobalXYLooksWrong(x, y))
                    return Cached();

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
