using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

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
        /// Full memory scan for the camera yaw address.
        ///
        /// The camera yaw may use a different zero-direction or range than the
        /// player's rotation matrix, so we can't scan for the player's yaw value.
        /// Instead:
        ///   1. Scan all writable memory for ANY float in the angle range [-4, 4]
        ///   2. Snapshot all their values
        ///   3. User turns camera
        ///   4. Filter to addresses whose value CHANGED (the camera yaw moved)
        ///   5. Exclude addresses inside the player object
        ///   6. Repeat until ≤10 candidates remain
        /// </summary>
        public void ScanCameraYaw()
        {
            Console.WriteLine("[*] Step 1: Stand still. Scanning all angle-range floats ...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candidates = ScanAngleFloats();
            sw.Stop();

            int playerObj = GetPlayerObject();
            if (playerObj != 0)
            {
                candidates = candidates.Where(c =>
                    c.addr.ToInt32() < playerObj || c.addr.ToInt32() > playerObj + 0x400).ToList();
            }

            Console.WriteLine($"[*] Found {candidates.Count} angle-range addresses in {sw.ElapsedMilliseconds}ms");

            int round = 0;
            while (candidates.Count > 10)
            {
                round++;
                Console.WriteLine($"\n[*] Round {round}: {candidates.Count} candidates.");
                Console.WriteLine("[*] TURN your camera in-game, then press any key ...");
                Console.ReadKey(true);

                // Keep only addresses whose value changed
                var filtered = new List<(IntPtr addr, float oldVal)>();
                foreach (var (addr, oldVal) in candidates)
                {
                    float newVal = MemoryHelper.ReadFloat(_hProcess, addr);
                    if (Math.Abs(newVal - oldVal) > 0.02f &&
                        !float.IsNaN(newVal) && !float.IsInfinity(newVal) &&
                        Math.Abs(newVal) <= 4f)
                    {
                        filtered.Add((addr, newVal));
                    }
                }

                Console.WriteLine($"[*] Changed: {filtered.Count} (from {candidates.Count})");
                candidates = filtered;

                if (filtered.Count == 0)
                {
                    Console.WriteLine("[!] No addresses changed. Make sure you turned the camera.");
                    break;
                }
            }

            Console.WriteLine($"\n── Final candidates ({candidates.Count}) ──");
            foreach (var (addr, val) in candidates)
            {
                float cur = MemoryHelper.ReadFloat(_hProcess, addr);
                Console.WriteLine($"  0x{addr.ToInt64():X8}  = {cur:F4}");
            }

            if (candidates.Count > 0)
            {
                _camYawAddr = candidates[0].addr;
                Console.WriteLine($"\n[+] Using 0x{_camYawAddr.ToInt64():X8} as camera yaw address.");
                Console.WriteLine("[*] Press [F] to test facing.");
            }
        }

        /// <summary>
        /// Scans all writable memory for floats in the angle range [-4, 4].
        /// Returns the address and current value of each match.
        /// </summary>
        private List<(IntPtr addr, float val)> ScanAngleFloats()
        {
            var results = new List<(IntPtr, float)>();
            IntPtr address = IntPtr.Zero;

            while (true)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, address,
                        out var mbi, (uint)Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>()))
                    break;

                bool writable = mbi.State == MemoryHelper.MEM_COMMIT &&
                                (mbi.Protect == MemoryHelper.PAGE_READWRITE ||
                                 mbi.Protect == MemoryHelper.PAGE_EXECUTE_READWRITE);

                if (writable)
                {
                    long regionSize = mbi.RegionSize.ToInt64();
                    var buf = new byte[regionSize];
                    MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress,
                        buf, (int)regionSize, out int bytesRead);

                    for (int i = 0; i <= bytesRead - 4; i += 4)
                    {
                        float fv = BitConverter.ToSingle(buf, i);
                        if (!float.IsNaN(fv) && !float.IsInfinity(fv) &&
                            Math.Abs(fv) > 0.1f && Math.Abs(fv) <= 4f)
                        {
                            results.Add((IntPtr.Add(mbi.BaseAddress, i), fv));
                        }
                    }
                }

                long next = address.ToInt64() + mbi.RegionSize.ToInt64();
                if (next <= 0 || next >= long.MaxValue) break;
                address = new IntPtr(next);
            }

            return results;
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
