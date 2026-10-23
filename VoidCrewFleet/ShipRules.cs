using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Newtonsoft.Json;
using Photon.Pun;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Fleet Battle ship rules: what a CUSTOM loadout (Ship Customizer) may use in a team match, so nobody brings a ship
    /// the other team can't match. The host sets them on the lobby screen's SHIP RULES tab while the teams are picked:
    /// which MK tiers may be fitted (the fabricator's starting level too), which categories, which single options are
    /// banned, and how many starting alloys a CUSTOM loadout may set. They live in a room property, so every player, late
    /// joiners too, sees the same rules, and the host's last rules are kept in the config for their next lobby. Fleet only
    /// publishes them (FleetApi); Ship Customizer greys refused options out at the terminal and, on the host, removes them
    /// from the ships it builds when the match starts. The game's own loadouts are not affected.
    /// </summary>
    internal static class ShipRules
    {
        private const string Key = "VCF_Rules";

        /// <summary>The starting-alloy choices the host cycles through: no limit, only the game's own amount, or a cap.</summary>
        public static readonly int[] AlloyChoices = { int.MaxValue, -1, 100, 250, 500, 1000 };

        [JsonObject(MemberSerialization.OptIn)]
        internal sealed class Rules
        {
            /// <summary>The MK tiers (1 to 3) that may be fitted, as digits: "123" allows all, "1" only MK I.</summary>
            [JsonProperty("t")]
            public string Tiers = "123";

            /// <summary>Ship Customizer's category names (upper case) that may not be used.</summary>
            [JsonProperty("c")]
            public List<string> BlockedCategories = new List<string>();

            /// <summary>Ship Customizer's option codes ("m:", "w:", "i:" or "c:" and the GUID's hex) that may not be used.</summary>
            [JsonProperty("b")]
            public List<string> Banned = new List<string>();

            /// <summary>The most starting alloys a CUSTOM loadout may set: int.MaxValue no limit, -1 only the game's own amount.</summary>
            [JsonProperty("a")]
            public int MaxAlloys = int.MaxValue;

            public bool TierAllowed(int tier)
            {
                return tier <= 0 || tier > 3 || Tiers.IndexOf((char)('0' + tier)) >= 0;
            }

            /// <summary>The highest MK tier allowed (at least 1).</summary>
            public int HighestTier
            {
                get
                {
                    for (int tier = 3; tier >= 1; tier--)
                    {
                        if (TierAllowed(tier)) return tier;
                    }
                    return 1;
                }
            }

            public bool CategoryAllowed(string category)
            {
                return string.IsNullOrEmpty(category) || !BlockedCategories.Contains(category.Trim().ToUpperInvariant());
            }

            public bool ItemAllowed(string code)
            {
                return string.IsNullOrEmpty(code) || !Banned.Contains(code.Trim().ToLowerInvariant());
            }

            /// <summary>Whether anything at all is restricted.</summary>
            public bool Restricts
            {
                get { return Tiers != "123" || BlockedCategories.Count > 0 || Banned.Count > 0 || MaxAlloys != int.MaxValue; }
            }

            public Rules Copy()
            {
                return new Rules
                {
                    Tiers = Tiers,
                    BlockedCategories = new List<string>(BlockedCategories),
                    Banned = new List<string>(Banned),
                    MaxAlloys = MaxAlloys
                };
            }

            /// <summary>Keeps what came from the room (or an older config) sane: at least one tier, no duplicates.</summary>
            public Rules Normalized()
            {
                string tiers = "";
                for (char digit = '1'; digit <= '3'; digit++)
                {
                    if (Tiers != null && Tiers.IndexOf(digit) >= 0) tiers += digit;
                }
                Tiers = tiers.Length > 0 ? tiers : "1";
                BlockedCategories = (BlockedCategories ?? new List<string>()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c.Trim().ToUpperInvariant())
                    .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
                Banned = (Banned ?? new List<string>()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c.Trim().ToLowerInvariant())
                    .Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
                if (MaxAlloys != int.MaxValue) MaxAlloys = Math.Max(-1, MaxAlloys);
                return this;
            }

            public string ToJson()
            {
                return JsonConvert.SerializeObject(this);
            }

            public static Rules FromJson(string json)
            {
                if (string.IsNullOrEmpty(json)) return new Rules();
                try
                {
                    Rules rules = JsonConvert.DeserializeObject<Rules>(json);
                    return (rules ?? new Rules()).Normalized();
                }
                catch (Exception e)
                {
                    Log.Warn("Ignoring unreadable ship rules (" + e.Message + ").");
                    return new Rules();
                }
            }
        }

        private static string _roomJson;
        private static Rules _room = new Rules();
        private static Rules _pending;
        private static string _pendingJson;
        private static float _pendingSince;

        private static string RoomJson()
        {
            object value;
            return PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(Key, out value)
                ? value as string
                : null;
        }

        /// <summary>The room's rules: none outside a room, or until the host sets some.</summary>
        public static Rules Current
        {
            get
            {
                string json = RoomJson();
                if (json != _roomJson)
                {
                    _roomJson = json;
                    _room = Rules.FromJson(json);
                }
                return _room;
            }
        }

        /// <summary>The raw rules as the room holds them, for signatures and logs.</summary>
        public static string CurrentJson
        {
            get { return RoomJson() ?? ""; }
        }

        /// <summary>
        /// What the lobby screen shows: on the host, its own latest change until the room has it (a property change takes a
        /// round trip, and quick clicks must build on each other); everywhere else the room's rules.
        /// </summary>
        public static Rules Shown
        {
            get
            {
                if (_pending != null)
                {
                    if (RoomJson() == _pendingJson || Time.unscaledTime - _pendingSince > 5f) _pending = null;
                    else return _pending;
                }
                return Current;
            }
        }

        /// <summary>
        /// The JSON of the rules Shown returns, without serializing them: the host's pending change, else the room's text ("" for
        /// none). It stays the same string until the rules change, so screens compare it to redraw only then.
        /// </summary>
        public static string ShownJson
        {
            get
            {
                Rules shown = Shown;
                return _pending != null && ReferenceEquals(shown, _pending) ? _pendingJson : CurrentJson;
            }
        }

        /// <summary>The rules can change only on the host, in the hub, while the Fleet Battle teams are being picked.</summary>
        public static bool CanEdit
        {
            get { return PhotonNetwork.IsMasterClient && PvpTeams.HubChoosing && FleetApi.IsTeamMatch; }
        }

        /// <summary>Host: change the rules (a copy of what is shown, changed by the caller) and remember them for next time.</summary>
        public static void Change(Action<Rules> change)
        {
            if (!CanEdit || PhotonNetwork.CurrentRoom == null) return;
            Rules rules = Shown.Copy();
            change(rules);
            rules.Normalized();
            string json = rules.ToJson();
            _pending = rules;
            _pendingJson = json;
            _pendingSince = Time.unscaledTime;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { Key, json } });
            Settings.PvpShipRules.Value = json;
            Log.Info("Ship rules: " + Describe(rules) + ".");
        }

        /// <summary>Host, when FLEET BATTLE is chosen: a room without rules starts with the host's last ones.</summary>
        public static void PublishSaved()
        {
            try
            {
                if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null || RoomJson() != null) return;
                Rules saved = Rules.FromJson(Settings.PvpShipRules.Value);
                if (!saved.Restricts) return;
                PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { Key, saved.ToJson() } });
                Log.Info("Ship rules from last time: " + Describe(saved) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("Restoring the last ship rules", e);
            }
        }

        public static string TierName(int tier)
        {
            return tier == 1 ? "MK I" : tier == 2 ? "MK II" : tier == 3 ? "MK III" : "-";
        }

        public static string AlloysText(int maxAlloys)
        {
            return maxAlloys == int.MaxValue ? "NO LIMIT" : maxAlloys < 0 ? "GAME DEFAULT" : "UP TO " + maxAlloys;
        }

        /// <summary>"MK I only, 2 categories blocked, 3 banned, starting alloys: game default", or "none".</summary>
        public static string Describe(Rules rules)
        {
            if (rules == null || !rules.Restricts) return "none";
            var parts = new List<string>();
            if (rules.Tiers != "123") parts.Add(string.Join(" + ", rules.Tiers.Select(c => TierName(c - '0')).ToArray()) + (rules.Tiers.Length == 1 ? " only" : ""));
            if (rules.BlockedCategories.Count > 0)
                parts.Add(rules.BlockedCategories.Count <= 2 ? string.Join(", ", rules.BlockedCategories.ToArray()) + " blocked" : rules.BlockedCategories.Count + " categories blocked");
            if (rules.Banned.Count > 0) parts.Add(rules.Banned.Count + (rules.Banned.Count == 1 ? " item banned" : " items banned"));
            if (rules.MaxAlloys != int.MaxValue) parts.Add("starting alloys: " + AlloysText(rules.MaxAlloys).ToLowerInvariant());
            return string.Join(", ", parts.ToArray());
        }
    }
}
