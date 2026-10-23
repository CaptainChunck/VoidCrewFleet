using System;
using System.IO;
using System.Linq;
using System.Text;
using CG.Client.Player.Interactions;
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
    /// picks a ship on the lobby screen (AutoTestCrewShip), starts a mission with the chosen fleet mode and ship count, and
    /// then writes what the fleet looks like to the log: the ships, their positions and slots, the crews, the clone tubes, the
    /// local player's ship, then (AutoTestRespawn) a death and re-cloning, (AutoTestDestroy) ships destroyed one by one,
    /// (AutoTestReturn) RETURN TO HUB, and (with AutoTestJump) a void jump of every ship, with screenshots in the BepInEx
    /// folder. It never runs unless switched on in the config.
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
            Fists,
            Respawning,
            Destroying,
            Returning,
            Jumping,
            Done
        }

        private static Step _step = Step.WaitMenu;
        private static float _stepStarted;
        private static float _lastReport;
        private static int _reports;
        private static int _shots;
        private static int _destroyed;
        private static bool _sequenceDone;
        private static Step _done = Step.WaitMenu;

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
                    if (Time.unscaledTime - _lastReport >= 15f)
                    {
                        _lastReport = Time.unscaledTime;
                        Log.Info("[autotest] Waiting for the main menu: state " + StateName() + ", Photon " + PhotonNetwork.NetworkClientState + ", in a room " +
                                 PhotonNetwork.InRoom + ".");
                    }
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
                    if (FleetSession.IsPvP && _reports == 0) StartHackTest();
                    if (++_reports < (FleetSession.IsPvP ? 10 : 3)) return;
                    if (Settings.AutoTestFists.Value && FleetSession.IsPvP && Step.Fists > _done)
                    {
                        _done = Step.Fists;
                        Go(Step.Fists);
                        _sequenceDone = false;
                        FleetRuntime.Instance.StartCoroutine(FistSequence());
                        return;
                    }
                    if (Settings.AutoTestRespawn.Value)
                    {
                        Go(Step.Respawning);
                        _sequenceDone = false;
                        FleetRuntime.Instance.StartCoroutine(RespawnSequence());
                    }
                    else
                    {
                        Go(Step.Destroying);
                    }
                    return;
                case Step.Fists:
                    if (!_sequenceDone && Since < 60f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The fist test didn't finish in 60 s.");
                    Go(Step.Report);
                    return;
                case Step.Respawning:
                    if (!_sequenceDone && Since < 90f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The respawn test didn't finish in 90 s.");
                    Go(Step.Destroying);
                    return;
                case Step.Destroying:
                    if (Since < 20f) return;
                    if (_destroyed > 0)
                    {
                        Report("after destroying ship " + DestroyList()[_destroyed - 1]);
                        Screenshot("destroyed");
                    }
                    if (_destroyed < DestroyList().Length && (ClientGame.Current == null || ClientGame.Current.GameState != ClientGameState.Ended))
                    {
                        DestroyShip(DestroyList()[_destroyed++]);
                        Go(Step.Destroying);
                        return;
                    }
                    if (Settings.AutoTestReturn.Value && ClientGame.Current != null && ClientGame.Current.GameState != ClientGameState.Ended)
                    {
                        Go(Step.Returning);
                        _sequenceDone = false;
                        FleetRuntime.Instance.StartCoroutine(ReturnSequence());
                    }
                    else if (Settings.AutoTestJump.Value && FleetSession.Mode == FleetMode.Coop && FleetShips.Active) StartJump();
                    else Finish();
                    return;
                case Step.Returning:
                    if (!_sequenceDone && Since < 180f) return;
                    if (!_sequenceDone) Fail("never got back to the hub after RETURN TO HUB");
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

        private static int TestShips
        {
            get { return Mathf.Clamp(Settings.AutoTestShips.Value, 1, FleetSlots.MaxCoopShips); }
        }

        private static void StartMission()
        {
            AutoTestMode mode = Settings.AutoTest.Value;
            Log.Info("[autotest] Starting a mission: " + mode + ", " + TestShips + (mode == AutoTestMode.PvP ? " ships per team." : " ships."));
            if (mode == AutoTestMode.FleetPilgrimage) HubFleet.RequestVariant(HubVariant.FleetPilgrimage);
            else if (mode == AutoTestMode.PvP) HubFleet.RequestVariant(HubVariant.FleetBattle);
            else HubQuestManager.Instance.SelectQuest(DataTable<QuestTable>.Instance.EndlessQuest.AssetGuid);
            // Give the room properties a moment, then start without waiting for the chairs.
            FleetRuntime.Instance.StartCoroutine(StartSoon(mode));
        }

        /// <summary>The loadouts the test chooses for ships 2, 3 ... (AutoTestLoadouts); empty entries keep the default.</summary>
        private static GUIDUnion[] LoadoutChoices()
        {
            string text = Settings.AutoTestLoadouts.Value != null ? Settings.AutoTestLoadouts.Value.Trim() : "";
            string[] parts = text.Length == 0 ? new string[0] : text.Split(',');
            var guids = new GUIDUnion[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string hex = parts[i].Trim();
                if (hex.Length == 0) continue;
                try
                {
                    guids[i] = new GUIDUnion(hex);
                }
                catch
                {
                    Log.Warn("[autotest] AutoTestLoadouts entry " + (i + 2) + " is not a loadout GUID: " + hex);
                }
            }
            return guids;
        }

        /// <summary>The ship numbers AutoTestDestroy names, in order.</summary>
        private static int[] DestroyList()
        {
            string text = Settings.AutoTestDestroy.Value != null ? Settings.AutoTestDestroy.Value : "";
            return text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Select(t =>
            {
                int ship;
                return int.TryParse(t, out ship) ? ship : 0;
            }).Where(n => n >= 1 && n <= FleetSlots.MaxSlots).ToArray();
        }

        private static void ChooseLoadouts()
        {
            GUIDUnion[] choices = LoadoutChoices();
            for (int i = 0; i < choices.Length; i++)
            {
                if (!choices[i].IsNull && HubFleet.ChosenLoadout(i + 1) != choices[i]) HubFleet.RequestLoadout(i + 1, choices[i]);
            }
        }

        private static System.Collections.IEnumerator StartSoon(AutoTestMode mode)
        {
            yield return new WaitForSeconds(1.5f);
            if (mode == AutoTestMode.PvP) HubFleet.RequestShipsPerTeam(TestShips);
            else HubFleet.RequestShipCount(Mathf.Max(2, TestShips));
            yield return new WaitForSeconds(1.5f);
            ChooseLoadouts();
            yield return new WaitForSeconds(1.5f);
            // A CUSTOM choice can be refused once while the Customizer marks the room as modded: ask again.
            ChooseLoadouts();
            yield return new WaitForSeconds(1.5f);
            GUIDUnion[] choices = LoadoutChoices();
            for (int i = 0; i < choices.Length; i++)
            {
                if (!choices[i].IsNull && HubFleet.ChosenLoadout(i + 1) != choices[i]) Log.Warn("[autotest] Ship " + (i + 2) + "'s loadout was not taken: " + choices[i].AsHex() + ".");
            }
            HubQuestManager hub = HubQuestManager.Instance;
            if (hub == null || hub.SelectedQuest == null)
            {
                Fail("no mission is selected");
                yield break;
            }
            if (mode == AutoTestMode.PvP)
            {
                // Fleet Battle picks the teams in the hub: the lobby screen is up for everyone; take BLUE 1 and confirm (one
                // player can't crew every ship, so the test confirms without the "every ship crewed" check).
                Log.Info("[autotest] Hub lobby showing: " + PvpTeamScreen.Visible + ", picking: " + PvpTeams.HubChoosing + ", ships " + HubFleet.PlannedShips +
                         ", check: " + (PvpTeams.CheckTeams() ?? "ok") + ".");
                Screenshot("hub-teams");
                PvpTeams.Join(0);
                yield return new WaitForSeconds(1.5f);
                Screenshot("hub-teams-picked");
                PvpTeams.ForceConfirm();
                yield return new WaitForSeconds(1.5f);
                Log.Info("[autotest] Hub teams locked: " + PvpTeams.Locked + ", local team " + FleetShips.TeamOfActor(FleetShips.LocalActor) + ", ship " +
                         (FleetShips.CrewSlot(FleetShips.LocalActor) + 1) + ", can edit ships " +
                         string.Join(",", Enumerable.Range(1, HubFleet.PlannedShips).Select(n => n + ":" + FleetApi.CanEditShip(FleetShips.LocalActor, n)).ToArray()) + ".");
            }
            else if (HubFleet.PlannedShips > 1)
            {
                // The co-op lobby screen is up for everyone: pick the test's ship and confirm the crews (one player can't crew
                // every ship, which co-op allows: the others fly empty).
                int pick = Mathf.Clamp(Settings.AutoTestCrewShip.Value, 1, HubFleet.PlannedShips) - 1;
                Log.Info("[autotest] Crews screen showing: " + PvpTeamScreen.Visible + ", picking: " + PvpTeams.HubChoosing + ", check: " +
                         (PvpTeams.CheckPicks() ?? "ok") + ".");
                Screenshot("hub-crews");
                PvpTeams.Join(pick);
                yield return new WaitForSeconds(1.5f);
                Screenshot("hub-crews-picked");
                Log.Info("[autotest] Local pick " + (FleetShips.CrewSlot(FleetShips.LocalActor) + 1) + ", check: " + (PvpTeams.CheckPicks() ?? "ok") + ".");
                PvpTeams.Confirm();
                yield return new WaitForSeconds(1.5f);
                Log.Info("[autotest] Crews locked: " + PvpTeams.Locked + ", screen showing " + PvpTeamScreen.Visible + ".");
            }
            Screenshot("hub");
            yield return new WaitForSeconds(0.5f);
            var planned = new StringBuilder();
            for (int slot = 0; slot < HubFleet.PlannedShips; slot++)
                planned.Append(slot == 0 ? "" : "; ").Append(HubFleet.ShipLabel(slot)).Append(" = ").Append(HubFleet.ShipDescription(slot))
                    .Append(HubFleet.ForcedType(slot).IsNull ? "" : " (type " + FleetSlots.TypeName(HubFleet.ForcedType(slot)) + ")");
            Log.Info("[autotest] Planned: " + HubFleet.PlannedMode + ", " + HubFleet.PlannedShips + " ships: " + planned + ".");
            hub.StartQuest(hub.SelectedQuest, HubQuestManager.SessionStartType.Fresh, hub.QuestSeed, hub.ChallengeSeed);
        }

        /// <summary>Host: destroys a ship (by number) the way the game does when its hull is gone.</summary>
        private static void DestroyShip(int number)
        {
            PlayerControlledShip ship = FleetShips.BySlot(number - 1);
            if (ship == null || !PhotonNetwork.IsMasterClient)
            {
                Log.Warn("[autotest] Ship " + number + " isn't there to destroy.");
                return;
            }
            Log.Info("[autotest] Destroying " + FleetShips.Describe(ship) + ".");
            ship.Kill();
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
                PvpTeams.ForceConfirm();
                _teamScreenSeen = float.MaxValue;
            }
        }

        /// <summary>PvP: hacking test without a second player: spawn a terminal by the player, and give BLUE door access to RED.</summary>
        private static void StartHackTest()
        {
            try
            {
                LocalPlayer me = LocalPlayer.Instance;
                if (me != null && PrefabSpawnerSync.Instance != null && HackTerminal.Registered)
                {
                    PrefabSpawnerSync.Instance.SpawnCloneStarObject(HackTerminal.Guid, me.transform.position + me.transform.forward * 1.2f + me.transform.up, Quaternion.identity);
                    Log.Info("[autotest] Spawned a hacking terminal in front of the player.");
                }
                Log.Info("[autotest] Hack: " + PvpHack.DebugAccess(FleetShips.BySlot(1), 0, 30f) + ".");
                Log.Info("[autotest] Cockpits: " + string.Join(", ", FleetShips.All.Select(s => FleetShips.Describe(s) + " " + (PvpHack.CockpitRoom(s) != null)).ToArray()) + ".");
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] hack test", e);
            }
        }

        private static string DescribeTerminals()
        {
            int count = 0;
            foreach (CG.Objects.CarryableObject item in UnityEngine.Object.FindObjectsOfType<CG.Objects.CarryableObject>())
            {
                if (HackTerminal.Is(item)) count++;
            }
            return count + " hacking terminal item(s) in the world";
        }

        /// <summary>
        /// The local player dies and is re-cloned: on the ship they were given in the lobby (AutoTestCrewShip), whatever ship
        /// the game itself calls the player ship.
        /// </summary>
        private static System.Collections.IEnumerator RespawnSequence()
        {
            LocalPlayer me = LocalPlayer.Instance;
            PlayerControlledShip own = FleetShips.LocalShip;
            if (me == null || own == null)
            {
                Log.Warn("[autotest] Respawn test skipped: no character or no ship of their own.");
                _sequenceDone = true;
                yield break;
            }
            Log.Info("[autotest] Killing the local player aboard " + FleetShips.Describe(FleetShips.AboardShip(me)) + "; they should be re-cloned on " +
                     FleetShips.Describe(own) + ".");
            me.Die(DeathCause.Explosion);
            yield return new WaitForSeconds(4f);
            Report("dead");
            Screenshot("dead");
            me = LocalPlayer.Instance;
            ClonetubeRespawn respawn = me != null && me.Locomotion != null ? me.Locomotion.GetAbility<ClonetubeRespawn>() : null;
            Log.Info("[autotest] Asking to be re-cloned: " + (respawn != null && respawn.StartAbility()) + ".");
            float until = Time.unscaledTime + 40f;
            while (Time.unscaledTime < until && (LocalPlayer.Instance == null || LocalPlayer.Instance.IsDead)) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(4f);
            me = LocalPlayer.Instance;
            PlayerControlledShip aboard = FleetShips.AboardShip(me);
            Log.Info("[autotest] Re-cloned: " + (me != null && !me.IsDead) + ", aboard " + FleetShips.Describe(aboard) + " (should be " + FleetShips.Describe(own) + "): " +
                     (aboard == own ? "RIGHT SHIP" : "WRONG SHIP") + ".");
            Report("re-cloned");
            Screenshot("recloned");
            _sequenceDone = true;
        }

        /// <summary>
        /// The fighting stance and punches: the local player's fists go up (seen from their eyes, then from a camera in front
        /// of them, where only the arms show: the body is first-person invisible), then a left and a right punch.
        /// </summary>
        private static System.Collections.IEnumerator FistSequence()
        {
            // Out of the clone tube first, as the game's leave key does: no fists in there.
            LocalPlayer inTube = LocalPlayer.Instance;
            InClonetube tube = inTube != null && inTube.Locomotion != null ? inTube.Locomotion.GetAbility<InClonetube>() : null;
            if (tube != null && tube.ActiveClonetube != null && (inTube.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0)
            {
                tube.ActiveClonetube.RequestOpenDoorFromInside();
                float until = Time.unscaledTime + 15f;
                while (Time.unscaledTime < until && LocalPlayer.Instance != null && (LocalPlayer.Instance.InteractionState.ActiveModes & InteractionMode.InCloneTube) != 0)
                    yield return new WaitForSeconds(0.25f);
                yield return new WaitForSeconds(1f);
                Log.Info("[autotest] Out of the clone tube: " +
                         (LocalPlayer.Instance != null && (LocalPlayer.Instance.InteractionState.ActiveModes & InteractionMode.InCloneTube) == 0) + ".");
            }
            PvpPunch.ForceStanceForTest(true);
            yield return new WaitForSeconds(1.5f);
            Log.Info("[autotest] Fists up: stance " + PvpPunch.LocalStanceUp + ", " + FistPose.Describe() + ".");
            Screenshot("fists-eyes");
            GameObject rig = null;
            LocalPlayer me = LocalPlayer.Instance;
            if (me != null)
            {
                rig = new GameObject("__FleetAutotestCamera");
                var camera = rig.AddComponent<Cinemachine.CinemachineVirtualCamera>();
                camera.Priority = 200;
                Transform body = me.transform;
                Vector3 head = body.position + body.up * 1.45f;
                rig.transform.SetPositionAndRotation(head + body.forward * 1.6f + body.right * 0.5f, Quaternion.LookRotation(head - (head + body.forward * 1.6f + body.right * 0.5f), body.up));
            }
            yield return new WaitForSeconds(1f);
            Screenshot("fists-front");
            PvpPunch.PunchForTest();
            yield return new WaitForSeconds(0.1f);
            Screenshot("punch-left");
            Log.Info("[autotest] Left punch: " + FistPose.Describe() + ".");
            yield return new WaitForSeconds(0.8f);
            PvpPunch.PunchForTest();
            yield return new WaitForSeconds(0.1f);
            Screenshot("punch-right");
            yield return new WaitForSeconds(0.8f);
            if (rig != null) UnityEngine.Object.Destroy(rig);
            yield return new WaitForSeconds(0.5f);
            Screenshot("punch-eyes-before");
            PvpPunch.PunchForTest();
            yield return new WaitForSeconds(0.1f);
            Screenshot("punch-eyes");
            yield return new WaitForSeconds(0.8f);
            PvpPunch.ForceStanceForTest(false);
            yield return new WaitForSeconds(1.5f);
            Log.Info("[autotest] Fists down: stance " + PvpPunch.LocalStanceUp + ", " + FistPose.Describe() + ".");
            Screenshot("fists-down");
            _sequenceDone = true;
        }

        /// <summary>The host's RETURN TO HUB: the escape menu shows it, the game's popup asks, and everyone goes back to the hub.</summary>
        private static System.Collections.IEnumerator ReturnSequence()
        {
            Log.Info("[autotest] RETURN TO HUB available: " + FleetReturn.Available + ".");
            try
            {
                MenuScreenController.Instance.Open<EscapeMenu>();
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] opening the escape menu", e);
            }
            yield return new WaitForSeconds(2f);
            Log.Info("[autotest] Escape menu open: " + MenuScreenController.IsOpen() + ", RETURN TO HUB shown: " + FleetReturn.ButtonShown + ".");
            Screenshot("escape-menu");
            FleetReturn.AskForTest();
            yield return new WaitForSeconds(2f);
            Screenshot("return-popup");
            try
            {
                while (MenuScreenController.IsOpen()) MenuScreenController.Instance.CloseTop(true);
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] closing the menus", e);
            }
            yield return new WaitForSeconds(1f);
            FleetReturn.ReturnNow();
            float until = Time.unscaledTime + 170f;
            bool tallied = false;
            while (Time.unscaledTime < until && !GameSessionManager.InHub)
            {
                if (!tallied && StateName() == "GSTallyScreen")
                {
                    tallied = true;
                    yield return new WaitForSeconds(3f);
                    Screenshot("tally");
                    // The end screen waits for every player's "ready" (what its continue button sends), as the game's own test loop does.
                    PhotonNetwork.RaiseEvent(10, new string[0], new Photon.Realtime.RaiseEventOptions
                    {
                        CachingOption = Photon.Realtime.EventCaching.AddToRoomCacheGlobal,
                        Receivers = Photon.Realtime.ReceiverGroup.All
                    }, ExitGames.Client.Photon.SendOptions.SendReliable);
                }
                yield return new WaitForSeconds(1f);
            }
            if (!GameSessionManager.InHub) yield break;
            yield return new WaitForSeconds(10f);
            Log.Info("[autotest] Back in the hub (state " + StateName() + "), crews " + string.Join(", ", FleetShips.Crews().Values.Select(e => e.ToString()).ToArray()) + ".");
            Screenshot("hub-again");
            _sequenceDone = true;
        }

        private static void StartJump()
        {
            Go(Step.Jumping);
            FleetRuntime.Instance.StartCoroutine(JumpSequence());
        }

        /// <summary>
        /// The co-op jump rules, then a jump: the escape vector set on the last ship reaches every ship; that ship's lever with
        /// uncharged drives is refused; with every drive charged it makes that ship the lead; the lead ship then jumps (debug
        /// charge and travel) and the others follow.
        /// </summary>
        private static System.Collections.IEnumerator JumpSequence()
        {
            var alive = FleetShips.AliveSlots();
            PlayerControlledShip last = alive.Count > 0 ? FleetShips.BySlot(alive[alive.Count - 1]) : null;
            VoidJumpSystem lastSystem = last != null ? last.GetComponentInChildren<VoidJumpSystem>() : null;
            VoidDriveModule lastDrive = last != null ? last.GetModule<VoidDriveModule>() : null;
            if (lastSystem == null || lastDrive == null)
            {
                Fail("the last ship has no void jump system");
                yield break;
            }
            Log.Info("[autotest] Setting the escape vector on " + FleetShips.Describe(last) + "'s system; every ship should take it.");
            lastSystem.ExitVectorSet(true);
            yield return new WaitForSeconds(2f);
            Report("escape vector");
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                VoidDriveModule drive = ship.GetModule<VoidDriveModule>();
                if (drive != null) drive.JumpCharge = 0f;
            }
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever with uncharged drives: it should be refused.");
            lastDrive.TryStartImpulseJump();
            yield return new WaitForSeconds(2f);
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                VoidDriveModule drive = ship.GetModule<VoidDriveModule>();
                if (drive != null) drive.JumpCharge = 1f;
            }
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever with every drive charged: it should lead.");
            lastDrive.TryStartImpulseJump();
            yield return new WaitForSeconds(2f);
            Log.Info("[autotest] Lead ship now " + (VoidJumpLink.LeaderSlot + 1) + " (expected " + (FleetShips.Slot(last) + 1) + ").");
            Report("lever");
            PlayerControlledShip lead = FleetShips.BySlot(VoidJumpLink.LeaderSlot);
            VoidJumpSystem system = lead != null ? lead.GetComponentInChildren<VoidJumpSystem>() : null;
            if (system == null)
            {
                Fail("the lead ship has no void jump system");
                yield break;
            }
            Log.Info("[autotest] Making " + FleetShips.Describe(lead) + " jump (debug charge + travel); the other ships should follow.");
            system.DebugTransitionToRotatingState();
        }

        private static void Finish()
        {
            try
            {
                Report("final");
            }
            catch (Exception e)
            {
                // Back in the hub there may be no ship to describe; the test is still done.
                Log.Failure("[autotest] the final report", e);
            }
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
                                ", crew " + FleetShips.CrewCount(FleetShips.Slot(ship)) + ", team " + FleetShips.TeamOfSlot(FleetShips.Slot(ship)) + ", faction " + ship.faction +
                                (ship.StartedDeath ? ", DYING" : ""));
            }
            text.AppendLine("  crews: " + string.Join(", ", FleetShips.Crews().Values.Select(e => e.ToString()).ToArray()) + "; lead ship " + (VoidJumpLink.LeaderSlot + 1) +
                            "; roster: " + string.Join(", ", (PvpRoster.Seats() ?? new System.Collections.Generic.List<RosterSeat>()).Select(r => r.ToString()).ToArray()));
            if (FleetShips.Active) text.AppendLine("  resources: " + ShipResources.Describe() + (ShipResources.Active ? "" : " (per-ship resources off)"));
            if (FleetSession.IsPvP)
            {
                text.AppendLine("  PvP: teams locked " + PvpTeams.Locked + ", choosing " + PvpTeams.Choosing + ", team screen " + PvpTeamScreen.Visible +
                                ", local team " + FleetShips.TeamOfActor(FleetShips.LocalActor) + ", spectator " + FleetShips.IsSpectator(FleetShips.LocalActor) +
                                ", watching " + FleetShips.Describe(FleetShips.Watched) + ", loser " + PvpMatch.LoserTeam +
                                ", local player faction " + (LocalPlayer.Instance != null ? LocalPlayer.Instance.faction.ToString() : "-"));
                foreach (PlayerControlledShip a in FleetShips.All)
                {
                    foreach (PlayerControlledShip b in FleetShips.All)
                    {
                        if (a == b || FleetShips.Slot(a) > FleetShips.Slot(b)) continue;
                        text.AppendLine("  PvP: " + FleetShips.Describe(a) + " / " + FleetShips.Describe(b) + ": hostile " + a.IsHostile(b) + "/" + b.IsHostile(a) + ", " +
                                        Vector3.Distance(a.transform.position, b.transform.position).ToString("0") + " m");
                    }
                    text.AppendLine("  PvP: " + FleetShips.Describe(a) + " parts " + FactionCounts(a) + "; turrets " + PvpTurrets.DescribeShip(a));
                }
                PlayerControlledShip red = FleetShips.BySlot(1);
                text.AppendLine("  PvP arena: " + PvpArena.Describe());
                if (red != null) text.AppendLine("  PvP hack: BLUE door access to RED 1 " + PvpHack.HasAccess(0, red) + "; " + PvpBoarding.DescribeAccess(red) + "; " + DescribeTerminals());
            }
            if (FleetSession.Mode == FleetMode.Coop)
            {
                text.AppendLine("  co-op: local ship " + (FleetShips.CrewSlot(FleetShips.LocalActor) + 1) + ", spectator " + FleetShips.IsSpectator(FleetShips.LocalActor) +
                                ", watching " + FleetShips.Describe(FleetShips.Watched) + ", RETURN TO HUB " + (FleetReturn.Available ? "available" : "not available"));
            }
            PlayerControlledShip game = ClientGame.Current != null ? ClientGame.Current.PlayerShip : null;
            LocalPlayer me = LocalPlayer.Instance;
            text.AppendLine("  game's player ship = " + FleetShips.Describe(game) + ", local crew ship = " + FleetShips.Describe(FleetShips.LocalShip) +
                            ", aboard = " + FleetShips.Describe(FleetShips.AboardShip(me)) + ", local character " +
                            (me == null ? "none" : (me.IsDead ? "dead" : "alive") + " at " + me.transform.position.ToString("0")) +
                            ", spectating " + Spectator.Active + ", game state " + StateName());
            text.AppendLine("  active sector " + (GameSessionManager.ActiveSector != null ? GameSessionManager.ActiveSector.Id.ToString() : "none") +
                            ", room players " + PhotonNetwork.PlayerList.Length + "/" + (PhotonNetwork.CurrentRoom != null ? PhotonNetwork.CurrentRoom.MaxPlayers : 0));
            text.AppendLine("  crew row: " + CrewBadge.Describe());
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
