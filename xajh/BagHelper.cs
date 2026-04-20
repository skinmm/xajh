using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace xajh
{
    /// <summary>
    /// Bag/Inventory operations: scan player's inventory, list items, use equipment (BUFF).
    ///
    /// Key game addresses:
    ///   0xDD4514 = playerObj global (player object pointer)
    ///   0x9A4870 = CSvClient::SendUseItem (packet 0x18A, 3 args + ECX=CSvClient)
    ///   0x9CD3B0 = CSvInventory::AddInventory (reveals item layout: size=0x90, items at +8)
    ///   0xDDD6C4 = CSvClient chain base ([+0x0C]+0x94 = CSvClient)
    ///
    /// Item structure (0x90 bytes each):
    ///   [+0x00] = field0 (item type ID via getter 0x82D480 / setter 0x8D5730)
    ///   [+0x04] = field1 (via getter 0x7F4FF0 / setter 0x8D5780)
    ///   [+0x0C] = UseItem arg1 (via getter 0x6E4290)
    ///   [+0x10] = UseItem arg2 (via getter 0x6E42B0)
    /// </summary>
    public class BagHelper
    {
        [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);
        [DllImport("kernel32.dll")] static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttr, uint stackSize, IntPtr lpStartAddr, IntPtr lpParam, uint flags, out uint lpThreadId);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr hObject, uint dwMilliseconds);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")] static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        readonly IntPtr _hProcess, _moduleBase;

        public BagHelper(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
            // Register Chinese code page provider if not already (needed for GB2312)
            try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        }

        public struct ItemInfo
        {
            public int Slot;
            public uint Addr;       // runtime addr of this item struct
            public uint Field0;     // [+0x00] item type ID
            public uint Field4;     // [+0x04]
            public uint Field8;     // [+0x08]
            public uint FieldC;     // [+0x0C] UseItem arg1
            public uint Field10;    // [+0x10] UseItem arg2
            public uint Field14;    // [+0x14]
            public uint Field18;    // [+0x18]
            public uint Field1C;    // [+0x1C]
        }

        /// <summary>Scan player object to find CSvInventory pointer candidates.
        /// Returns list of (player_offset, inventory_ptr) pairs that look like inventory.</summary>
        public List<(int off, uint ptr)> FindInventoryCandidates()
        {
            var result = new List<(int off, uint ptr)>();
            int playerObjPtr = MemoryHelper.ReadInt32(_hProcess, new IntPtr(0xDD4514));
            Console.WriteLine($"  [BagHelper] playerObj @ 0xDD4514 = 0x{playerObjPtr:X}");
            if (playerObjPtr == 0) return result;

            byte[] playerBuf = new byte[0x800];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(playerObjPtr), playerBuf, playerBuf.Length, out int pr))
            {
                Console.WriteLine("  [BagHelper] failed to read player obj");
                return result;
            }

            for (int off = 0; off < pr - 4; off += 4)
            {
                uint ptr = BitConverter.ToUInt32(playerBuf, off);
                if (ptr < 0x100000 || ptr > 0x40000000) continue;

                // Try to read [ptr+8] as if it's the first item.
                byte[] itemBuf = new byte[0x200];
                if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(ptr + 8), itemBuf, itemBuf.Length, out int ir)) continue;
                if (ir < 0x100) continue;

                uint f0_slot0 = BitConverter.ToUInt32(itemBuf, 0x00);
                uint f0_slot1 = BitConverter.ToUInt32(itemBuf, 0x90);
                if ((f0_slot0 > 0 && f0_slot0 < 0x100000) || (f0_slot1 > 0 && f0_slot1 < 0x100000))
                {
                    result.Add((off, ptr));
                }
            }
            return result;
        }

        /// <summary>Read N items from an inventory candidate. Returns non-empty items only.</summary>
        public List<ItemInfo> ReadItems(uint invPtr, int maxSlots = 40)
        {
            var items = new List<ItemInfo>();
            byte[] buf = new byte[8 + 0x90 * maxSlots];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(invPtr), buf, buf.Length, out int r)) return items;

            for (int i = 0; i < maxSlots; i++)
            {
                int baseOff = 8 + i * 0x90;
                if (baseOff + 0x20 > r) break;
                uint f00 = BitConverter.ToUInt32(buf, baseOff + 0x00);
                uint f04 = BitConverter.ToUInt32(buf, baseOff + 0x04);
                uint f08 = BitConverter.ToUInt32(buf, baseOff + 0x08);
                uint f0C = BitConverter.ToUInt32(buf, baseOff + 0x0C);
                uint f10 = BitConverter.ToUInt32(buf, baseOff + 0x10);
                uint f14 = BitConverter.ToUInt32(buf, baseOff + 0x14);
                uint f18 = BitConverter.ToUInt32(buf, baseOff + 0x18);
                uint f1C = BitConverter.ToUInt32(buf, baseOff + 0x1C);

                // Skip empty slots (all primary fields zero)
                if (f00 == 0 && f04 == 0 && f0C == 0 && f10 == 0) continue;

                items.Add(new ItemInfo
                {
                    Slot = i,
                    Addr = invPtr + (uint)baseOff,
                    Field0 = f00,
                    Field4 = f04,
                    Field8 = f08,
                    FieldC = f0C,
                    Field10 = f10,
                    Field14 = f14,
                    Field18 = f18,
                    Field1C = f1C,
                });
            }
            return items;
        }

        /// <summary>Full scan: find inventory candidates, dump items from each, print to console.</summary>
        public void ScanInventory()
        {
            var candidates = FindInventoryCandidates();
            Console.WriteLine($"  [BagHelper] Found {candidates.Count} inventory candidates");
            foreach (var (off, ptr) in candidates.Take(20))
            {
                Console.WriteLine($"    player+0x{off:X3} → 0x{ptr:X}");
                var items = ReadItems(ptr, 8);
                foreach (var it in items)
                {
                    Console.WriteLine($"      slot[{it.Slot}] @ 0x{it.Addr:X}: f00=0x{it.Field0:X} f04=0x{it.Field4:X} f08=0x{it.Field8:X} fC=0x{it.FieldC:X} f10=0x{it.Field10:X} f14=0x{it.Field14:X} f18=0x{it.Field18:X} f1C=0x{it.Field1C:X}");
                }
                if (items.Count == 0)
                    Console.WriteLine("      (no non-empty items)");
            }
        }

        /// <summary>Deeper inspection: dump 0x90 bytes of each slot for one candidate, following pointers.</summary>
        public void InspectCandidate(uint invPtr, int slotCount = 16)
        {
            Console.WriteLine($"  [InspectCandidate] invPtr=0x{invPtr:X}, item_size=0x90, slots={slotCount}");
            byte[] buf = new byte[8 + 0x90 * slotCount];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(invPtr), buf, buf.Length, out int r))
            {
                Console.WriteLine("    read failed");
                return;
            }
            Console.WriteLine($"    header [0..8]: {BitConverter.ToUInt32(buf, 0):X} {BitConverter.ToUInt32(buf, 4):X}");
            for (int i = 0; i < slotCount; i++)
            {
                int baseOff = 8 + i * 0x90;
                if (baseOff + 0x90 > r) break;

                // Check if slot is empty
                uint f00 = BitConverter.ToUInt32(buf, baseOff + 0x00);
                uint f04 = BitConverter.ToUInt32(buf, baseOff + 0x04);
                uint f0C = BitConverter.ToUInt32(buf, baseOff + 0x0C);
                uint f10 = BitConverter.ToUInt32(buf, baseOff + 0x10);
                if (f00 == 0 && f04 == 0 && f0C == 0 && f10 == 0) continue;

                Console.WriteLine($"    === slot[{i}] @ 0x{invPtr + (uint)baseOff:X} ===");
                // Dump full 0x90 as hex rows
                for (int j = 0; j < 0x90; j += 16)
                {
                    int o = baseOff + j;
                    if (o + 16 > r) break;
                    var chunk = new byte[16];
                    Array.Copy(buf, o, chunk, 0, 16);
                    var hex = BitConverter.ToString(chunk).Replace("-", " ");
                    // Also show as 4 DWORDs
                    uint d0 = BitConverter.ToUInt32(buf, o);
                    uint d1 = BitConverter.ToUInt32(buf, o + 4);
                    uint d2 = BitConverter.ToUInt32(buf, o + 8);
                    uint d3 = BitConverter.ToUInt32(buf, o + 12);
                    Console.WriteLine($"      +{j:X2}: {d0,10:X8} {d1,10:X8} {d2,10:X8} {d3,10:X8}");
                }

                // If any DWORD looks like a pointer, try to dereference and look for a string
                for (int j = 0; j < 0x40; j += 4)
                {
                    uint val = BitConverter.ToUInt32(buf, baseOff + j);
                    if (val > 0x400000 && val < 0x40000000)
                    {
                        byte[] str = new byte[64];
                        if (MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(val), str, str.Length, out int sr))
                        {
                            string s = ExtractGbkString(str, 0, Math.Min(sr, 32));
                            if (!string.IsNullOrEmpty(s) && s.Length >= 2)
                            {
                                Console.WriteLine($"      +{j:X2} → 0x{val:X} → \"{s}\"");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Follow a pointer and dump contents + attempt to find strings at various offsets.</summary>
        public void FollowPointer(uint ptr, int size = 0x200)
        {
            Console.WriteLine($"  [FollowPointer] @ 0x{ptr:X} ({size} bytes):");
            byte[] buf = new byte[size];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(ptr), buf, buf.Length, out int r))
            {
                Console.WriteLine("    read failed");
                return;
            }
            // Dump as DWORDs + try ASCII/GBK at each offset
            for (int i = 0; i < r; i += 16)
            {
                if (i + 16 > r) break;
                uint d0 = BitConverter.ToUInt32(buf, i);
                uint d1 = BitConverter.ToUInt32(buf, i + 4);
                uint d2 = BitConverter.ToUInt32(buf, i + 8);
                uint d3 = BitConverter.ToUInt32(buf, i + 12);
                // Also show ASCII of 16 bytes
                var chars = new System.Text.StringBuilder();
                for (int j = 0; j < 16; j++)
                {
                    byte b = buf[i + j];
                    chars.Append((b >= 0x20 && b < 0x7F) ? (char)b : '.');
                }
                Console.WriteLine($"    +{i:X3}: {d0,10:X8} {d1,10:X8} {d2,10:X8} {d3,10:X8}  {chars}");
            }

            // Try reading GBK string starting at offset 0 (maybe this IS a string)
            string asString = ExtractGbkString(buf, 0, Math.Min(r, 64));
            if (!string.IsNullOrEmpty(asString))
                Console.WriteLine($"    as GBK string: \"{asString}\"");

            // Try dereferencing each DWORD-aligned offset to see if it's a pointer to a string
            Console.WriteLine("    Pointers that look like strings:");
            for (int i = 0; i < r - 4; i += 4)
            {
                uint val = BitConverter.ToUInt32(buf, i);
                if (val < 0x400000 || val > 0x40000000) continue;
                byte[] str = new byte[64];
                if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(val), str, str.Length, out int sr)) continue;
                string s = ExtractGbkString(str, 0, Math.Min(sr, 32));
                if (!string.IsNullOrEmpty(s) && s.Length >= 2)
                {
                    Console.WriteLine($"    +{i:X3} → 0x{val:X} → \"{s}\"");
                }
            }
        }

        private static string ExtractGbkString(byte[] buf, int start, int maxLen)
        {
            // Find end of string (null terminator)
            int end = start;
            while (end < start + maxLen && end < buf.Length && buf[end] != 0) end++;
            int len = end - start;
            if (len < 2) return "";
            try
            {
                var gbk = System.Text.Encoding.GetEncoding("GB2312");
                var s = gbk.GetString(buf, start, len);
                // Filter out non-printable/garbage
                bool allPrint = true;
                foreach (var c in s)
                {
                    if (c < 0x20 && c != '\n' && c != '\t') { allPrint = false; break; }
                }
                if (!allPrint) return "";
                return s;
            }
            catch { return ""; }
        }

        /// <summary>Search all game memory for a specific item ID value.
        /// Tries multiple encodings: DWORD, WORD, DWORD+1, DWORD-1, and shifted patterns.</summary>
        public void SearchByItemId(uint itemId)
        {
            Console.WriteLine($"  [SearchByItemId] scanning for item ID 0x{itemId:X} ({itemId}) in multiple forms...");
            // Primary DWORD search
            SearchByItemIdCore(itemId, $"DWORD 0x{itemId:X}");
            // WORD search (16-bit) — only if value fits
            if (itemId < 0x10000)
            {
                SearchByItemIdWord((ushort)itemId, $"WORD 0x{itemId:X}");
            }
        }

        private void SearchByItemIdCore(uint itemId, string label)
        {
            byte[] target = BitConverter.GetBytes(itemId);
            var hits = new List<uint>();
            IntPtr addr = IntPtr.Zero;
            while (addr.ToInt64() < 0x40000000L)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                long regSize = (long)mbi.RegionSize.ToInt64();
                if (regSize <= 0) break;
                if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                {
                    int size = (int)Math.Min(regSize, 0x400000);
                    byte[] buf = new byte[size];
                    if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                    {
                        for (int i = 0; i <= br - 4; i += 4)
                        {
                            if (buf[i] == target[0] && buf[i + 1] == target[1] && buf[i + 2] == target[2] && buf[i + 3] == target[3])
                            {
                                hits.Add((uint)(mbi.BaseAddress.ToInt64() + i));
                                if (hits.Count > 2000) break;
                            }
                        }
                    }
                }
                if (hits.Count > 2000) break;
                addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
            }

            // Template filter
            var templateHits = new HashSet<uint>();
            hits.Sort();
            for (int i = 0; i + 2 < hits.Count; i++)
            {
                uint d1 = hits[i + 1] - hits[i];
                uint d2 = hits[i + 2] - hits[i + 1];
                if (d1 == d2 && d1 > 0 && d1 < 0x1000)
                {
                    templateHits.Add(hits[i]);
                    templateHits.Add(hits[i + 1]);
                    templateHits.Add(hits[i + 2]);
                }
            }
            var realInstances = hits.Where(h => !templateHits.Contains(h)).ToList();
            Console.WriteLine($"  [{label}] total={hits.Count}, template filtered={templateHits.Count}, real instances={realInstances.Count}");
            foreach (var hit in realInstances.Take(30))
            {
                byte[] ctx = new byte[32];
                if (MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(hit - 8), ctx, ctx.Length, out int cr) && cr >= 32)
                {
                    uint m2 = BitConverter.ToUInt32(ctx, 0);
                    uint m1 = BitConverter.ToUInt32(ctx, 4);
                    uint v = BitConverter.ToUInt32(ctx, 8);
                    uint p1 = BitConverter.ToUInt32(ctx, 12);
                    uint p2 = BitConverter.ToUInt32(ctx, 16);
                    uint p3 = BitConverter.ToUInt32(ctx, 20);
                    uint p4 = BitConverter.ToUInt32(ctx, 24);
                    uint p5 = BitConverter.ToUInt32(ctx, 28);
                    Console.WriteLine($"    0x{hit:X8}: [-8]=0x{m2:X} [-4]=0x{m1:X} [0]=0x{v:X} [+4]=0x{p1:X} [+8]=0x{p2:X} [+C]=0x{p3:X} [+10]=0x{p4:X} [+14]=0x{p5:X}");
                }
            }
        }

        private void SearchByItemIdWord(ushort itemId, string label)
        {
            byte[] target = BitConverter.GetBytes(itemId);
            var hits = new List<uint>();
            IntPtr addr = IntPtr.Zero;
            while (addr.ToInt64() < 0x40000000L)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                long regSize = (long)mbi.RegionSize.ToInt64();
                if (regSize <= 0) break;
                if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                {
                    int size = (int)Math.Min(regSize, 0x400000);
                    byte[] buf = new byte[size];
                    if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                    {
                        // Scan 2-byte aligned positions for WORD match.
                        // To reduce noise, only consider positions where the WORD immediately after
                        // is zero (i.e. the surrounding DWORD isn't a random thing starting with this WORD).
                        for (int i = 0; i <= br - 4; i += 2)
                        {
                            if (buf[i] == target[0] && buf[i + 1] == target[1])
                            {
                                // heuristic: the following WORD should be "reasonable" (low or meta)
                                // skip obvious false positives where the next value is large garbage
                                hits.Add((uint)(mbi.BaseAddress.ToInt64() + i));
                                if (hits.Count > 3000) break;
                            }
                        }
                    }
                }
                if (hits.Count > 3000) break;
                addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
            }

            // Filter: remove template-like strides (common differences)
            hits.Sort();
            var templateHits = new HashSet<uint>();
            for (int i = 0; i + 2 < hits.Count; i++)
            {
                uint d1 = hits[i + 1] - hits[i];
                uint d2 = hits[i + 2] - hits[i + 1];
                if (d1 == d2 && d1 > 0 && d1 < 0x1000)
                {
                    templateHits.Add(hits[i]);
                    templateHits.Add(hits[i + 1]);
                    templateHits.Add(hits[i + 2]);
                }
            }
            var realInstances = hits.Where(h => !templateHits.Contains(h)).ToList();
            Console.WriteLine($"  [{label}] total={hits.Count}, template filtered={templateHits.Count}, real instances={realInstances.Count} (showing 30 most interesting):");
            // Only show hits in HEAP range (where items typically live, 0x0A-0x20 million typically)
            var heapHits = realInstances.Where(h => h > 0x04000000 && h < 0x20000000).ToList();
            Console.WriteLine($"    Heap-range (0x04000000-0x20000000): {heapHits.Count}");
            foreach (var hit in heapHits.Take(30))
            {
                byte[] ctx = new byte[32];
                if (MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(hit - 8), ctx, ctx.Length, out int cr) && cr >= 32)
                {
                    ushort m4 = BitConverter.ToUInt16(ctx, 4);
                    ushort m2 = BitConverter.ToUInt16(ctx, 6);
                    ushort v = BitConverter.ToUInt16(ctx, 8);
                    ushort p1 = BitConverter.ToUInt16(ctx, 10);
                    ushort p2 = BitConverter.ToUInt16(ctx, 12);
                    ushort p3 = BitConverter.ToUInt16(ctx, 14);
                    ushort p4 = BitConverter.ToUInt16(ctx, 16);
                    Console.WriteLine($"    0x{hit:X8}: [-4]=0x{m4:X} [-2]=0x{m2:X} [0]=0x{v:X} [+2]=0x{p1:X} [+4]=0x{p2:X} [+6]=0x{p3:X} [+8]=0x{p4:X}");
                }
            }
        }

        /// <summary>Install a hook on SendUseItem (0x9A4870) that captures (arg1, arg2, arg3, ecx)
        /// to global scratch memory. User can then trigger buff manually (via game or 3rd party tool)
        /// and inspect captured values to learn the correct packet format.
        /// 
        /// Scratch layout:
        ///   0xDD5700 = captured arg1
        ///   0xDD5704 = captured arg2
        ///   0xDD5708 = captured arg3 (first 4 bytes of OID)
        ///   0xDD570C = captured ECX (CSvClient ptr)
        ///   0xDD5710 = call counter</summary>
        static uint _hookStubAddr = 0;
        public bool InstallUseItemHook()
        {
            if (_hookStubAddr != 0)
            {
                Console.WriteLine($"  [Hook] already installed at 0x{_hookStubAddr:X}");
                return true;
            }

            // Zero out scratch first so we can see fresh captures
            byte[] zeros = new byte[24];
            MemoryHelper.WriteProcessMemory(_hProcess, new IntPtr(0xDD5700), zeros, zeros.Length, out _);

            // Alloc stub
            IntPtr stub = VirtualAllocEx(_hProcess, IntPtr.Zero, 128, 0x3000, 0x40);
            if (stub == IntPtr.Zero) { Console.WriteLine("  [Hook] alloc failed"); return false; }
            uint stubAddr = (uint)stub.ToInt64();

            byte[] stubCode = new byte[] {
                0x60,                                                  // pushad
                0x9C,                                                  // pushfd
                0x8B, 0x44, 0x24, 0x28,                                // mov eax, [esp+0x28] (arg1)
                0xA3, 0x00, 0x57, 0xDD, 0x00,                          // mov [0xDD5700], eax
                0x8B, 0x44, 0x24, 0x2C,                                // mov eax, [esp+0x2C] (arg2)
                0xA3, 0x04, 0x57, 0xDD, 0x00,                          // mov [0xDD5704], eax
                0x8B, 0x44, 0x24, 0x30,                                // mov eax, [esp+0x30] (arg3)
                0xA3, 0x08, 0x57, 0xDD, 0x00,                          // mov [0xDD5708], eax
                0x8B, 0x44, 0x24, 0x1C,                                // mov eax, [esp+0x1C] (ECX)
                0xA3, 0x0C, 0x57, 0xDD, 0x00,                          // mov [0xDD570C], eax
                0xFF, 0x05, 0x10, 0x57, 0xDD, 0x00,                    // inc [0xDD5710]
                0x9D,                                                  // popfd
                0x61,                                                  // popad
                // Replay original 5 bytes: 55 8B EC 6A FF
                0x55, 0x8B, 0xEC, 0x6A, 0xFF,
                0xE9, 0x00, 0x00, 0x00, 0x00                           // jmp rel32 placeholder
            };
            uint jmpInsnAddr = stubAddr + (uint)stubCode.Length - 5;
            uint jmpTarget = 0x9A4875;
            int rel32 = (int)(jmpTarget - (jmpInsnAddr + 5));
            Array.Copy(BitConverter.GetBytes(rel32), 0, stubCode, stubCode.Length - 4, 4);

            MemoryHelper.WriteProcessMemory(_hProcess, stub, stubCode, stubCode.Length, out _);

            // Patch 0x9A4870 with JMP stubAddr
            byte[] patchJmp = new byte[5];
            patchJmp[0] = 0xE9;
            int patchRel = (int)(stubAddr - (0x9A4870 + 5));
            Array.Copy(BitConverter.GetBytes(patchRel), 0, patchJmp, 1, 4);

            if (!VirtualProtectEx(_hProcess, new IntPtr(0x9A4870), new UIntPtr(5), 0x40, out uint oldProt))
            {
                Console.WriteLine("  [Hook] VirtualProtect failed");
                return false;
            }
            MemoryHelper.WriteProcessMemory(_hProcess, new IntPtr(0x9A4870), patchJmp, 5, out _);
            VirtualProtectEx(_hProcess, new IntPtr(0x9A4870), new UIntPtr(5), oldProt, out _);

            _hookStubAddr = stubAddr;
            Console.WriteLine($"  [Hook] installed. Stub @ 0x{stubAddr:X}");
            Console.WriteLine($"  [Hook] Now trigger buff in-game, then press [Y] again to view captured args.");
            return true;
        }

        /// <summary>Read the captured args from hook scratch memory.</summary>
        public void ReadCapturedArgs()
        {
            byte[] buf = new byte[24];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(0xDD5700), buf, buf.Length, out int r) || r < 20)
            {
                Console.WriteLine("  [Hook] failed to read scratch");
                return;
            }
            uint arg1 = BitConverter.ToUInt32(buf, 0);
            uint arg2 = BitConverter.ToUInt32(buf, 4);
            uint arg3 = BitConverter.ToUInt32(buf, 8);
            uint ecx = BitConverter.ToUInt32(buf, 12);
            uint cnt = BitConverter.ToUInt32(buf, 16);
            Console.WriteLine($"  [Hook] counter={cnt} (times SendUseItem was called since hook install)");
            Console.WriteLine($"  [Hook] last arg1 = 0x{arg1:X} ({arg1})");
            Console.WriteLine($"  [Hook] last arg2 = 0x{arg2:X} ({arg2})");
            Console.WriteLine($"  [Hook] last arg3 = 0x{arg3:X} ({arg3})");
            Console.WriteLine($"  [Hook] last ECX  = 0x{ecx:X} (CSvClient ptr)");
        }

        /// <summary>Auto-find the single CSvClient instance by scanning for its vtable.
        /// Returns 0 if not found. Uses vtable 0xBBBA34 (CSvClient type).
        /// This is STABLE across game sessions because the vtable is in the .rdata section 
        /// and the class is a singleton.</summary>
        public uint FindCSvClient()
        {
            const uint csvClientVtable = 0xBBBA34;
            byte[] needle = BitConverter.GetBytes(csvClientVtable);
            IntPtr addr = IntPtr.Zero;
            while (addr.ToInt64() < 0x40000000L)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                long regSize = (long)mbi.RegionSize.ToInt64();
                if (regSize <= 0) break;
                // Only scan HEAP regions (type 0x20000 = MEM_PRIVATE for heap allocations)
                if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                {
                    long regionBase = mbi.BaseAddress.ToInt64();
                    // Skip module range (we know it's on heap)
                    if (regionBase < 0x01000000) { addr = new IntPtr(regionBase + regSize); continue; }
                    if (regionBase >= 0x20000000) { addr = new IntPtr(regionBase + regSize); continue; }
                    int size = (int)Math.Min(regSize, 0x400000);
                    byte[] buf = new byte[size];
                    if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                    {
                        for (int i = 0; i <= br - 4; i += 4)
                        {
                            if (buf[i] == needle[0] && buf[i + 1] == needle[1] && buf[i + 2] == needle[2] && buf[i + 3] == needle[3])
                            {
                                return (uint)(regionBase + i);
                            }
                        }
                    }
                }
                addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
            }
            return 0;
        }

        /// <summary>Automatic buff: finds CSvClient, then calls SendUseItem with bag/slot args.
        /// This is the full production-ready buff call.</summary>
        public bool AutoBuff(uint bagSlot, uint itemSlot, uint targetOid = 31351)
        {
            uint csvClient = FindCSvClient();
            if (csvClient == 0)
            {
                Console.WriteLine("  [AutoBuff] Could not find CSvClient (vtable 0xBBBA34). Game not fully loaded?");
                return false;
            }
            Console.WriteLine($"  [AutoBuff] CSvClient found @ 0x{csvClient:X}");

            byte[] a1 = BitConverter.GetBytes(bagSlot);
            byte[] a2 = BitConverter.GetBytes(itemSlot);
            byte[] a3 = BitConverter.GetBytes(targetOid);
            byte[] ec = BitConverter.GetBytes(csvClient);

            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, a3[0], a3[1], a3[2], a3[3],             // push targetOid (arg3)
                0x68, a2[0], a2[1], a2[2], a2[3],             // push itemSlot (arg2)
                0x68, a1[0], a1[1], a1[2], a1[3],             // push bagSlot (arg1)
                0xB9, ec[0], ec[1], ec[2], ec[3],             // mov ecx, CSvClient
                0xB8, 0x70, 0x48, 0x9A, 0x00,                 // mov eax, 0x9A4870 (SendUseItem)
                0xFF, 0xD0,                                   // call eax
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                GetExitCodeThread(thread, out uint exitCode);
                Console.WriteLine($"  [AutoBuff] bag={bagSlot} slot={itemSlot} tgt=0x{targetOid:X}, exitCode=0x{exitCode:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Find all heap objects whose first DWORD is the given vtable pointer.
        /// Useful for finding class instances without needing fragile pointer chains.</summary>
        public void FindInstancesByVtable(uint vtable)
        {
            Console.WriteLine($"  [FindInstancesByVtable] searching for objects with vtable=0x{vtable:X}...");
            byte[] needle = BitConverter.GetBytes(vtable);
            var hits = new List<uint>();
            IntPtr addr = IntPtr.Zero;
            while (addr.ToInt64() < 0x40000000L)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                long regSize = (long)mbi.RegionSize.ToInt64();
                if (regSize <= 0) break;
                if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                {
                    int size = (int)Math.Min(regSize, 0x400000);
                    byte[] buf = new byte[size];
                    if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                    {
                        for (int i = 0; i <= br - 4; i += 4)
                        {
                            if (buf[i] == needle[0] && buf[i + 1] == needle[1] && buf[i + 2] == needle[2] && buf[i + 3] == needle[3])
                            {
                                hits.Add((uint)(mbi.BaseAddress.ToInt64() + i));
                            }
                        }
                    }
                }
                addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
            }

            Console.WriteLine($"  Found {hits.Count} objects with this vtable:");
            // Filter to HEAP range (objects, not .rdata references)
            var heapOnly = hits.Where(h => h >= 0x01000000 && h < 0x20000000).ToList();
            Console.WriteLine($"  {heapOnly.Count} in heap range:");
            foreach (var h in heapOnly.Take(30))
                Console.WriteLine($"    0x{h:X}");
        }

        /// <summary>Recursively find pointer chains from stable module memory (.data globals)
        /// that eventually reach the target address. Returns chains as lists of offsets.
        /// Example result: 0xDC1234 → +0x10 → +0x94 → target</summary>
        public void FindChainsTo(uint target, int maxDepth = 3)
        {
            Console.WriteLine($"  [FindChainsTo] searching for stable chains to 0x{target:X} (max depth {maxDepth})...");
            // Collected "known good" addresses at each depth
            var level = new HashSet<uint> { target };
            // At each depth N, find what points to things in level[N-1]
            // At depth N, any hits in module range = winning chain
            var pointerMap = new Dictionary<uint, List<uint>>(); // target -> list of (address that contains this target)

            for (int d = 0; d < maxDepth; d++)
            {
                Console.WriteLine($"    Depth {d}: searching for pointers to {level.Count} addresses...");
                var nextLevel = new HashSet<uint>();
                var targets = new HashSet<uint>(level);

                IntPtr addr = IntPtr.Zero;
                int totalHits = 0;
                while (addr.ToInt64() < 0x40000000L)
                {
                    if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                    long regSize = (long)mbi.RegionSize.ToInt64();
                    if (regSize <= 0) break;
                    if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                    {
                        int size = (int)Math.Min(regSize, 0x400000);
                        byte[] buf = new byte[size];
                        if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                        {
                            for (int i = 0; i <= br - 4; i += 4)
                            {
                                uint val = BitConverter.ToUInt32(buf, i);
                                if (targets.Contains(val))
                                {
                                    uint hitAddr = (uint)(mbi.BaseAddress.ToInt64() + i);
                                    if (!pointerMap.ContainsKey(val)) pointerMap[val] = new List<uint>();
                                    if (pointerMap[val].Count < 10) pointerMap[val].Add(hitAddr);

                                    // Report stable hits immediately
                                    if (hitAddr >= 0x400000 && hitAddr < 0xE00000)
                                    {
                                        Console.WriteLine($"      STABLE @ 0x{hitAddr:X} → 0x{val:X} (depth {d})");
                                    }
                                    nextLevel.Add(hitAddr);
                                    totalHits++;
                                    if (totalHits > 2000) break;
                                }
                            }
                        }
                    }
                    if (totalHits > 2000) break;
                    addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
                }
                Console.WriteLine($"      {totalHits} total hits this depth");
                if (nextLevel.Count == 0) break;
                // Skip already-seen addresses at next depth
                level = new HashSet<uint>(nextLevel.Where(a => !targets.Contains(a)).Take(200));
                if (level.Count == 0) break;
            }

            // Report stable chains found
            Console.WriteLine("\n  [FindChainsTo] Summary of stable chains (module-range hits):");
            foreach (var kv in pointerMap)
            {
                foreach (var ptr in kv.Value)
                {
                    if (ptr >= 0x400000 && ptr < 0xE00000)
                    {
                        Console.WriteLine($"    STABLE 0x{ptr:X}  →  0x{kv.Key:X}");
                    }
                }
            }
        }

        /// <summary>Find all places in memory that contain a pointer to the given target address.
        /// Used to discover stable chains — e.g. if we captured ECX via hook and want to find a 
        /// reliable path from module globals.</summary>
        public void FindPointersTo(uint target)
        {
            Console.WriteLine($"  [FindPointersTo] scanning for pointers to 0x{target:X}...");
            byte[] needle = BitConverter.GetBytes(target);
            var hits = new List<uint>();
            IntPtr addr = IntPtr.Zero;
            while (addr.ToInt64() < 0x40000000L)
            {
                if (!MemoryHelper.VirtualQueryEx(_hProcess, addr, out var mbi, (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryHelper.MEMORY_BASIC_INFORMATION>())) break;
                long regSize = (long)mbi.RegionSize.ToInt64();
                if (regSize <= 0) break;
                if (mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Type & 0x1000000) == 0)
                {
                    int size = (int)Math.Min(regSize, 0x400000);
                    byte[] buf = new byte[size];
                    if (MemoryHelper.ReadProcessMemory(_hProcess, mbi.BaseAddress, buf, size, out int br))
                    {
                        for (int i = 0; i <= br - 4; i += 4)
                        {
                            if (buf[i] == needle[0] && buf[i + 1] == needle[1] && buf[i + 2] == needle[2] && buf[i + 3] == needle[3])
                            {
                                hits.Add((uint)(mbi.BaseAddress.ToInt64() + i));
                                if (hits.Count > 500) break;
                            }
                        }
                    }
                }
                if (hits.Count > 500) break;
                addr = new IntPtr(mbi.BaseAddress.ToInt64() + regSize);
            }
            Console.WriteLine($"  [FindPointersTo] found {hits.Count} locations containing 0x{target:X}");
            // Prioritize: module range (0x400000-0xE00000) = stable; heap = volatile
            var moduleHits = hits.Where(h => h >= 0x400000 && h < 0xE00000).ToList();
            Console.WriteLine($"    Stable (in module .data): {moduleHits.Count}");
            foreach (var h in moduleHits.Take(30))
                Console.WriteLine($"      0x{h:X}");
            var heapHits = hits.Where(h => h >= 0x04000000 && h < 0x20000000).ToList();
            Console.WriteLine($"    Heap (unstable, may change next run): {heapHits.Count}");
            foreach (var h in heapHits.Take(30))
                Console.WriteLine($"      0x{h:X}");
        }

        /// <summary>Call SendUseItem using the CORRECT ECX captured by the hook.
        /// This reads ECX from [0xDD570C] (scratch), which the hook wrote when the game 
        /// last called SendUseItem. This ECX is the real CSvClient UI handler that works.</summary>
        public bool UseEquipmentViaCapturedEcx(uint arg1, uint arg2, uint targetOid = 31351)
        {
            // Read captured ECX
            byte[] ecxBuf = new byte[4];
            if (!MemoryHelper.ReadProcessMemory(_hProcess, new IntPtr(0xDD570C), ecxBuf, 4, out _) || BitConverter.ToUInt32(ecxBuf, 0) == 0)
            {
                Console.WriteLine("  [UseEq2] No captured ECX! Run hook first and trigger an auto-buff.");
                return false;
            }
            uint ecx = BitConverter.ToUInt32(ecxBuf, 0);
            Console.WriteLine($"  [UseEq2] using captured ECX=0x{ecx:X}");

            byte[] a1 = BitConverter.GetBytes(arg1);
            byte[] a2 = BitConverter.GetBytes(arg2);
            byte[] a3 = BitConverter.GetBytes(targetOid);
            byte[] ec = BitConverter.GetBytes(ecx);

            byte[] sc = new byte[] {
                0x60,                                         // pushad
                0x68, a3[0], a3[1], a3[2], a3[3],             // push targetOid (arg3)
                0x68, a2[0], a2[1], a2[2], a2[3],             // push arg2
                0x68, a1[0], a1[1], a1[2], a1[3],             // push arg1
                0xB9, ec[0], ec[1], ec[2], ec[3],             // mov ecx, <captured>
                0xB8, 0x70, 0x48, 0x9A, 0x00,                 // mov eax, 0x9A4870
                0xFF, 0xD0,                                   // call eax
                0x61,                                         // popad
                0xC3                                          // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                GetExitCodeThread(thread, out uint ec2);
                Console.WriteLine($"  [UseEq2] arg1=0x{arg1:X} arg2=0x{arg2:X} tgt=0x{targetOid:X}, exitCode=0x{ec2:X}");
                CloseHandle(thread);
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return true;
        }

        /// <summary>Call CSvClient::SendUseItem (0x9A4870). Sends packet 0x18A.
        /// Args are item's [+0x0C] and [+0x10] fields, plus target OID.
        /// For a self-buff equipment: pass item.FieldC, item.Field10, self OID.</summary>
        public bool UseEquipment(uint arg1, uint arg2, uint targetOid = 31351)
        {
            byte[] a1 = BitConverter.GetBytes(arg1);
            byte[] a2 = BitConverter.GetBytes(arg2);
            byte[] a3 = BitConverter.GetBytes(targetOid);

            // Shellcode:
            //   pushad
            //   push a3 (targetOid)     — pushed first so becomes arg3
            //   push a2                 — arg2
            //   push a1                 — arg1
            //   mov eax, [0xDDD6C4]     — chain base
            //   test eax,eax / jz cleanup
            //   mov eax, [eax+0x0C]
            //   test eax,eax / jz cleanup
            //   mov ecx, [eax+0x94]     — CSvClient
            //   mov eax, 0x9A4870
            //   call eax
            //   jmp end
            // cleanup:
            //   add esp, 12
            // end:
            //   popad
            //   ret
            byte[] sc = new byte[] {
                0x60,                                                   // pushad
                0x68, a3[0], a3[1], a3[2], a3[3],                       // push targetOid
                0x68, a2[0], a2[1], a2[2], a2[3],                       // push arg2
                0x68, a1[0], a1[1], a1[2], a1[3],                       // push arg1
                0xA1, 0xC4, 0xD6, 0xDD, 0x00,                           // mov eax, [0xDDD6C4]
                0x85, 0xC0,                                             // test eax,eax
                0x74, 0x16,                                             // jz cleanup (+22)
                0x8B, 0x40, 0x0C,                                       // mov eax, [eax+0x0C]
                0x85, 0xC0,                                             // test eax,eax
                0x74, 0x0F,                                             // jz cleanup (+15)
                0x8B, 0x88, 0x94, 0x00, 0x00, 0x00,                     // mov ecx, [eax+0x94]
                0xB8, 0x70, 0x48, 0x9A, 0x00,                           // mov eax, 0x9A4870
                0xFF, 0xD0,                                             // call eax
                0xEB, 0x03,                                             // jmp end (+3)
                0x83, 0xC4, 0x0C,                                       // add esp, 12 (cleanup)
                0x61,                                                   // popad
                0xC3                                                    // ret
            };

            IntPtr alloc = VirtualAllocEx(_hProcess, IntPtr.Zero, (uint)sc.Length, 0x3000, 0x40);
            if (alloc == IntPtr.Zero) return false;
            MemoryHelper.WriteProcessMemory(_hProcess, alloc, sc, sc.Length, out _);
            uint tid;
            IntPtr thread = CreateRemoteThread(_hProcess, IntPtr.Zero, 0, alloc, IntPtr.Zero, 0, out tid);
            bool ok = false;
            if (thread != IntPtr.Zero)
            {
                WaitForSingleObject(thread, 2000);
                uint ec;
                GetExitCodeThread(thread, out ec);
                Console.WriteLine($"  [UseEquipment] arg1=0x{arg1:X} arg2=0x{arg2:X} tgt=0x{targetOid:X}, exitCode=0x{ec:X}");
                CloseHandle(thread);
                ok = true;
            }
            VirtualFreeEx(_hProcess, alloc, 0, 0x8000);
            return ok;
        }
    }
}
