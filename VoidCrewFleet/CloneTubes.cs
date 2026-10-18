using System;
using System.Collections.Generic;
using System.Linq;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using Photon.Pun;

namespace VoidCrewFleet
{
    /// <summary>
    /// Clone tubes are the only places players spawn. The game keeps one list (the last ship's tubes win) with indices
    /// that restart at 0 per ship, and sends those indices over the network. Here every ship's tubes go into one list,
    /// ship 1's first (ships ordered by root view ID, the same on every client), and each tube's Index becomes its
    /// place in that list, so the game's spawn messages keep working. The host then gives each player a tube on their
    /// own crew's ship (and, in PvP, nobody spawns before the teams are confirmed).
    /// </summary>
    internal static class CloneTubes
    {
        public const string Feature = FleetShips.Feature;

        private sealed class ShipTubes
        {
            public PlayerControlledShip Ship;
            public int ShipView;
            public CloneTubeModule Module;
            public List<CloneTube> Tubes;
        }

        private static readonly List<ShipTubes> Registered = new List<ShipTubes>();
        private static CloneTubeModule _disabling;
        private static Photon.Realtime.Player _spawning;

        /// <summary>When set (PvP before the teams are confirmed), the host holds every spawn request in the queue.</summary>
        public static Func<bool> HoldSpawns;

        // ---------------------------------------------------------------- one list for all ships

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RegisterCloneTubes", PatchKind.Prefix)]
        private static bool RegisterPrefix(List<CloneTube> cts, ref List<CloneTube> ___clonetubes)
        {
            try
            {
                if (cts == null || cts.Count == 0 || cts[0] == null) return true;
                CloneTubeModule module = cts[0].GetComponentInParent<CloneTubeModule>();
                PlayerControlledShip ship = FleetShips.OwnerOf(cts[0]);
                Registered.RemoveAll(r => r.Module == null || (module != null && r.Module == module) || r.Tubes == cts);
                Registered.Add(new ShipTubes { Ship = ship, ShipView = FleetShips.ViewId(ship), Module = module, Tubes = new List<CloneTube>(cts) });
                ___clonetubes = Rebuild();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Registering a ship's clone tubes", e);
                return true;
            }
        }

        [GamePatch(Feature, "CG.Ship.Modules.CloneTubeModule", "OnDisable", PatchKind.Prefix)]
        private static void ModuleDisablePrefix(CloneTubeModule __instance)
        {
            _disabling = __instance;
        }

        [GamePatch(Feature, "CG.Ship.Modules.CloneTubeModule", "OnDisable", PatchKind.Finalizer)]
        private static void ModuleDisableFinalizer()
        {
            _disabling = null;
        }

        /// <summary>A ship's tube module going away removes only that ship's tubes (the game empties the whole list).</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "ClearCloneTubes", PatchKind.Prefix)]
        private static bool ClearPrefix(ref List<CloneTube> ___clonetubes)
        {
            try
            {
                if (_disabling != null)
                {
                    CloneTubeModule module = _disabling;
                    Registered.RemoveAll(r => r.Module == null || r.Module == module);
                }
                else
                {
                    Registered.Clear();
                }
                ___clonetubes = Rebuild();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Removing a ship's clone tubes", e);
                return true;
            }
        }

        private static List<CloneTube> Rebuild()
        {
            var all = new List<CloneTube>();
            foreach (ShipTubes entry in Registered.OrderBy(r => r.ShipView))
            {
                foreach (CloneTube tube in entry.Tubes)
                {
                    if (tube == null) continue;
                    tube.Index = all.Count;
                    all.Add(tube);
                }
            }
            return all;
        }

        private static List<CloneTube> TubesOf(PlayerControlledShip ship)
        {
            var tubes = new List<CloneTube>();
            foreach (ShipTubes entry in Registered)
            {
                if (entry.Ship == ship) tubes.AddRange(entry.Tubes.Where(t => t != null));
            }
            return tubes;
        }

        // ---------------------------------------------------------------- which ship a player spawns on

