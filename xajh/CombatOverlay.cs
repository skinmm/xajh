using System;
using System.Collections.Generic;
using System.Linq;

namespace xajh
{
    /// <summary>
    /// Turns the player toward NPCs by rotating the back-trace camera.
    ///
    /// The game uses a third-person "back-trace" camera — the player model
    /// follows the camera's yaw direction.  Moving the camera is what makes
    /// the player turn visually.  This is how xajhtoy.exe / zxxy.dll does
    /// it: write the camera yaw, and the player follows.
    ///
    /// Camera yaw scan: we use a known global pointer to find the camera
    /// object, then scan its fields for a yaw-like float that matches
    /// the player's current facing direction.
    /// </summary>
    public class CombatOverlay
    {
        private IntPtr _hProcess;
        private IntPtr _moduleBase;

        private IntPtr _camYawAddr = IntPtr.Zero;
        private bool _camSearchDone;

        // Known static offsets for camera pointer chains to try
        private static readonly int[] CamGlobalOffsets = {
            0x9E2C60, 0x9D4518, 0x9D451C, 0x9E2C64
        };

        public CombatOverlay(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
        }

        public string FaceNearest(float px, float py, float pz, List<Npc> npcs)
        {
            var nearest = npcs
                .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                .FirstOrDefault();
            if (nearest == null) return null;

            float dx = nearest.X - px;
            float dz = nearest.Z - pz;
            float yaw = (float)Math.Atan2(dx, dz);

            if (!_camSearchDone)
            {
                float curCamYaw = GetCurrentCameraYaw();
                _camYawAddr = FindCameraYawAddr(curCamYaw);
                _camSearchDone = true;
            }

            if (_camYawAddr == IntPtr.Zero)
                return "[!] Camera yaw addr not found — press [D] to scan";

            float oldYaw = MemoryHelper.ReadFloat(_hProcess, _camYawAddr);
            MemoryHelper.WriteFloat(_hProcess, _camYawAddr, yaw);

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (dist={dist:F0}, cam {oldYaw:F2}->{yaw:F2})";
        }

