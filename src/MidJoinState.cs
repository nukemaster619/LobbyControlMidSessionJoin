namespace LobbyControlMidSessionJoin;

internal static class MidJoinState
{
    internal const string SnapshotMessage = "amevirus.LCMSJ.LevelSnapshot.v1";
    internal static bool HandlerRegistered;
    internal static bool ApplyingSnapshot;
    internal static int SnapshotsSent;
    internal static int SnapshotsReceived;
    internal static string LastStatus = "No synchronization attempted yet.";

    internal static bool IsActiveMoon
    {
        get
        {
            var round = StartOfRound.Instance;
            return round != null && !round.inShipPhase && round.shipHasLanded;
        }
    }
}
