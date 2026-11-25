using System.Text;
using TerkirdoComputer.Cli;
using TerkirdoComputer.Windows;

namespace TerkirdoComputer;

public static class Program
{
    public static int Main(string[] args)
    {
        NativeMethods.EnsurePerMonitorDpiAwareness();
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        return CommandLineParser.Run(args, Console.In, Console.Out, Console.Error);
    }
}
