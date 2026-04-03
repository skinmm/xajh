using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace xajh
{
    public class Enemy
    {
        public IntPtr BaseAddress { get; set; }
        public int HP { get; set; }
        public int MaxHP { get; set; }
        public int OID { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public bool IsAlive => HP > 0;
        public bool IsValid { get; set; }

        public override string ToString() =>
            $"[0x{BaseAddress.ToInt64():X}] OID={OID,6}  HP={HP}/{MaxHP}  Pos=({PosX:F0},{PosY:F0},{PosZ:F0})";
    }

    public class EntityManager
    {
        // ── Static globals (RVAs from ImageBase 0x400000) ────────────────────
        public static long MgrPtrOffset = 0x9E2C60;
        public static long EntityListOffset = 0xA14B80;
        public static long EntityCountStatic = 0xA15DDC;
        public static int EntityStride = 4;

        // ── Confirmed per-entity offsets ──────────────────────────────────────
        // Each pointer from the entity list lands at a record where:
        //   +0x000  HP      (int32)
        //   +0x004  MaxHP   (int32)
        //   +0x008  type tag (4-char ASCII: "n33r", "w33n", etc.)
        //   +0x02C  -1 (no OID) | 1 (has OID in +0x030)
        //   +0x030  OID / template ID  (creature.btb key)
        //   +0x038  ptr A
        //   +0x03C  ptr B
        //   +0x040  ptr C
        //   +0x044  ptr D
        //
        // Position offsets TBD — need 0x400-byte dump to locate.
        public static int OffHP = 0x000;
        public static int OffMaxHP = 0x004;
        public static int OffOIDFlg = 0x02C;   // 1 = OID valid
        public static int OffOID = 0x030;   // template ID when flag==1

        // Position — NOT YET FOUND (still 0,0,0)
        public static int OffPosX = -1;      // -1 = unknown
        public static int OffPosY = -1;
        public static int OffPosZ = -1;

        // ─────────────────────────────────────────────────────────────────────
        private readonly IntPtr _hProcess;
        private readonly IntPtr _moduleBase;

        public EntityManager(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
        }

        /// <summary>
        /// Returns ALL entities from the list: monsters, NPCs, and zone managers.
        /// The caller can filter by HP range, OID, or firstDword as needed.
        /// </summary>
        public List<Enemy> GetAllEntities()
        {
            var list = new List<Enemy>();
            int count = MemoryHelper.ReadInt32(
                _hProcess, IntPtr.Add(_moduleBase, (int)EntityCountStatic));
            if (count <= 0 || count > 100000) return list;

            var arrayBase = IntPtr.Add(_moduleBase, (int)EntityListOffset);
            for (int i = 0; i < count; i++)
            {
                int raw = MemoryHelper.ReadInt32(_hProcess,
                    IntPtr.Add(arrayBase, i * EntityStride));
                if (raw == 0) continue;
                var ep = new IntPtr((uint)raw);
                var e = TryReadEntity(ep);
                if (e != null) list.Add(e);
            }
            return list;
        }

        /// <summary>
        /// Returns only monster/NPC entities — excludes zone-manager containers.
        /// A zone manager is identified by having HP > MaxHP OR MaxHP > 100,000.
        /// Template ID (OID) is included when the flag at +0x02C equals 1.
        /// </summary>
        public List<Enemy> GetEnemies()
        {
            var enemies = new List<Enemy>();
            foreach (var e in GetAllEntities())
            {
                // Skip zone manager containers (huge HP or HP > MaxHP)
                if (e.MaxHP <= 0 || e.MaxHP > 100_000) continue;
                if (e.HP > e.MaxHP || e.HP <= 0) continue;
                enemies.Add(e);
            }
            return enemies;
        }

        private Enemy TryReadEntity(IntPtr ep)
        {
            try
            {
                int hp = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(ep, OffHP));
                int maxHp = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(ep, OffMaxHP));

                // Minimal sanity — both must be positive
                if (hp <= 0 || maxHp <= 0) return null;

                // Read OID if flag is set
                int oidFlag = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(ep, OffOIDFlg));
                int oid = 0;
                if (oidFlag == 1)
                    oid = MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(ep, OffOID));

                // Position — zero until offsets confirmed
                float x = 0, y = 0, z = 0;
                if (OffPosX > 0)
                {
                    x = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(ep, OffPosX));
                    y = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(ep, OffPosY));
                    z = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(ep, OffPosZ));
                }

                return new Enemy
                {
                    BaseAddress = ep,
                    HP = hp,
                    MaxHP = maxHp,
                    OID = oid,
                    PosX = x,
                    PosY = y,
                    PosZ = z,
                    IsValid = true
                };
            }
            catch { return null; }
        }

        public bool KillEnemy(Enemy e)
        {
            if (!e.IsValid || e.BaseAddress == IntPtr.Zero) return false;
            return MemoryHelper.WriteInt32(_hProcess, IntPtr.Add(e.BaseAddress, OffHP), 0);
        }

        public int KillAll()
        {
            int k = 0;
            foreach (var e in GetEnemies()) if (KillEnemy(e)) k++;
            return k;
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        /// <summary>
        /// Dumps entity memory from offset 0 to 'length'.
        /// Pass length=0x400 to see past the +0x200 boundary where position may live.
        /// Marks fields that look like float coordinates in game range.
        /// </summary>
        public void DumpEntity(IntPtr ep, int length = 0x400)
        {
            var buf = new byte[length];
            MemoryHelper.ReadProcessMemory(_hProcess, ep, buf, length, out _);
            Console.WriteLine($"\n── Entity @ 0x{ep.ToInt64():X} (len=0x{length:X}) ──");
            Console.WriteLine($"  {"Off",-6} {"i32":>12} {"float":>12}  hex");
            Console.WriteLine(new string('─', 60));
            for (int i = 0; i <= length - 4; i += 4)
            {
                int iv = BitConverter.ToInt32(buf, i);
                float fv = BitConverter.ToSingle(buf, i);
                string hex = $"{buf[i]:X2}{buf[i + 1]:X2}{buf[i + 2]:X2}{buf[i + 3]:X2}";

                // Flag values that look like game world coordinates (float range)
                string mark = "";
                if (!float.IsNaN(fv) && !float.IsInfinity(fv))
                {
                    float af = Math.Abs(fv);
                    // X/Y coords: typically 0–15000, Z (height): 0–2000
                    if (af > 30f && af < 20000f && fv != 0f)
                        mark = $"  ← float {fv:F1}";
                }

                Console.WriteLine($"  +0x{i:X3}  {iv,12}  {fv,12:F2}  {hex}{mark}");
            }
        }

        /// <summary>
        /// Dumps a raw memory region (for sub-object exploration).
        /// </summary>
        public void DumpRaw(IntPtr addr, int length = 0x100)
        {
            var buf = new byte[length];
            MemoryHelper.ReadProcessMemory(_hProcess, addr, buf, length, out _);
            Console.WriteLine($"\n── Raw @ 0x{addr.ToInt64():X} ──");
            for (int i = 0; i <= length - 4; i += 4)
            {
                int iv = BitConverter.ToInt32(buf, i);
                float fv = BitConverter.ToSingle(buf, i);
                string hex = $"{buf[i]:X2}{buf[i + 1]:X2}{buf[i + 2]:X2}{buf[i + 3]:X2}";
                string mark = "";
                if (!float.IsNaN(fv) && !float.IsInfinity(fv))
                {
                    float af = Math.Abs(fv);
                    if (af > 30f && af < 20000f && fv != 0f)
                        mark = $"  ← COORD? {fv:F1}";
                }
                Console.WriteLine($"  +0x{i:X3}  {iv,12}  {hex}{mark}");
            }
        }

        /// <summary>
        /// Print a summary of all entities in the list for matching against /whonpc.
        /// Groups by MaxHP to identify NPC types.
        /// </summary>
        public void PrintAllEntities()
        {
            var all = GetAllEntities();
            Console.WriteLine($"\nTotal raw entities: {all.Count}");
            Console.WriteLine($"  {"Address",-14} {"HP":>6} {"MaxHP":>8} {"OID":>8}  tag");
            Console.WriteLine(new string('─', 55));
            foreach (var e in all)
            {
                // Read the 4-char tag at +0x008
                try
                {
                    var tagBytes = new byte[4];
                    MemoryHelper.ReadProcessMemory(_hProcess, IntPtr.Add(e.BaseAddress, 8),
                        tagBytes, 4, out _);
                    string tag = Encoding.ASCII.GetString(tagBytes)
                        .Replace('\0', '.').Replace('\r', '.').Replace('\n', '.');
                    Console.WriteLine($"  0x{e.BaseAddress.ToInt64():X10}  {e.HP,6}  {e.MaxHP,8}  {e.OID,8}  {tag}");
                }
                catch
                {
                    Console.WriteLine($"  0x{e.BaseAddress.ToInt64():X10}  {e.HP,6}  {e.MaxHP,8}  {e.OID,8}");
                }
            }
        }
    }
}
