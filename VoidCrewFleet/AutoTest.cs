using System;
using System.IO;
using System.Linq;
using System.Text;
using CG.Game;
using CG.Game.Player;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Ship.Modules;
using CG.Space;
using Gameplay.Hub;
using Gameplay.Quests;
using Gameplay.Ship.VoidJump;
using PlatformAbstractionLayer;
using Photon.Pun;
using ToolClasses;
using UnityEngine;

namespace VoidCrewFleet
{
    internal enum AutoTestMode
    {
        Off,
        TwoShips,
        FleetPilgrimage,
        PvP
    }

    /// <summary>
    /// Developer self-test, off by default (Diagnostics > AutoTest). From the main menu it hosts a private one-player room,
    /// starts a mission with the chosen fleet mode, and then writes what the fleet looks like to the log: the ships, their
    /// positions and slots, the crews, the clone tubes, the local player's ship, and (with AutoTestJump) a void jump of both
    /// ships, with screenshots in the BepInEx folder. It never runs unless switched on in the config.
    /// </summary>
    internal static class AutoTest
    {
        private enum Step
        {
            WaitMenu,
            Hosting,
            WaitHub,
            Starting,
            WaitMission,
            Report,
            Jumping,
            Done
        }

        private static Step _step = Step.WaitMenu;
        private static float _stepStarted;
        private static float _lastReport;
        private static int _reports;
        private static int _shots;

        private static float Since
        {
            get { return Time.unscaledTime - _stepStarted; }
        }

        private static void Go(Step step)
        {
            _step = step;
            _stepStarted = Time.unscaledTime;
            Log.Info("[autotest] " + step);
        }

