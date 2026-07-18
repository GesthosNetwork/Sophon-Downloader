using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Core
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Utils.CenterConsole();
            AppConfig.Load();

            if (args.Length > 0)
                CliHandler.ParseArgsAndSetConfig(args);

            var version = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";

            Console.Title = $"HK4E Sophon Downloader v{version}";

            if (args.Length == 0)
                return await InteractiveMenu.RunInteractiveMenu();

            return await CliHandler.RunWithArgs(args);
        }
    }
}