        /// <summary>
        /// Reads the player's current facing yaw from the rotation matrix.
        /// </summary>
        private float GetCurrentCameraYaw()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0) return 0f;
            var obj = new IntPtr((uint)playerObj);
            float cosA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x10));
            float sinA = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(obj, 0x1C));
            return (float)Math.Atan2(sinA, cosA);
        }

        /// <summary>
        /// Scans known global pointers and their sub-objects for a float
        /// matching the current camera/player yaw.  The camera yaw is a
        /// standalone float (not part of a rotation matrix) that the game
        /// reads each frame to orient the back-trace camera.
        /// </summary>
        private IntPtr FindCameraYawAddr(float expectedYaw)
        {
            int playerObj = GetPlayerObject();

            foreach (int globalOff in CamGlobalOffsets)
            {
                int basePtr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, globalOff));
                if (basePtr == 0 || basePtr < 0x10000) continue;

                // Search 3 levels of pointer indirection, up to 0x400 bytes each
                SearchPointerTree(new IntPtr((uint)basePtr), expectedYaw, playerObj, 0);
                if (_camYawAddr != IntPtr.Zero) return _camYawAddr;
            }

            return IntPtr.Zero;
        }

        private void SearchPointerTree(IntPtr baseAddr, float expectedYaw, int playerObj, int depth)
        {
            if (depth > 2 || _camYawAddr != IntPtr.Zero) return;
            if (baseAddr == IntPtr.Zero) return;

            int scanLen = depth == 0 ? 0x200 : 0x100;
            var buf = new byte[scanLen];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, baseAddr, buf, scanLen, out int read) || read < 8)
                return;

            // Skip if this is the player object itself
            if (baseAddr.ToInt32() == playerObj) return;

            for (int off = 0; off < read - 4; off += 4)
            {
                float val = BitConverter.ToSingle(buf, off);
                if (!float.IsNaN(val) && !float.IsInfinity(val) &&
                    Math.Abs(val) > 0.01f && Math.Abs(val) <= Math.PI + 0.5f &&
                    Math.Abs(val - expectedYaw) < 0.8f)
                {
                    IntPtr addr = IntPtr.Add(baseAddr, off);
                    Console.WriteLine($"[+] Camera yaw candidate: 0x{addr.ToInt64():X8} " +
                                      $"= {val:F4} (expected {expectedYaw:F4}, " +
                                      $"base=0x{baseAddr.ToInt64():X8}+0x{off:X})");
                    _camYawAddr = addr;
                    return;
                }
            }

            // Follow pointers at early offsets
            for (int off = 0; off < Math.Min(read, 0x40); off += 4)
            {
                int ptr = BitConverter.ToInt32(buf, off);
                if (ptr > 0x10000 && ptr < 0x7FFFFFFF && ptr != playerObj &&
                    ptr != baseAddr.ToInt32())
                {
                    SearchPointerTree(new IntPtr((uint)ptr), expectedYaw, playerObj, depth + 1);
                    if (_camYawAddr != IntPtr.Zero) return;
                }
            }
        }

        /// <summary>
        /// Interactive camera scan: dumps a wide area around all known global
        /// pointers, showing candidate yaw fields.  Use [D] twice (before/after
        /// turning the camera) to identify which field is the camera yaw.
        /// </summary>
        public float[] DumpCameraWide()
        {
            float playerYaw = GetCurrentCameraYaw();
            Console.WriteLine($"\n── Camera scan — player yaw: {playerYaw:F4} rad ──\n");

            var allFloats = new List<(IntPtr addr, float val, string path)>();

            foreach (int globalOff in CamGlobalOffsets)
            {
                int basePtr = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(_moduleBase, globalOff));
                if (basePtr == 0 || basePtr < 0x10000) continue;

                string label = $"[base+0x{globalOff:X}]";
                ScanAndCollect(new IntPtr((uint)basePtr), label, allFloats, 0);
            }

            Console.WriteLine($"  {"Address",-14} {"Value",10}  Path");
            Console.WriteLine(new string('─', 60));

            var result = new float[allFloats.Count];
            for (int i = 0; i < allFloats.Count; i++)
            {
                var (addr, val, path) = allFloats[i];
                result[i] = val;
                string mark = Math.Abs(val - playerYaw) < 0.8f ? " ← MATCH" : "";
                Console.WriteLine($"  0x{addr.ToInt64():X8}  {val,10:F4}  {path}{mark}");
            }
            Console.WriteLine();
            return result;
        }

        private void ScanAndCollect(IntPtr baseAddr, string pathPrefix,
            List<(IntPtr, float, string)> results, int depth)
        {
            if (depth > 1) return;
            int playerObj = GetPlayerObject();
            if (baseAddr.ToInt32() == playerObj) return;

            int scanLen = 0x200;
            var buf = new byte[scanLen];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, baseAddr, buf, scanLen, out int read) || read < 8)
                return;

            for (int off = 0; off < read - 4; off += 4)
            {
                float val = BitConverter.ToSingle(buf, off);
                if (!float.IsNaN(val) && !float.IsInfinity(val) &&
                    Math.Abs(val) > 0.01f && Math.Abs(val) <= Math.PI + 0.5f)
                {
                    results.Add((IntPtr.Add(baseAddr, off), val, $"{pathPrefix}+0x{off:X}"));
                }
            }

            if (depth < 1)
            {
                for (int off = 0; off < Math.Min(read, 0x40); off += 4)
                {
                    int ptr = BitConverter.ToInt32(buf, off);
                    if (ptr > 0x10000 && ptr < 0x7FFFFFFF && ptr != playerObj &&
                        ptr != baseAddr.ToInt32())
                    {
                        ScanAndCollect(new IntPtr((uint)ptr), $"{pathPrefix}->+0x{off:X}", results, depth + 1);
                    }
                }
            }
        }

        public static void CompareDumps(float[] before, float[] after)
        {
            if (before == null || after == null) return;
            Console.WriteLine("── Changed values ──");
            int count = Math.Min(before.Length, after.Length);
            for (int i = 0; i < count; i++)
            {
                if (Math.Abs(before[i] - after[i]) > 0.001f)
                    Console.WriteLine($"  [{i}]  {before[i],10:F4} -> {after[i],10:F4}");
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
