using System;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace xajh
{
    public class Npc
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public uint NodeAddr { get; set; }    // list node pointer (node+0x4C -> npc_obj)
        public uint NpcObjAddr { get; set; }  // raw npc_obj pointer, for direct call injection
        public uint Oid { get; set; }         // unique server OID at npc_obj+0x644

        public override string ToString() =>
            $"{Name,-16}  ({X,9:F2},{Y,9:F2},{Z,7:F2})";
    }

    /// <summary>
    /// Reads all live NPCs — matches /whonpc output exactly.
    ///
    /// Structure (confirmed from iterator Begin/Next disassembly):
    ///
    ///   [moduleBase + 0x9D451C]  →  NpcListMgr*
    ///   NpcListMgr + 4           =  iterator container
    ///   container  + 4           =  first_node ptr   (Begin: returns [self+4])
    ///
    ///   Singly linked list via node+0x0C:
    ///     node + 0x0C  →  next node   (Next: follows [current+0x0C])
    ///     node + 0x4C  →  npc_obj*    (GetNpcObj: [ecx+0x4C])
    ///
    ///   npc_obj + 0x19C  =  inline name string object
    ///     +0x04          =  byte length
    ///     +0x08          →  char* (GBK)
    ///   npc_obj + 0x94/0x98/0x9C  =  X/Y/Z float32
    /// </summary>
    public class NpcReader
    {
        public static long NpcListMgrOffset = 0x9D451C;

        public static int OffFirstNode = 0x04;  // container+4 = first node
        public static int OffNextNode = 0x0C;  // node+0x0C = next node
        public static int OffNpcObj = 0x4C;  // node+0x4C = npc_obj*
        public static int OffNameStr = 0x19C; // npc_obj → inline string obj
        public static int OffStrLen = 0x04;
        public static int OffStrCharPtr = 0x08;
        public static int OffPosX = 0x94;
        public static int OffPosY = 0x98;
        public static int OffPosZ = 0x9C;

        private readonly IntPtr _hProcess;
        private readonly IntPtr _moduleBase;

        public NpcReader(IntPtr hProcess, IntPtr moduleBase)
        {
            _hProcess = hProcess;
            _moduleBase = moduleBase;
        }

        public List<Npc> GetAllNpcs()
        {
            var result = new List<Npc>();
            try
            {
                int mgrRaw = MemoryHelper.ReadInt32(_hProcess,
                    IntPtr.Add(_moduleBase, (int)NpcListMgrOffset));
                if (mgrRaw == 0) return result;

                // container = NpcListMgr+4; first_node at container+4
                int firstRaw = MemoryHelper.ReadInt32(_hProcess,
                    new IntPtr((uint)(mgrRaw + 4 + OffFirstNode)));
                if (firstRaw == 0) return result;

                uint node = (uint)firstRaw;
                int safety = 0;

                while (node != 0 && safety++ < 10000)
                {
                    var nodePtr = new IntPtr(node);

                    int npcRaw = MemoryHelper.ReadInt32(_hProcess,
                        IntPtr.Add(nodePtr, OffNpcObj));

                    if (npcRaw != 0)
                    {
                        var npc = ReadNpc(new IntPtr((uint)npcRaw), node);
                        if (npc != null) result.Add(npc);
                    }

                    node = (uint)MemoryHelper.ReadInt32(_hProcess,
                        IntPtr.Add(nodePtr, OffNextNode));
                }
            }
            catch { }
            return result;
        }

        private Npc ReadNpc(IntPtr npcObj, uint nodeAddr)
        {
            try
            {
                float x = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(npcObj, OffPosX));
                float y = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(npcObj, OffPosY));
                float z = MemoryHelper.ReadFloat(_hProcess, IntPtr.Add(npcObj, OffPosZ));

                // Reject invalid or unplaced entries (0,0,0 = not yet spawned)
                if (float.IsNaN(x) || float.IsInfinity(x) ||
                    float.IsNaN(y) || float.IsInfinity(y) ||
                    float.IsNaN(z) || float.IsInfinity(z)) return null;
                if (x == 0f && y == 0f && z == 0f) return null;  // unplaced
                if (Math.Abs(x) > 100000f || Math.Abs(y) > 100000f ||
                    Math.Abs(z) > 100000f) return null;  // garbage

                var nameStr = IntPtr.Add(npcObj, OffNameStr);
                int nameLen = MemoryHelper.ReadInt32(_hProcess,
                    IntPtr.Add(nameStr, OffStrLen));
                int charPtrRaw = MemoryHelper.ReadInt32(_hProcess,
                    IntPtr.Add(nameStr, OffStrCharPtr));
                string name = "";
                if (charPtrRaw != 0 && nameLen > 0 && nameLen < 256)
                {
                    var buf = new byte[nameLen];
                    MemoryHelper.ReadProcessMemory(_hProcess,
                        new IntPtr((uint)charPtrRaw), buf, nameLen, out _);
                    name = Encoding.GetEncoding("GBK").GetString(buf);
                }
                return new Npc
                {
                    Name = name,
                    X = x,
                    Y = y,
                    Z = z,
                    NodeAddr = nodeAddr,
                    NpcObjAddr = (uint)npcObj.ToInt64(),
                    Oid = (uint)MemoryHelper.ReadInt32(_hProcess, IntPtr.Add(npcObj, 0x644))
                };

            }
            catch { return null; }
        }
    }
}
