using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Core
{
    public class Program
    {
        static Program()
        {
            var version = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? "unknown";

            Console.Title = $"HK4E Sophon Downloader v{version}";
        }

        public static async Task<int> Main()
        {
            Utils.CenterConsole();
            Utils.SetQuickEdit(true);
            Config.Load();
            return await Menu.RunMenu();
        }
    }
}
