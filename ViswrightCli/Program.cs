namespace Viswright.Cli
{
    /// <summary>
    /// The executable. Everything lives in the Viswright library, which tests can reach; this only
    /// gives it a Main.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args) => Viswright.Program.Main(args);
    }
}
