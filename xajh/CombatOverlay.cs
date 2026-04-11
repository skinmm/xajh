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

        // The game has separate matrices for player display and camera state.
        // We keep a list of all locked matrices and write to all of them every
        // tick. The user picks them via the [D] sweep — typically one for the
        // player and one for the camera.
        private readonly List<(IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11)> _camMatrices
            = new List<(IntPtr, IntPtr, IntPtr, IntPtr)>();
        private float _yawOffset = 0f;

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
            float yaw = (float)Math.Atan2(dx, dz) + _yawOffset;

            if (_camMatrices.Count > 0)
            {
                WriteRotation2x2All(yaw);
            }
            else if (_camYawAddr != IntPtr.Zero)
            {
                MemoryHelper.WriteFloat(_hProcess, _camYawAddr, yaw);
            }
            else
            {
                return "[!] No matrices locked — press [D] to scan";
            }

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (d={dist:F0}, yaw={yaw:F2}, {_camMatrices.Count} mtx)";
        }

        /// <summary>
        /// Scan the player object for ALL 2x2 rotation submatrices and write
        /// a target yaw to each one. This finds authoritative/input/target
        /// rotation fields that the client-side display matrix doesn't cover.
        ///
        /// Previous [G] only wrote to +0x10..+0x20 which turned out to be the
        /// local display matrix (visible to you but not synced to the server).
        /// Most MMO player structs have multiple rotation fields packed
        /// together; this writes to all of them in one shot.
        /// </summary>
        public string FaceNearestAllPlayerMatrices(float px, float py, float pz, List<Npc> npcs)
        {
            var nearest = npcs
                .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                .FirstOrDefault();
            if (nearest == null) return null;

            int playerObj = GetPlayerObject();
            if (playerObj == 0) return "[!] Player object not found";

            float dx = nearest.X - px;
            float dz = nearest.Z - pz;
            float yaw = (float)Math.Atan2(dx, dz) + _yawOffset;
            float c = (float)Math.Cos(yaw);
            float s = (float)Math.Sin(yaw);

            var pObj = new IntPtr((uint)playerObj);
            const int ScanLen = 0x400;  // 1 KB of player struct
            var buf = new byte[ScanLen];
            MemoryHelper.ReadProcessMemory(_hProcess, pObj, buf, ScanLen, out int read);
            if (read < 0x20) return "[!] Player object read failed";

            // Find all offsets where a 2x2 rotation submatrix lives.
            // Layout: m00 at +off, m01 at +off+4, m10 at +off+12, m11 at +off+16
            // Row stride is 12 (three floats per row in a 3x3 matrix).
            var hits = new List<int>();
            for (int off = 0; off + 20 <= read; off += 4)
            {
                float a = BitConverter.ToSingle(buf, off);
                float b = BitConverter.ToSingle(buf, off + 4);
                float cc = BitConverter.ToSingle(buf, off + 12);
                float d = BitConverter.ToSingle(buf, off + 16);
                if (float.IsNaN(a + b + cc + d) || float.IsInfinity(a + b + cc + d)) continue;
                if (Math.Abs(a) > 1.01f || Math.Abs(b) > 1.01f ||
                    Math.Abs(cc) > 1.01f || Math.Abs(d) > 1.01f) continue;
                if (Math.Abs(a * a + b * b - 1f) > 0.03f) continue;   // row 0 unit
                if (Math.Abs(cc * cc + d * d - 1f) > 0.03f) continue;  // row 1 unit
                if (Math.Abs(a * cc + b * d) > 0.03f) continue;        // orthogonal
                if (Math.Abs(a) < 0.01f || Math.Abs(b) < 0.01f) continue; // skip identity/axis-aligned
                hits.Add(off);
            }

            // Write (c, -s, s, c) to each hit
            foreach (int off in hits)
            {
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, off + 4), -s);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, off + 12), s);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, off), c);
                MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, off + 16), c);
            }

            // Also try writing a single yaw float at common input-angle offsets.
            // Many games keep a radian yaw in the player struct separate from
            // the display matrix, in the +0x100..+0x200 range.
            var yawCandidates = new List<(int off, float oldVal)>();
            for (int off = 0; off + 4 <= read; off += 4)
            {
                float v = BitConverter.ToSingle(buf, off);
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                if (Math.Abs(v) > 0.01f && Math.Abs(v) <= 3.20f)
                    yawCandidates.Add((off, v));
            }

            double dist = Math.Sqrt(dx * dx + dz * dz);
            string hitsStr = string.Join(",", hits.ConvertAll(h => $"0x{h:X}"));
            return $"{nearest.Name} d={dist:F0} yaw={yaw:F2} → wrote {hits.Count} mtx at [{hitsStr}], {yawCandidates.Count} yaw-floats available (use [P] to dump)";
        }

        /// <summary>
        /// Dump the player object with all matrix block translations highlighted,
        /// and scan sub-objects for world position coordinates.
        /// </summary>
        public void DumpPlayerObject()
        {
            int playerObj = GetPlayerObject();
            if (playerObj == 0) { Console.WriteLine("[!] Player object not found"); return; }

            var pObj = new IntPtr((uint)playerObj);
            const int Len = 0x400;
            var buf = new byte[Len];
            MemoryHelper.ReadProcessMemory(_hProcess, pObj, buf, Len, out int read);

            Console.WriteLine($"\n── Player @ 0x{playerObj:X8} ──");

            // Show all 4 matrix block translations (translation = block + 0x24)
            // Block stride = 0x30 (48 bytes), blocks at +0x10, +0x40, +0x70, +0xA0
            Console.WriteLine("\n  Matrix block translations (potential world pos):");
            int[] blockStarts = { 0x10, 0x40, 0x70, 0xA0 };
            for (int b = 0; b < blockStarts.Length; b++)
            {
                int tOff = blockStarts[b] + 0x24;  // translation = rotation (9 floats = 0x24) + translation
                if (tOff + 12 > read) continue;
                float tx = BitConverter.ToSingle(buf, tOff);
                float ty = BitConverter.ToSingle(buf, tOff + 4);
                float tz = BitConverter.ToSingle(buf, tOff + 8);
                Console.WriteLine($"    Block {b} (+0x{blockStarts[b]:X2}): trans @ +0x{tOff:X3} = ({tx:F2}, {ty:F2}, {tz:F2})");
            }

            // Show the old position we've been reading
            if (read >= 0xA0)
            {
                float ox = BitConverter.ToSingle(buf, 0x94);
                float oy = BitConverter.ToSingle(buf, 0x98);
                float oz = BitConverter.ToSingle(buf, 0x9C);
                Console.WriteLine($"    OLD +0x094 (block 3 row 1): ({ox:F2}, {oy:F2}, {oz:F2})  ← what we were reading");
            }

            // Scan entire struct for any float in world-coordinate range (abs > 1000)
            Console.WriteLine("\n  All world-coord-range floats (|v| > 1000):");
            Console.WriteLine($"    {"Off",-8} {"value",12}  note");
            for (int i = 0; i + 4 <= read; i += 4)
            {
                float fv = BitConverter.ToSingle(buf, i);
                if (float.IsNaN(fv) || float.IsInfinity(fv)) continue;
                if (Math.Abs(fv) > 1000f && Math.Abs(fv) < 100000f)
                {
                    int iv = BitConverter.ToInt32(buf, i);
                    // Check if it looks like a pointer instead
                    if (iv > 0x00400000 && iv < 0x7FFFFFFF && Math.Abs(fv) > 50000f)
                        continue; // probably a pointer, not a coordinate

                    Console.WriteLine($"    +0x{i:X3}  {fv,12:F2}");
                }
            }

            // Follow pointers in the player struct and scan each sub-object
            Console.WriteLine("\n  Sub-object scan (following pointers for world coords):");
            for (int i = 0; i + 4 <= read && i < 0x100; i += 4)
            {
                int ptrVal = BitConverter.ToInt32(buf, i);
                if (ptrVal < 0x00100000 || ptrVal > 0x7FFFFFFF) continue;
                // Skip if it's too close to playerObj itself
                if (Math.Abs(ptrVal - playerObj) < 0x1000) continue;

                // Read 0x200 bytes of the sub-object
                var sub = new byte[0x200];
                if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr((uint)ptrVal), sub, 0x200, out int sr) || sr < 16)
                    continue;

                for (int j = 0; j + 4 <= sr; j += 4)
                {
                    float fv = BitConverter.ToSingle(sub, j);
                    if (float.IsNaN(fv) || float.IsInfinity(fv)) continue;
                    if (Math.Abs(fv) > 5000f && Math.Abs(fv) < 50000f)
                    {
                        // Found a world coord candidate — print a few neighbors
                        Console.Write($"    ptr +0x{i:X3} → 0x{ptrVal:X8} +0x{j:X3}: ");
                        for (int k = 0; k < 3 && j + k * 4 + 4 <= sr; k++)
                        {
                            float nv = BitConverter.ToSingle(sub, j + k * 4);
                            Console.Write($"{nv:F2}  ");
                        }
                        Console.WriteLine();
                        break;  // one hit per sub-object is enough
                    }
                }
            }
        }

        /// <summary>
        /// Direct player-matrix write (single matrix at +0x10..+0x20).
        /// Kept for [G] — use [X] for the multi-matrix version.
        /// </summary>
        public string FaceNearestDirect(float px, float py, float pz, List<Npc> npcs)
        {
            var nearest = npcs
                .OrderBy(n => Math.Pow(n.X - px, 2) + Math.Pow(n.Z - pz, 2))
                .FirstOrDefault();
            if (nearest == null) return null;

            int playerObj = GetPlayerObject();
            if (playerObj == 0) return "[!] Player object not found";

            float dx = nearest.X - px;
            float dz = nearest.Z - pz;
            float yaw = (float)Math.Atan2(dx, dz) + _yawOffset;
            float c = (float)Math.Cos(yaw);
            float s = (float)Math.Sin(yaw);

            var pObj = new IntPtr((uint)playerObj);
            float oldC = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(pObj, 0x10));
            float oldS = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(pObj, 0x1C));

            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, 0x14), -s);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, 0x1C), s);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, 0x10), c);
            MemoryHelper.WriteFloat(_hProcess, IntPtr.Add(pObj, 0x20), c);

            double dist = Math.Sqrt(dx * dx + dz * dz);
            return $"{nearest.Name} (d={dist:F0}, yaw={yaw:F2}, was c={oldC:F2} s={oldS:F2}) [DIRECT player@0x{playerObj:X8}]";
        }

        /// <summary>
        /// Write a yaw rotation to ALL locked 2x2 submatrices.
        /// Order matters: write off-diagonal cells first so a partial read
        /// stays close to orthogonal.
        /// </summary>
        private void WriteRotation2x2All(float yaw)
        {
            float c = (float)Math.Cos(yaw);
            float s = (float)Math.Sin(yaw);
            foreach (var m in _camMatrices)
            {
                MemoryHelper.WriteFloat(_hProcess, m.m01, -s);
                MemoryHelper.WriteFloat(_hProcess, m.m10, s);
                MemoryHelper.WriteFloat(_hProcess, m.m00, c);
                MemoryHelper.WriteFloat(_hProcess, m.m11, c);
            }
        }

        /// <summary>
        /// Write a single 2x2 submatrix — used during the scan sweep test.
        /// </summary>
        private void WriteRotation2x2Single(
            (IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11) m, float yaw)
        {
            float c = (float)Math.Cos(yaw);
            float s = (float)Math.Sin(yaw);
            MemoryHelper.WriteFloat(_hProcess, m.m01, -s);
            MemoryHelper.WriteFloat(_hProcess, m.m10, s);
            MemoryHelper.WriteFloat(_hProcess, m.m00, c);
            MemoryHelper.WriteFloat(_hProcess, m.m11, c);
        }

        /// <summary>
        /// Given a (cosAddr, sinAddr) pair found by ScanMatrixPairs (offset 12
        /// apart), find the full 2x2 rotation submatrix it belongs to.
        /// The pair could be either column 0 (cAddr=m00, sAddr=m10) or
        /// column 1 (cAddr=m01, sAddr=m11). Returns the layout that reads
        /// as a valid 2x2 rotation, or null if neither does.
        /// </summary>
        private (IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11)? ExpandToMatrix(
            IntPtr cAddr, IntPtr sAddr)
        {
            // Case A: pair is column 0
            var a = (m00: cAddr, m01: IntPtr.Add(cAddr, 4),
                     m10: sAddr, m11: IntPtr.Add(sAddr, 4));
            if (Is2x2Rotation(a.m00, a.m01, a.m10, a.m11)) return a;

            // Case B: pair is column 1
            var b = (m00: IntPtr.Add(cAddr, -4), m01: cAddr,
                     m10: IntPtr.Add(sAddr, -4), m11: sAddr);
            if (Is2x2Rotation(b.m00, b.m01, b.m10, b.m11)) return b;

            return null;
        }

        /// <summary>
        /// Verify that 4 floats form a valid 2x2 rotation matrix:
        /// rows are unit length and orthogonal to each other.
        /// </summary>
        private bool Is2x2Rotation(IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11)
        {
            try
            {
                float a = MemoryHelper.ReadFloat(_hProcess, m00);
                float b = MemoryHelper.ReadFloat(_hProcess, m01);
                float c = MemoryHelper.ReadFloat(_hProcess, m10);
                float d = MemoryHelper.ReadFloat(_hProcess, m11);
                if (float.IsNaN(a + b + c + d) || float.IsInfinity(a + b + c + d)) return false;
                if (Math.Abs(a) > 1.01f || Math.Abs(b) > 1.01f ||
                    Math.Abs(c) > 1.01f || Math.Abs(d) > 1.01f) return false;
                if (Math.Abs(a * a + b * b - 1f) > 0.03f) return false;  // row 0 unit
                if (Math.Abs(c * c + d * d - 1f) > 0.03f) return false;  // row 1 unit
                if (Math.Abs(a * c + b * d) > 0.03f) return false;        // orthogonal
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Find the camera rotation matrix using PAIRED-FLOAT scanning.
        ///
        /// The game stores rotation as cos/sin 12 bytes apart (same layout as
        /// the player matrix at +0x10/+0x1C). A single radian float doesn't
        /// exist — that's why all our previous yaw scans failed to rotate
        /// anything. We need to find both halves of the pair and write them
        /// together.
        ///
        /// Filter pipeline:
        ///   1. Scan all writable memory for (c, s) pairs at offset +12 where
        ///      both are in [-1.01, 1.01] AND c² + s² ≈ 1. This signature is
        ///      extremely strong — almost nothing in memory satisfies it by
        ///      chance.
        ///   2. Idle pass (2 sec): drop pairs that change while held still.
        ///   3. Drag rounds: keep pairs where BOTH values changed during a
        ///      mouse drag, then re-verify they're stable when idle again.
        ///   4. Manual confirmation: write a sweep of (cos θ, sin θ) for each
        ///      survivor and let the user press Y when their character snaps.
        /// </summary>
        public void ScanCameraYaw()
        {
            const int PairOffset = 12;   // matches player matrix +0x10 → +0x1C
            const float UnitTol = 0.03f; // c² + s² must be within this of 1
            const int FilterRounds = 4;

            Console.WriteLine("[*] Step 1: Scanning for cos/sin matrix pairs (offset=+12) ...");
            Console.WriteLine("[*] Stand still, do not touch mouse.");
            System.Threading.Thread.Sleep(300);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pairs = ScanMatrixPairs(PairOffset, UnitTol);
            sw.Stop();

            int playerObj = GetPlayerObject();
            if (playerObj != 0)
            {
                pairs = pairs.Where(p =>
                    p.cos.ToInt32() < playerObj || p.cos.ToInt32() > playerObj + 0x400).ToList();
            }
            Console.WriteLine($"[*] Initial: {pairs.Count} valid (c,s) pairs ({sw.ElapsedMilliseconds}ms)");

            if (pairs.Count == 0)
            {
                Console.WriteLine("[!] No matrix pairs found at offset +12. The camera may use a");
                Console.WriteLine("    different layout (offset +4, +16, or full 3x3 matrix).");
                return;
            }

            // Snapshot baselines
            var live = pairs.ToDictionary(p => p.cos, p => (sin: p.sin, cv: p.cv, sv: p.sv));

            // ── Idle blacklist pass ────────────────────────────────────────────
            Console.WriteLine("[*] Step 2: Idle blacklist. Hold completely still for 2 seconds ...");
            System.Threading.Thread.Sleep(2000);
            int killedIdle = 0;
            foreach (var kv in live.ToList())
            {
                float c = MemoryHelper.ReadFloat(_hProcess, kv.Key);
                float s = MemoryHelper.ReadFloat(_hProcess, kv.Value.sin);
                if (Math.Abs(c - kv.Value.cv) > 0.0005f || Math.Abs(s - kv.Value.sv) > 0.0005f)
                {
                    live.Remove(kv.Key); killedIdle++;
                }
                else
                {
                    live[kv.Key] = (kv.Value.sin, c, s);
                }
            }
            Console.WriteLine($"[*]   blacklisted {killedIdle} idle-changing pairs, {live.Count} stable");

            // ── Active drag rounds ─────────────────────────────────────────────
            int round = 0;
            while (live.Count > 6 && round < FilterRounds)
            {
                round++;
                Console.WriteLine($"\n[*] Round {round} ({live.Count} left): DRAG MOUSE freely (any direction), press a key when done ...");
                Console.ReadKey(true);

                var moved = new Dictionary<IntPtr, (IntPtr sin, float cv, float sv)>();
                foreach (var kv in live)
                {
                    float c = MemoryHelper.ReadFloat(_hProcess, kv.Key);
                    float s = MemoryHelper.ReadFloat(_hProcess, kv.Value.sin);
                    if (float.IsNaN(c) || float.IsNaN(s)) continue;
                    // Both halves must move, AND still be a unit pair
                    bool moved1 = Math.Abs(c - kv.Value.cv) > 0.005f;
                    bool moved2 = Math.Abs(s - kv.Value.sv) > 0.005f;
                    float mag = c * c + s * s;
                    if (moved1 && moved2 && Math.Abs(mag - 1f) < UnitTol)
                        moved[kv.Key] = (kv.Value.sin, c, s);
                }
                Console.WriteLine($"[*]   moved & still unit-length: {moved.Count}");

                if (moved.Count == 0)
                {
                    Console.WriteLine("[!] Nothing moved. Did you actually drag the mouse?");
                    continue;
                }

                Console.WriteLine("[*]   verifying — hold still for 1 second ...");
                System.Threading.Thread.Sleep(1000);
                var stable = new Dictionary<IntPtr, (IntPtr sin, float cv, float sv)>();
                foreach (var kv in moved)
                {
                    float c = MemoryHelper.ReadFloat(_hProcess, kv.Key);
                    float s = MemoryHelper.ReadFloat(_hProcess, kv.Value.sin);
                    if (Math.Abs(c - kv.Value.cv) <= 0.0005f && Math.Abs(s - kv.Value.sv) <= 0.0005f)
                        stable[kv.Key] = (kv.Value.sin, c, s);
                }
                Console.WriteLine($"[*]   stable after drag: {stable.Count}");
                if (stable.Count == 0)
                {
                    Console.WriteLine("[!] All movers kept ticking. Retrying round.");
                    continue;
                }
                live = stable;
            }

            if (live.Count == 0)
            {
                Console.WriteLine("[!] Lost all candidates.");
                return;
            }

            // ── Expand pairs to full 2x2 submatrices ──────────────────────────
            // The scanner found (c,s) pairs satisfying c²+s²=1, but each pair
            // is actually one COLUMN of a 2x2 rotation submatrix. We need both
            // columns to write a valid rotation. Try both layouts:
            //   Case A: pair is column 0 → m00=cAddr, m10=sAddr, m01=cAddr+4, m11=sAddr+4
            //   Case B: pair is column 1 → m01=cAddr, m11=sAddr, m00=cAddr-4, m10=sAddr-4
            var matrices = new List<(IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11)>();
            foreach (var kv in live)
            {
                var exp = ExpandToMatrix(kv.Key, kv.Value.sin);
                if (exp.HasValue) matrices.Add(exp.Value);
            }
            // Deduplicate (the same matrix can show up via column 0 and column 1)
            matrices = matrices.GroupBy(m => m.m00).Select(g => g.First()).ToList();

            if (matrices.Count == 0)
            {
                Console.WriteLine("[!] No survivors expanded to a valid 2x2 rotation matrix.");
                return;
            }

            Console.WriteLine($"\n── {matrices.Count} 2x2 rotation matrices ──");
            for (int i = 0; i < matrices.Count; i++)
            {
                var m = matrices[i];
                float a = MemoryHelper.ReadFloat(_hProcess, m.m00);
                float b = MemoryHelper.ReadFloat(_hProcess, m.m01);
                float c = MemoryHelper.ReadFloat(_hProcess, m.m10);
                float d = MemoryHelper.ReadFloat(_hProcess, m.m11);
                Console.WriteLine($"  [{i}] base=0x{m.m00.ToInt64():X8}  [{a:F2} {b:F2}; {c:F2} {d:F2}]");
            }

            // ── Persistence filter ────────────────────────────────────────────
            // Most rotation matrices in memory are SINKS — the player/NPC display
            // matrices that the engine overwrites every frame from camera state.
            // Writing to them does nothing visible because the next frame undoes
            // our write. We want SOURCES — the camera state that drives the player.
            //
            // Test: write a marker rotation, wait long enough for the game to
            // tick (≥100ms = ~6 frames at 60fps), read back. If our values stuck,
            // it's a source. If they reverted to something else, it's a sink.
            Console.WriteLine("\n[*] Step 3: Persistence test (filtering display matrices) ...");
            Console.WriteLine("[*] STAND STILL, do not move mouse or character.");
            System.Threading.Thread.Sleep(300);

            float markerYaw = (float)(Math.PI / 4);   // 45° — far from typical idle
            float mc = (float)Math.Cos(markerYaw);
            float ms = (float)Math.Sin(markerYaw);

            var persistent = new List<(IntPtr m00, IntPtr m01, IntPtr m10, IntPtr m11)>();
            foreach (var m in matrices)
            {
                float b00 = MemoryHelper.ReadFloat(_hProcess, m.m00);
                float b01 = MemoryHelper.ReadFloat(_hProcess, m.m01);
                float b10 = MemoryHelper.ReadFloat(_hProcess, m.m10);
                float b11 = MemoryHelper.ReadFloat(_hProcess, m.m11);

                MemoryHelper.WriteFloat(_hProcess, m.m01, -ms);
                MemoryHelper.WriteFloat(_hProcess, m.m10, ms);
                MemoryHelper.WriteFloat(_hProcess, m.m00, mc);
                MemoryHelper.WriteFloat(_hProcess, m.m11, mc);

                System.Threading.Thread.Sleep(150);

                float r00 = MemoryHelper.ReadFloat(_hProcess, m.m00);
                float r01 = MemoryHelper.ReadFloat(_hProcess, m.m01);
                float r10 = MemoryHelper.ReadFloat(_hProcess, m.m10);
                float r11 = MemoryHelper.ReadFloat(_hProcess, m.m11);

                bool stuck = Math.Abs(r00 - mc) < 0.05f &&
                             Math.Abs(r01 + ms) < 0.05f &&
                             Math.Abs(r10 - ms) < 0.05f &&
                             Math.Abs(r11 - mc) < 0.05f;

                // Restore in case it was a sink
                MemoryHelper.WriteFloat(_hProcess, m.m00, b00);
                MemoryHelper.WriteFloat(_hProcess, m.m01, b01);
                MemoryHelper.WriteFloat(_hProcess, m.m10, b10);
                MemoryHelper.WriteFloat(_hProcess, m.m11, b11);

                if (stuck)
                {
                    persistent.Add(m);
                    Console.WriteLine($"  ✓ [persist] base=0x{m.m00.ToInt64():X8}");
                }
            }

            Console.WriteLine($"[*] {persistent.Count}/{matrices.Count} matrices accept writes (sources)");
            if (persistent.Count == 0)
            {
                Console.WriteLine("[!] No matrix held a write. The camera is not stored as a 2x2 rotation");
                Console.WriteLine("    submatrix — it may be a quaternion, lookAt vector, or hidden behind");
                Console.WriteLine("    a different memory layout. Tell me what you see and I'll add a");
                Console.WriteLine("    quaternion/lookAt scanner.");
                return;
            }
            matrices = persistent;

            Console.WriteLine("\n[*] Step 4: Sweep test. I'll write a full 2x2 rotation for θ = 0, π/2, π, -π/2.");
            Console.WriteLine("    Watch your CHARACTER and CAMERA. For each candidate:");
            Console.WriteLine("      [Y] = lock this one and keep testing the rest (use for player AND camera)");
            Console.WriteLine("      [N] = skip to next");
            Console.WriteLine("      [Q] = stop testing");
            Console.WriteLine("    Lock at least 1 (player) and ideally 1 more (camera) so the back-trace follows.");

            float[] sweep = { 0f, (float)(Math.PI / 2), (float)Math.PI, -(float)(Math.PI / 2) };
            _camMatrices.Clear();

            for (int i = 0; i < matrices.Count; i++)
            {
                var m = matrices[i];
                float b00 = MemoryHelper.ReadFloat(_hProcess, m.m00);
                float b01 = MemoryHelper.ReadFloat(_hProcess, m.m01);
                float b10 = MemoryHelper.ReadFloat(_hProcess, m.m10);
                float b11 = MemoryHelper.ReadFloat(_hProcess, m.m11);

                Console.Write($"  [{i}] 0x{m.m00.ToInt64():X8} sweep ");
                bool matched = false, skip = false, abort = false;
                for (int k = 0; k < sweep.Length && !matched && !skip && !abort; k++)
                {
                    WriteRotation2x2Single(m, sweep[k]);
                    Console.Write($"→{sweep[k]:F2} ");

                    var t0 = Environment.TickCount;
                    while (Environment.TickCount - t0 < 700)
                    {
                        if (Console.KeyAvailable)
                        {
                            var k0 = Console.ReadKey(true).Key;
                            if (k0 == ConsoleKey.Y) { matched = true; break; }
                            if (k0 == ConsoleKey.N) { skip = true; break; }
                            if (k0 == ConsoleKey.Q) { abort = true; break; }
                        }
                        System.Threading.Thread.Sleep(20);
                    }
                }

                // Always restore the test writes — the next FaceNearest call
                // will set the correct yaw on locked matrices.
                MemoryHelper.WriteFloat(_hProcess, m.m00, b00);
                MemoryHelper.WriteFloat(_hProcess, m.m01, b01);
                MemoryHelper.WriteFloat(_hProcess, m.m10, b10);
                MemoryHelper.WriteFloat(_hProcess, m.m11, b11);

                if (matched)
                {
                    _camMatrices.Add(m);
                    Console.WriteLine($" [LOCKED — {_camMatrices.Count} total]");
                }
                else
                {
                    Console.WriteLine();
                }

                if (abort) break;
            }

            if (_camMatrices.Count == 0)
            {
                Console.WriteLine("\n[!] Nothing locked.");
                return;
            }

            Console.WriteLine($"\n[+] {_camMatrices.Count} matrices locked:");
            foreach (var m in _camMatrices)
                Console.WriteLine($"    base=0x{m.m00.ToInt64():X8}");
            Console.WriteLine("[*] Press [F] (or [A] for auto-face). Both player and camera matrices will");
            Console.WriteLine("    be written together each tick.");

        }

        /// <summary>
        /// Scans all writable memory for (cos, sin) pairs at the given byte
        /// offset, where both values are in [-1.01, 1.01] and c² + s² ≈ 1.
        /// </summary>
        private List<(IntPtr cos, IntPtr sin, float cv, float sv)> ScanMatrixPairs(int pairOffset, float unitTol)
        {
            var results = new List<(IntPtr, IntPtr, float, float)>();
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

                    int limit = bytesRead - 4 - pairOffset;
                    for (int i = 0; i <= limit; i += 4)
                    {
                        float c = BitConverter.ToSingle(buf, i);
                        if (float.IsNaN(c) || float.IsInfinity(c)) continue;
                        if (Math.Abs(c) > 1.01f) continue;

                        float s = BitConverter.ToSingle(buf, i + pairOffset);
                        if (float.IsNaN(s) || float.IsInfinity(s)) continue;
                        if (Math.Abs(s) > 1.01f) continue;

                        float mag = c * c + s * s;
                        if (Math.Abs(mag - 1f) > unitTol) continue;

                        // Skip pure (1,0) and (0,1) — too many false positives
                        if (Math.Abs(c) < 0.01f || Math.Abs(s) < 0.01f) continue;

                        results.Add((
                            IntPtr.Add(mbi.BaseAddress, i),
                            IntPtr.Add(mbi.BaseAddress, i + pairOffset),
                            c, s));
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
