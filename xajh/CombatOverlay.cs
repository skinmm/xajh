using System;
using System.Collections.Generic;
using System.Linq;

namespace xajh
{
    /// <summary>
    /// Faces the player toward NPCs by writing two Y-axis rotation matrices
    /// directly into the player object's memory.
    ///
    /// Layout confirmed from dump (yaw ≈ 1.95 rad before face, ≈ 1.06 after):
    ///
    ///   Matrix A (3x3 at +0x070):            Matrix B (3x3 at +0x0A0):
    ///   +0x070  cos   +0x074 -sin  +0x078 0  +0x0A0  cos   +0x0A4  sin  +0x0A8 0
    ///   +0x07C  sin   +0x080  cos  +0x084 0  +0x0AC -sin   +0x0B0  cos  +0x0B4 0
    ///   +0x088  0     +0x08C  0    +0x090 1  +0x0B8  0     +0x0BC  0    +0x0C0 1
    ///
    ///   Position: +0x094 X, +0x098 Y, +0x09C Z  (between the two matrices)
    ///
    /// Matrix A is a standard 2D rotation [ cos -sin ; sin cos ]
    /// Matrix B is its transpose (inverse) [ cos sin ; -sin cos ]
    /// Both must be updated together or the game detects the inconsistency.
    /// </summary>
    public class CombatOverlay
    {
        private IntPtr _hProcess;
        private IntPtr _moduleBase;

        public CombatOverlay(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
        }

        public string FaceNearest(float px, float py, float pz, List<Npc> npcs)
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0) return null;

            var nearest = npcs
                .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                .FirstOrDefault();
            if (nearest == null) return null;

            float dx = nearest.X - px;
            float dz = nearest.Z - pz;
            float yaw = (float)Math.Atan2(dx, dz);
            float cosY = (float)Math.Cos(yaw);
            float sinY = (float)Math.Sin(yaw);

            var obj = new IntPtr((uint)playerObj);

            float oldCos = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x10));
            float oldSin = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x1C));
            float oldYaw = (float)Math.Atan2(oldSin, oldCos);

            WriteYawToAllMatrices(obj, cosY, sinY);

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (dist={dist:F0}, yaw {oldYaw:F2}->{yaw:F2})";
        }

        /// <summary>
        /// Writes cos/sin yaw to all four rotation matrix copies in the
        /// player object, plus the partial copy at +0x0E0.
        ///
        /// Layout (confirmed from wide dump diff):
        ///   Set 1 (+0x010): primary forward  [cos -sin ; sin cos]
        ///   Set 2 (+0x040): primary inverse  [cos sin ; -sin cos]
        ///   Set 3 (+0x070): copy of Set 1
        ///   Set 4 (+0x0A0): copy of Set 2
        ///   +0x0E0/+0x0E4:  partial (sin, cos)
        /// </summary>
        private void WriteYawToAllMatrices(IntPtr obj, float cosY, float sinY)
        {
            // Set 1 (+0x010): PRIMARY forward rotation — game reads this
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x10), cosY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x14), -sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x1C), sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x20), cosY);

            // Set 2 (+0x040): PRIMARY inverse rotation
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x40), cosY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x44), sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x4C), -sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x50), cosY);

            // Set 3 (+0x070): copy forward
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x70), cosY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x74), -sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x7C), sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x80), cosY);

            // Set 4 (+0x0A0): copy inverse
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xA0), cosY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xA4), sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xAC), -sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xB0), cosY);

            // Partial at +0x0E0
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xE0), sinY);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xE4), cosY);
        }

        /// <summary>
        /// Wide dump: reads +0x000 to +0x400 of the player object, prints every
        /// DWORD as both float and int.  Highlights values in the yaw range
        /// (-π to +π) so we can find the source yaw field that the game uses to
        /// recalculate the rotation matrices each frame.
        ///
        /// Usage: press [D] to dump, turn in-game, press key to dump again.
        /// Compare both dumps — the float that changed AND is in [-3.15, 3.15]
        /// range is the source yaw field.
        /// </summary>
        public float[] DumpPlayerWide()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0)
            {
                Console.WriteLine("[!] Cannot read player object.");
                return null;
            }

            var obj = new IntPtr((uint)playerObj);
            const int dumpLen = 0x400;
            var buf = new byte[dumpLen];
            MemoryHelper.ReadProcessMemory(_hProcess, obj, buf, dumpLen, out _);

            float cosA = BitConverter.ToSingle(buf, 0x70);
            float sinA = BitConverter.ToSingle(buf, 0x7C);
            float curYaw = (float)Math.Atan2(sinA, cosA);

            Console.WriteLine($"\n── Player 0x{playerObj:X8} — wide dump (0x000..0x{dumpLen:X}) ──");
            Console.WriteLine($"  Matrix yaw: {curYaw:F4} rad ({curYaw * 180f / Math.PI:F1} deg)");
            Console.WriteLine($"  {"Off",-7} {"float",12} {"int",12}  Note");
            Console.WriteLine(new string('─', 65));

            var floats = new float[dumpLen / 4];
            for (int off = 0; off < dumpLen; off += 4)
            {
                float fv = BitConverter.ToSingle(buf, off);
                int iv = BitConverter.ToInt32(buf, off);
                floats[off / 4] = fv;

                string note = "";
                if (off == 0x94) note = "  ← X";
                else if (off == 0x98) note = "  ← Y";
                else if (off == 0x9C) note = "  ← Z";
                else if (off == 0x70) note = "  ← A.cos";
                else if (off == 0x7C) note = "  ← A.sin";
                else if (!float.IsNaN(fv) && !float.IsInfinity(fv) &&
                         Math.Abs(fv) > 0.01f && Math.Abs(fv) <= Math.PI + 0.1f)
                {
                    note = "  ← YAW?";
                }

                if (!float.IsNaN(fv) && !float.IsInfinity(fv))
                    Console.WriteLine($"  +0x{off:X3}  {fv,12:F4} {iv,12}{note}");
            }
            Console.WriteLine();
            return floats;
        }

        /// <summary>
        /// Compares two wide dumps and prints offsets where the float value changed.
        /// </summary>
        public static void CompareDumps(float[] before, float[] after)
        {
            if (before == null || after == null) return;
            Console.WriteLine("── Changed floats ──");
            Console.WriteLine($"  {"Off",-7} {"Before",12} {"After",12}");
            Console.WriteLine(new string('─', 40));
            int count = Math.Min(before.Length, after.Length);
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(before[i] - after[i]) > 0.0001f)
                {
                    int off = i * 4;
                    Console.WriteLine($"  +0x{off:X3}  {before[i],12:F4} {after[i],12:F4}");
                }
            }
            Console.WriteLine();
        }

        private int GetPlayerObject()
        {
            int mgr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, 0x9D4518));
            if (mgr == 0) return 0;
            int list = MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(mgr + 8)));
            if (list == 0) return 0;
            return MemoryHelper.ReadInt32(_hProcess, new IntPtr((uint)(list + 0x4C)));
        }
    }
}