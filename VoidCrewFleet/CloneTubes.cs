using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using HarmonyLib;
using Photon.Pun;

namespace VoidCrewFleet
{
    /// <summary>
    /// Clone tubes are the only places players spawn. The game keeps one list (the last ship's tubes win) with indices
    /// that restart at 0 per ship, and sends those indices over the network. Here every ship's tubes go into one list,
    /// ship 1's first (ships ordered by root view ID, the same on every client), and each tube's Index becomes a code
    /// that never changes while the ship exists: its ship's slot x 100 + its place on that ship (ship 1's tubes keep the
    /// game's own numbers). The game's spawn messages carry that code, and the game's lookups of a tube by index find the
    /// tube with that code, so a ship lost from the middle of the list doesn't renumber the others. The host then gives
    /// each player a tube on their own ship, the one they were given, and no other (in PvP nobody spawns before the teams
    /// are confirmed; a player whose ship was lost doesn't spawn at all).
    /// </summary>
    internal static class CloneTubes
    {
        public const string Feature = FleetShips.Feature;

        private const int SlotStride = 100;

        private static readonly MethodInfo ListGetItem = AccessTools.PropertyGetter(typeof(List<CloneTube>), "Item");
        private static readonly MethodInfo TubeAtMethod = AccessTools.Method(typeof(CloneTubes), "TubeAt");

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

        /// <summary>
        /// A clone tube's synced state can name an occupant whose character this client hasn't loaded yet (someone was
        /// in a tube while this player loaded in, more likely with more players and two ships). The game's healing state
        /// then throws, which cut the rest of the character set-up short. Without the occupant there is nothing to heal
        /// here yet: drop that one error and let the set-up carry on.
        /// </summary>
        [GamePatch(Feature, "CloneTubeHealing", "OnEnter", PatchKind.Finalizer)]
        private static Exception HealingEnterFinalizer(Exception __exception, CloneTube cloneTube)
        {
            if (!(__exception is NullReferenceException) || cloneTube == null) return __exception;
            try
            {
                if (cloneTube.GetOccupant() != null) return __exception;
            }
            catch
            {
                return __exception;
            }
            Log.WarnOnce("tube occupant", "A clone tube's occupant hadn't loaded yet on this client; skipped its healing set-up.");
            return null;
        }

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
                int slot = entry.Ship != null ? FleetShips.Slot(entry.Ship) : -1;
                for (int local = 0; local < entry.Tubes.Count; local++)
                {
                    CloneTube tube = entry.Tubes[local];
                    if (tube == null) continue;
                    // Tubes that aren't on a player ship (the hub's) keep their place in the list, as in the game.
                    tube.Index = slot >= 0 ? slot * SlotStride + local : all.Count;
                    all.Add(tube);
                }
            }
            return all;
        }

        /// <summary>
        /// A few times a second: every tube's code follows its ship's slot as this client knows it now (a client can see a
        /// ship before the host has said which slot it is).
        /// </summary>
        public static void Recode()
        {
            try
            {
                if (Registered.Count < 2) return;
                foreach (ShipTubes entry in Registered)
                {
                    int slot = entry.Ship != null ? FleetShips.Slot(entry.Ship) : -1;
                    if (slot < 0) continue;
                    for (int local = 0; local < entry.Tubes.Count; local++)
                    {
                        CloneTube tube = entry.Tubes[local];
                        if (tube != null) tube.Index = slot * SlotStride + local;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Numbering the clone tubes", e);
            }
        }

        /// <summary>The game's "tube number index" lookups: the tube with that code, else (the game's own meaning) its place in the list.</summary>
        public static CloneTube TubeAt(List<CloneTube> list, int index)
        {
            if (list == null) return null;
            foreach (CloneTube tube in list)
            {
                if (tube != null && tube.Index == index) return tube;
            }
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RespawnPlayer", PatchKind.Transpiler)]
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "InitializeCharacter", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> TubeLookupTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && ListGetItem != null && Equals(instruction.operand, ListGetItem))
                {
                    var call = new CodeInstruction(OpCodes.Call, TubeAtMethod);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);
                    yield return call;
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Clone tubes: " + original.DeclaringType.FullName + "." + original.Name + " no longer looks a tube up by index; the game may have changed.");
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
        /// The ship a player spawns on: always the ship they were given (in the lobby, or by the host when they joined), never
        /// another. Null when they must wait: the host hasn't placed them yet (it does within a moment), PvP before the teams
        /// are set, or a spectator (their ship was lost: they are not re-cloned).
        /// </summary>
        private static PlayerControlledShip SpawnShipFor(Photon.Realtime.Player player)
        {
            if (player == null) return null;
            CrewEntry entry;
            if (!FleetShips.TryGetEntry(player.ActorNumber, out entry) || entry.IsSpectator) return null;
            PlayerControlledShip own = FleetShips.BySlot(entry.Slot);
            return own != null && !own.StartedDeath ? own : null;
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

        /// <summary>A respawn message names a tube by its code; refuse codes this client doesn't know (its ship may be gone here).</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RespawnPlayer", PatchKind.Prefix)]
        private static bool RespawnPrefix(int clonetubeIndex, List<CloneTube> ___clonetubes)
        {
            if (TubeAt(___clonetubes, clonetubeIndex) != null) return true;
            Log.Warn("The host sent clone tube " + clonetubeIndex + " but this client knows " + (___clonetubes != null ? ___clonetubes.Count : 0) +
                     " tubes and none with that number; asking to spawn again.");
            if (SpawnSystem.Instance != null) SpawnSystem.Instance.RequestSpawn();
            return false;
        }
    }
}