        /// <summary>
        /// The ship a player should spawn on: their crew's ship, unless it is gone or dying in co-op, then the other one.
        /// Null when they must wait (PvP before the teams are set, or their ship is gone in PvP).
        /// </summary>
        private static PlayerControlledShip SpawnShipFor(Photon.Realtime.Player player)
        {
            if (player == null) return null;
            PlayerControlledShip own = FleetShips.ShipOfActor(player.ActorNumber);
            if (own != null && !own.StartedDeath) return own;
            if (FleetSession.IsPvP) return null;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship != null && !ship.StartedDeath) return ship;
            }
            return null;
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "TrySpawnPlayer", PatchKind.Prefix)]
        private static void TrySpawnPrefix(Photon.Realtime.Player player)
        {
            _spawning = player;
            ShipRedirect.BeginOverride(FleetShips.Active ? SpawnShipFor(player) : null);
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "TrySpawnPlayer", PatchKind.Finalizer)]
        private static void TrySpawnFinalizer()
        {
            ShipRedirect.EndOverride();
            _spawning = null;
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "CheckSpawningFromQueue", PatchKind.Prefix)]
        private static void QueuePrefix(Queue<Photon.Realtime.Player> ___playersQueuedToSpawn)
        {
            _spawning = null;
            try
            {
                if (___playersQueuedToSpawn != null && ___playersQueuedToSpawn.Count > 0)
                {
                    if (FleetShips.Active && ___playersQueuedToSpawn.Count > 1 && !(HoldSpawns != null && HoldSpawns())) FrontLoad(___playersQueuedToSpawn);
                    _spawning = ___playersQueuedToSpawn.Peek();
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reading the spawn queue", e);
            }
            ShipRedirect.BeginOverride(FleetShips.Active && _spawning != null ? SpawnShipFor(_spawning) : null);
        }

        /// <summary>
        /// The game only looks at the first player in its spawn queue. With two ships, a player waiting for a tube on a
        /// busy ship would hold up everyone of the other ship, so the first player who can spawn right now goes first.
        /// </summary>
        private static void FrontLoad(Queue<Photon.Realtime.Player> queue)
        {
            Photon.Realtime.Player[] waiting = queue.ToArray();
            int ready = Array.FindIndex(waiting, player =>
            {
                PlayerControlledShip ship = SpawnShipFor(player);
                return ship != null && TubesOf(ship).Any(t => t.IsAvailable());
            });
            if (ready <= 0) return;
            queue.Clear();
            queue.Enqueue(waiting[ready]);
            for (int i = 0; i < waiting.Length; i++)
            {
                if (i != ready) queue.Enqueue(waiting[i]);
            }
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "CheckSpawningFromQueue", PatchKind.Finalizer)]
        private static void QueueFinalizer()
        {
            ShipRedirect.EndOverride();
            _spawning = null;
        }

        /// <summary>A free tube on the spawning player's ship (or none, which queues them).</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "GetNextFreeCloneTube", PatchKind.Prefix)]
        private static bool NextFreeTubePrefix(ref CloneTube __result)
        {
            try
            {
                if (HoldSpawns != null && PhotonNetwork.IsMasterClient && HoldSpawns())
                {
                    __result = null;
                    return false;
                }
                if (!FleetShips.Active || _spawning == null) return true;
                PlayerControlledShip ship = SpawnShipFor(_spawning);
                __result = ship == null ? null : TubesOf(ship).FirstOrDefault(t => t.IsAvailable());
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing a clone tube", e);
                return true;
            }
        }

        /// <summary>The new character is placed on (and moves with) the ship of its tube.</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "SpawnPlayer", PatchKind.Prefix)]
        private static void SpawnPlayerPrefix(CloneTube tube)
        {
            ShipRedirect.BeginOverride(FleetShips.Active && tube != null ? FleetShips.OwnerOf(tube) : null);
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "SpawnPlayer", PatchKind.Finalizer)]
        private static void SpawnPlayerFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>A respawn message names a tube by its index in the combined list; refuse indices this client doesn't have.</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RespawnPlayer", PatchKind.Prefix)]
        private static bool RespawnPrefix(int clonetubeIndex, List<CloneTube> ___clonetubes)
        {
            if (___clonetubes != null && clonetubeIndex >= 0 && clonetubeIndex < ___clonetubes.Count) return true;
            Log.Warn("The host sent clone tube " + clonetubeIndex + " but this client knows " + (___clonetubes != null ? ___clonetubes.Count : 0) +
                     " tubes; asking to spawn again.");
            if (SpawnSystem.Instance != null) SpawnSystem.Instance.RequestSpawn();
            return false;
        }
    }
}
