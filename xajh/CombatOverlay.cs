using System;
using System.Collections.Generic;
using System.Linq;

namespace xajh
{
    /// <summary>
    /// Faces the player toward NPCs by writing the rotation matrix directly
    /// into the player object's memory.
    ///
    /// Player object layout — 4x3 column-major transform matrix:
    ///
    ///   +0x080  m00  cos(yaw)     Row0 of rotation
    ///   +0x084  m01  0
    ///   +0x088  m02  0
    ///   +0x08C  m10  0            Row1 (Y-axis, unchanged for yaw-only)
    ///   +0x090  m11  1
    ///   +0x094  tx   X position
    ///   +0x098  ty   Y position
    ///   +0x09C  tz   Z position
    ///   +0x0A0  ???  (not facing — unknown field)
    ///   +0x0A4  m20  sin(yaw)     Row2 of rotation
    ///   +0x0A8  m21  0
    ///   +0x0AC  m22  -sin(yaw)    (negative for standard right-hand yaw)
    ///   +0x0B0  m00' cos(yaw)     (duplicate / shadow copy)
    ///
    /// The game uses a Y-up, right-handed coordinate system.
    /// Yaw rotation around Y axis:
    ///   cos(θ) goes to +0x080 and +0x0B0
    ///   sin(θ) goes to +0x0A4
    ///  -sin(θ) goes to +0x0AC
    ///
    /// Confirmed from dump:  original yaw ≈ 1.43 rad
    ///   cos(1.43) ≈ 0.1411  →  +0x080 = 0.1411, +0x0B0 = 0.1411  ✓
    ///   sin(1.43) ≈ 0.9900  →  +0x0A4 = 0.9900                   ✓
    ///  -sin(1.43) ≈ -0.990  →  +0x0AC = -0.9900                  ✓
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

        /// <summary>
        /// Turns the player to face the nearest NPC by writing the rotation
        /// matrix elements (cos/sin of yaw).
        /// </summary>
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

            float oldCos = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x80));
            float oldYaw = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, oldCos)));

            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x80), cosY);     // m00
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xA4), sinY);     // m20
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xAC), -sinY);    // m22
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xB0), cosY);     // m00'

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (dist={dist:F0}, yaw {oldYaw:F2}->{yaw:F2})";
        }

        /// <summary>
        /// Dumps floats from +0x070 to +0x0C0 for debugging.
        /// Pauses the main loop — call this only when paused.
        /// </summary>
        public void DumpPlayerFloats()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0)
            {
                Console.WriteLine("[!] Cannot read player object.");
                return;
            }

            var obj = new IntPtr((uint)playerObj);

            float m00 = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x80));
            float m20 = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0xA4));
            float curYaw = (float)Math.Atan2(m20, m00);

            Console.WriteLine($"\n── Player 0x{playerObj:X8} — rotation matrix + position ──");
            Console.WriteLine($"  Current yaw: {curYaw:F4} rad ({curYaw * 180f / Math.PI:F1} deg)");
            Console.WriteLine($"  {"Offset",-8} {"Value",12}  Note");
            Console.WriteLine(new string('─', 60));

            for (int off = 0x70; off <= 0xC0; off += 4)
            {
                float val = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, off));
                string note = off switch
                {
                    0x80 => $"  ← cos(yaw) = {Math.Cos(curYaw):F4}",
                    0x94 => "  ← X",
                    0x98 => "  ← Y",
                    0x9C => "  ← Z",
                    0xA4 => $"  ← sin(yaw) = {Math.Sin(curYaw):F4}",
                    0xAC => $"  ← -sin(yaw)",
                    0xB0 => $"  ← cos(yaw) copy",
                    _ => ""
                };

                if (!float.IsNaN(val) && !float.IsInfinity(val))
                    Console.WriteLine($"  +0x{off:X3}   {val,12:F4}{note}");
                else
                    Console.WriteLine($"  +0x{off:X3}   {"(invalid)",12}{note}");
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