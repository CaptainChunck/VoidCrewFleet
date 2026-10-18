using System;
using System.Collections.Generic;
using System.Reflection;
using CG.Game.Player;
using CG.Space;
using HarmonyLib;
using UI.ObjectMarker;
using UnityEngine;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// What players see of the other team in PvP, the way they see NPC enemies:
    /// - markers are friend or foe relative to the local player's team (the game counts faction 1, the players, as the
    ///   only ally), so the BLUE ship is an enemy to the RED team and the other way round;
    /// - the enemy ship gets a ship marker like an NPC ship (the game never marks player ships), with its health, shields
    ///   and lock-on, which also makes it a target for aim assist and the helm's target lock;
    /// - enemy players' name tags are red and show only within 35 m and in plain sight (the game's tags show through
    ///   walls and stick to the screen edge), and never show whether an enemy is talking.
    /// </summary>
    internal static class PvpMarkers
    {
        public const string Feature = "PvP enemy markers";

        private const float EnemyTagRange = 35f;
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        private static readonly MethodInfo AddMarkerMethod = AccessTools.Method(typeof(SectorMarkersProvider), "AddMarker");
        private static readonly FieldInfo SpeakingField = GameMember.Field(typeof(PlayerMarkerVE), "_speaking");
        private static readonly FieldInfo NameField = GameMember.Field(typeof(PlayerMarkerVE), "_playerName");

        private static readonly List<WeakReference> Providers = new List<WeakReference>();
        private static readonly Dictionary<PlayerMarkerVE, int> NameTags = new Dictionary<PlayerMarkerVE, int>();

        private static int LocalTeam
        {
            get { return FleetShips.CrewSlot(FleetShips.LocalActor); }
        }

        private static bool Relative
        {
            get { return PvpCombat.Active && LocalTeam >= 0; }
        }

        private static bool IsEnemyActor(int actor)
        {
            int mine = LocalTeam;
            int theirs = FleetShips.CrewSlot(actor);
            return mine >= 0 && theirs >= 0 && mine != theirs;
        }

        // ---------------------------------------------------------------- friend or foe

        /// <summary>The local player's own team is the ally; the other team is an enemy; neutral stays neutral.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerHelpers", "GetAffiliationBasedOnFaction", PatchKind.Postfix)]
        private static void AffiliationPostfix(OrbitObject obj, ref ObjectMarkerData.EAffiliation __result)
        {
            if (obj == null || !Relative) return;
            int own = PvpCombat.FactionOfSlot(LocalTeam);
            if (obj.faction == own) __result = ObjectMarkerData.EAffiliation.Ally;
            else if (obj.faction == 0) __result = ObjectMarkerData.EAffiliation.Neutral;
            else __result = ObjectMarkerData.EAffiliation.Enemy;
        }

        /// <summary>A player ship's marker is a ship's marker (not a station's), an enemy's or an ally's.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.OrbitObjectMarker", ".ctor", PatchKind.Postfix)]
        private static void MarkerCtorPostfix(OrbitObjectMarker __instance, OrbitObject obj)
        {
            var ship = obj as PlayerControlledShip;
            if (ship == null || !Relative) return;
            __instance.MarkerType = ObjectMarkerData.EMarkerType.SpaceCraft;
            __instance.Affiliation = PvpBoarding.IsEnemyShip(ship) ? ObjectMarkerData.EAffiliation.Enemy : ObjectMarkerData.EAffiliation.Ally;
        }

        // ---------------------------------------------------------------- the enemy ship's marker

        [GamePatch(Feature, "UI.ObjectMarker.SectorMarkersProvider", ".ctor", PatchKind.Postfix)]
        private static void ProviderCtorPostfix(SectorMarkersProvider __instance)
        {
            Providers.RemoveAll(r => !r.IsAlive);
            Providers.Add(new WeakReference(__instance));
        }

        /// <summary>The game marks every sector object except player ships; the enemy ship is marked like an NPC ship.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.SectorMarkersProvider", "OnSectorObjectAdded", PatchKind.Postfix)]
        private static void ObjectAddedPostfix(SectorMarkersProvider __instance, CG.Utils.IBaseObject baseObject)
        {
            var ship = baseObject as PlayerControlledShip;
            if (ship != null) MarkEnemyShip(__instance, ship);
        }

        private static void MarkEnemyShip(SectorMarkersProvider provider, PlayerControlledShip ship)
        {
            try
            {
                if (provider == null || ship == null || AddMarkerMethod == null || !Relative || !PvpBoarding.IsEnemyShip(ship)) return;
                if (!provider.CanAddNewMarkers || provider.Markers.ContainsKey(ship)) return;
                AddMarkerMethod.Invoke(provider, new object[] { ship, new OrbitObjectMarker(ship) });
                Log.Info("PvP: the enemy ship (" + FleetShips.Describe(ship) + ") is marked.");
            }
            catch (Exception e)
            {
                Log.Failure("Marking the enemy ship", e);
            }
        }

        // ---------------------------------------------------------------- enemy name tags

        [GamePatch(Feature, "UI.ObjectMarker.PlayerMarkerVE", "Init", PatchKind.Postfix)]
        private static void NameTagPostfix(PlayerMarkerVE __instance, Photon.Realtime.Player player)
        {
            if (player == null) return;
            NameTags[__instance] = player.ActorNumber;
        }

        /// <summary>An enemy's tag never shows them talking.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.PlayerMarkerVE", "SpeechDetected", PatchKind.Postfix)]
        private static void SpeechPostfix(PlayerMarkerVE __instance)
        {
            int actor;
            if (!Relative || !NameTags.TryGetValue(__instance, out actor) || !IsEnemyActor(actor)) return;
            var speaking = SpeakingField != null ? SpeakingField.GetValue(__instance) as VisualElement : null;
            if (speaking != null) speaking.style.display = DisplayStyle.None;
        }

        // ---------------------------------------------------------------- a few times a second

        public static void Tick()
        {
            if (Providers.Count > 0 && Relative)
            {
                foreach (WeakReference reference in Providers)
                {
                    var provider = reference.Target as SectorMarkersProvider;
                    if (provider == null) continue;
                    foreach (PlayerControlledShip ship in FleetShips.All) MarkEnemyShip(provider, ship);
                }
            }
            UpdateNameTags();
        }

        private static void UpdateNameTags()
        {
            if (NameTags.Count == 0) return;
            Camera camera = Camera.main;
            var gone = new List<PlayerMarkerVE>();
            foreach (KeyValuePair<PlayerMarkerVE, int> pair in NameTags)
            {
                PlayerMarkerVE tag = pair.Key;
                if (tag == null || tag.panel == null)
                {
                    gone.Add(tag);
                    continue;
                }
                bool enemy = Relative && IsEnemyActor(pair.Value);
                var name = NameField != null ? NameField.GetValue(tag) as Label : null;
                if (name != null) name.style.color = enemy ? new StyleColor(Ui.Warning) : new StyleColor(StyleKeyword.Null);
                bool show = !enemy || CanSee(camera, tag);
                tag.style.visibility = show ? Visibility.Visible : Visibility.Hidden;
            }
            foreach (PlayerMarkerVE tag in gone) NameTags.Remove(tag);
        }

        private static bool CanSee(Camera camera, PlayerMarkerVE tag)
        {
            try
            {
                if (camera == null || tag.Data == null) return false;
                Vector3 eye = camera.transform.position;
                Vector3 target = tag.Data.WorldPos + Vector3.up * 1.2f;
                float distance = Vector3.Distance(eye, target);
                if (distance > EnemyTagRange) return false;
                return !Physics.Linecast(eye, target, SightMask, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
        }
    }
}
