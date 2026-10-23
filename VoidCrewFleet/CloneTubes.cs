using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

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
    /// are confirmed; a player whose ship was lost doesn't spawn at all; a player whose character is alive gets none), and a
    /// player's game refuses a tube of a ship that is being destroyed. These rules hold for the whole of a fleet mission,
    /// also once only one of its ships is left.
    /// </summary>
    internal static class CloneTubes
    {
        public const string Feature = FleetShips.Feature;

        private const int SlotStride = 100;

        private static readonly MethodInfo ListGetItem = Getter(GameMember.Property(typeof(List<CloneTube>), "Item"));
        private static readonly MethodInfo TubeAtMethod = new Func<List<CloneTube>, int, CloneTube>(TubeAt).Method;
        private static readonly MethodInfo SpawnTubeAtMethod = new Func<List<CloneTube>, int, CloneTube>(SpawnTubeAt).Method;

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

        /// <summary>
        /// A fleet mission whose ships the host has spawned and published: every player spawns on their own ship only, however
        /// many of its ships are left. (Should spawning the fleet have failed before that, spawning stays the game's own.)
        /// </summary>
        private static bool FleetMission
        {
            get { return FleetSession.ShipCount > 1 && FleetShips.PublishedCount > 1; }
        }

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
                Registered.RemoveAll(r => r.Module == null || (module != null && r.Module == module));
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
                    // A module registered before it was on its ship: its ship is known by now.
                    if (entry.Ship == null && entry.Module != null)
                    {
                        entry.Ship = FleetShips.OwnerOf(entry.Module);
                        if (entry.Ship != null) entry.ShipView = FleetShips.ViewId(entry.Ship);
                    }
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

        /// <summary>
        /// The game's "tube number index" lookups: the tube with that code. Outside fleet missions also (the game's own
        /// meaning) the tube at that place in the list; in a fleet mission a place in the list can be another ship's tube, so
        /// a code this client doesn't know finds none.
        /// </summary>
        public static CloneTube TubeAt(List<CloneTube> list, int index)
        {
            if (list == null) return null;
            foreach (CloneTube tube in list)
            {
                if (tube != null && tube.Index == index) return tube;
            }
            if (FleetMission) return null;
            return index >= 0 && index < list.Count ? list[index] : null;
        }

        /// <summary>
        /// The tube a player's new character starts in (the game's first spawn, on the player's own client): the tube with
        /// that code. In a fleet mission, when this client knows no tube with it (its ship's number isn't known here yet), a free
        /// tube of the player's own ship instead, so the character is still placed: the game's set-up would stop with an error
        /// halfway (a respawn can ask again instead, RespawnPrefix; a first spawn can't).
        /// </summary>
        public static CloneTube SpawnTubeAt(List<CloneTube> list, int index)
        {
            CloneTube tube = TubeAt(list, index);
            if (tube != null || !FleetMission) return tube;
            try
            {
                PlayerControlledShip own = FleetShips.LocalShip;
                tube = own != null ? FreeTubeOf(own, true) : null;
                if (tube == null && list != null)
                {
                    // No ship of their own known here yet: any free tube, else any tube, rather than none.
                    foreach (CloneTube other in list)
                    {
                        if (other == null) continue;
                        if (other.IsAvailable())
                        {
                            tube = other;
                            break;
                        }
                        if (tube == null) tube = other;
                    }
                }
                Log.Warn("The host sent clone tube " + index + " for this player's first spawn, which this client doesn't know; they start in tube " +
                         (tube != null ? tube.Index + " of " + FleetShips.Describe(FleetShips.OwnerOf(tube)) : "none") + " instead.");
            }
            catch (Exception e)
            {
                Log.Failure("Choosing a clone tube for a new character", e);
            }
            return tube;
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RespawnPlayer", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> TubeLookupTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return ReplaceTubeLookup(instructions, original, TubeAtMethod);
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "InitializeCharacter", PatchKind.Transpiler)]
        private static IEnumerable<CodeInstruction> SpawnTubeLookupTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            return ReplaceTubeLookup(instructions, original, SpawnTubeAtMethod);
        }

        private static IEnumerable<CodeInstruction> ReplaceTubeLookup(IEnumerable<CodeInstruction> instructions, MethodBase original, MethodInfo lookup)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && ListGetItem != null && Equals(instruction.operand, ListGetItem))
                {
                    var call = new CodeInstruction(OpCodes.Call, lookup);
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

        /// <summary>A property's getter, or null when the property is missing.</summary>
        private static MethodInfo Getter(PropertyInfo property)
        {
            return property != null ? property.GetGetMethod(true) : null;
        }

        /// <summary>A free tube of a ship (none: null; with orAny, then any of its tubes).</summary>
        private static CloneTube FreeTubeOf(PlayerControlledShip ship, bool orAny)
        {
            CloneTube any = null;
            foreach (ShipTubes entry in Registered)
            {
                if (entry.Ship != ship) continue;
                foreach (CloneTube tube in entry.Tubes)
                {
                    if (tube == null) continue;
                    if (tube.IsAvailable()) return tube;
                    if (any == null) any = tube;
                }
            }
            return orAny ? any : null;
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

        /// <summary>The living ship with the lowest number, or null.</summary>
        private static PlayerControlledShip FirstLivingShip()
        {
            List<int> alive = FleetShips.AliveSlots();
            return alive.Count > 0 ? FleetShips.BySlot(alive[0]) : null;
        }

        /// <summary>
        /// Host, a spawn request in a fleet mission. While the game handles it, "the player ship" is the player's own ship. A
        /// player without one to spawn on (not placed yet, waiting for a seat, or DEAD) gets the living ship with the lowest
        /// number instead, only so the game queues their request rather than dropping it (it drops one when "the player ship"
        /// is gone or dying, which was whatever ship the host was on); the tube choice still gives them none. A request from
        /// a player whose character is alive is refused: the game would clone them anew in a tube of their ship (a way home
        /// from anywhere for a modified game); an unmodified game only asks when the character is dead or not there yet.
        /// </summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "TrySpawnPlayer", PatchKind.Prefix)]
        private static bool TrySpawnPrefix(Photon.Realtime.Player player)
        {
            _spawning = player;
            PlayerControlledShip ship = null;
            bool refused = false;
            try
            {
                if (FleetMission)
                {
                    if (AliveCharacter(player))
                    {
                        refused = true;
                        Log.WarnOnce("spawn while alive " + player.ActorNumber, player.NickName + " asked for a clone tube while their character is alive; refused.");
                    }
                    else
                    {
                        ReleaseTubesOf(player);
                        ship = SpawnShipFor(player);
                        if (ship == null) ship = FirstLivingShip();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Reading a spawn request", e);
            }
            ShipRedirect.BeginOverride(ship);
            return !refused;
        }

        /// <summary>Whether a player's character exists and is alive (as this client sees it).</summary>
        private static bool AliveCharacter(Photon.Realtime.Player player)
        {
            Player character = player != null ? FleetShips.CharacterOf(player.ActorNumber) : null;
            return character != null && !PlayerLife.IsDead(character);
        }

        /// <summary>
        /// Host: a player asking for a tube is in none, so a tube still closed on their character lets go of it. The host
        /// closes a tube on a player when it sends them there; when their client doesn't know that tube it refuses and asks
        /// again (RespawnPrefix), and without this the tube would stay closed on nobody for the rest of the mission.
        /// </summary>
        private static void ReleaseTubesOf(Photon.Realtime.Player player)
        {
            Player character = player != null ? FleetShips.CharacterOf(player.ActorNumber) : null;
            if (character == null) return;
            foreach (ShipTubes entry in Registered)
            {
                foreach (CloneTube tube in entry.Tubes)
                {
                    if (tube == null || tube.GetOccupant() != character || tube.photonView == null || !tube.photonView.AmController) continue;
                    tube.ReleaseOccupant();
                    tube.ChangeState(tube.StateOpenAvailable);
                    Log.Info("Clone tube " + tube.Index + " was still closed on " + player.NickName + ", who never reached it; it is free again.");
                }
            }
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
            PlayerControlledShip ship = null;
            try
            {
                if (___playersQueuedToSpawn != null && ___playersQueuedToSpawn.Count > 0)
                {
                    if (FleetMission && ___playersQueuedToSpawn.Count > 1 && !(HoldSpawns != null && HoldSpawns())) FrontLoad(___playersQueuedToSpawn);
                    _spawning = ___playersQueuedToSpawn.Peek();
                }
                if (FleetMission && _spawning != null) ship = SpawnShipFor(_spawning);
            }
            catch (Exception e)
            {
                Log.Failure("Reading the spawn queue", e);
            }
            ShipRedirect.BeginOverride(ship);
        }

        /// <summary>
        /// The game only looks at the first player in its spawn queue. In a fleet mission a player waiting for a tube on a
        /// busy ship would hold up everyone of the other ships, and a spectator (who stays queued, never spawned) would hold
        /// up everyone, so the first player who can spawn right now goes first.
        /// </summary>
        private static void FrontLoad(Queue<Photon.Realtime.Player> queue)
        {
            Photon.Realtime.Player[] waiting = queue.ToArray();
            int ready = -1;
            for (int i = 0; i < waiting.Length && ready < 0; i++)
            {
                PlayerControlledShip ship = SpawnShipFor(waiting[i]);
                if (ship != null && FreeTubeOf(ship, false) != null) ready = i;
            }
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

        /// <summary>A free tube on the spawning player's ship (or none, which queues them; a spectator always gets none).</summary>
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
                if (!FleetMission || _spawning == null) return true;
                PlayerControlledShip ship = SpawnShipFor(_spawning);
                __result = ship == null ? null : FreeTubeOf(ship, false);
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
            PlayerControlledShip ship = null;
            try
            {
                if (FleetMission && tube != null) ship = FleetShips.OwnerOf(tube);
            }
            catch (Exception e)
            {
                Log.Failure("Reading which ship a new character spawns on", e);
            }
            ShipRedirect.BeginOverride(ship);
        }

        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "SpawnPlayer", PatchKind.Finalizer)]
        private static void SpawnPlayerFinalizer()
        {
            ShipRedirect.EndOverride();
        }

        /// <summary>
        /// A respawn message names a tube by its code; refuse codes this client doesn't know (its ship may be gone here, or
        /// its number not known yet), and in a fleet mission a tube of a ship that is being destroyed (the host chose it just
        /// before its pilot's game started the ship's death), and ask again a moment later. Asking again also frees that tube
        /// on the host (ReleaseTubesOf), which closed it on this player when it sent them there.
        /// </summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "RespawnPlayer", PatchKind.Prefix)]
        private static bool RespawnPrefix(int clonetubeIndex, List<CloneTube> ___clonetubes)
        {
            try
            {
                CloneTube tube = TubeAt(___clonetubes, clonetubeIndex);
                PlayerControlledShip ship = tube != null && FleetMission ? FleetShips.OwnerOf(tube) : null;
                if (tube != null && (ship == null || !ship.StartedDeath)) return true;
                if (tube == null)
                    Log.Warn("The host sent clone tube " + clonetubeIndex + " but this client knows " + (___clonetubes != null ? ___clonetubes.Count : 0) +
                             " tubes and none with that number; asking to spawn again.");
                else
                    Log.Warn("The host sent clone tube " + clonetubeIndex + " of " + FleetShips.Describe(ship) + ", which is being destroyed; asking to spawn again.");
                if (FleetRuntime.Instance != null) FleetRuntime.Instance.StartCoroutine(RequestSpawnLater());
                else if (SpawnSystem.Instance != null) SpawnSystem.Instance.RequestSpawn();
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking the clone tube the host sent", e);
                return true;
            }
        }

        /// <summary>Asks the host for a tube again after a second, unless this player has been re-cloned meanwhile.</summary>
        private static IEnumerator RequestSpawnLater()
        {
            yield return new WaitForSeconds(1f);
            LocalPlayer me = LocalPlayer.Instance;
            if (SpawnSystem.Instance != null && me != null && PlayerLife.IsDead(me)) SpawnSystem.Instance.RequestSpawn();
        }
    }
}
