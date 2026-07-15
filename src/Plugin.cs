using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace LobbyControlMidSessionJoin;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("mattymatty.LobbyControl", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance = null!;
    public const string Guid = "amevirus.LobbyControlMidSessionJoin";
    public const string Name = "LobbyControl Mid-Session Join";
    public const string Version = "0.3.5";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> AutoOpenLobby = null!;
    internal static ConfigEntry<bool> SpawnInShip = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    private Harmony? _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Enabled = Config.Bind("General", "Enabled", true,
            "Allow players to join while the ship is landed on a moon.");
        AutoOpenLobby = Config.Bind("General", "AutoOpenLobby", true,
            "Keep/reopen the Steam lobby while a moon round is active.");
        SpawnInShip = Config.Bind("General", "SpawnInShip", true,
            "Move a newly joined local player to the ship after level synchronization.");
        DebugLogging = Config.Bind("Debug", "Enabled", false,
            "Write verbose synchronization information to the BepInEx log.");

        _harmony = new Harmony(Guid);
        _harmony.PatchAll();
        Logger.LogInfo($"{Name} {Version} loaded.");
    }

    internal static void Debug(string text)
    {
        if (DebugLogging.Value) Log.LogInfo("[debug] " + text);
    }
}
