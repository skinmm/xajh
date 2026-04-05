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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            bool bypassLogin = args.Length == 0 || Array.Exists(args, a =>
                a.Equals("--bypass", StringComparison.OrdinalIgnoreCase));

            if (bypassLogin)
            {
                var (cloudH, cloudBase) = LoginBypasser.Bypass();
                if (cloudH == IntPtr.Zero)
                {
                    Console.WriteLine("[!] Login bypass could not attach. Continuing anyway ...");
                }
            }

            Console.WriteLine("[*] Waiting for game process (vrchat1) ...");
            Process game = null;
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName("vrchat1");
                if (procs.Length > 0) { game = procs[0]; break; }
                System.Threading.Thread.Sleep(500);
            }

            if (game == null) { Console.WriteLine("[!] Game not found."); Console.ReadKey(); return; }

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

            var overlay = new CombatOverlay(hProcess, moduleBase);
            overlay.Run();
        }
    }
}
