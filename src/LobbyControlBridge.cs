using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace LobbyControlMidSessionJoin;

internal static class LobbyControlBridge
{
    private const BindingFlags STATIC_FLAGS =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] ConnectionMemberNames =
    {
        "_allowNewConnection",
        "allowNewConnection",
        "AllowNewConnection",
        "_acceptingConnections",
        "acceptingConnections",
        "AcceptingConnections"
    };

    private static readonly string[] LobbyModificationMemberNames =
    {
        "CanModifyLobby",
        "_canModifyLobby",
        "canModifyLobby"
    };

    private static readonly Type? LateJoinType =
        AccessTools.TypeByName("LobbyControl.Patches.LateJoinPatches") ??
        AccessTools.TypeByName("LobbyControl.Patches.LateJoin") ??
        AccessTools.TypeByName("LobbyControl.Patches.ConnectionQueuePatches");

    private static readonly Type? MainType =
        AccessTools.TypeByName("LobbyControl.LobbyControl") ??
        AccessTools.TypeByName("LobbyControl.Plugin");

    private static readonly MemberInfo? AllowNewConnection =
        FindBooleanMember(LateJoinType, ConnectionMemberNames);

    private static readonly MemberInfo? CanModifyLobby =
        FindBooleanMember(MainType, LobbyModificationMemberNames);

    internal static bool Available => AllowNewConnection != null || CanModifyLobby != null;

    internal static void PermitConnections(bool value)
    {
        bool connectionStateUpdated = TrySetBooleanMember(AllowNewConnection, value);
        bool lobbyStateUpdated = TrySetBooleanMember(CanModifyLobby, value);

        if (!connectionStateUpdated && !lobbyStateUpdated)
        {
            Plugin.Log.LogWarning(
                "LobbyControl compatibility bridge could not find a supported connection-state member. " +
                "LobbyControl will still handle its own queue and approval flow.");
            return;
        }

        Plugin.Debug(
            $"LobbyControl bridge: allowUpdated={connectionStateUpdated}, " +
            $"canModifyUpdated={lobbyStateUpdated}, value={value}");
    }

    private static MemberInfo? FindBooleanMember(Type? type, IEnumerable<string> names)
    {
        if (type == null)
            return null;

        foreach (string name in names)
        {
            FieldInfo? field = type.GetField(name, STATIC_FLAGS);
            if (field?.FieldType == typeof(bool))
                return field;

            PropertyInfo? property = type.GetProperty(name, STATIC_FLAGS);
            if (property?.PropertyType == typeof(bool) && property.SetMethod != null)
                return property;
        }

        return null;
    }

    private static bool TrySetBooleanMember(MemberInfo? member, bool value)
    {
        if (member == null)
            return false;

        try
        {
            switch (member)
            {
                case FieldInfo field:
                    field.SetValue(null, value);
                    return true;
                case PropertyInfo property:
                    property.SetValue(null, value);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning(
                $"Could not update LobbyControl member {member.DeclaringType?.FullName}.{member.Name}: " +
                ex.Message);
            return false;
        }
    }
}
