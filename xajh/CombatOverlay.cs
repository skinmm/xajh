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
        /// Full memory scan for the camera yaw address (Cheat Engine approach):
        ///   1. Read the player's current facing yaw from rotation matrix
        ///   2. Scan ALL writable memory for that float value
        ///   3. User turns camera in-game
        ///   4. Read new yaw, filter candidates to those that changed to match
        ///   5. Repeat until 1-5 addresses remain — one is the camera yaw
        /// </summary>
        public void ScanCameraYaw()
        {
            float yaw1 = GetCurrentCameraYaw();
            Console.WriteLine($"[*] Current yaw: {yaw1:F4} rad ({yaw1 * 180f / Math.PI:F1} deg)");
            Console.WriteLine("[*] Scanning all memory for this float ...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candidates = MemoryHelper.ScanForFloat(_hProcess, yaw1, 0.02f);
            sw.Stop();
            Console.WriteLine($"[*] Found {candidates.Count} addresses in {sw.ElapsedMilliseconds}ms");

            if (candidates.Count == 0) { Console.WriteLine("[!] No matches."); return; }

            // Exclude addresses inside the player object (we know those aren't camera)
            int playerObj = GetPlayerObject();
            if (playerObj != 0)
            {
                candidates = candidates.Where(a =>
                    a.ToInt32() < playerObj || a.ToInt32() > playerObj + 0x400).ToList();
                Console.WriteLine($"[*] After excluding player object: {candidates.Count}");
            }

            // Filter loop
            while (candidates.Count > 5)
            {
                Console.WriteLine($"\n[*] {candidates.Count} candidates remaining.");
                Console.WriteLine("[*] TURN your camera in-game, then press any key ...");
                Console.ReadKey(true);

                float yaw2 = GetCurrentCameraYaw();
                Console.WriteLine($"[*] New yaw: {yaw2:F4} rad ({yaw2 * 180f / Math.PI:F1} deg)");

                if (Math.Abs(yaw1 - yaw2) < 0.05f)
                {
                    Console.WriteLine("[!] Yaw didn't change enough. Turn more and try again.");
                    continue;
                }

                candidates = MemoryHelper.FilterByFloat(_hProcess, candidates, yaw2, 0.05f);
                Console.WriteLine($"[*] After filter: {candidates.Count}");
                yaw1 = yaw2;
            }

            Console.WriteLine($"\n── Final candidates ({candidates.Count}) ──");
            foreach (var addr in candidates)
            {
                float val = MemoryHelper.ReadFloat(_hProcess, addr);
                Console.WriteLine($"  0x{addr.ToInt64():X8}  = {val:F4}");
            }

            if (candidates.Count > 0)
            {
                _camYawAddr = candidates[0];
                Console.WriteLine($"\n[+] Using 0x{_camYawAddr.ToInt64():X8} as camera yaw address.");
                Console.WriteLine("[*] Press [F] to test facing.");
            }
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