        public static void Tick()
        {
            if (Settings.AutoTest == null || Settings.AutoTest.Value == AutoTestMode.Off || _step == Step.Done) return;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] step " + _step, e);
            }
        }

        private static string StateName()
        {
            GameStateMachine machine = GameStateMachine.Instance;
            return machine != null && machine.CurrentState != null ? machine.CurrentState.GetType().Name : "";
        }

        private static void Run()
        {
            switch (_step)
            {
                case Step.WaitMenu:
                    if (StateName() != "GSMainMenu" || !PhotonNetwork.IsConnectedAndReady || PhotonNetwork.InRoom) return;
                    if (Since < 3f) return;
                    Host();
                    Go(Step.Hosting);
                    return;
                case Step.Hosting:
                    if (PhotonNetwork.InRoom && GameSessionManager.InHub && HubQuestManager.Instance != null) Go(Step.WaitHub);
                    else if (Since > 90f) Fail("the hub never loaded");
                    return;
                case Step.WaitHub:
                    if (Since < 8f) return;
                    StartMission();
                    Go(Step.Starting);
                    return;
                case Step.Starting:
                    if (!GameSessionManager.InHub && StateName() == "GSIngame" && LocalPlayer.Instance != null) Go(Step.WaitMission);
                    else if (PvpTeams.Choosing && PvpTeamScreen.Visible) PickTeams();
                    else if (Since > 180f) Fail("the mission never started");
                    return;
                case Step.WaitMission:
                    if (Since < 10f) return;
                    Go(Step.Report);
                    return;
                case Step.Report:
                    if (Time.unscaledTime - _lastReport < 5f) return;
                    _lastReport = Time.unscaledTime;
                    Report("mission");
                    Screenshot("mission");
                    if (FleetSession.IsPvP && _reports == 0) StartBreachTest();
                    if (++_reports < (FleetSession.IsPvP ? 10 : 3)) return;
                    if (Settings.AutoTestJump.Value && FleetSession.Mode == FleetMode.TwoShips) StartJump();
                    else Finish();
                    return;
                case Step.Jumping:
                    if (Time.unscaledTime - _lastReport < 3f) return;
                    _lastReport = Time.unscaledTime;
                    Report("jump");
                    if (_shots < 12) Screenshot("jump");
                    if (Since > 75f) Finish();
                    return;
            }
        }

        private static void Host()
        {
            Log.Info("[autotest] Hosting a private test room.");
            PunSingleton<PhotonService>.I.CreateRoom(false, false)
                .Then(() => { PAL.Matchmaking.CreateLobby(false); })
                .Then(() => { PunSingleton<PhotonService>.I.SetCurrentRoomPlayerLimit(1); })
                .Then(() => { PunSingleton<PhotonService>.I.SetCurrentRoomName("Fleet autotest"); })
                .Then(() =>
                {
                    GameSessionManager.Instance.SetNextGameSession(GameSessionLoadPromise.LoadLobbyData());
                    GameStateMachine.Instance.ChangeState<GSLoadingGame>();
                })
                .Catch(e => Fail("hosting failed: " + e.Message));
        }

        private static void StartMission()
        {
            AutoTestMode mode = Settings.AutoTest.Value;
            Log.Info("[autotest] Starting a mission: " + mode + ".");
            HubFleet.RequestTwoShips(true);
            if (mode == AutoTestMode.FleetPilgrimage) HubFleet.RequestVariant(HubVariant.FleetPilgrimage);
            else if (mode == AutoTestMode.PvP) HubFleet.RequestVariant(HubVariant.FleetBattle);
            else HubQuestManager.Instance.SelectQuest(DataTable<QuestTable>.Instance.EndlessQuest.AssetGuid);
            GUIDUnion ship2 = Ship2Choice();
            if (!ship2.IsNull) HubFleet.RequestShipB(ship2);
            // Give the room properties a moment, then start without waiting for the chairs.
            FleetRuntime.Instance.StartCoroutine(StartSoon(ship2));
        }

        private static GUIDUnion Ship2Choice()
        {
            string hex = Settings.AutoTestShip2.Value != null ? Settings.AutoTestShip2.Value.Trim() : "";
            if (hex.Length == 0) return GUIDUnion.Empty();
            try
            {
                return new GUIDUnion(hex);
            }
            catch
            {
                Log.Warn("[autotest] AutoTestShip2 is not a loadout GUID: " + hex);
                return GUIDUnion.Empty();
            }
        }

        private static System.Collections.IEnumerator StartSoon(GUIDUnion ship2)
        {
            yield return new WaitForSeconds(1.5f);
            // A CUSTOM choice can be refused once while the Customizer marks the room as modded: ask again.
            if (!ship2.IsNull && HubFleet.ChosenShipB != ship2) HubFleet.RequestShipB(ship2);
            yield return new WaitForSeconds(1.5f);
            if (!ship2.IsNull && HubFleet.ChosenShipB != ship2) Log.Warn("[autotest] Ship 2's loadout was not taken: " + ship2.AsHex() + ".");
            HubQuestManager hub = HubQuestManager.Instance;
            if (hub == null || hub.SelectedQuest == null)
            {
                Fail("no mission is selected");
                yield break;
            }
            Log.Info("[autotest] Planned: " + HubFleet.PlannedMode + ", ship 2 = " + HubFleet.DescribeLoadout(HubFleet.ShipBDef) + ".");
            hub.StartQuest(hub.SelectedQuest, HubQuestManager.SessionStartType.Fresh, hub.QuestSeed, hub.ChallengeSeed);
        }

        private static float _teamScreenSeen = -1f;
        private static bool _teamJoined;

        /// <summary>PvP: the team screen is up and nobody spawns until the host confirms; join BLUE, then confirm.</summary>
        private static void PickTeams()
        {
            if (_teamScreenSeen < 0f)
            {
                _teamScreenSeen = Time.unscaledTime;
                Log.Info("[autotest] The PvP team screen is showing; spawning is on hold until the teams are confirmed.");
                return;
            }
            float shown = Time.unscaledTime - _teamScreenSeen;
            if (!_teamJoined && shown > 3f)
            {
                Screenshot("teams-empty");
                PvpTeams.Join(0);
                _teamJoined = true;
                return;
            }
            if (_teamJoined && shown > 6f && PhotonNetwork.IsMasterClient)
            {
                Screenshot("teams-picked");
                Log.Info("[autotest] Confirming the teams (local player crew slot " + FleetShips.CrewSlot(FleetShips.LocalActor) + ").");
                PvpTeams.Confirm();
                _teamScreenSeen = float.MaxValue;
            }
        }

        /// <summary>PvP: print-free breaching charge test: spawn one by the player, and blow the RED ship's airlock.</summary>
        private static void StartBreachTest()
        {
            try
            {
                LocalPlayer me = LocalPlayer.Instance;
                if (me != null && PrefabSpawnerSync.Instance != null && BreachCharge.Registered)
                {
                    PrefabSpawnerSync.Instance.SpawnCloneStarObject(BreachCharge.Guid, me.transform.position + me.transform.forward * 1.2f + me.transform.up, Quaternion.identity);
                    Log.Info("[autotest] Spawned a breaching charge in front of the player.");
                }
                Log.Info("[autotest] Breach: " + PvpBreach.DebugArm(FleetShips.BySlot(1), 4f) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] breach test", e);
            }
        }

        private static string DescribeCharges()
        {
            int count = 0;
            foreach (CG.Objects.CarryableObject item in UnityEngine.Object.FindObjectsOfType<CG.Objects.CarryableObject>())
            {
                if (BreachCharge.Is(item)) count++;
            }
            return count + " breaching charge item(s) in the world";
        }

        private static void StartJump()
        {
            PlayerControlledShip lead = FleetShips.BySlot(0);
            VoidJumpSystem system = lead != null ? lead.GetComponentInChildren<VoidJumpSystem>() : null;
            if (system == null)
            {
                Fail("ship 1 has no void jump system");
                return;
            }
            Log.Info("[autotest] Making ship 1 jump (debug charge + travel); ship 2 should follow.");
            system.DebugTransitionToRotatingState();
            Go(Step.Jumping);
        }

        private static void Finish()
        {
            Report("final");
            Screenshot("final");
            Log.Info("[autotest] DONE. Leave the game running to look around, or close it.");
            Go(Step.Done);
        }

        private static void Fail(string why)
        {
            Log.Error("[autotest] FAILED: " + why);
            Go(Step.Done);
        }

        public static void Report(string label)
        {
            var text = new StringBuilder();
            text.AppendLine("[autotest] ---- report (" + label + ") ----");
            text.AppendLine("  mode " + FleetSession.Mode + ", plan " + (FleetSession.Current != null ? FleetSession.Current.ToString() : "none") +
                            ", ships registered " + FleetShips.All.Count + ", active " + FleetShips.Active);
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                VoidJumpSystem system = ship.GetComponentInChildren<VoidJumpSystem>();
                CloneTubeModule tubes = ship.GetModule<CloneTubeModule>();
                text.AppendLine("  " + FleetShips.Describe(ship) + ": view " + FleetShips.ViewId(ship) + ", pos " + ship.Platform.Position + ", sector " +
                                (ship.Sector != null ? ship.Sector.Id.ToString() : "none") + ", jump " + (system != null && system.ActiveState != null ? system.ActiveState.GetType().Name : "?") +
                                ", hp " + ship.HitPoints.ToString("0") + ", clone tubes " + (tubes != null ? string.Join(",", tubes.CloneTubes.Select(t => t.Index.ToString()).ToArray()) : "none") +
                                ", crew " + FleetShips.CrewCount(FleetShips.Slot(ship)));
            }
            if (FleetShips.Active) text.AppendLine("  resources: " + ShipResources.Describe() + (ShipResources.Active ? "" : " (per-ship resources off)"));
            if (FleetSession.IsPvP)
            {
                PlayerControlledShip blue = FleetShips.BySlot(0);
                PlayerControlledShip red = FleetShips.BySlot(1);
                text.AppendLine("  PvP: teams locked " + PvpTeams.Locked + ", choosing " + PvpTeams.Choosing + ", team screen " + PvpTeamScreen.Visible +
                                ", local team " + FleetShips.CrewSlot(FleetShips.LocalActor) +
                                ", local player faction " + (LocalPlayer.Instance != null ? LocalPlayer.Instance.faction.ToString() : "-"));
                if (blue != null && red != null)
                {
                    text.AppendLine("  PvP: blue faction " + blue.faction + ", red faction " + red.faction + ", blue hostile to red " + blue.IsHostile(red) +
                                    ", red hostile to blue " + red.IsHostile(blue) + ", distance " + Vector3.Distance(blue.transform.position, red.transform.position).ToString("0") + " m");
                    text.AppendLine("  PvP: red parts " + FactionCounts(red) + "; blue parts " + FactionCounts(blue));
                    text.AppendLine("  PvP breach: " + PvpBreach.Describe(red) + "; " + DescribeCharges());
                }
            }
            PlayerControlledShip game = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
            text.AppendLine("  game's player ship = " + FleetShips.Describe(game) + ", local crew ship = " + FleetShips.Describe(FleetShips.LocalShip) +
                            ", aboard = " + FleetShips.Describe(FleetShips.AboardShip(LocalPlayer.Instance)));
            text.AppendLine("  active sector " + (GameSessionManager.ActiveSector != null ? GameSessionManager.ActiveSector.Id.ToString() : "none") +
                            ", room players " + PhotonNetwork.PlayerList.Length + "/" + (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.MaxPlayers : 0));
            Log.Info(text.ToString());
        }

        /// <summary>"faction 5 x40, faction 0 x3": the factions of a ship's parts (its own children and its hull's).</summary>
        private static string FactionCounts(PlayerControlledShip ship)
        {
            var counts = new System.Collections.Generic.SortedDictionary<int, int>();
            var parts = new System.Collections.Generic.List<OrbitObject>(ship.GetComponentsInChildren<OrbitObject>(true));
            if (ship.Hull != null) parts.AddRange(ship.Hull.GetComponentsInChildren<OrbitObject>(true));
            foreach (OrbitObject part in parts)
            {
                int count;
                counts.TryGetValue(part.faction, out count);
                counts[part.faction] = count + 1;
            }
            return string.Join(", ", counts.Select(p => "faction " + p.Key + " x" + p.Value).ToArray());
        }

        private static void Screenshot(string label)
        {
            try
            {
                string folder = Path.Combine(BepInEx.Paths.BepInExRootPath, "fleet-autotest");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, (++_shots).ToString("00") + "-" + label + ".png");
                ScreenCapture.CaptureScreenshot(path);
                Log.Info("[autotest] screenshot " + path);
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] screenshot", e);
            }
        }
    }
}
