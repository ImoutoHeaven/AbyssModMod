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

    public sealed class ControllerTestConfigEntry<T>
    {
        public ControllerTestConfigEntry(T value) => Value = value;

        public T Value { get; set; }
    }

    // These are the exact config reads made by the linked production Controller.  They are
    // simple test values rather than a second controller implementation.
    public static class Config
    {
        internal static ControllerTestConfigEntry<int> NetherAutoClimbMaxDepth { get; } = new(130);
        internal static ControllerTestConfigEntry<int> NetherAutoClimbSoftErosionLimit { get; } = new(90);
        internal static ControllerTestConfigEntry<int> NetherAutoClimbMinimumCharacterHpPermille { get; } = new(300);
        internal static ControllerTestConfigEntry<AbyssMod.Services.NetherCombatLane> NetherAutoClimbCombatLane { get; } = new(AbyssMod.Services.NetherCombatLane.Auto);
        internal static ControllerTestConfigEntry<int> NetherAutoClimbCodeReloadReserve { get; } = new(1);
        internal static ControllerTestConfigEntry<AbyssMod.Services.NetherTreasureMode> NetherAutoClimbTreasureMode { get; } = new(AbyssMod.Services.NetherTreasureMode.KeyOnly);
        internal static ControllerTestConfigEntry<AbyssMod.Services.NetherShopMode> NetherAutoClimbShopMode { get; } = new(AbyssMod.Services.NetherShopMode.Off);
        internal static ControllerTestConfigEntry<bool> NetherAutoClimbDetailedLogging { get; } = new(false);
        internal static ControllerTestConfigEntry<string> BattleSessionAutoSLNetherPreserveItemIds { get; } = new(string.Empty);
    }
}
