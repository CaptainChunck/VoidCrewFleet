using System;
using System.Collections.Generic;
using System.Linq;
using ResourceAssets;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// How the fleet's ships are numbered, and the ship types. Internally a ship is a slot, 0-based; players and other
    /// plugins see ship numbers 1-6.
    /// - Co-op: ships 1-3 (slot 0 is ship 1, the game's own selection at the loadout terminal).
    /// - Fleet Battle: 1-3 ships per team, alternating: slot 0 BLUE 1, slot 1 RED 1, slot 2 BLUE 2, slot 3 RED 2, slot 4
    ///   BLUE 3, slot 5 RED 3. A 1v1 match is ship 1 against ship 2, as in earlier versions.
    /// A ship TYPE is the ship of a loadout (ShipLoadoutDataDef.ShipGuid): the Destroyer, Frigate and Striker today.
    /// </summary>
    internal static class FleetSlots
    {
        /// <summary>The most ships a mission can have (3 per team in Fleet Battle).</summary>
        public const int MaxSlots = 6;

        /// <summary>The most ships in a co-op mission.</summary>
        public const int MaxCoopShips = 3;

        /// <summary>The most ships per team in Fleet Battle.</summary>
        public const int MaxShipsPerTeam = 3;

        /// <summary>The most players one ship's crew may have.</summary>
        public const int MaxCrewPerShip = 8;

        /// <summary>A slot's team in a team match (0 BLUE, 1 RED); 0 for every ship outside one.</summary>
        public static int TeamOf(int slot, bool teamMatch)
        {
            if (slot < 0) return -1;
            return teamMatch ? slot % 2 : 0;
        }

        /// <summary>A ship's number within its team in a team match (1-3), or its ship number (1-6) outside one.</summary>
        public static int NumberInTeam(int slot, bool teamMatch)
        {
            return teamMatch ? slot / 2 + 1 : slot + 1;
        }

        /// <summary>The slot of a team's ship (number 1-3 within the team).</summary>
        public static int SlotOf(int team, int numberInTeam)
        {
            return (numberInTeam - 1) * 2 + team;
        }

        /// <summary>"SHIP 3" outside a team match, "BLUE 2" / "RED 1" in one.</summary>
        public static string Name(int slot, bool teamMatch)
        {
            if (slot < 0) return "NO SHIP";
            return teamMatch ? PvpTeams.TeamName(slot % 2) + " " + (slot / 2 + 1) : "SHIP " + (slot + 1);
        }

        /// <summary>The slots of one team in a team match with this many ships, in team order.</summary>
        public static IEnumerable<int> SlotsOfTeam(int team, int slots)
        {
            for (int slot = team; slot < slots; slot += 2) yield return slot;
        }

        // ---------------------------------------------------------------- ship types

        private static List<GUIDUnion> _types;

        private static ShipLoadoutDataContainer Container
        {
            get { return ResourceAssetContainer<ShipLoadoutDataContainer, ShipLoadoutData, ShipLoadoutDataDef>.Instance; }
        }

        /// <summary>The ship types the loadout terminal lists, in its order (only types that have a loadout of their own).</summary>
        public static List<GUIDUnion> Types
        {
            get
            {
                if (_types != null && _types.Count > 0) return _types;
                var types = new List<GUIDUnion>();
                try
                {
                    ShipLoadoutDataContainer container = Container;
                    var withLoadouts = new HashSet<GUIDUnion>();
                    foreach (ShipLoadoutDataDef def in container.AssetDescriptions)
                    {
                        GUIDUnion type = TypeOf(def);
                        if (!type.IsNull) withLoadouts.Add(type);
                    }
                    if (container.Ships != null)
                    {
                        foreach (MonoBehaviourRef ship in container.Ships)
                        {
                            if (ship != null && !ship.IsNull && withLoadouts.Contains(ship.AssetGuid) && !types.Contains(ship.AssetGuid)) types.Add(ship.AssetGuid);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Failure("Listing the ship types", e);
                }
                if (types.Count > 0) _types = types;
                return types;
            }
        }

        /// <summary>A loadout's ship type, or empty.</summary>
        public static GUIDUnion TypeOf(ShipLoadoutDataDef def)
        {
            try
            {
                return def != null && def.Asset != null && def.Asset.ShipLoadout != null && def.Asset.ShipLoadout.ReferenceShip != null ? def.ShipGuid : GUIDUnion.Empty();
            }
            catch
            {
                return GUIDUnion.Empty();
            }
        }

        /// <summary>A ship type's name as the terminal shows it, without "METEM": "DESTROYER".</summary>
        public static string TypeName(GUIDUnion type)
        {
            if (type.IsNull) return "ANY SHIP";
            try
            {
                ShipLoadoutDataDef def = DefaultLoadout(type);
                var context = def != null ? def.ShipContextInfo : null;
                string name = context != null ? context.HeaderText : null;
                if (!string.IsNullOrEmpty(name)) return name.Replace("METEM ", "").ToUpperInvariant();
            }
            catch
            {
                // fall through
            }
            return "SHIP TYPE";
        }

        /// <summary>Each ship type's default loadout, once settled.</summary>
        private static readonly Dictionary<GUIDUnion, ShipLoadoutDataDef> Defaults = new Dictionary<GUIDUnion, ShipLoadoutDataDef>();

        /// <summary>Each ship type's default loadout by the game's own lists, used until the Customizer answers.</summary>
        private static readonly Dictionary<GUIDUnion, ShipLoadoutDataDef> GameDefaults = new Dictionary<GUIDUnion, ShipLoadoutDataDef>();

        /// <summary>
        /// A ship type's default loadout: the game's default loadout when it is for this type (ROOKIE), otherwise the lowest-rank
        /// loadout the terminal lists for it (never a challenge or test loadout), otherwise its first. Ship Customizer builds
        /// its CUSTOM loadout on the same one; when it offers GetDefaultLoadout, its answer is used, so both mods agree. While
        /// it can still answer (its list of ships isn't ready yet), the game's choice is used but not kept, and it is asked
        /// again next time.
        /// </summary>
        public static ShipLoadoutDataDef DefaultLoadout(GUIDUnion type)
        {
            if (type.IsNull) return null;
            ShipLoadoutDataDef cached;
            if (Defaults.TryGetValue(type, out cached) && cached != null) return cached;
            ShipLoadoutDataDef result = CustomizerLink.GetDefaultLoadout(type);
            if (result != null && TypeOf(result) == type)
            {
                Defaults[type] = result;
                return result;
            }
            if (!GameDefaults.TryGetValue(type, out result) || result == null)
            {
                result = GameDefault(type);
                if (result != null) GameDefaults[type] = result;
            }
            if (result != null && !CustomizerLink.MayGiveDefaultLoadout) Defaults[type] = result;
            return result;
        }

        /// <summary>A ship type's default loadout by the game's own lists (see <see cref="DefaultLoadout"/>), or null.</summary>
        private static ShipLoadoutDataDef GameDefault(GUIDUnion type)
        {
            ShipLoadoutDataDef result = null;
            try
            {
                ShipLoadoutDataContainer container = Container;
                ShipLoadoutDataDef gameDefault = null;
                try
                {
                    gameDefault = container.GetDefault();
                }
                catch
                {
                    // the lowest-rank listed loadout instead
                }
                if (gameDefault != null && TypeOf(gameDefault) == type)
                {
                    result = gameDefault;
                }
                else
                {
                    var listed = new List<ShipLoadoutDataDef>();
                    try
                    {
                        listed.AddRange(container.GetUnlockedShipDefinitionsFromHighestRankPlayer());
                        listed.AddRange(container.GetLockedShipDefinitionsFromHighestRankPlayer());
                    }
                    catch (Exception e)
                    {
                        Log.Verbose("Could not read which loadouts the terminal lists: " + e.Message);
                    }
                    result = listed.Where(d => TypeOf(d) == type).OrderBy(RankRequirement).FirstOrDefault();
                    if (result == null)
                    {
                        foreach (ShipLoadoutDataDef def in container.AssetDescriptions)
                        {
                            if (TypeOf(def) != type) continue;
                            result = def;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Failure("Finding a ship type's default loadout", e);
            }
            return result;
        }

        private static int RankRequirement(ShipLoadoutDataDef def)
        {
            try
            {
                UnlockItemDef unlock = ResourceAssetContainer<UnlockContainer, UnityEngine.Object, UnlockItemDef>.Instance.GetAssetDefById(def.AssetGuid, false);
                return unlock != null ? unlock.UnlockOptions.RankRequirement : int.MaxValue;
            }
            catch
            {
                return int.MaxValue;
            }
        }

        /// <summary>The next ship type after this one in the terminal's order (wrapping), for the host's TYPE buttons.</summary>
        public static GUIDUnion NextType(GUIDUnion type, bool allowAny)
        {
            List<GUIDUnion> types = Types;
            if (types.Count == 0) return GUIDUnion.Empty();
            int index = type.IsNull ? -1 : types.IndexOf(type);
            if (index < 0) return types[0];
            if (index + 1 < types.Count) return types[index + 1];
            return allowAny ? GUIDUnion.Empty() : types[0];
        }

        /// <summary>
        /// The default type of a team's ship number k (1-3) in Fleet Battle: the game's default ship (the Destroyer) first, then
        /// the other types in the terminal's order.
        /// </summary>
        public static GUIDUnion DefaultTeamType(int numberInTeam)
        {
            var types = new List<GUIDUnion>(Types);
            if (types.Count == 0) return GUIDUnion.Empty();
            try
            {
                GUIDUnion standard = TypeOf(Container.GetDefault());
                if (!standard.IsNull && types.Remove(standard)) types.Insert(0, standard);
            }
            catch
            {
                // the terminal's order then
            }
            return types[Mathf.Max(0, numberInTeam - 1) % types.Count];
        }
    }
}
