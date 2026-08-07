using System.IO;

namespace BepInEx
{
    // The real lease only needs this framework path boundary. Tests use a disposable directory.
    public static class Paths
    {
        public static string ConfigPath { get; set; } = Path.GetTempPath();
    }
}

namespace AbyssMod
{
    // Logging is intentionally outside the behavior asserted by the lease tests.
    public static class Logger
    {
        public static void Info(string message) { }
        public static void Error(string message) { }
    }
}
