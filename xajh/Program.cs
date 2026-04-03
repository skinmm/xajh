using System;
using System.Diagnostics;
using System.Text;
using xajh;

namespace Xajh
{
    class Program
    {
        static void Main(string[] args)
        {
            // Required on .NET 5+ for GBK encoding (Chinese NPC names)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var procs = Process.GetProcessesByName("vrchat1");
            if (procs.Length == 0) { Console.WriteLine("Game not found."); return; }

            var game = procs[0];
            IntPtr moduleBase = game.MainModule.BaseAddress;
            IntPtr hProcess = MemoryHelper.OpenProcess(
                MemoryHelper.PROCESS_ALL_ACCESS, false, game.Id);

            if (hProcess == IntPtr.Zero)
            {
                Console.WriteLine("[!] Failed to open process. Run as Administrator.");
                Console.ReadKey(); return;
            }
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XajhSmileDll.dll");
            if (!DllInjector.Inject(hProcess, dllPath))
            {
                Console.WriteLine("[!] Injection failed.");
                Console.ReadKey(); return;
            }

            // Continue with your EntityManager, NpcReader, etc.
            var overlay = new CombatOverlay(hProcess, moduleBase);
            overlay.Run();

        }
    }
}
