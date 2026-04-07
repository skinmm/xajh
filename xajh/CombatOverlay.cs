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

            float oldCosA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x70));
            float oldSinA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x7C));
            float oldYaw = (float)Math.Atan2(oldSinA, oldCosA);

            // Matrix A: standard rotation [ cos -sin ; sin cos ]
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x70), cosY);   // A.m00
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x74), -sinY);  // A.m01
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x7C), sinY);   // A.m10
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0x80), cosY);   // A.m11

            // Matrix B: transpose (inverse rotation) [ cos sin ; -sin cos ]
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xA0), cosY);   // B.m00
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xA4), sinY);   // B.m01
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xAC), -sinY);  // B.m10
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(obj, 0xB0), cosY);   // B.m11

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (dist={dist:F0}, yaw {oldYaw:F2}->{yaw:F2})";
        }

        public void DumpPlayerFloats()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0)
            {
                Console.WriteLine("[!] Cannot read player object.");
                return;
            }

            var obj = new IntPtr((uint)playerObj);

            float cosA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x70));
            float sinA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x7C));
            float curYaw = (float)Math.Atan2(sinA, cosA);

            Console.WriteLine($"\n── Player 0x{playerObj:X8} ──");
            Console.WriteLine($"  Yaw: {curYaw:F4} rad ({curYaw * 180f / Math.PI:F1} deg)");
            Console.WriteLine($"  {"Offset",-8} {"Value",12}  Note");
            Console.WriteLine(new string('─', 65));

            for (int off = 0x70; off <= 0xC0; off += 4)
            {
                float val = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, off));
                string note = off switch
                {
                    0x70 => "  ← A.cos(yaw)",
                    0x74 => "  ← A.-sin(yaw)",
                    0x7C => "  ← A.sin(yaw)",
                    0x80 => "  ← A.cos(yaw)",
                    0x90 => "  ← 1.0 (Y-axis)",
                    0x94 => "  ← X",
                    0x98 => "  ← Y",
                    0x9C => "  ← Z",
                    0xA0 => "  ← B.cos(yaw)",
                    0xA4 => "  ← B.sin(yaw)",
                    0xAC => "  ← B.-sin(yaw)",
                    0xB0 => "  ← B.cos(yaw)",
                    0xC0 => "  ← 1.0 (Y-axis)",
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