using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CG.Game;
using CG.Game.Player;
using CG.Space;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// A ship's destruction. The game announces "the player ship is dying" with no ship attached, and everything listening
    /// reacts: every death sequence plays, everyone dies, every clone tube closes, the mission fails, and the host destroys
    /// "the" player ship. With several ships only what concerns the dying ship reacts:
    /// - its own death sequence, clone tubes and destruction;
    /// - characters aboard it die with it, and so does anyone out in space (aboard no ship) close enough to be caught in the
    ///   explosion (BlastRadius) when it goes off; a crew member further out plays on. Only the screens of players aboard it
    ///   show its death sequence (DiesWith, LocalGameOver);
    /// - the mission only counts the ships as lost when no ship is left (co-op), and PvP decides the winner itself;
    /// - everyone given that ship in the lobby (co-op and PvP) can't be re-cloned for the rest of the mission: they play on
    ///   while their character lives (aboard another ship, or out in space away from the explosion), then watch the
    ///   remaining ships (Spectator);
    /// - the host may carry on with the ships that are left, or take everyone back to the hub (FleetReturn);
    /// - every client notes when the ship started dying, so a player who becomes host during its death still removes the
    ///   wreck (Tick);
    /// - only the ship's own client (its owner, which kills it when its hull is gone) or the host may start its death: the
    ///   game's death message from anyone else is ignored (a modified game could otherwise destroy any ship).
    /// </summary>
    internal static class ShipDeath
    {
        public const string Feature = FleetShips.Feature;

        /// <summary>
        /// How close to a dying ship's position a character out in space (aboard no ship) must be when it explodes to be caught
        /// in the explosion.
        /// </summary>
        public const float BlastRadius = 150f;

        /// <summary>
        /// A lost ship's wreck still there this long after its death sequence should have ended (when the host that saw the death
        /// begin destroys the ship, about 9 seconds in) is removed by the host.
        /// </summary>
        private const float WreckGraceSeconds = 3f;

        /// <summary>When each lost ship's wreck is due to be gone (Time.unscaledTime on this client).</summary>
        private static readonly Dictionary<PlayerControlledShip, float> WreckDue = new Dictionary<PlayerControlledShip, float>();

        /// <summary>The ship whose death is being announced right now (during the game's event).</summary>
        public static PlayerControlledShip Dying { get; private set; }

        /// <summary>Raised on every client when a player ship starts dying while another one is still there.</summary>
        public static event Action<PlayerControlledShip> ShipLost;

        private static bool Filtering
        {
            get { return Dying != null && FleetShips.All.Count > 1; }
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "NetPublishShipDeathBegan", PatchKind.Prefix)]
        private static bool DeathBeganPrefix(PlayerControlledShip __instance)
        {
            try
            {
                if (!SentByShipOrHost(__instance)) return false;
            }
            catch (Exception e)
            {
                Log.Failure("Checking who started a ship's death", e);
            }
            Dying = __instance;
            if (FleetShips.All.Count < 2) return true;
            try
            {
                Log.Info(FleetShips.Describe(__instance) + " is destroyed.");
                if (__instance != null && !WreckDue.ContainsKey(__instance)) WreckDue[__instance] = Time.unscaledTime + SequenceSeconds() + WreckGraceSeconds;
            }
            catch (Exception e)
            {
                Log.Failure("Noting a destroyed ship", e);
            }
            RaiseShipLost(__instance);
            if (!PhotonNetwork.IsMasterClient) return true;
            try
            {
                HandleCrew(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Moving a destroyed ship's crew", e);
            }
            return true;
        }

        /// <summary>ShipLost, each handler on its own: one that fails doesn't stop the others.</summary>
        private static void RaiseShipLost(PlayerControlledShip ship)
        {
            Action<PlayerControlledShip> lost = ShipLost;
            if (lost == null) return;
            foreach (Delegate handler in lost.GetInvocationList())
            {
                try
                {
                    ((Action<PlayerControlledShip>)handler)(ship);
                }
                catch (Exception e)
                {
                    Log.Failure("Reacting to a destroyed ship (" + handler.Method.DeclaringType.Name + "." + handler.Method.Name + ")", e);
                }
            }
        }

        /// <summary>
        /// Whether the game's "this ship starts dying" message comes from someone allowed to send it, in a fleet mission: the
        /// client that owns the ship's network object (it kills the ship when its hull reaches 0: the host, or the pilot who
        /// flies it) or the host. Anyone else's (a modified game's) is ignored on every client. When the sender can't be told
        /// (RpcSender not in), or outside a fleet mission, the message counts as the game would count it.
        /// </summary>
        private static bool SentByShipOrHost(PlayerControlledShip ship)
        {
            if (ship == null || FleetSession.ShipCount < 2) return true;
            PhotonView view = ship.photonView;
            int actor;
            if (view == null || !RpcSender.Of(view, out actor)) return true;
            if (actor == view.OwnerActorNr || actor == view.ControllerActorNr) return true;
            Photon.Realtime.Player host = PhotonNetwork.MasterClient;
            if (host != null && actor == host.ActorNumber) return true;
            Log.WarnOnce("ship death from " + actor + " for " + view.ViewID,
                "Player " + actor + " sent the destruction of " + FleetShips.Describe(ship) + ", which isn't theirs to send; ignored.");
            return false;
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "NetPublishShipDeathBegan", PatchKind.Finalizer)]
        private static void DeathBeganFinalizer()
        {
            Dying = null;
        }

        /// <summary>The living ships other than the dying one.</summary>
        private static List<int> SurvivingSlots(PlayerControlledShip dying)
        {
            List<int> alive = FleetShips.AliveSlots();
            alive.Remove(FleetShips.Slot(dying));
            return alive;
        }

        /// <summary>
        /// Host: a destroyed ship's crew can't be re-cloned for the rest of the mission. They are DEAD from now on, wherever they
        /// are: they play on while their character lives (aboard another ship, or out in space away from the explosion), then
        /// watch. The roster does it when it runs; this is the same without one. With no ship left, the mission ends the game's
        /// own way (co-op); in PvP a team's last ship decides the match (PvpMatch).
        /// </summary>
        private static void HandleCrew(PlayerControlledShip dying)
        {
            int deadSlot = FleetShips.Slot(dying);
            List<int> alive = SurvivingSlots(dying);
            if (deadSlot < 0 || alive.Count == 0) return;
            if (!PvpRoster.OnShipLost(deadSlot)) FleetShips.PublishCrew(CrewRules.LoseShip(FleetShips.Crews(), deadSlot));
            if (!FleetSession.IsPvP)
            {
                int left = alive.Count;
                Notice.ShowAll(FleetSlots.Name(deadSlot, false) + " is lost! Its crew can't be re-cloned this mission: they play on while they live, then watch. The fleet carries on with " +
                               left + (left == 1 ? " ship." : " ships."));
            }
            if (FleetSession.IsPvP && !TeamFightsOn(FleetShips.TeamOfSlot(deadSlot), dying)) return;
            FleetReturn.ShipWasLost();
        }

        /// <summary>PvP: whether a team still has a living ship other than the dying one.</summary>
        public static bool TeamFightsOn(int team, PlayerControlledShip dying)
        {
            foreach (int slot in SurvivingSlots(dying))
            {
                if (FleetShips.TeamOfSlot(slot) == team) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- who dies with the ship

        /// <summary>
        /// A character dies with a ship when aboard it (its own crew, a visitor or a boarder), or when aboard no ship (out in
        /// space) and within BlastRadius of the ship, caught in the explosion. Anyone else plays on, the ship's own crew too:
        /// they can't be re-cloned (the host marks them DEAD), so they watch once their character dies.
        /// </summary>
        public static bool DiesWith(Player character, PlayerControlledShip ship)
        {
            if (character == null || ship == null) return false;
            PlayerControlledShip aboard = FleetShips.AboardShip(character);
            if (aboard != null) return aboard == ship;
            return (character.WorldPosition - ship.WorldPosition).sqrMagnitude <= BlastRadius * BlastRadius;
        }

        /// <summary>The local player dies with the dying ship (DiesWith), as things stand right now.</summary>
        private static bool LocalDies
        {
            get
            {
                try
                {
                    return DiesWith(LocalPlayer.Instance, Dying);
                }
                catch
                {
                    return true;
                }
            }
        }

        /// <summary>The local player is aboard the dying ship (its own crew, a visitor or a boarder).</summary>
        private static bool LocalAboard
        {
            get
            {
                try
                {
                    LocalPlayer me = LocalPlayer.Instance;
                    return me != null && Dying != null && FleetShips.AboardShip(me) == Dying;
                }
                catch
                {
                    return true;
                }
            }
        }

        private static bool AnotherShipSurvives
        {
            get
            {
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship != null && ship != Dying && !ship.StartedDeath) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Whether the local player's side still has a ship besides the dying one: in co-op any ship, in PvP one of their own
        /// team's (a player without a team has none).
        /// </summary>
        private static bool SideFightsOn
        {
            get
            {
                if (!FleetSession.IsPvP) return AnotherShipSurvives;
                int team = FleetShips.TeamOfActor(FleetShips.LocalActor);
                return team >= 0 && TeamFightsOn(team, Dying);
            }
        }

        /// <summary>
        /// Whether the local player gets the game's full "ship lost" ending: the death camera, the fade to black, the HUD
        /// and markers switched off. Only for players aboard the dying ship: its own crew, who watch the rest of the mission
        /// afterwards (Spectator), and anyone aboard whose side has no other ship. Anyone else caught in the explosion just dies
        /// when it goes off (the normal death screen): a visitor from another ship or a PvP boarder is re-cloned on their own
        /// ship; the ship's own crew out in space watch once dead.
        /// </summary>
        private static bool LocalGameOver
        {
            get
            {
                if (!LocalAboard) return false;
                if (OwnCrewOfDying) return true;
                return !SideFightsOn;
            }
        }

        /// <summary>The local player crews the dying ship (or did, as a spectator of it).</summary>
        private static bool OwnCrewOfDying
        {
            get
            {
                CrewEntry entry;
                return Dying != null && FleetShips.TryGetEntry(FleetShips.LocalActor, out entry) && entry.Slot >= 0 && entry.Slot == FleetShips.Slot(Dying);
            }
        }

        // ---------------------------------------------------------------- the listeners

        /// <summary>
        /// The dying ship's own death sequence plays it out (death camera, dying, destruction) for players who get the full
        /// ending (LocalGameOver). The host, if it doesn't, still destroys the ship once the sequence would have.
        /// </summary>
        [GamePatch(Feature, "PlayerShipDeathSequence", "OnPlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool DeathSequencePrefix(PlayerShipDeathSequence __instance)
        {
            try
            {
                if (!Filtering) return true;
                PlayerControlledShip own = FleetShips.OwnerOf(__instance);
                if (own != null && own != Dying) return false;
                if (own == null) Log.WarnOnce("death sequence owner", "A ship death sequence could not tell which ship it belongs to.");
                if (LocalGameOver) return true;
                // Not the end for this player, and no death sequence: if the explosion catches them when it goes off (aboard,
                // visiting from another ship or boarding it, or out in space close to it), they die then, when the sequence
                // would have killed them. A visitor or boarder is re-cloned on their own ship; the ship's own crew watch.
                if (FleetRuntime.Instance != null)
                {
                    float killAt = __instance.startExplodeDelay + __instance.startStageTwoDelay + __instance.startStageThreeDelay + __instance.disableRenderersDelay;
                    FleetRuntime.Instance.StartCoroutine(DieWithShipLater(Dying, killAt));
                }
                if (PhotonNetwork.IsMasterClient && FleetRuntime.Instance != null)
                {
                    float delay = __instance.startExplodeDelay + __instance.startStageTwoDelay + __instance.startStageThreeDelay + __instance.disableRenderersDelay +
                                  __instance.actualDeathDelay;
                    FleetRuntime.Instance.StartCoroutine(DestroyLater(Dying, delay));
                }
                return false;
            }
            catch (Exception e)
            {
                // Not the full sequence then (it would kill this player wherever they are); the wreck still goes (Tick).
                Log.Failure("Choosing who sees a ship's death sequence", e);
                return false;
            }
        }

        /// <summary>The ship explodes: the local player dies if it catches them now (DiesWith), without the death sequence.</summary>
        private static IEnumerator DieWithShipLater(PlayerControlledShip ship, float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                LocalPlayer me = LocalPlayer.Instance;
                if (me == null || me.IsDead || ship == null) yield break;
                if (!DiesWith(me, ship)) yield break;
                Log.Info("The local player is caught in the explosion of " + FleetShips.Describe(ship) + ".");
                me.Die(DeathCause.Explosion);
            }
            catch (Exception e)
            {
                Log.Failure("Dying with a lost ship", e);
            }
        }

        private static IEnumerator DestroyLater(PlayerControlledShip ship, float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                if (ship != null) ship.SequenceDestroyShip();
            }
            catch (Exception e)
            {
                Log.Failure("Destroying a lost ship", e);
            }
        }

        /// <summary>How long a ship's death sequence takes, from its start to the ship's destruction (the game's timings).</summary>
        private static float SequenceSeconds()
        {
            try
            {
                PlayerShipDeathSequence sequence = UnityEngine.Object.FindObjectOfType<PlayerShipDeathSequence>();
                if (sequence != null)
                    return sequence.startExplodeDelay + sequence.startStageTwoDelay + sequence.startStageThreeDelay + sequence.disableRenderersDelay +
                           sequence.actualDeathDelay;
            }
            catch
            {
                // the game's usual timings then
            }
            return 9f;
        }

        /// <summary>
        /// A few times a second: the host destroys a lost ship's wreck still there after its death sequence should have ended.
        /// The host that saw the death begin destroys the ship at the end of the sequence; a player who becomes host in the
        /// meantime had nothing planned, and the wreck would otherwise follow the fleet through every later sector. Every
        /// client notes the time, so a new host removes the wreck at its first tick once that time has passed.
        /// </summary>
        public static void Tick()
        {
            if (WreckDue.Count == 0) return;
            List<PlayerControlledShip> due = null;
            foreach (KeyValuePair<PlayerControlledShip, float> pair in WreckDue)
            {
                if (pair.Key == null || (PhotonNetwork.IsMasterClient && Time.unscaledTime > pair.Value))
                {
                    if (due == null) due = new List<PlayerControlledShip>();
                    due.Add(pair.Key);
                }
            }
            if (due == null) return;
            foreach (PlayerControlledShip ship in due)
            {
                WreckDue.Remove(ship);
                // Gone already (destroyed as planned): nothing to do.
                if (ship == null) continue;
                try
                {
                    Log.Info("The wreck of " + FleetShips.Describe(ship) + " was still there; the host removes it.");
                    ship.SequenceDestroyShip();
                }
                catch (Exception e)
                {
                    Log.Failure("Removing a lost ship's wreck", e);
                }
            }
        }

        [GamePatch(Feature, "CloneTube", "PlayerShipDestroyed", PatchKind.Prefix)]
        private static bool CloneTubePrefix(CloneTube __instance)
        {
            try
            {
                return !Filtering || FleetShips.OwnerOf(__instance) == Dying;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing which clone tubes close as a ship dies", e);
                return true;
            }
        }

        /// <summary>The game's reaction to a ship's death runs only at the local player's game over (LocalGameOver).</summary>
        private static bool AtGameOver(string what)
        {
            try
            {
                return !Filtering || LocalGameOver;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing whether " + what + " ends with a ship", e);
                return true;
            }
        }

        /// <summary>
        /// The game stops every ability of the characters as the ship starts dying (part of its death sequence): only of those
        /// aboard it. Players out in space get no death sequence; the explosion catches them or not when it goes off.
        /// </summary>
        [GamePatch(Feature, "CustomCharacterLocomotion", "PlayerShipDestroyed", PatchKind.Prefix)]
        private static bool CharacterPrefix(CustomCharacterLocomotion __instance)
        {
            try
            {
                if (!Filtering) return true;
                Player character = __instance.GetComponent<Player>();
                return character == null || FleetShips.AboardShip(character) == Dying;
            }
            catch (Exception e)
            {
                Log.Failure("Choosing whose abilities stop as a ship dies", e);
                return true;
            }
        }

        [GamePatch(Feature, "CG.Graphics.LocalPlayerOcclusionController", "OnPlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool OcclusionPrefix()
        {
            return AtGameOver("the interior view");
        }

        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "OnShipDestroyed", PatchKind.Prefix)]
        private static bool MarkersPrefix()
        {
            return AtGameOver("the markers");
        }

        private static readonly System.Reflection.MethodInfo ForceEndMessage = GameMember.Method(typeof(CG.Client.Audio.VoiceController), "ForceEndMessage");

        /// <summary>
        /// The ship AI's voice stops for players who die with a ship. The game also mutes it for good; while their side has
        /// another ship, they still play (or watch) there and must hear it, so only the current line stops.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Audio.VoiceController", "PlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool VoicePrefix(CG.Client.Audio.VoiceController __instance)
        {
            try
            {
                if (!Filtering) return true;
                if (!LocalDies) return false;
                if (!SideFightsOn || ForceEndMessage == null) return true;
                ForceEndMessage.Invoke(__instance, null);
            }
            catch (Exception e)
            {
                Log.Failure("Stopping the ship's voice line", e);
            }
            return false;
        }

        /// <summary>The HUD lets go of the lost ship only at a game over; otherwise ShipMarkers moves it to the surviving ship.</summary>
        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "UnsubscribeFromPlayerShip", PatchKind.Prefix)]
        private static bool ShipHudPrefix()
        {
            return AtGameOver("the HUD");
        }

        /// <summary>The mission's "the ship was destroyed" checks: in co-op only when no ship is left; PvP decides itself.</summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.PlayerStatus", "PlayerShipDeath", PatchKind.Prefix)]
        private static bool StatusConditionPrefix()
        {
            return MissionLost();
        }

        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.PlayerShipDestroyed", "PlayerShipDeath", PatchKind.Prefix)]
        private static bool DestroyedConditionPrefix()
        {
            return MissionLost();
        }

        /// <summary>Whether the mission counts its ship as lost: in co-op only when no other ship is left; in PvP never.</summary>
        private static bool MissionLost()
        {
            try
            {
                return !Filtering || (!FleetSession.IsPvP && !AnotherShipSurvives);
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether the mission's ships are lost", e);
                return true;
            }
        }

        /// <summary>
        /// The mission's "your ship has been destroyed" end (a scenario rule on any player ship's destruction, when the ship's
        /// wreck is removed): in co-op only when no other ship is left; in PvP never (PvpMatch ends the match when a team has
        /// no ships left).
        /// </summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.SpaceObjectDestruction", "OnSpaceObjectDestroyed", PatchKind.Prefix)]
        private static bool ObjectDestroyedConditionPrefix(object orbitObject)
        {
            try
            {
                var ship = orbitObject as PlayerControlledShip;
                if (ship == null || FleetSession.ShipCount < 2) return true;
                if (FleetSession.IsPvP) return false;
                foreach (PlayerControlledShip other in FleetShips.All)
                {
                    if (other != null && other != ship && !other.StartedDeath) return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Failure("Checking whether the mission's ships are lost", e);
                return true;
            }
        }
    }
}
