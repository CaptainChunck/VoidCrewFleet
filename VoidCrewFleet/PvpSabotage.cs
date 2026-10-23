using System;
using System.Collections.Generic;
using CG.Game;
using CG.Game.Player;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.Enhancements;
using Gameplay.Power;
using Photon.Pun;
using UnityEngine;
using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace VoidCrewFleet
{
    /// <summary>
    /// Sabotage in PvP. A player who completes a System Enhancement panel of the enemy ship (its minigame, or its switch)
    /// doesn't boost it: the equipment under that panel is switched off for 120 seconds. Nobody can switch it back on
    /// until then; the panel shows "unpowered" meanwhile, so it can't be sabotaged twice. A sabotaged power generator
    /// also stops providing power, which can overload the ship's breakers.
    /// - The enemy player's client sends the request instead of the game's own state change (and a failed minigame
    ///   costs the enemy ship nothing).
    /// - The host checks the player is on the other team, alive, aboard that ship and next to the panel, then keeps the
    ///   list of sabotaged panels with their end times in a room property, so every client (and late joiners) applies
    ///   the same thing.
    /// - Every client refuses to switch the equipment on; whoever owns it keeps it off and switches it back on after.
    /// </summary>
    internal static class PvpSabotage
    {
        public const string Feature = "PvP panel sabotage";

        public const int DisableSeconds = 120;
        private const float MaxReach = 8f;
        private const string KeySabotage = "VCF_Sab";

        private sealed class Applied
        {
            public Enhancement Panel;
            public PowerDrain Drain;
            public PowerProvider Provider;
            public Func<bool, bool, bool> Validator;
            public bool WasOn;
            public bool PanelOnly;
        }

        private static Enhancement _context;
        private static bool _contextIsSwitch;
        private static readonly Dictionary<long, Applied> Active = new Dictionary<long, Applied>();

        /// <summary>Apply's working sets, reused every tick: the panels the list still holds, and those to switch back on.</summary>
        private static readonly HashSet<long> Listed = new HashSet<long>();
        private static readonly List<long> Ended = new List<long>();

        /// <summary>
        /// The host's own latest list. A room property only changes on the host itself once the server sends the change
        /// back (the game broadcasts property changes to everyone), so right after a write the room still holds the old
        /// list, and a second change built on it would undo the first. So from its first write in a room the host builds on
        /// its own list, for as long as it is that room's host; any other client, and a new host until its first write,
        /// reads the room property.
        /// </summary>
        private static int[] _hostEntries;

        private static string _hostRoom;
        private static int _hostActor = -1;

        // ---------------------------------------------------------------- the enemy player's client

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementCrypticActivator", "OnCrypticSuccess", PatchKind.Prefix)]
        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementCrypticActivator", "OnCrypticFail", PatchKind.Prefix)]
        private static void CrypticPrefix(Component __instance)
        {
            SetContext(__instance, false);
        }

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementSwitchActivator", "OnSwitchClicked", PatchKind.Prefix)]
        private static void SwitchPrefix(Component __instance)
        {
            SetContext(__instance, true);
        }

        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementCrypticActivator", "OnCrypticSuccess", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementCrypticActivator", "OnCrypticFail", PatchKind.Finalizer)]
        [GamePatch(Feature, "Gameplay.Enhancements.EnhancementSwitchActivator", "OnSwitchClicked", PatchKind.Finalizer)]
        private static void ActivatorFinalizer()
        {
            _context = null;
        }

        private static void SetContext(Component activator, bool isSwitch)
        {
            _context = null;
            if (!PvpCombat.Active || activator == null) return;
            var panel = activator.GetComponent<Enhancement>();
            if (panel == null || !PvpBoarding.IsEnemyShip(FleetShips.OwnerOf(panel))) return;
            _context = panel;
            _contextIsSwitch = isSwitch;
        }

        /// <summary>An enemy completing the panel sabotages it instead of changing its state (a failed attempt does nothing).</summary>
        [GamePatch(Feature, "Gameplay.Enhancements.Enhancement", "RequestStateChange", PatchKind.Prefix)]
        private static bool RequestStatePrefix(Enhancement __instance, EnhancementState newState)
        {
            if (_context == null || _context != __instance) return true;
            if (newState == EnhancementState.Active || _contextIsSwitch) Request(__instance);
            return false;
        }

        /// <summary>A switch bound straight to the panel's "active" value: an enemy flipping it sabotages it.</summary>
        [GamePatch(Feature, "Gameplay.Enhancements.Enhancement", "TryRequestChange", PatchKind.Prefix)]
        private static bool SwitchValuePrefix(Enhancement __instance, bool oldVal, bool newVal, ref bool __result)
        {
            if (!PvpCombat.Active || !PvpBoarding.IsEnemyShip(FleetShips.OwnerOf(__instance))) return true;
            if (LocalPlayer.Instance == null || Vector3.Distance(LocalPlayer.Instance.transform.position, __instance.InteractableTransform.position) > MaxReach) return true;
            if (newVal) Request(__instance);
            __result = oldVal;
            return false;
        }

        /// <summary>An enemy may start any powered panel, whatever its state (boosted, cooling down or failed).</summary>
        [GamePatch(Feature, "Gameplay.Enhancements.Enhancement", "CanBeLocallyUsed", PatchKind.Postfix)]
        private static void CanUsePostfix(Enhancement __instance, ref bool __result)
        {
            if (__result || !PvpCombat.Active || __instance.State == EnhancementState.Disabled) return;
            if (PvpBoarding.IsEnemyShip(FleetShips.OwnerOf(__instance))) __result = true;
        }

        private static void Request(Enhancement panel)
        {
            int view, index;
            if (!Identify(panel, out view, out index)) return;
            Log.Info("PvP: sabotaging an enemy panel (view " + view + ", panel " + index + ").");
            Net.ToHost(Msg.Sabotage, view, index);
        }

        // ---------------------------------------------------------------- naming a panel the same way on every client

        private static bool Identify(Enhancement panel, out int view, out int index)
        {
            view = 0;
            index = -1;
            PhotonView photonView = panel != null ? panel.photonView : null;
            if (photonView == null) return false;
            view = photonView.ViewID;
            index = Array.IndexOf(photonView.GetComponentsInChildren<Enhancement>(true), panel);
            return index >= 0;
        }

        private static Enhancement Resolve(int view, int index)
        {
            PhotonView photonView = PhotonView.Find(view);
            if (photonView == null) return null;
            Enhancement[] panels = photonView.GetComponentsInChildren<Enhancement>(true);
            return index >= 0 && index < panels.Length ? panels[index] : null;
        }

        private static long Key(int view, int index)
        {
            return ((long)view << 16) | (uint)(index & 0xFFFF);
        }

        // ---------------------------------------------------------------- the host

        public static void Listen()
        {
            Net.On(Msg.Sabotage, (sender, data) =>
            {
                if (!PhotonNetwork.IsMasterClient || !PvpCombat.Active) return;
                int view = Net.Get(data, 0, 0);
                int index = Net.Get(data, 1, -1);
                Enhancement panel = Resolve(view, index);
                if (panel == null) return;
                PlayerControlledShip ship = FleetShips.OwnerOf(panel);
                if (!PvpBoarding.IsEnemyOf(sender, ship) || panel.State == EnhancementState.Disabled) return;
                Player saboteur = FleetShips.CharacterOf(sender);
                if (saboteur == null || PlayerLife.IsDead(saboteur) || FleetShips.AboardShip(saboteur) != ship ||
                    Vector3.Distance(saboteur.transform.position, panel.InteractableTransform.position) > MaxReach)
                {
                    Log.Warn("PvP: ignored a sabotage request from player " + sender + ", who is not alive at that panel aboard " + FleetShips.Describe(ship) + ".");
                    return;
                }
                List<int> entries = ReadRoom();
                for (int i = 0; i + 3 < entries.Count; i += 4)
                {
                    if (entries[i] == view && entries[i + 1] == index) return;
                }
                PowerDrain drain = panel.GetComponentInParent<PowerDrain>();
                entries.Add(view);
                entries.Add(index);
                entries.Add(unchecked(PhotonNetwork.ServerTimestamp + DisableSeconds * 1000));
                entries.Add(drain != null && drain.IsOn.Value ? 1 : 0);
                WriteRoom(entries);

                int team = FleetShips.TeamOfActor(sender);
                Notice.ShowAll("SABOTAGE! The " + PvpTeams.TeamName(team) + " team switched off " + FleetShips.Describe(ship) + "'s " + Describe(panel) + " for " +
                               DisableSeconds + " seconds.");
                Apply();
            });
        }

        private static string Describe(Enhancement panel)
        {
            try
            {
                CellModule module = panel.GetComponentInParent<CellModule>();
                if (module != null && !string.IsNullOrEmpty(module.DisplayName)) return module.DisplayName.ToUpperInvariant();
            }
            catch
            {
                // Fall back to a generic name.
            }
            return "equipment";
        }

        /// <summary>True while the host's own list is the one to use (see _hostEntries); forgets it once that no longer holds.</summary>
        private static bool HostHasLatest()
        {
            if (_hostEntries == null) return false;
            Photon.Realtime.Room room = PhotonNetwork.CurrentRoom;
            Photon.Realtime.Player master = PhotonNetwork.MasterClient;
            if (room != null && PhotonNetwork.IsMasterClient && master != null && master.ActorNumber == _hostActor && room.Name == _hostRoom) return true;
            // Another room, or not its host any more: the room property is the list again.
            _hostEntries = null;
            return false;
        }

        /// <summary>The list as it stands (the host's own, or the room's), not copied: null or empty when nothing is sabotaged.</summary>
        private static int[] RoomEntries()
        {
            if (HostHasLatest()) return _hostEntries;
            object value;
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(KeySabotage, out value))
                return value as int[];
            return null;
        }

        /// <summary>A copy of the list, to add to.</summary>
        private static List<int> ReadRoom()
        {
            var entries = new List<int>();
            int[] current = RoomEntries();
            if (current != null) entries.AddRange(current);
            return entries;
        }

        private static void WriteRoom(List<int> entries)
        {
            if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null) return;
            int[] data = entries.Count > 0 ? entries.ToArray() : null;
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { KeySabotage, data } });
            _hostEntries = data ?? new int[0];
            _hostRoom = PhotonNetwork.CurrentRoom.Name;
            _hostActor = FleetShips.LocalActor;
        }

        /// <summary>Host, when a match starts or the hub loads: nothing is sabotaged.</summary>
        public static void ResetRoom()
        {
            WriteRoom(new List<int>());
        }

        private static bool Expired(int end)
        {
            return unchecked(PhotonNetwork.ServerTimestamp - end) >= 0;
        }

        // ---------------------------------------------------------------- every client

        /// <summary>
        /// A few times a second: what is sabotaged stays off, and comes back on when its time is up. With nothing sabotaged
        /// and nothing to switch back on (most of the time) it stops at once; the working sets are reused.
        /// </summary>
        public static void Apply()
        {
            // Notices a new room or host between matches too.
            HostHasLatest();
            if (Active.Count == 0 && (!PvpCombat.Active || !PhotonNetwork.InRoom)) return;
            int[] entries = PvpCombat.Active ? RoomEntries() : null;
            int count = entries != null ? entries.Length : 0;
            if (count < 4 && Active.Count == 0) return;
            Listed.Clear();
            bool expiredAny = false;
            for (int i = 0; i + 3 < count; i += 4)
            {
                if (Expired(entries[i + 2]))
                {
                    expiredAny = true;
                    continue;
                }
                long key = Key(entries[i], entries[i + 1]);
                Listed.Add(key);
                Applied applied;
                if (!Active.TryGetValue(key, out applied))
                {
                    applied = Start(entries[i], entries[i + 1], entries[i + 3] == 1);
                    if (applied == null) continue;
                    Active[key] = applied;
                }
                Enforce(applied);
            }
            if (Active.Count > 0)
            {
                Ended.Clear();
                foreach (KeyValuePair<long, Applied> pair in Active)
                {
                    if (!Listed.Contains(pair.Key)) Ended.Add(pair.Key);
                }
                for (int i = 0; i < Ended.Count; i++)
                {
                    Restore(Active[Ended[i]]);
                    Active.Remove(Ended[i]);
                }
            }
            if (expiredAny && PhotonNetwork.IsMasterClient)
            {
                var kept = new List<int>();
                for (int i = 0; i + 3 < count; i += 4)
                {
                    if (Expired(entries[i + 2])) continue;
                    kept.Add(entries[i]);
                    kept.Add(entries[i + 1]);
                    kept.Add(entries[i + 2]);
                    kept.Add(entries[i + 3]);
                }
                WriteRoom(kept);
            }
        }

        private static Applied Start(int view, int index, bool wasOn)
        {
            Enhancement panel = Resolve(view, index);
            if (panel == null) return null;
            var applied = new Applied { Panel = panel, WasOn = wasOn, Drain = panel.GetComponentInParent<PowerDrain>() };
            if (applied.Drain != null)
            {
                applied.Validator = (oldValue, newValue) => !newValue;
                applied.Drain.IsOn.ChangeValidators.Add(applied.Validator);
                var generator = applied.Drain.GetComponentInParent<PowerGeneratorModule>() ?? applied.Drain.GetComponentInChildren<PowerGeneratorModule>();
                if (generator != null)
                {
                    applied.Provider = generator.GetComponentInChildren<PowerProvider>(true);
                    if (applied.Provider != null) applied.Provider.SetPowerProvidedExternallyBlocked(true);
                }
            }
            else
            {
                applied.PanelOnly = true;
            }
            Log.Info("PvP: " + Describe(panel) + " of " + FleetShips.Describe(FleetShips.OwnerOf(panel)) + " is sabotaged.");
            return applied;
        }

        private static void Enforce(Applied applied)
        {
            try
            {
                if (applied.Drain != null)
                {
                    if (applied.Drain.photonView != null && applied.Drain.photonView.AmOwner && applied.Drain.IsOn.Value) applied.Drain.IsOn.ForceChange(false);
                }
                else if (applied.Panel != null && applied.Panel.photonView != null && applied.Panel.photonView.AmOwner && applied.Panel.State != EnhancementState.Disabled)
                {
                    applied.Panel.SetPowered(false);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Keeping sabotaged equipment off", e);
            }
        }

        private static void Restore(Applied applied)
        {
            try
            {
                if (applied.Provider != null) applied.Provider.SetPowerProvidedExternallyBlocked(false);
                if (applied.Drain != null)
                {
                    applied.Drain.IsOn.ChangeValidators.Remove(applied.Validator);
                    if (applied.Drain.photonView != null && applied.Drain.photonView.AmOwner && !applied.Drain.IsOn.Value &&
                        (applied.Drain.AutoPowerOn || applied.WasOn) && applied.Drain.CanTurnOn())
                        applied.Drain.IsOn.RequestChange(true);
                }
                else if (applied.PanelOnly && applied.Panel != null && applied.Panel.photonView != null && applied.Panel.photonView.AmOwner)
                {
                    applied.Panel.SetPowered(true);
                }
                if (applied.Panel != null) Log.Info("PvP: " + Describe(applied.Panel) + " of " + FleetShips.Describe(FleetShips.OwnerOf(applied.Panel)) + " works again.");
            }
            catch (Exception e)
            {
                Log.Failure("Switching sabotaged equipment back on", e);
            }
        }
    }
}
