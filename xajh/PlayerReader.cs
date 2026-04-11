using System;

namespace xajh
{
    class PlayerReader
    {
        // Optional X/Y from [O] scanner (absolute VA in the game process).
        // Default IntPtr.Zero — never use a stale hardcoded VA (ASLR).
        const long MgrOffset = 0x9D4518;
        public static IntPtr GlobalPosAddr = IntPtr.Zero;

        const int OffListPlayer = 0x4C;
        /// <summary>translation = rotation_block + 0x24 (3×3 floats then XYZ)</summary>
        const int TransFromBlock = 0x24;

        /// <summary>Cached translation offset from player object base (-1 = none).</summary>
        static int s_cachedTransOffset = -1;

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

        /// <summary>
        /// World-ish float: finite, not absurd. Allows origin (0) and small maps (|v| &lt; 1).
        /// </summary>
        static bool IsPlausibleCoord(float v) =>
            !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 500_000f;

        static bool IsPlausibleVec3(float x, float y, float z) =>
            IsPlausibleCoord(x) && IsPlausibleCoord(y) && IsPlausibleCoord(z);

        static bool TryReadVec3(IntPtr h, int objAddr, int byteOffset, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (objAddr == 0 || byteOffset < 0 || byteOffset + 12 > 0x800) return false;
            var p = new IntPtr(objAddr);
            try
            {
                x = MemoryHelper.ReadFloat(h, IntPtr.Add(p, byteOffset));
                y = MemoryHelper.ReadFloat(h, IntPtr.Add(p, byteOffset + 4));
                z = MemoryHelper.ReadFloat(h, IntPtr.Add(p, byteOffset + 8));
            }
            catch { return false; }
            return IsPlausibleVec3(x, y, z);
        }

        /// <summary>
        /// Try known matrix layout and a 0x30 stride sweep (extra blocks if layout shifted).
        /// </summary>
        static bool TryTranslationsFromMatrices(IntPtr h, int playerObj, out float x, out float y, out float z, out int transOff)
        {
            x = y = z = 0f;
            transOff = -1;
            int[] preferred = { 0x70, 0x10, 0x40, 0xA0 };
            foreach (int block in preferred)
            {
                int t = block + TransFromBlock;
                if (TryReadVec3(h, playerObj, t, out x, out y, out z))
                {
                    transOff = t;
                    return true;
                }
            }
            for (int block = 0x10; block + TransFromBlock + 12 <= 0x300; block += 0x30)
            {
                int t = block + TransFromBlock;
                if (Array.IndexOf(preferred, block) >= 0) continue;
                if (TryReadVec3(h, playerObj, t, out x, out y, out z))
                {
                    transOff = t;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// npc_obj-style layout at +0x94 (sometimes valid on player root).
        /// </summary>
        static bool TryNpcStylePos(IntPtr h, int playerObj, out float x, out float y, out float z) =>
            TryReadVec3(h, playerObj, 0x94, out x, out y, out z);

        /// <summary>
        /// Last resort: find any plausible XYZ triplet in the first 0x300 bytes.
        /// Skips offsets that often hold pointers (heuristic: int in user-mode heap range).
        /// </summary>
        static bool TryScanStructForVec3(IntPtr h, int playerObj, out float x, out float y, out float z, out int vecOff)
        {
            x = y = z = 0f;
            vecOff = -1;
            const int Len = 0x300;
            var buf = new byte[Len];
            if (!MemoryHelper.ReadProcessMemory(h, new IntPtr(playerObj), buf, Len, out int rd) || rd < 12)
                return false;

            int bestOff = -1;
            float bx = 0, by = 0, bz = 0;
            int bestScore = -1;

            for (int off = 0; off + 12 <= rd; off += 4)
            {
                float tx = BitConverter.ToSingle(buf, off);
                float ty = BitConverter.ToSingle(buf, off + 4);
                float tz = BitConverter.ToSingle(buf, off + 8);
                if (!IsPlausibleVec3(tx, ty, tz)) continue;

                int i0 = BitConverter.ToInt32(buf, off);
                bool p0 = i0 > 0x0100_0000 && i0 < 0x7FFF_FFFF;
                int i1 = BitConverter.ToInt32(buf, off + 4);
                bool p1 = i1 > 0x0100_0000 && i1 < 0x7FFF_FFFF;
                int i2 = BitConverter.ToInt32(buf, off + 8);
                bool p2 = i2 > 0x0100_0000 && i2 < 0x7FFF_FFFF;
                if (p0 && p1 && p2) continue;

                int score = 0;
                if (Math.Abs(tx) > 0.05f || Math.Abs(ty) > 0.05f) score += 2;
                if (Math.Abs(tz) > 0.05f) score += 1;
                if ((off - 0x34) % 0x30 == 0 && off >= 0x34) score += 3;
                if (off == 0x94) score += 2;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestOff = off;
                    bx = tx; by = ty; bz = tz;
                }
            }

            if (bestOff < 0) return false;
            vecOff = bestOff;
            x = bx; y = by; z = bz;
            return true;
        }

        static bool TryResolveWorldFromPlayer(IntPtr h, int playerObj, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (playerObj == 0) return false;

            if (s_cachedTransOffset >= 0 &&
                TryReadVec3(h, playerObj, s_cachedTransOffset, out x, out y, out z))
                return true;

            s_cachedTransOffset = -1;

            if (TryTranslationsFromMatrices(h, playerObj, out x, out y, out z, out int tOff))
            {
                s_cachedTransOffset = tOff;
                return true;
            }

            if (TryNpcStylePos(h, playerObj, out x, out y, out z))
            {
                s_cachedTransOffset = 0x94;
                return true;
            }

            if (TryScanStructForVec3(h, playerObj, out x, out y, out z, out int vecOff))
            {
                s_cachedTransOffset = vecOff;
                return true;
            }

            return false;
        }

        static bool GlobalXYLooksWrong(float gx, float gy)
        {
            if (float.IsNaN(gx) || float.IsNaN(gy)) return true;
            if (float.IsInfinity(gx) || float.IsInfinity(gy)) return true;
            if (Math.Abs(gx) > 1_000_000f || Math.Abs(gy) > 1_000_000f) return true;
            return false;
        }

        public (float x, float y, float z) Get()
        {
            try
            {
                int playerObj = ResolvePlayerObject(_h, _m);
                if (playerObj == 0)
                    return Cached();

                if (!TryResolveWorldFromPlayer(_h, playerObj, out float x, out float y, out float z))
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

                if (!IsPlausibleVec3(x, y, z))
                    return Cached();

                _cx = x; _cy = y; _cz = z; _hasCache = true;
                return (x, y, z);
            }
            catch { return Cached(); }
        }

        (float, float, float) Cached() => _hasCache ? (_cx, _cy, _cz) : (0f, 0f, 0f);
    }
}
