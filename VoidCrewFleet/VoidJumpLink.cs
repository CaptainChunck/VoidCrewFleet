using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Client;
using CG.Ship.Modules;
using CG.Space;
using Code.Gameplay;
using Gameplay.Quests;
using Gameplay.Ship;
using Gameplay.Ship.VoidJump;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Void jumps with several ships. The session has one active sector, so the ships always jump together:
    /// - the escape vector is the sector's: set on any ship's astral map, every ship has it;
    /// - every ship's crew charges its own void drive;
    /// - once every crewed ship's drive is charged, any ship's void lever starts the jump: that ship becomes the LEAD ship
    ///   and every other ship is carried along, living through every step of the jump with its own crew. While a crewed
    ///   ship's drive isn't charged, the lever names it and nothing starts (a ship nobody crews is carried along whatever its
    ///   charge). During the jump any ship's lever acts as the lead ship's.
    /// The game runs each ship's jump on one client: the host's, or that of the player at its helm (sitting there hands the
    /// ship's network objects to the pilot). A lever pull is decided where its ship's void drive is run: that client makes
    /// the ship the lead at once and, when it isn't the host, tells every other player at once (the host then shares the
    /// lead ship as a room property). Before the first jump (and while it is lost) the lead is the living ship with the
    /// lowest number.
    /// A drive is charged where it is run, and the other players get its charge through the game's own updates; when a drive
    /// becomes fully charged its whole state is sent again, so it reads as charged on every player's game, not only there.
    ///
    /// Each ship has its own VoidJumpSystem. The followers' follow the leader's: on the client that runs a follower's jump
    /// system, every state change of the leader is applied to it too, and the game's own sync carries its state to everyone
    /// else. A follower's jump system makes no moves of its own (its timers and "not charged" signals are ignored), and the
    /// parts of a jump that must only happen once (the interdiction roll and counters, leaving the sector, the autosave, the
    /// "the ship arrived" scenario rules) are skipped for it; its own systems still hear that it entered the void. The ships
    /// arrive side by side at the destination's entry point.
    /// PvP matches have no void jumps.
    /// </summary>
    internal static class VoidJumpLink
    {
        public const string Feature = "Ships jump together";

        private const int StateInactive = 0;
        private const int StateAligning = 2;
        private const int StateTravellingUnstable = 8;
        private const int FirstEndState = 9;

        private static readonly MethodInfo ChangeStateByIndex = GameMember.Method(typeof(VoidJumpSystem), "ChangeActiveStateByIndex");
        private static readonly FieldInfo StateIndexField = GameMember.Field(typeof(VoidJumpSystem), "activeStateIndex");

        /// <summary>The field behind the jump system's OnVoidEntered event (raised for a following ship here).</summary>
        private static readonly FieldInfo VoidEnteredField = GameMember.Field(typeof(VoidJumpSystem), "OnVoidEntered");

        private static readonly List<bool> Counted = new List<bool>();
        private static int _followerDepth;
        private static bool _mirroring;
        private static bool _starting;
        private static VoidJumpSystem _reading;
        private static float _nextWarning;

        /// <summary>Each ship's jump system, by the ship's instance ID (asked for every ship every physics step, SectorKeeper).</summary>
        private static readonly Dictionary<int, VoidJumpSystem> Systems = new Dictionary<int, VoidJumpSystem>();

        /// <summary>True while a following ship's jump system is changing state (the once-only parts of a jump are skipped then).</summary>
        private static bool InFollowerStep
        {
            get { return _followerDepth > 0; }
        }

        /// <summary>A co-op mission with more than one ship: the ships jump together and share one astral map choice.</summary>
        internal static bool Linked
        {
            get { return FleetSession.Mode == FleetMode.Coop && FleetShips.Active; }
        }

        /// <summary>The room property with the lead ship's slot (cleared between missions, FleetShips.ClearRoom).</summary>
        internal const string KeyLead = "VCF_JumpLead";

        /// <summary>The host's own choice of lead ship (the room's copy only updates after a round trip to the server).</summary>
        private static int _hostLead = -1;
        private static object _leadSession;
        private static int _lastLead = -1;

        /// <summary>
        /// A client that runs a ship's void drive and isn't the host: the lead ship its own lever pull just chose, gone by until
        /// the room's lead catches up (or a few seconds pass, should the host never hear of it).
        /// </summary>
        private static int _pendingLead = -1;
        private static int _pendingRoomLead = -1;
        private static float _pendingUntil;
        private const float PendingSeconds = 3f;

        /// <summary>
        /// The lead ship's slot: the ship whose void lever started the current (or last) jump, shared by the host as a room
        /// property; before any jump, and while that ship is lost, the living ship with the lowest number. A lead ship lost
        /// mid-jump hands over to the next one, which is in the same state.
        /// </summary>
        public static int LeaderSlot
        {
            get
            {
                object session = GameSessionManager.ActiveSession;
                if (!ReferenceEquals(session, _leadSession))
                {
                    _leadSession = session;
                    _hostLead = -1;
                    _pendingLead = -1;
                }
                int chosen = ChosenLead();
                int lead = chosen >= 0 && FleetShips.IsAlive(chosen) ? chosen : -1;
                if (lead < 0) lead = LowestLivingSlot();
                if (lead != _lastLead && _lastLead >= 0 && lead >= 0) Log.Info("Ship " + (lead + 1) + " leads the void jump now.");
                _lastLead = lead;
                return lead;
            }
        }

        /// <summary>Whether a ship is living (not dying); a ship being torn down counts as dying.</summary>
        private static bool Living(PlayerControlledShip ship)
        {
            try
            {
                return ship != null && !ship.StartedDeath;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The lowest slot of a living ship, or -1 (FleetShips.AliveSlots()[0], without making a list).</summary>
        private static int LowestLivingSlot()
        {
            int lowest = -1;
            IList<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (!Living(ship)) continue;
                int slot = FleetShips.Slot(ship);
                if (slot >= 0 && (lowest < 0 || slot < lowest)) lowest = slot;
            }
            return lowest;
        }

        /// <summary>
        /// The lead ship this client goes by, whether or not it still lives. The host: its own choice, else (a new host) the
        /// room's. Anyone else: the room's, except just after their own lever pull chose one (the room's copy follows a round
        /// trip later).
        /// </summary>
        private static int ChosenLead()
        {
            if (PhotonNetwork.IsMasterClient)
            {
                if (_hostLead < 0 && _pendingLead >= 0 && Time.unscaledTime < _pendingUntil)
                {
                    // Chosen by this player's own lever pull just before they became the host: it stands.
                    int pending = _pendingLead;
                    _pendingLead = -1;
                    SetLead(pending);
                }
                return _hostLead >= 0 ? _hostLead : RoomLead();
            }
            // A host's own choice is out of date once someone else is the host.
            _hostLead = -1;
            int room = RoomLead();
            if (_pendingLead >= 0)
            {
                if (room != _pendingLead && room == _pendingRoomLead && Time.unscaledTime < _pendingUntil) return _pendingLead;
                _pendingLead = -1;
            }
            return room;
        }

        private static int RoomLead()
        {
            object value;
            return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeyLead, out value) && value is int
                ? (int)value
                : -1;
        }

        /// <summary>
        /// The ship whose lever was pulled leads the jump. On the host it becomes the room's lead ship. Elsewhere (the client
        /// running that ship's drive is its pilot's) it leads here at once, and every other player is told: the host makes it
        /// the room's, the others go by it until the room's catches up.
        /// </summary>
        private static void SetLead(int slot)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                HoldPendingLead(slot);
                Net.ToOthers(Msg.JumpLead, slot);
                Log.Info("Ship " + (slot + 1) + " leads the void jump (its lever was pulled here; the other players are told).");
                return;
            }
            _hostLead = slot;
            try
            {
                if (PhotonNetwork.CurrentRoom != null) PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { KeyLead, slot } });
            }
            catch (Exception e)
            {
                Log.Failure("Sharing the lead ship of the void jump", e);
            }
            Log.Info("Ship " + (slot + 1) + " leads the void jump.");
        }

        /// <summary>A client that isn't the host goes by this lead ship until the room's lead catches up (or a few seconds pass).</summary>
        private static void HoldPendingLead(int slot)
        {
            _pendingLead = slot;
            _pendingRoomLead = RoomLead();
            _pendingUntil = Time.unscaledTime + PendingSeconds;
        }

        /// <summary>
        /// The jump's messages: the lead ship chosen where a lever pull was decided (a ship's pilot), which the host makes the
        /// room's and everyone else goes by at once; and, for the player who pulled a lever, why the jump didn't start.
        /// </summary>
        public static void Listen()
        {
            Net.On(Msg.JumpLead, (sender, data) =>
            {
                if (!Linked) return;
                int slot = Net.Get(data, 0, -1);
                if (slot < 0 || !FleetShips.IsAlive(slot)) return;
                if (PhotonNetwork.IsMasterClient)
                {
                    // Taken from whoever sends it: a lead ship can change at any point of a jump, as the ships are in the same state.
                    if (slot != _hostLead) SetLead(slot);
                    return;
                }
                // The lead ship's first steps of the jump come straight from the player whose client runs it, and reach this
                // client before the host's room value could: a pilot here whose ship follows must already know which ship
                // leads, or that ship misses the start of the jump. (Players on an older version only tell the host.)
                if (RunsADrive(sender)) HoldPendingLead(slot);
            });
            Net.On(Msg.JumpRefused, (sender, data) =>
            {
                string text = Net.Get<string>(data, 0, null);
                if (string.IsNullOrEmpty(text) || !Linked || !RunsADrive(sender)) return;
                Notice.Show(text);
            });
        }

        /// <summary>Whether a player's client decides lever pulls: the host's, or that of the owner of a living ship's void drive.</summary>
        private static bool RunsADrive(int actor)
        {
            if (Net.FromHost(actor)) return true;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship == null || ship.StartedDeath) continue;
                VoidDriveModule drive = ship.GetModule<VoidDriveModule>();
                if (drive != null && drive.photonView != null && drive.photonView.OwnerActorNr == actor) return true;
            }
            return false;
        }

        /// <summary>True while any living ship is somewhere in a jump (past setting the escape vector).</summary>
        public static bool JumpUnderWay()
        {
            IList<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (ship == null || ship.StartedDeath) continue;
                VoidJumpSystem system = SystemOf(ship);
                VoidJumpState state = system != null ? system.ActiveState : null;
                if (state != null && !(state is VoidJumpInactive) && !(state is VoidJumpExitVectorSet)) return true;
            }
            return false;
        }

        private static bool IsFollower(VoidJumpSystem system)
        {
            if (!Linked || system == null || system.PlayerShip == null) return false;
            int slot = FleetShips.Slot(system.PlayerShip);
            return slot >= 0 && slot != LeaderSlot;
        }

        private static bool IsLeader(VoidJumpSystem system)
        {
            return Linked && system != null && system.PlayerShip != null && FleetShips.Slot(system.PlayerShip) == LeaderSlot;
        }

        /// <summary>A ship's jump system, looked up once per ship.</summary>
        private static VoidJumpSystem SystemOf(PlayerControlledShip ship)
        {
            if (ship == null) return null;
            int id = ship.GetInstanceID();
            VoidJumpSystem system;
            if (Systems.TryGetValue(id, out system))
            {
                if (system != null) return system;
                Systems.Remove(id);
            }
            system = ship.GetComponentInChildren<VoidJumpSystem>();
            if (system == null) return null;
            // Those of missions that are over.
            if (Systems.Count >= 32) Systems.Clear();
            Systems[id] = system;
            return system;
        }

        /// <summary>True while a ship is somewhere between leaving a sector and arriving in the next one.</summary>
        public static bool IsTravelling(PlayerControlledShip ship)
        {
            VoidJumpSystem system = SystemOf(ship);
            if (system == null) return false;
            VoidJumpState state = system.ActiveState;
            return state is VoidJumpTravellingStable || state is VoidJumpTravellingUnstable || state is VoidJumpApproachingDestination ||
                   state is VoidJumpSpinningDown || state is VoidJumpInterdiction;
        }

        // ---------------------------------------------------------------- ship 2 follows ship 1

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Prefix)]
        private static bool ChangeStatePrefix(VoidJumpSystem __instance, int newActiveStateIndex)
        {
            if (!IsFollower(__instance))
            {
                Counted.Add(false);
                return true;
            }
            bool allowed = _mirroring || _starting || _reading == __instance || _vectorDepth > 0;
            if (!allowed || newActiveStateIndex >= FirstEndState)
            {
                Counted.Add(false);
                return false;
            }
            _followerDepth++;
            Counted.Add(true);
            return true;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Finalizer)]
        private static void ChangeStateFinalizer()
        {
            if (Counted.Count == 0) return;
            bool counted = Counted[Counted.Count - 1];
            Counted.RemoveAt(Counted.Count - 1);
            if (counted) _followerDepth--;
        }

        /// <summary>The lead ship changed state: the client driving each follower's jump system makes it do the same.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "ChangeActiveStateByIndex", PatchKind.Postfix)]
        private static void ChangeStatePostfix(VoidJumpSystem __instance, int newActiveStateIndex)
        {
            if (!IsLeader(__instance) || newActiveStateIndex >= FirstEndState || ChangeStateByIndex == null) return;
            foreach (PlayerControlledShip followerShip in new List<PlayerControlledShip>(FleetShips.All))
            {
                if (followerShip == null || followerShip == __instance.PlayerShip) continue;
                VoidJumpSystem follower = SystemOf(followerShip);
                if (follower == null || follower.photonView == null || !follower.photonView.AmOwner) continue;
                try
                {
                    if (followerShip.StartedDeath) continue;
                    int current = StateIndexField != null ? (int)StateIndexField.GetValue(follower) : -1;
                    if (current == newActiveStateIndex) continue;
                    if (newActiveStateIndex >= StateAligning && newActiveStateIndex <= StateTravellingUnstable && follower.ActiveSystemHook == null)
                    {
                        VoidDriveModule drive = followerShip.GetModule<VoidDriveModule>();
                        if (drive != null) follower.ActiveSystemHook = drive;
                    }
                    // Taken straight out of Inactive (its escape vector never came), it skips the step that notes the sector it
                    // leaves, which aligning steers by: it leaves the lead ship's.
                    if (current == StateInactive && newActiveStateIndex >= StateAligning && follower.FromSector == null)
                        follower.FromSector = __instance.FromSector ?? GameSessionManager.ActiveSector;
                    _mirroring = true;
                    ChangeStateByIndex.Invoke(follower, new object[] { newActiveStateIndex });
                }
                catch (Exception e)
                {
                    Log.Failure("Moving " + FleetShips.Describe(followerShip) + " through the void jump with the lead ship", e);
                }
                finally
                {
                    _mirroring = false;
                }
            }
        }

        /// <summary>Other clients take ship 2's state from the game's own sync, as for any ship.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "OnPhotonSerializeView", PatchKind.Prefix)]
        private static void SerializePrefix(VoidJumpSystem __instance, PhotonStream stream)
        {
            if (stream != null && stream.IsReading && IsFollower(__instance)) _reading = __instance;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "OnPhotonSerializeView", PatchKind.Finalizer)]
        private static void SerializeFinalizer()
        {
            _reading = null;
        }

        /// <summary>At the start of a mission every ship's jump system picks its first state (in the void or not).</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "Start", PatchKind.Prefix)]
        private static void StartPrefix()
        {
            _starting = true;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "Start", PatchKind.Finalizer)]
        private static void StartFinalizer()
        {
            _starting = false;
        }

        private static int _vectorDepth;
        private static bool _sharingVector;

        /// <summary>
        /// A following ship's own signals (its timers, "not charged", "aligned") don't move it; the lead ship's jump does. The
        /// escape vector is the sector's, though: set (or cleared) on any ship's astral map before a jump, it is set (or
        /// cleared) on every ship at once. The client running the ship that got it passes it on: straight to the other ships
        /// it runs, and through the game's own call (sent to whoever runs that ship) to a ship a pilot runs. Only a signal
        /// that changes its ship is passed on, so the ships don't pass it back and forth.
        /// </summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "SendSignal", PatchKind.Prefix)]
        private static bool SignalPrefix(VoidJumpSystem __instance, VoidJumpSignal signal, out bool __state)
        {
            __state = false;
            if (!Linked) return true;
            bool vector = signal == VoidJumpSignal.ExitVectorSet || signal == VoidJumpSignal.ExitVectorUnset;
            if (!vector || JumpUnderWay()) return !IsFollower(__instance);
            __state = true;
            _vectorDepth++;
            if (_sharingVector || __instance.photonView == null || !__instance.photonView.AmOwner || !ChangesVector(__instance, signal)) return true;
            _sharingVector = true;
            try
            {
                foreach (PlayerControlledShip ship in new List<PlayerControlledShip>(FleetShips.All))
                {
                    VoidJumpSystem other = SystemOf(ship);
                    if (other == null || other == __instance || ship.StartedDeath || other.photonView == null) continue;
                    if (other.photonView.AmOwner) other.SendSignal(signal);
                    else other.ExitVectorSet(signal == VoidJumpSignal.ExitVectorSet);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Sharing the escape vector with every ship", e);
            }
            finally
            {
                _sharingVector = false;
            }
            return true;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "SendSignal", PatchKind.Finalizer)]
        private static void SignalFinalizer(bool __state)
        {
            if (__state) _vectorDepth--;
        }

        /// <summary>Whether an escape-vector signal changes a jump system (before a jump): set while inactive, cleared while set.</summary>
        private static bool ChangesVector(VoidJumpSystem system, VoidJumpSignal signal)
        {
            VoidJumpState state = system.ActiveState;
            return signal == VoidJumpSignal.ExitVectorSet ? state is VoidJumpInactive : state is VoidJumpExitVectorSet;
        }

        // ---------------------------------------------------------------- once per jump, not once per ship

        [GamePatch(Feature, "Gameplay.Quests.Quest", "NextJumpInterdictionCheck", PatchKind.Prefix)]
        private static bool InterdictionRollPrefix(ref ValueTuple<bool, bool> __result)
        {
            if (!InFollowerStep) return true;
            __result = new ValueTuple<bool, bool>(false, false);
            return false;
        }

        [GamePatch(Feature, "Gameplay.Quests.Quest", "MarkInterdictionEntered", PatchKind.Prefix)]
        private static bool InterdictionCountPrefix()
        {
            return !InFollowerStep;
        }

        [GamePatch(Feature, "GameSessionSectorManager", "ExitCurrentSector", PatchKind.Prefix)]
        private static bool ExitSectorPrefix()
        {
            return !InFollowerStep;
        }

        /// <summary>
        /// A following ship enters the void with the lead ship. Its own systems hear it (its crew's astral map marks the sector
        /// done and moves on, its sockets meant to close in the void close, its collision checks start over), but the
        /// once-per-jump parts of entering the void (the autosave, clearing NPCs left in the old sector) are the lead ship's.
        /// </summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "EnterVoid", PatchKind.Prefix)]
        private static bool EnterVoidPrefix(VoidJumpSystem __instance)
        {
            if (!IsFollower(__instance)) return true;
            try
            {
                var entered = VoidEnteredField != null ? VoidEnteredField.GetValue(__instance) as Action : null;
                if (entered != null) entered();
            }
            catch (Exception e)
            {
                Log.Failure("Telling " + FleetShips.Describe(__instance.PlayerShip) + "'s systems it entered the void", e);
            }
            return false;
        }

        /// <summary>Ship 2 can reach "arrived" a moment before the sector is entered on a client; it joins the sector then.</summary>
        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpInactive", "OnEnter", PatchKind.Prefix)]
        private static bool InactiveEnterPrefix(VoidJumpSystem ___voidJumpSystem)
        {
            if (!IsFollower(___voidJumpSystem) || GameSessionManager.ActiveSector != null || ___voidJumpSystem.DestinationSector != null) return true;
            if (___voidJumpSystem.ActiveSystemHook != null)
            {
                ___voidJumpSystem.ActiveSystemHook.OnEnter((VoidJumpState)___voidJumpSystem.GetState<VoidJumpInactive>());
                ___voidJumpSystem.ActiveSystemHook = null;
            }
            ___voidJumpSystem.FromSector = null;
            return false;
        }

        [GamePatch(Feature, "Gameplay.Ship.VoidJump.VoidJumpSystem", "EnterSector", PatchKind.Prefix)]
        private static bool EnterSectorPrefix(VoidJumpSystem __instance)
        {
            return !IsFollower(__instance) || GameSessionManager.ActiveSector != null || __instance.DestinationSector != null;
        }

        /// <summary>
        /// The mission's "the player ship arrived" rules count the lead ship's arrival only: every ship announces its own
        /// arrival (its own systems react to it), which set those rules off once per ship.
        /// </summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.ImpulseStatus", "OnOrbitObjectArrivedAtDestination", PatchKind.Prefix)]
        private static bool ArrivalRulePrefix(object orbitObject)
        {
            var ship = orbitObject as PlayerControlledShip;
            if (ship == null || !Linked) return true;
            int slot = FleetShips.Slot(ship);
            return slot < 0 || slot == LeaderSlot;
        }

        // ---------------------------------------------------------------- who may pull the lever

        /// <summary>
        /// The player whose lever pull this client is deciding right now (inside the game's start request, which the game sends
        /// to whoever runs that ship's void drive: the host, or the ship's pilot), or -1.
        /// </summary>
        private static int _startRequestFrom = -1;

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "TryStartImpulseJumpInternal", PatchKind.Prefix)]
        private static void StartRequestPrefix(PhotonMessageInfo info)
        {
            _startRequestFrom = info.Sender != null ? info.Sender.ActorNumber : (PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : 0);
        }

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "TryStartImpulseJumpInternal", PatchKind.Finalizer)]
        private static void StartRequestFinalizer()
        {
            _startRequestFrom = -1;
        }

        /// <summary>
        /// Pulling a void lever. In PvP there are no jumps. In co-op any ship's lever starts the jump for every ship, once
        /// every crewed ship's void drive is charged (and that ship has the escape vector, as the game checks): that ship leads
        /// the jump and the others follow it (TryStartJumpPostfix). While a crewed ship's drive isn't charged, the lever says
        /// which ship and nothing starts. During a jump, any ship's lever acts as the lead ship's.
        /// The game checks a pull twice: on the puller's screen (for the message), then where that ship's drive is run (the
        /// host, or the ship's pilot), which decides and starts the jump.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "TryStartJumpEngine", PatchKind.Prefix)]
        private static bool TryStartJumpPrefix(VoidDriveModule __instance, ref ImpulseJumpOutcome __result)
        {
            try
            {
                if (!FleetShips.Active) return true;
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (FleetSession.IsPvP)
                {
                    __result = ImpulseJumpOutcome.CantFlyNoImpulse;
                    Warn("No void jumps in Fleet Battle.");
                    return false;
                }
                if (!Linked) return true;
                bool deciding = _startRequestFrom >= 0;
                int slot = FleetShips.Slot(ship);
                if (JumpUnderWay())
                {
                    int lead = LeaderSlot;
                    if (slot == lead || lead < 0) return true;
                    // A following ship's lever, mid-jump: it pulls the lead ship's (the game sends that to whoever runs the lead
                    // ship's drive, and checks it for that ship).
                    if (deciding)
                    {
                        PlayerControlledShip leadShip = FleetShips.BySlot(lead);
                        VoidDriveModule leaderDrive = leadShip != null ? leadShip.GetModule<VoidDriveModule>() : null;
                        __result = ImpulseJumpOutcome.CantFlyNoImpulse;
                        if (leaderDrive != null && leaderDrive != __instance) leaderDrive.TryStartImpulseJump();
                    }
                    else
                    {
                        __result = ImpulseJumpOutcome.InTransit;
                    }
                    return false;
                }
                // Starting a jump: every crewed ship's void drive must be charged.
                List<string> uncharged = UnchargedShips();
                if (uncharged.Count > 0)
                {
                    __result = ImpulseJumpOutcome.CantFlyNoImpulse;
                    string text = "Void drive not charged on " + string.Join(" and ", uncharged.ToArray()) + ": every crewed ship's void drive must be charged to jump.";
                    if (deciding)
                    {
                        Log.Info("Void jump refused (" + FleetShips.Describe(ship) + "'s lever): " + text + " Drives as seen here: " + DescribeCharges() + ".");
                        // The player who pulled it hears why (on this client, their own check already told them).
                        if (_startRequestFrom != FleetShips.LocalActor)
                            Net.ToActor(_startRequestFrom, PhotonNetwork.IsMasterClient ? Msg.Notice : Msg.JumpRefused, text);
                    }
                    else if (Warn(text))
                    {
                        Log.Info("Void jump refused on this player's screen (" + FleetShips.Describe(ship) + "'s lever): " + text + " Drives as seen here: " +
                                 DescribeCharges() + ".");
                    }
                    return false;
                }
                // The game's own checks follow (the escape vector, power, the helm, its charge).
            }
            catch (Exception e)
            {
                Log.Failure("Checking the void jump", e);
            }
            return true;
        }

        /// <summary>
        /// A lever pull that starts the jump, where its ship's drive is run, makes that ship the lead ship; one the game
        /// refuses (no escape vector, no power, the helm off, not charged) changes nothing, and is logged with the drives as
        /// this client has them (Fleet's own refusals are logged where they are made). The lead is set before the jump's first
        /// step, which the game takes right after this check.
        /// On the puller's screen the game checks the drive's charge before it sends the pull on; a copy of a drive run on
        /// another player's game can read a hair short of full (<see cref="ChargedCopy"/>), so such a pull goes on to the
        /// drive's own game, which checks the charge exactly. A charged drive's engines all keep charging (each lever stays
        /// locked until a player lets it go), so for such a copy the game answers "partially charging"; "not charging" only
        /// once an engine has let go. Both go on.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "TryStartJumpEngine", PatchKind.Postfix)]
        private static void TryStartJumpPostfix(VoidDriveModule __instance, ref ImpulseJumpOutcome __result)
        {
            try
            {
                if (!Linked) return;
                bool deciding = _startRequestFrom >= 0;
                PlayerControlledShip ship = FleetShips.OwnerOf(__instance);
                if (__result == ImpulseJumpOutcome.ImpulseFlightStarted)
                {
                    int slot = FleetShips.Slot(ship);
                    if (deciding && slot >= 0) SetLead(slot);
                    return;
                }
                bool notCharged = __result == ImpulseJumpOutcome.CantFlyImpulsePartiallyCharging || __result == ImpulseJumpOutcome.CantFlyImpulseNotCharging;
                if (!deciding && notCharged && __instance.photonView != null && !__instance.photonView.AmOwner && __instance.JumpCharge >= ChargedCopy)
                {
                    Log.Info("Void jump: " + FleetShips.Describe(ship) + "'s drive reads " + (__instance.JumpCharge * 100f).ToString("0.0") +
                             " % here; the pull goes on to the game that runs it, which checks its charge exactly.");
                    __result = ImpulseJumpOutcome.ImpulseFlightStarted;
                    return;
                }
                // The game never answers CantFlyNoImpulse itself: that is Fleet's own refusal. InTransit is a pull mid-jump.
                if (__result == ImpulseJumpOutcome.InTransit || __result == ImpulseJumpOutcome.CantFlyNoImpulse) return;
                Log.Info("Void jump refused by the game (" + FleetShips.Describe(ship) + "'s lever" + (deciding ? "" : ", on this player's screen") + "): " + __result +
                         ". Drives as seen here: " + DescribeCharges() + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Choosing the lead ship of the void jump", e);
            }
        }

        /// <summary>
        /// How charged a copy of a drive run on another player's game must read to count as charged here. Photon leaves out a
        /// change of less than 0.01 since the update before, so a full drive's copy can stop just short of full, but always
        /// above 0.99; the drive's own game sends its whole state once it is full (SendWholeState), but a player on an older
        /// Fleet doesn't, and their drive would otherwise hold every jump up. The drive's own game still checks its own charge
        /// exactly.
        /// </summary>
        private const float ChargedCopy = 0.99f;

        /// <summary>
        /// "Ship 2", "Ship 3": the living ships whose void drive isn't fully charged, among those with a crew member in the room.
        /// A ship nobody crews (co-op with fewer players than ships, or its crew left) doesn't hold the jump up: the lead ship's
        /// jump carries it along whatever its charge. A drive run on another player's game counts as charged a hair short of
        /// full (<see cref="ChargedCopy"/>).
        /// </summary>
        private static List<string> UnchargedShips()
        {
            var names = new List<string>();
            foreach (int slot in FleetShips.AliveSlots())
            {
                if (!FleetShips.HasCrewInRoom(slot)) continue;
                PlayerControlledShip ship = FleetShips.BySlot(slot);
                VoidDriveModule drive = ship != null ? ship.GetModule<VoidDriveModule>() : null;
                if (drive == null) names.Add("Ship " + (slot + 1));
                else if (!drive.IsFullyCharged && (drive.photonView == null || drive.photonView.AmOwner || drive.JumpCharge < ChargedCopy)) names.Add("Ship " + (slot + 1));
            }
            return names;
        }

        /// <summary>
        /// For the log: each living ship's void drive charge as this client has it, and where the drive is run (its charge is
        /// only worked out there): "ship 1 100.0 % (run here), ship 2 99.4 % (run by player 3)".
        /// </summary>
        private static string DescribeCharges()
        {
            var parts = new List<string>();
            foreach (int slot in FleetShips.AliveSlots())
            {
                PlayerControlledShip ship = FleetShips.BySlot(slot);
                VoidDriveModule drive = ship != null ? ship.GetModule<VoidDriveModule>() : null;
                if (drive == null)
                {
                    parts.Add("ship " + (slot + 1) + " has no drive");
                    continue;
                }
                PhotonView view = drive.photonView;
                string runner = view == null ? "not networked" : (view.AmOwner ? "run here" : "run by player " + view.OwnerActorNr);
                parts.Add("ship " + (slot + 1) + " " + (drive.JumpCharge * 100f).ToString("0.0") + " % (" + runner + (FleetShips.HasCrewInRoom(slot) ? "" : ", no crew") + ")");
            }
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Shows a warning on this player's screen, at most every 3 seconds; true when it was shown.</summary>
        private static bool Warn(string text)
        {
            if (Time.unscaledTime < _nextWarning) return false;
            _nextWarning = Time.unscaledTime + 3f;
            try
            {
                ViewEventBus.Instance.OnShowQuickWarning.Publish(text);
            }
            catch
            {
                Notice.Show(text);
            }
            return true;
        }

        // ---------------------------------------------------------------- every player's copy of a charged drive

        /// <summary>What Photon last sent of a network object (it compares the next update with this).</summary>
        private static readonly FieldInfo LastSentField = GameMember.Field(typeof(PhotonView), "lastOnSerializeDataSent");

        /// <summary>
        /// A drive's charge is worked out only where the drive is run (the host, or the ship's pilot); everyone else gets it
        /// through Photon's updates, which leave out a number that has changed by less than 0.01 since the update before
        /// (Photon's float precision). The last step up to a full charge is often that small, so on the other players' games
        /// the drive could stop at 99 % (or show 100 % without being full) until it was charged again: it read as not charged
        /// there, so a lever pulled there, and every "each crewed ship's drive is charged" check made there, was refused. The
        /// host runs the drive of every ship nobody sits at the helm of, which is why the host's own pulls went through and
        /// other players' didn't. So when a drive run here becomes fully charged, Photon sends its whole state with the next
        /// update (as Photon does itself for every object when a player joins), and every copy reads exactly full.
        /// </summary>
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "UpdateOn", PatchKind.Prefix, Optional = true)]
        private static void ChargePrefix(VoidDriveModule __instance, out float __state)
        {
            __state = __instance.JumpCharge;
        }

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "UpdateOn", PatchKind.Postfix, Optional = true)]
        private static void ChargePostfix(VoidDriveModule __instance, float __state)
        {
            // Every physics step while the drive is powered: only the step that fills it up goes further.
            if (__state >= 1f || !__instance.IsFullyCharged || FleetSession.Mode != FleetMode.Coop) return;
            SendWholeState(__instance);
        }

        /// <summary>Photon sends a drive's whole state with its next update, from the client that runs it.</summary>
        private static void SendWholeState(VoidDriveModule drive)
        {
            try
            {
                PhotonView view = drive.photonView;
                if (view == null || !view.AmOwner || LastSentField == null) return;
                LastSentField.SetValue(view, null);
                int slot = FleetShips.Slot(FleetShips.OwnerOf(drive));
                Log.Info((slot >= 0 ? "Ship " + (slot + 1) + "'s" : "A") +
                         " void drive is fully charged (it is run here): its state goes out to every player again, so it reads as charged on every screen.");
            }
            catch (Exception e)
            {
                Log.Failure("Sending a charged void drive's state to every player", e);
            }
        }

        // ---------------------------------------------------------------- arriving side by side

        /// <summary>
        /// A ship's place in the line of ships arriving side by side: by its number among the living ships (every frame of
        /// the arrival, so counted without making a list).
        /// </summary>
        private static Vector3 ArrivalOffset(VoidJumpSystem system, Sector.ImpulseEnterPoint enter)
        {
            if (!Linked || system == null || system.PlayerShip == null || enter == null) return Vector3.zero;
            int mine = FleetShips.Slot(system.PlayerShip);
            if (mine < 0) return Vector3.zero;
            bool living = false;
            int lower = 0;
            int count = 0;
            IList<PlayerControlledShip> ships = FleetShips.All;
            for (int i = 0; i < ships.Count; i++)
            {
                PlayerControlledShip ship = ships[i];
                if (!Living(ship)) continue;
                int slot = FleetShips.Slot(ship);
                if (slot < 0) continue;
                count++;
                if (slot < mine) lower++;
                else if (slot == mine) living = true;
            }
            if (!living) return Vector3.zero;
            Vector3 look = enter.LookDirectionPosition - enter.Position;
            look.y = 0f;
            if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, look.normalized);
            return ShipSpawn.LineOffset(lower, count, right, Settings.ShipSpacing.Value);
        }

        private static Sector.ImpulseEnterPoint EnterPoint(VoidJumpSystem system)
        {
            GameSessionSector destination = system != null ? system.DestinationSector : null;
            return destination != null && destination.SectorAsset != null ? destination.SectorAsset.PlayerShipEnterPoint : null;
        }

        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "OnEnter", PatchKind.Postfix)]
        private static void DriveEnterPostfix(VoidJumpState jumpState, VoidJumpSystem ___voidJumpSystem, ref Vector3 ___velocityToRelocateInVoid)
        {
            try
            {
                if (!Linked || !(jumpState is VoidJumpApproachingDestination || jumpState is VoidJumpInterdiction)) return;
                Sector.ImpulseEnterPoint enter = EnterPoint(___voidJumpSystem);
                if (enter == null) return;
                Vector3 target = enter.Position + ArrivalOffset(___voidJumpSystem, enter);
                ___velocityToRelocateInVoid = (target - ___voidJumpSystem.PlayerShipPlatform.Position) / 0.1f;
            }
            catch (Exception e)
            {
                Log.Failure("Aiming a ship at its arrival point", e);
            }
        }

        /// <summary>The game's arrival steering, aimed at each ship's own arrival point.</summary>
        [GamePatch(Feature, "CG.Ship.Modules.VoidDriveModule", "UpdateState", PatchKind.Prefix)]
        private static bool DriveUpdatePrefix(VoidDriveModule __instance, VoidJumpState jumpState, VoidJumpSystem ___voidJumpSystem, Helm ___helm,
            ShipEngine ___shipEngine, Vector3 ___velocityToRelocateInVoid)
        {
            if (!Linked || !(jumpState is VoidJumpApproachingDestination || jumpState is VoidJumpInterdiction)) return true;
            try
            {
                if (!__instance.HookPv.AmController) return false;
                Sector.ImpulseEnterPoint enter = EnterPoint(___voidJumpSystem);
                if (enter == null) return true;
                if (___helm.RotateTowardsPerFrame(enter.LookDirectionPosition - enter.Position)) ___shipEngine.SetInput(Vector3.zero, Vector3.zero);
                Vector3 target = enter.Position + ArrivalOffset(___voidJumpSystem, enter);
                Vector3 toTarget = target - ___voidJumpSystem.PlayerShipPlatform.Position;
                if (toTarget.magnitude < 5f || Vector3.Dot(toTarget.normalized, ___velocityToRelocateInVoid.normalized) < 0f)
                {
                    ___voidJumpSystem.PlayerShipPlatform.Velocity = Vector3.zero;
                }
                else
                {
                    ___voidJumpSystem.PlayerShipPlatform.SetVelocity(___velocityToRelocateInVoid, ___voidJumpSystem.PlayerShipPlatform.AngularVelocity);
                }
                return false;
            }
            catch (Exception e)
            {
                Log.Failure("Steering a ship to its arrival point", e);
                return true;
            }
        }
    }
}
