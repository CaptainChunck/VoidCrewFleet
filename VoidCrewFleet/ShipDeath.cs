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
    /// - characters aboard it (or of its crew, floating outside) die with it, and only their screens show it;
    /// - the mission only counts the ships as lost when no ship is left (co-op), and PvP decides the winner itself;
    /// - everyone given that ship in the lobby (co-op and PvP) can't be re-cloned for the rest of the mission: they play on
    ///   while their character lives (aboard another ship), then watch the remaining ships (Spectator);
    /// - the host may carry on with the ships that are left, or take everyone back to the hub (FleetReturn);
    /// - every client notes when the ship started dying, so a player who becomes host during its death still removes the
    ///   wreck (Tick).
    /// </summary>
    internal static class ShipDeath
    {
        public const string Feature = FleetShips.Feature;

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
        private static void DeathBeganPrefix(PlayerControlledShip __instance)
        {
            Dying = __instance;
            if (FleetShips.All.Count < 2) return;
            Log.Info(FleetShips.Describe(__instance) + " is destroyed.");
            if (__instance != null && !WreckDue.ContainsKey(__instance)) WreckDue[__instance] = Time.unscaledTime + SequenceSeconds() + WreckGraceSeconds;
            try
            {
                Action<PlayerControlledShip> lost = ShipLost;
                if (lost != null) lost(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Reacting to a destroyed ship", e);
            }
            if (!PhotonNetwork.IsMasterClient) return;
            try
            {
                HandleCrew(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Moving a destroyed ship's crew", e);
            }
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
        /// Host: a destroyed ship's crew can't be re-cloned for the rest of the mission. They are DEAD from now on: they play on
        /// while their character lives (aboard another ship), then watch. The roster does it when it runs; this is the same
        /// without one. With no ship left, the mission ends the game's own way (co-op); in PvP a team's last ship decides the
        /// match (PvpMatch).
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
        /// A character dies with a ship when aboard it, or when it is their ship and they are not aboard another one (out in
        /// space). Their ship is the one they were given, even once the host has marked its crew DEAD (the host does that
        /// before the others hear of it).
        /// </summary>
        public static bool DiesWith(Player character, PlayerControlledShip ship)
        {
            if (character == null || ship == null) return false;
            PlayerControlledShip aboard = FleetShips.AboardShip(character);
            if (aboard != null) return aboard == ship;
            int actor = character.photonView != null && character.photonView.Owner != null ? character.photonView.Owner.ActorNumber : -1;
            CrewEntry entry;
            return FleetShips.TryGetEntry(actor, out entry) && entry.State != CrewState.Waiting && entry.Slot >= 0 && FleetShips.BySlot(entry.Slot) == ship;
        }

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
        /// and markers switched off. For players who die with the ship when the game is over for them, and for the ship's
        /// own crew, who watch the rest of the mission afterwards (Spectator). Anyone else who dies with it (a visitor from
        /// another ship, or a PvP boarder) just dies (the normal death screen) and is re-cloned on their own ship.
        /// </summary>
        private static bool LocalGameOver
        {
            get
            {
                if (!LocalDies) return false;
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
        /// The dying ship's own death sequence plays it out (death camera, dying, destruction) for players who die with it.
        /// The host, if it doesn't die with the ship, still destroys it once the sequence would have.
        /// </summary>
        [GamePatch(Feature, "PlayerShipDeathSequence", "OnPlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool DeathSequencePrefix(PlayerShipDeathSequence __instance)
        {
            if (!Filtering) return true;
            PlayerControlledShip own = FleetShips.OwnerOf(__instance);
            if (own != null && own != Dying) return false;
            if (own == null) Log.WarnOnce("death sequence owner", "A ship death sequence could not tell which ship it belongs to.");
            if (LocalGameOver) return true;
            // Not the end for this player, but they are aboard (visiting from another ship, or boarding it): they die in the
            // explosion, when the sequence would have killed them, and are re-cloned on their own ship.
            if (LocalDies && FleetRuntime.Instance != null)
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

        private static IEnumerator DieWithShipLater(PlayerControlledShip ship, float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                LocalPlayer me = LocalPlayer.Instance;
                if (me == null || me.IsDead || ship == null) yield break;
                if (FleetShips.AboardShip(me) != ship) yield break;
                Log.Info("The local player dies with " + FleetShips.Describe(ship) + ".");
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
            return !Filtering || FleetShips.OwnerOf(__instance) == Dying;
        }

        [GamePatch(Feature, "CustomCharacterLocomotion", "PlayerShipDestroyed", PatchKind.Prefix)]
        private static bool CharacterPrefix(CustomCharacterLocomotion __instance)
        {
            if (!Filtering) return true;
            Player character = __instance.GetComponent<Player>();
            return character == null || DiesWith(character, Dying);
        }

        [GamePatch(Feature, "CG.Graphics.LocalPlayerOcclusionController", "OnPlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool OcclusionPrefix()
        {
            return !Filtering || LocalGameOver;
        }

        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "OnShipDestroyed", PatchKind.Prefix)]
        private static bool MarkersPrefix()
        {
            return !Filtering || LocalGameOver;
        }

        private static readonly System.Reflection.MethodInfo ForceEndMessage = GameMember.Method(typeof(CG.Client.Audio.VoiceController), "ForceEndMessage");

        /// <summary>
        /// The ship AI's voice stops for players who die with a ship. The game also mutes it for good; while their side has
        /// another ship, they still play (or watch) there and must hear it, so only the current line stops.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Audio.VoiceController", "PlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool VoicePrefix(CG.Client.Audio.VoiceController __instance)
        {
            if (!Filtering) return true;
            if (!LocalDies) return false;
            if (!SideFightsOn || ForceEndMessage == null) return true;
            try
            {
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
            return !Filtering || LocalGameOver;
        }

        /// <summary>The mission's "the ship was destroyed" checks: in co-op only when no ship is left; PvP decides itself.</summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.PlayerStatus", "PlayerShipDeath", PatchKind.Prefix)]
        private static bool StatusConditionPrefix()
        {
            return !Filtering || (!FleetSession.IsPvP && !AnotherShipSurvives);
        }

        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.PlayerShipDestroyed", "PlayerShipDeath", PatchKind.Prefix)]
        private static bool DestroyedConditionPrefix()
        {
            return !Filtering || (!FleetSession.IsPvP && !AnotherShipSurvives);
        }

        /// <summary>
        /// The mission's "your ship has been destroyed" end (a scenario rule on any player ship's destruction, when the ship's
        /// wreck is removed): in co-op only when no other ship is left; in PvP never (PvpMatch ends the match when a team has
        /// no ships left).
        /// </summary>
        [GamePatch(Feature, "CG.Game.Scenarios.Conditions.SpaceObjectDestruction", "OnSpaceObjectDestroyed", PatchKind.Prefix)]
        private static bool ObjectDestroyedConditionPrefix(object orbitObject)
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
    }
}
