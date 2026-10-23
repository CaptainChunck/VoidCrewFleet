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
    /// - enemy players' name tags are red and show only within 35 m, on screen and in plain sight (the game's tags show
    ///   through walls and stick to the screen's edge), and never show whether an enemy is talking.
    /// </summary>
    internal static class PvpMarkers
    {
        public const string Feature = "PvP enemy markers";

        private const float EnemyTagRange = 35f;
        private const int SightMask = (1 << 10) | (1 << 12) | (1 << 27);

        private static readonly MethodInfo AddMarkerMethod = AccessTools.Method(typeof(SectorMarkersProvider), "AddMarker");
        private static readonly FieldInfo SpeakingField = GameMember.Field(typeof(PlayerMarkerVE), "_speaking");
        private static readonly FieldInfo NameField = GameMember.Field(typeof(PlayerMarkerVE), "_playerName");
        private static readonly AccessTools.FieldRef<PlayerMarkerVE, VisualElement> Speaking = FieldRef<PlayerMarkerVE, VisualElement>(SpeakingField);
        private static readonly AccessTools.FieldRef<PlayerMarkerVE, Label> PlayerName = FieldRef<PlayerMarkerVE, Label>(NameField);

        /// <summary>A player's name tag: whose it is, its team badge, and what was last written to it (written again only on a change).</summary>
        private sealed class NameTag
        {
            public int Actor;
            public Label Badge;

            /// <summary>The name label the colour was written to; null until it is.</summary>
            public Label Named;

            public int Team;
            public bool Enemy;

            /// <summary>The team the badge says; -1 when it shows none, -2 before it is written.</summary>
            public int BadgeTeam = -2;

            /// <summary>Whether the tag was last shown or hidden; null before the first time.</summary>
            public bool? Shown;
        }

        private static readonly List<WeakReference> Providers = new List<WeakReference>();
        private static readonly Dictionary<PlayerMarkerVE, NameTag> NameTags = new Dictionary<PlayerMarkerVE, NameTag>();
        private static readonly List<PlayerMarkerVE> GoneTags = new List<PlayerMarkerVE>();

        private static int LocalTeam
        {
            get { return FleetShips.TeamOfActor(FleetShips.LocalActor); }
        }

        private static bool Relative
        {
            get { return PvpCombat.Active && LocalTeam >= 0; }
        }

        private static bool IsEnemyActor(int actor)
        {
            int mine = LocalTeam;
            int theirs = FleetShips.TeamOfActor(actor);
            return mine >= 0 && theirs >= 0 && mine != theirs;
        }

        /// <summary>A reader for a game's field (no reflection call per read); null when the field is missing.</summary>
        private static AccessTools.FieldRef<T, F> FieldRef<T, F>(FieldInfo field) where T : class
        {
            if (field == null) return null;
            try
            {
                return AccessTools.FieldRefAccess<T, F>(field);
            }
            catch (Exception e)
            {
                Log.Warn("Game field " + field.DeclaringType.Name + "." + field.Name + " can't be read directly (" + e.Message + "); the game may have changed.");
                return null;
            }
        }

        // ---------------------------------------------------------------- friend or foe

        /// <summary>The local player's own team is the ally; the other team is an enemy; neutral stays neutral.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerHelpers", "GetAffiliationBasedOnFaction", PatchKind.Postfix)]
        private static void AffiliationPostfix(OrbitObject obj, ref ObjectMarkerData.EAffiliation __result)
        {
            if (obj == null || !Relative) return;
            int own = PvpCombat.FactionOfTeam(LocalTeam);
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

        /// <summary>
        /// The game marks every sector object except player ships; every other ship is marked like an NPC ship: an enemy
        /// team's as an enemy, the player's own team's other ships as allies (their own ship isn't marked).
        /// </summary>
        [GamePatch(Feature, "UI.ObjectMarker.SectorMarkersProvider", "OnSectorObjectAdded", PatchKind.Postfix)]
        private static void ObjectAddedPostfix(SectorMarkersProvider __instance, CG.Utils.IBaseObject baseObject)
        {
            var ship = baseObject as PlayerControlledShip;
            if (ship != null) MarkShip(__instance, ship);
        }

        private static void MarkShip(SectorMarkersProvider provider, PlayerControlledShip ship)
        {
            try
            {
                if (provider == null || ship == null || AddMarkerMethod == null || !Relative || ship == FleetShips.LocalShip) return;
                if (!provider.CanAddNewMarkers || provider.Markers.ContainsKey(ship) || ship.StartedDeath) return;
                AddMarkerMethod.Invoke(provider, new object[] { ship, new OrbitObjectMarker(ship) });
                Log.Info("PvP: " + FleetShips.Describe(ship) + " is marked (" + (PvpBoarding.IsEnemyShip(ship) ? "enemy" : "ally") + ").");
            }
            catch (Exception e)
            {
                Log.Failure("Marking a ship", e);
            }
        }

        // ---------------------------------------------------------------- enemy name tags

        [GamePatch(Feature, "UI.ObjectMarker.PlayerMarkerVE", "Init", PatchKind.Postfix)]
        private static void NameTagPostfix(PlayerMarkerVE __instance, Photon.Realtime.Player player)
        {
            if (player == null) return;
            try
            {
                // A tag set up again (for this or another player) is written afresh; its badge stays.
                NameTag known;
                NameTags.TryGetValue(__instance, out known);
                NameTags[__instance] = new NameTag { Actor = player.ActorNumber, Badge = known != null ? known.Badge : null };
            }
            catch (Exception e)
            {
                Log.Failure("Noting a player's name tag", e);
            }
        }

        /// <summary>An enemy's tag never shows them talking.</summary>
        [GamePatch(Feature, "UI.ObjectMarker.PlayerMarkerVE", "SpeechDetected", PatchKind.Postfix)]
        private static void SpeechPostfix(PlayerMarkerVE __instance)
        {
            try
            {
                NameTag tag;
                if (!Relative || !NameTags.TryGetValue(__instance, out tag) || !IsEnemyActor(tag.Actor)) return;
                VisualElement speaking = Speaking != null ? Speaking(__instance) : null;
                if (speaking != null) speaking.style.display = DisplayStyle.None;
            }
            catch (Exception e)
            {
                Log.Failure("Hiding an enemy's talking indicator", e);
            }
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
                    foreach (PlayerControlledShip ship in FleetShips.All) MarkShip(provider, ship);
                }
            }
            UpdateNameTags();
        }

        private static void UpdateNameTags()
        {
            if (NameTags.Count == 0) return;
            Camera camera = Camera.main;
            int mine = LocalTeam;
            bool relative = PvpCombat.Active && mine >= 0;
            bool teamMatch = FleetApi.IsTeamMatch;
            GoneTags.Clear();
            foreach (KeyValuePair<PlayerMarkerVE, NameTag> pair in NameTags)
            {
                PlayerMarkerVE tag = pair.Key;
                NameTag state = pair.Value;
                if (tag == null || tag.panel == null)
                {
                    GoneTags.Add(tag);
                    continue;
                }
                int theirs = FleetShips.TeamOfActor(state.Actor);
                int team = teamMatch ? theirs : -1;
                bool enemy = relative && theirs >= 0 && theirs != mine;
                Label name = PlayerName != null ? PlayerName(tag) : null;
                if (name != null && (name != state.Named || team != state.Team || enemy != state.Enemy))
                    name.style.color = team >= 0 ? new StyleColor(FleetApi.TeamColor(team)) : (enemy ? new StyleColor(Ui.Warning) : new StyleColor(StyleKeyword.Null));
                state.Named = name;
                state.Team = team;
                state.Enemy = enemy;
                SetBadge(tag, state, team);
                bool show = !enemy || CanSee(camera, tag);
                if (state.Shown == show) continue;
                state.Shown = show;
                tag.style.visibility = show ? Visibility.Visible : Visibility.Hidden;
            }
            foreach (PlayerMarkerVE tag in GoneTags) NameTags.Remove(tag);
        }

        /// <summary>A team badge above a player's name in a team match (Fleet Battle, in the hub and in the match).</summary>
        private static void SetBadge(PlayerMarkerVE tag, NameTag state, int team)
        {
            Label badge = state.Badge;
            if (badge == null || badge.parent != tag)
            {
                if (team < 0) return;
                badge = Ui.Label("", 13, Color.white, true);
                badge.pickingMode = PickingMode.Ignore;
                badge.style.unityTextAlign = TextAnchor.MiddleCenter;
                tag.Insert(0, badge);
                state.Badge = badge;
                state.BadgeTeam = -2;
            }
            if (team == state.BadgeTeam) return;
            state.BadgeTeam = team;
            if (team < 0)
            {
                badge.style.display = DisplayStyle.None;
                return;
            }
            badge.text = PvpTeams.TeamName(team) + " TEAM";
            badge.style.color = FleetApi.TeamColor(team);
            badge.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Whether the local player can see the enemy a tag is over: within 35 m, the tag on the screen (the game pins one off
        /// the screen to its edge, which would show where they are) and nothing in the way.
        /// </summary>
        private static bool CanSee(Camera camera, PlayerMarkerVE tag)
        {
            try
            {
                if (camera == null || tag.Data == null) return false;
                Vector3 eye = camera.transform.position;
                Vector3 target = tag.Data.WorldPos + Vector3.up * 1.2f;
                if ((target - eye).sqrMagnitude > EnemyTagRange * EnemyTagRange) return false;
                // Where the game puts the tag (PlayerMarkerVE.WorldPos): in front of the camera and inside its view.
                LocalPlayer me = LocalPlayer.Instance;
                Vector3 view = camera.WorldToViewportPoint(me != null ? tag.Data.WorldPos + me.transform.rotation * tag.Offset : tag.Data.WorldPos);
                if (view.z <= 0f || view.x < 0f || view.x > 1f || view.y < 0f || view.y > 1f) return false;
                return !Physics.Linecast(eye, target, SightMask, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
        }
    }
}
