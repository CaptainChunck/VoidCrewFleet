using Photon.Pun;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// Who sent the Photon RPC that is running right now. Most of the game's RPC methods take no PhotonMessageInfo, so a
    /// check inside one can't tell who asked for it. PUN runs every RPC, one from the network or a player's RPC to
    /// themselves, inside PhotonNetwork.ExecuteRpc, which is given the sender (the server stamps it on the event, so a
    /// client can't fake it) and calls the RPC method before it returns. That call is watched here: while it runs, the
    /// sender and the network object the RPC is for are known. It is patched only once the game has loaded: patching
    /// PhotonNetwork at start-up set Photon up before the game did, and its connection then never got past the name server.
    /// It is a feature of its own, used by several others, so one of them being switched off doesn't take it from the rest.
    /// (Its patches go in after the start-up report, so the report doesn't list it; a patch that is missing logs a warning.)
    /// </summary>
    internal static class RpcSender
    {
        public const string Feature = "RPC senders";

        /// <summary>The RPC event's key for the target's view ID (PUN's own key: the byte 0).</summary>
        private static readonly object ViewKey = (byte)0;

        private struct Running
        {
            public Photon.Realtime.Player Sender;
            public int View;
        }

        private static Running _current;

        /// <summary>An RPC starts: remember its sender and view (an RPC run inside another one restores the outer one after).</summary>
        [GamePatch(Feature, "Photon.Pun.PhotonNetwork", "ExecuteRpc", PatchKind.Prefix, Optional = true, AfterGameLoad = true)]
        private static void ExecutePrefix(Hashtable rpcData, Photon.Realtime.Player sender, out Running __state)
        {
            __state = _current;
            object view = null;
            if (rpcData != null) rpcData.TryGetValue(ViewKey, out view);
            _current = new Running { Sender = sender, View = view is int ? (int)view : 0 };
        }

        [GamePatch(Feature, "Photon.Pun.PhotonNetwork", "ExecuteRpc", PatchKind.Finalizer, Optional = true, AfterGameLoad = true)]
        private static void ExecuteFinalizer(Running __state)
        {
            _current = __state;
        }

        /// <summary>The actor number of whoever sent the RPC running now on this network object; false outside such an RPC.</summary>
        public static bool Of(PhotonView view, out int actor)
        {
            actor = -1;
            Photon.Realtime.Player sender = _current.Sender;
            if (sender == null || view == null || _current.View == 0 || _current.View != view.ViewID) return false;
            actor = sender.ActorNumber;
            return true;
        }

        /// <summary>
        /// Whether the RPC running now on this network object was sent by a player who isn't in the room any more: the game's
        /// rooms keep a leaver's buffered RPCs, and PUN replays them to every player who joins later without a sender. False
        /// outside such an RPC, and when this feature isn't in.
        /// </summary>
        public static bool FromAbsentPlayer(PhotonView view)
        {
            return _current.Sender == null && view != null && _current.View != 0 && _current.View == view.ViewID;
        }
    }
}
