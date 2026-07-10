namespace LobbyControlMidSessionJoin;

internal static class MidJoinState
{
    internal static int SnapshotsSent;
    internal static int LandingSequencesSuppressed;
    internal static bool ClientLateJoinSyncActive;
    internal static bool ClientLateJoinSyncCompleted;
    internal static bool ClientPlayerPositioned;
    internal static string LastStatus = "No synchronization attempted yet.";

    internal static void ResetClientState()
    {
        ClientLateJoinSyncActive = false;
        ClientLateJoinSyncCompleted = false;
        ClientPlayerPositioned = false;
    }

    internal static bool IsActiveMoon
    {
        get
        {
            var round = StartOfRound.Instance;
            return round != null && !round.inShipPhase && round.shipHasLanded;
        }
    }
}
