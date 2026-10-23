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
    /// the ship the lead at once and, when it isn't the host, tells the host, who shares the lead ship as a room property.
    /// Before the first jump (and while it is lost) the lead is the living ship with the lowest number.
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
        public const string Feature = "Two ships jump together";

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

        /// <summary>True while a following ship's jump system is changing state (the once-only parts of a jump are skipped then).</summary>
        private static bool InFollowerStep
        {
            get { return _followerDepth > 0; }
        }

        private static bool Linked
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
                if (lead < 0)
                {
                    List<int> alive = FleetShips.AliveSlots();
                    lead = alive.Count > 0 ? alive[0] : -1;
                }
                if (lead != _lastLead && _lastLead >= 0 && lead >= 0) Log.Info("Ship " + (lead + 1) + " leads the void jump now.");
                _lastLead = lead;
                return lead;
            }
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
        /// running that ship's drive is its pilot's) it leads here at once, and the host is told so it becomes the room's.
        /// </summary>
        private static void SetLead(int slot)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                _pendingLead = slot;
                _pendingRoomLead = RoomLead();
                _pendingUntil = Time.unscaledTime + PendingSeconds;
                Net.ToHost(Msg.JumpLead, slot);
                Log.Info("Ship " + (slot + 1) + " leads the void jump (its lever was pulled here; the host is told).");
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

        /// <summary>
        /// The jump's messages: the host takes the lead ship chosen where a lever pull was decided (a ship's pilot); the player
        /// who pulled a lever hears why the jump didn't start.
        /// </summary>
        public static void Listen()
        {
            Net.On(Msg.JumpLead, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !Linked) return;
                int slot = Net.Get(data, 0, -1);
                if (slot < 0 || !FleetShips.IsAlive(slot)) return;
                // Taken from whoever sends it: a lead ship can change at any point of a jump, as the ships are in the same state.
                if (slot != _hostLead) SetLead(slot);
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
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
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

        private static VoidJumpSystem SystemOf(PlayerControlledShip ship)
        {
            return ship != null ? ship.GetComponentInChildren<VoidJumpSystem>() : null;
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
        /// the jump and the others follow it. While a crewed ship's drive isn't charged, the lever says which ship and nothing
        /// starts. During a jump, any ship's lever acts as the lead ship's.
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
                    Warn("No void jumps in a PvP match: the fight ends when a ship is destroyed.");
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
                        Log.Info("Void jump refused (" + FleetShips.Describe(ship) + "'s lever): " + text);
                        // The player who pulled it hears why (on this client, their own check already told them).
                        if (_startRequestFrom != FleetShips.LocalActor)
                            Net.ToActor(_startRequestFrom, PhotonNetwork.IsMasterClient ? Msg.Notice : Msg.JumpRefused, text);
                    }
                    else
                    {
                        Warn(text);
                    }
                    return false;
                }
                // This ship leads the jump; the game's own checks follow (the escape vector, power, the helm, its charge).
                if (deciding && slot >= 0) SetLead(slot);
            }
            catch (Exception e)
            {
                Log.Failure("Checking the void jump", e);
            }
            return true;
        }

        /// <summary>
        /// "Ship 2", "Ship 3": the living ships whose void drive isn't fully charged, among those with a crew member in the room.
        /// A ship nobody crews (co-op with fewer players than ships, or its crew left) doesn't hold the jump up: the lead ship's
        /// jump carries it along whatever its charge.
        /// </summary>
        private static List<string> UnchargedShips()
        {
            var names = new List<string>();
            foreach (int slot in FleetShips.AliveSlots())
            {
                if (!FleetShips.HasCrewInRoom(slot)) continue;
                PlayerControlledShip ship = FleetShips.BySlot(slot);
                VoidDriveModule drive = ship != null ? ship.GetModule<VoidDriveModule>() : null;
                if (drive == null || !drive.IsFullyCharged) names.Add("Ship " + (slot + 1));
            }
            return names;
        }

        private static void Warn(string text)
        {
            if (Time.unscaledTime < _nextWarning) return;
            _nextWarning = Time.unscaledTime + 3f;
            try
            {
                ViewEventBus.Instance.OnShowQuickWarning.Publish(text);
            }
            catch
            {
                Notice.Show(text);
            }
        }

        // ---------------------------------------------------------------- arriving side by side

        /// <summary>A ship's place in the line of ships arriving side by side: by its number among the living ships.</summary>
        private static Vector3 ArrivalOffset(VoidJumpSystem system, Sector.ImpulseEnterPoint enter)
        {
            if (!Linked || system == null || system.PlayerShip == null || enter == null) return Vector3.zero;
            Vector3 look = enter.LookDirectionPosition - enter.Position;
            look.y = 0f;
            if (look.sqrMagnitude < 0.01f) look = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, look.normalized);
            List<int> alive = FleetShips.AliveSlots();
            int index = alive.IndexOf(FleetShips.Slot(system.PlayerShip));
            if (index < 0) return Vector3.zero;
            return ShipSpawn.LineOffset(index, alive.Count, right, Settings.ShipSpacing.Value);
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
