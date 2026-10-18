using System;
using System.Collections;
using System.Collections.Generic;
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
    /// "the" player ship. With two ships only what concerns the dying ship reacts:
    /// - its own death sequence, clone tubes and destruction;
    /// - characters aboard it (or of its crew, floating outside) die with it, and only their screens show it;
    /// - the mission only counts the ships as lost when no ship is left (co-op), and PvP decides the winner itself;
    /// - in co-op the dead ship's crew moves to the other ship and respawns there.
    /// </summary>
    internal static class ShipDeath
    {
        public const string Feature = FleetShips.Feature;

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
            try
            {
                Action<PlayerControlledShip> lost = ShipLost;
                if (lost != null) lost(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Reacting to a destroyed ship", e);
            }
            if (PhotonNetwork.IsMasterClient && FleetSession.Mode == FleetMode.TwoShips) MoveCrewOff(__instance);
        }

        [GamePatch(Feature, "CG.Space.PlayerControlledShip", "NetPublishShipDeathBegan", PatchKind.Finalizer)]
        private static void DeathBeganFinalizer()
        {
            Dying = null;
        }

        /// <summary>Host, co-op: the crew of a destroyed ship joins the other ship's crew (and respawns there).</summary>
        private static void MoveCrewOff(PlayerControlledShip dying)
        {
            int deadSlot = FleetShips.Slot(dying);
            PlayerControlledShip survivor = null;
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship != dying && ship != null && !ship.StartedDeath) survivor = ship;
            }
            if (survivor == null || deadSlot < 0) return;
            int liveSlot = FleetShips.Slot(survivor);
            Dictionary<int, int> crews = FleetShips.CrewMap();
            var moved = new List<int>();
            foreach (KeyValuePair<int, int> pair in crews)
            {
                if (pair.Value == deadSlot) moved.Add(pair.Key);
            }
            foreach (int actor in moved) crews[actor] = liveSlot;
            FleetShips.PublishCrew(crews);
            Notice.ShowAll(FleetShips.Describe(dying) + " is lost. Its crew will be re-cloned aboard " + FleetShips.Describe(survivor) + ".");
        }

        // ---------------------------------------------------------------- who dies with the ship

        /// <summary>A character dies with a ship when aboard it, or when they crew it and are not aboard another ship.</summary>
        public static bool DiesWith(Player character, PlayerControlledShip ship)
        {
            if (character == null || ship == null) return false;
            PlayerControlledShip aboard = FleetShips.AboardShip(character);
            if (aboard != null) return aboard == ship;
            int actor = character.photonView != null && character.photonView.Owner != null ? character.photonView.Owner.ActorNumber : -1;
            return FleetShips.ShipOfActor(actor) == ship;
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
            if (LocalDies) return true;
            if (PhotonNetwork.IsMasterClient && FleetRuntime.Instance != null)
            {
                float delay = __instance.startExplodeDelay + __instance.startStageTwoDelay + __instance.startStageThreeDelay + __instance.disableRenderersDelay +
                              __instance.actualDeathDelay;
                FleetRuntime.Instance.StartCoroutine(DestroyLater(Dying, delay));
            }
            return false;
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
            return !Filtering || LocalDies;
        }

        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "OnShipDestroyed", PatchKind.Prefix)]
        private static bool MarkersPrefix()
        {
            return !Filtering || LocalDies;
        }

        private static readonly System.Reflection.MethodInfo ForceEndMessage = GameMember.Method(typeof(CG.Client.Audio.VoiceController), "ForceEndMessage");

        /// <summary>
        /// The ship AI's voice stops for players who die with a ship. The game also mutes it for good; in co-op, while
        /// another ship survives, the crew moves there and must still hear it, so only the current line stops.
        /// </summary>
        [GamePatch(Feature, "CG.Client.Audio.VoiceController", "PlayerShipDeathInitiated", PatchKind.Prefix)]
        private static bool VoicePrefix(CG.Client.Audio.VoiceController __instance)
        {
            if (!Filtering) return true;
            if (!LocalDies) return false;
            if (FleetSession.IsPvP || !AnotherShipSurvives || ForceEndMessage == null) return true;
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

        [GamePatch(Feature, "UI.MainHUD.ShipInfoHUD", "UnsubscribeFromPlayerShip", PatchKind.Prefix)]
        private static bool ShipHudPrefix()
        {
            return !Filtering || LocalDies;
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
    }
}
