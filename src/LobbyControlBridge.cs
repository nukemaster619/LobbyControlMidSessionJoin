using System;
using System.Reflection;
using HarmonyLib;

namespace LobbyControlMidSessionJoin;

internal static class LobbyControlBridge
{
    private static readonly Type? LateJoinType = AccessTools.TypeByName("LobbyControl.Patches.LateJoinPatches");
    private static readonly FieldInfo? AllowNewConnection = LateJoinType == null
        ? null
        : AccessTools.Field(LateJoinType, "_allowNewConnection");

    private static readonly Type? MainType = AccessTools.TypeByName("LobbyControl.LobbyControl");
    private static readonly FieldInfo? CanModifyLobby = MainType == null
        ? null
        : AccessTools.Field(MainType, "CanModifyLobby");

    internal static bool Available => AllowNewConnection != null;

    internal static void PermitConnections(bool value)
    {
        try
        {
            AllowNewConnection?.SetValue(null, value);
            CanModifyLobby?.SetValue(null, value);
            Plugin.Debug($"LobbyControl bridge: allow={value}, canModify={value}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning("Could not update LobbyControl state: " + ex.Message);
        }
    }
}
