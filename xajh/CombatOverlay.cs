using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Faces the player toward NPCs by writing the yaw angle directly into
    /// the player object's memory.
    ///
    /// Why this works when CreateRemoteThread didn't:
    ///   The game function at 0x6ACCC0 runs on a separate OS thread via
    ///   CreateRemoteThread — the result is overwritten by the game's main
    ///   loop on the very next frame.  zxxy.dll (inside the game process)
    ///   either hooks the main loop or writes the facing angle directly.
    ///   Writing the float value into the player object is the simplest
    ///   reliable approach from an external process.
    ///
    /// Player object layout (same struct as NPC):
    ///   +0x94  float X
    ///   +0x98  float Y
    ///   +0x9C  float Z
    ///   +0xA0  float facing yaw (radians, 0 = +Z axis, increases CW)
    ///
    /// If +0xA0 is not the facing field, press [D] to dump nearby floats
    /// so you can identify the correct offset.
    /// </summary>
    public class CombatOverlay
    {
        private IntPtr _hProcess;
        private IntPtr _moduleBase;

        public static int OffFacing = 0xA0;

        public CombatOverlay(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
        }

        /// <summary>
        /// Turns the player to face the nearest NPC by writing the yaw angle.
        /// Returns the NPC name, or null if nothing to face.
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

            IntPtr facingAddr = IntPtr.Add(new IntPtr((uint)playerObj), OffFacing);
            float oldYaw = MemoryHelper.ReadFloat(_hProcess, facingAddr);
            bool ok = MemoryHelper.WriteFloat(_hProcess, facingAddr, yaw);

            double dist = Math.Sqrt(dx * dx + dz * dz);
            string result = $"{nearest.Name} (dist={dist:F0}, yaw {oldYaw:F2}->{yaw:F2})";
            if (!ok) return null;
            return result;
        }

        /// <summary>
        /// Dumps floats around the player's position offsets so you can
        /// identify the correct facing offset visually.
        /// Turn your character in-game and press [D] again to see which
        /// float value changed — that's the facing field.
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
            Console.WriteLine($"\n── Player object 0x{playerObj:X8} — floats near position ──");
            Console.WriteLine($"  {"Offset",-8} {"Value",12}  Note");
            Console.WriteLine(new string('─', 50));

            for (int off = 0x80; off <= 0xC0; off += 4)
            {
                float val = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, off));
                string note = "";
                if (off == 0x94) note = "  ← X";
                else if (off == 0x98) note = "  ← Y";
                else if (off == 0x9C) note = "  ← Z";
                else if (off == OffFacing) note = "  ← facing (current)";

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