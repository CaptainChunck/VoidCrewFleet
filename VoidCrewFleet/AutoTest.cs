using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CG.Client.Player.Input;
using CG.Client.Player.Interactions;
using CG.Client.Player.Interactions.Build;
using CG.Game;
using CG.Game.Player;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Game.SpaceObjects.Controllers;
using CG.Ship.Hull;
using CG.Ship.Modules;
using CG.Ship.Object;
using CG.Space;
using Gameplay.Hub;
using Gameplay.Quests;
using Gameplay.Ship;
using Gameplay.Ship.VoidJump;
using PlatformAbstractionLayer;
using Photon.Pun;
using ResourceAssets;
using ToolClasses;
using UnityEngine;
using UnityEngine.UIElements;

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
    /// folder: a debug jump, or (AutoTestLeverJump) one made the way players make it, led by the last ship's lever and astral
    /// map. It never runs unless switched on in the config.
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
            Tour,
            Fists,
            Respawning,
            Cruisers,
            Deconstructing,
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
                    if (!_hubPreviewStarted)
                    {
                        _hubPreviewStarted = true;
                        _sequenceDone = false;
                        FleetRuntime.Instance.StartCoroutine(HubCrewListSequence());
                        return;
                    }
                    if (!_sequenceDone && Since < 40f) return;
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
                    // The second shot: a full crew's stand-in rows over the other ships' cards (set up a report earlier, as the
                    // cards are worked out a few times a second); the third and later ones the real list.
                    if (_reports == 0) CrewListPreview(true);
                    else if (_reports == 2) CrewListPreview(false);
                    Screenshot(_reports == 1 ? "crew-list" : "mission");
                    if (FleetSession.IsPvP && _reports == 0) StartHackTest();
                    if (++_reports < (FleetSession.IsPvP ? 10 : 3)) return;
                    if (Settings.AutoTestTour.Value && FleetSession.IsPvP && Step.Tour > _done)
                    {
                        _done = Step.Tour;
                        Go(Step.Tour);
                        _sequenceDone = false;
                        FleetRuntime.Instance.StartCoroutine(TourSequence());
                        return;
                    }
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
                        AfterRespawn();
                    }
                    return;
                case Step.Tour:
                    if (!_sequenceDone && Since < 300f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The camera tour didn't finish in 300 s.");
                    Go(Step.Report);
                    return;
                case Step.Fists:
                    if (!_sequenceDone && Since < 60f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The fist test didn't finish in 60 s.");
                    Go(Step.Report);
                    return;
                case Step.Respawning:
                    if (!_sequenceDone && Since < 90f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The respawn test didn't finish in 90 s.");
                    AfterRespawn();
                    return;
                case Step.Cruisers:
                    if (!_sequenceDone && Since < 240f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The cruiser test didn't finish in 240 s.");
                    AfterCruisers();
                    return;
                case Step.Deconstructing:
                    if (!_sequenceDone && Since < 240f) return;
                    if (!_sequenceDone) Log.Warn("[autotest] The deconstruct test didn't finish in 240 s.");
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
                    // The lever jump logs each of its own steps and takes a few minutes: a report every 10 s, and done when it is.
                    if (Time.unscaledTime - _lastReport < (_leverJump ? 10f : 3f)) return;
                    _lastReport = Time.unscaledTime;
                    Report("jump");
                    if (_shots < 12) Screenshot("jump");
                    if (_leverJump ? _sequenceDone || Since > 240f : Since > 75f)
                    {
                        if (_leverJump && !_sequenceDone) Log.Warn("[autotest] The lever jump didn't finish in 240 s.");
                        Finish();
                    }
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

        /// <summary>After the respawn test: the cruiser test (AutoTestCruisers, co-op), then the ships are destroyed.</summary>
        private static void AfterRespawn()
        {
            if (Settings.AutoTestCruisers.Value && FleetSession.Mode == FleetMode.Coop)
            {
                Go(Step.Cruisers);
                _sequenceDone = false;
                FleetRuntime.Instance.StartCoroutine(CruiserSequence());
                return;
            }
            AfterCruisers();
        }

        /// <summary>After the cruiser test: the deconstruct test (AutoTestDeconstruct, co-op with several ships), then the ships are destroyed.</summary>
        private static void AfterCruisers()
        {
            if (Settings.AutoTestDeconstruct.Value && FleetSession.Mode == FleetMode.Coop && FleetShips.Active)
            {
                Go(Step.Deconstructing);
                _sequenceDone = false;
                FleetRuntime.Instance.StartCoroutine(DeconstructSequence());
                return;
            }
            Go(Step.Destroying);
        }

        /// <summary>
        /// Taking modules apart on every ship, and building them again (AutoTestDeconstruct): the host takes a module apart on
        /// its own ship first (the game's one build controller then keeps that ship's sockets), then one on each other ship,
        /// and checks that each comes out of its slot and that the slot takes the module back from its build box. In Fleet
        /// 0.4.4 a module on any ship but the first stayed stuck in its slot, switched off, and the slot with it.
        /// </summary>
        private static System.Collections.IEnumerator DeconstructSequence()
        {
            BuildProcessController builder = ClientGame.Current != null ? ClientGame.Current.GetComponent<BuildProcessController>() : null;
            PlayerControlledShip own = FleetShips.LocalShip ?? FleetShips.BySlot(0);
            if (!PhotonNetwork.IsMasterClient || builder == null || own == null)
            {
                Log.Warn("[autotest] Deconstruct: skipped (host " + PhotonNetwork.IsMasterClient + ", build controller " + (builder != null) + ", own ship " + (own != null) + ").");
                _sequenceDone = true;
                yield break;
            }
            var ships = new List<PlayerControlledShip> { own };
            foreach (int slot in FleetShips.AliveSlots())
            {
                PlayerControlledShip ship = FleetShips.BySlot(slot);
                if (ship != null && ship != own) ships.Add(ship);
            }
            float wait = ClientGame.Current.SharedConfiguration.DeconstructDuration + 20f;
            int passed = 0, failed = 0;
            foreach (PlayerControlledShip ship in ships)
            {
                string passedOver;
                BuildSocket socket = TakeApartCandidate(ship, out passedOver);
                if (socket == null)
                {
                    Log.Warn("[autotest] Deconstruct: nothing on " + FleetShips.Describe(ship) + " can be taken apart; skipped (" + passedOver + ").");
                    continue;
                }
                CellModule module = socket.InstalledModule;
                string name = module.DisplayName;
                // The game only takes apart a module with nothing in its item slots (a turret's ammo, a shield's battery), as
                // a player would empty them first.
                int emptied = 0;
                foreach (CarryablesSocket slot in module.ConnectedSockets)
                {
                    CarryableObject item = slot != null ? slot.Payload : null;
                    if (item == null || item.DestroyRequested) continue;
                    item.Kill();
                    emptied++;
                }
                if (emptied > 0)
                {
                    Log.Info("[autotest] Deconstruct: emptied " + emptied + " item slot(s) of " + name + " first.");
                    yield return new WaitForSeconds(2f);
                }
                Log.Info("[autotest] Deconstruct: taking apart " + name + " on " + FleetShips.Describe(ship) + " (socket " + socket.Index + ").");
                bool asked = false;
                try
                {
                    asked = builder.TryDeconstructModule(module);
                }
                catch (Exception e)
                {
                    Log.Failure("[autotest] asking to take apart " + name, e);
                }
                float started = Time.unscaledTime;
                while (Time.unscaledTime - started < wait && socket.InstalledModule == module) yield return new WaitForSeconds(1f);
                if (socket.InstalledModule == module)
                {
                    failed++;
                    Log.Error("[autotest] Deconstruct: FAILED on " + FleetShips.Describe(ship) + ", " + name + " is still in its slot after " + wait.ToString("0") +
                              " s (asked " + asked + ", marked as being taken apart " + (module != null && module.IsBeingDeconstructed) + ").");
                    continue;
                }
                Log.Info("[autotest] Deconstruct: " + name + " came out of its slot on " + FleetShips.Describe(ship) + " in " + (Time.unscaledTime - started).ToString("0") + " s.");
                // The slot takes the module back from the build box it left there.
                yield return new WaitForSeconds(2f);
                var box = socket.Payload as BuildBox;
                bool rebuilding = false;
                try
                {
                    rebuilding = box != null && builder.TryConstructModule(box);
                }
                catch (Exception e)
                {
                    Log.Failure("[autotest] building " + name + " again", e);
                }
                started = Time.unscaledTime;
                while (rebuilding && Time.unscaledTime - started < wait && socket.InstalledModule == null) yield return new WaitForSeconds(1f);
                if (socket.InstalledModule != null)
                {
                    passed++;
                    Log.Info("[autotest] Deconstruct: PASSED on " + FleetShips.Describe(ship) + ", " + socket.InstalledModule.DisplayName + " is built in that slot again.");
                }
                else
                {
                    failed++;
                    Log.Error("[autotest] Deconstruct: FAILED on " + FleetShips.Describe(ship) + ", the slot didn't take " + name + " back (build box " + (box != null) +
                              ", building " + rebuilding + ").");
                }
            }
            Log.Info("[autotest] Deconstruct: " + passed + " passed, " + failed + " failed.");
            _sequenceDone = true;
        }

        /// <summary>
        /// A socket of a ship whose module can be taken apart (allowed, not in use, undamaged; its item slots are emptied
        /// first), a weapon if there is one; null if none, with <paramref name="passedOver"/> saying why the others weren't.
        /// </summary>
        private static BuildSocket TakeApartCandidate(PlayerControlledShip ship, out string passedOver)
        {
            passedOver = "no sockets";
            BuildSocketController sockets = ship != null ? ship.GetComponent<BuildSocketController>() : null;
            if (sockets == null || sockets.Sockets == null) return null;
            BuildSocket any = null;
            int empty = 0, notAllowed = 0, inUse = 0, damaged = 0;
            foreach (BuildSocket socket in sockets.Sockets)
            {
                CellModule module = socket != null ? socket.InstalledModule : null;
                if (module == null)
                {
                    empty++;
                    continue;
                }
                if (module is VoidDriveModule || module.BuildingConstraints == null || !module.BuildingConstraints.AllowDeconstruction)
                {
                    notAllowed++;
                    continue;
                }
                if (module.IsInUse || module.IsBeingDeconstructed)
                {
                    inUse++;
                    continue;
                }
                if (module.DefectSystem != null && module.DefectSystem.ActiveDefectAmount > 0)
                {
                    damaged++;
                    continue;
                }
                if (module is Gameplay.CompositeWeapons.CompositeWeaponModule) return socket;
                if (any == null) any = socket;
            }
            passedOver = sockets.Sockets.Count + " sockets: " + empty + " empty, " + notAllowed + " can't be taken apart, " + inUse + " in use, " + damaged + " damaged";
            return any;
        }

        /// <summary>The Fleet Battle mission's capital-ship spawner profile (SpawnerProfile_Hollow_FleetBattle_CapitolShips).</summary>
        private const string CruiserProfile = "617c51d5b70105d46956fb25c40603f4";

        /// <summary>The tag that spawner goes by and gives its cruisers (Tag_Spawner_Hollow_FleetBattle_CapitolShip).</summary>
        private const string CruiserTag = "1424f8c92ce67d142b620e2203ae8a11";

        /// <summary>
        /// The Fleet Battle mission's HOLLOW Cruisers, without flying to a Fleet Battle sector (AutoTestCruisers): the host
        /// brings in that mission's capital-ship spawner, the game's own (one HOLLOW Cruiser Elite at a time, which takes its
        /// whole budget, and the next one 30 s after the last one died), lets it send a cruiser to a spot ahead of the fleet,
        /// destroys it and checks that the next one comes, as with one ship. In Fleet 0.4.4 the next one never came in a
        /// Fleet Pilgrimage: the enemy scaling left a remainder on the spawner's count.
        /// </summary>
        private static System.Collections.IEnumerator CruiserSequence()
        {
            Gameplay.NPC.AI.AIDirector director = Singleton<Gameplay.NPC.AI.AIDirector>.I;
            System.Reflection.FieldInfo countField = GameMember.Field(typeof(Gameplay.NPC.AI.Spawner), "currentIntensity");
            System.Reflection.FieldInfo unitsField = GameMember.Field(typeof(Gameplay.NPC.AI.Spawner), "trackedUnits");
            var tag = new ScriptableObjectRef(new GUIDUnion(CruiserTag));
            var profileGuid = new GUIDUnion(CruiserProfile);
            Gameplay.NPC.AI.SpawnerProfile profile = null;
            try
            {
                if (ScriptableObjectContainer.Instance.HasItem(profileGuid))
                    profile = ScriptableObjectContainer.Instance.GetItem(profileGuid).Asset as Gameplay.NPC.AI.SpawnerProfile;
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] finding the Fleet Battle cruiser spawner", e);
            }
            PlayerControlledShip near = FleetShips.LocalShip ?? FleetShips.BySlot(0);
            if (!PhotonNetwork.IsMasterClient || director == null || profile == null || near == null || GameSessionManager.ActiveSector == null ||
                countField == null || unitsField == null)
            {
                Log.Warn("[autotest] Cruisers: skipped (host " + PhotonNetwork.IsMasterClient + ", AI Director " + (director != null) + ", spawner profile " +
                         (profile != null) + ", a ship to send them to " + (near != null) + ", the spawner's fields " + (countField != null && unitsField != null) + ").");
                _sequenceDone = true;
                yield break;
            }
            if (director.GetSpawner(tag, true) != null)
            {
                Log.Warn("[autotest] Cruisers: skipped, this sector already has the Fleet Battle cruiser spawner.");
                _sequenceDone = true;
                yield break;
            }
            FleetPlan plan = FleetSession.Current;
            Log.Info("[autotest] Cruisers: Fleet Pilgrimage enemy scaling is " + (plan != null && plan.ScaleEnemies ? "on" : "OFF, so this only shows the game's own spawner") +
                     "; bringing in the Fleet Battle cruiser spawner.");
            director.CreateSpawner(profile);
            Gameplay.NPC.AI.Spawner spawner = null;
            float until = Time.unscaledTime + 10f;
            while (spawner == null && Time.unscaledTime < until)
            {
                yield return new WaitForSeconds(0.5f);
                spawner = director.GetSpawner(tag, true);
            }
            if (spawner == null)
            {
                Log.Error("[autotest] Cruisers: FAILED, the spawner didn't start.");
                _sequenceDone = true;
                yield break;
            }
            Vector3 spot = near.WorldPosition + near.transform.forward * 1500f;
            director.SetSpawnerSpawnHandle(tag, new CG.Game.Scenarios.Actions.SpawnHandle(GameSessionManager.ActiveSector, spot, Quaternion.identity));
            Log.Info("[autotest] Cruisers: the spawner is up (budget " + spawner.TargetIntensity + " of " + spawner.MaxIntensity + ", counts " +
                     SpawnerCount(countField, spawner).ToString("R") + "); its cruisers jump in 1.5 km ahead of " + FleetShips.Describe(near) + ".");

            // The first cruiser: at the AI Director's next turn (every 15 s).
            OrbitObject first = null;
            until = Time.unscaledTime + 45f;
            while (first == null && Time.unscaledTime < until)
            {
                yield return new WaitForSeconds(1f);
                first = SpawnerUnit(unitsField, spawner, null);
            }
            if (first == null)
            {
                Log.Error("[autotest] Cruisers: FAILED, the spawner sent no cruiser in 45 s (it counts " + SpawnerCount(countField, spawner).ToString("R") + ").");
                EndCruiserTest(director, tag, unitsField, spawner);
                _sequenceDone = true;
                yield break;
            }
            Log.Info("[autotest] Cruisers: the first cruiser is " + first.name + " (the spawner counts " + SpawnerCount(countField, spawner).ToString("R") + ").");
            // It comes through a void jump: destroyed once it is in the sector (or after 40 s, wherever it is).
            until = Time.unscaledTime + 40f;
            while (first != null && !first.IsDestroyed && first.Sector != GameSessionManager.ActiveSector && Time.unscaledTime < until)
                yield return new WaitForSeconds(1f);
            bool gone = first == null || first.IsDestroyed;
            bool arrived = !gone && first.Sector == GameSessionManager.ActiveSector;
            Log.Info("[autotest] Cruisers: it " + (gone ? "was destroyed already." : (arrived ? "arrived, " + Vector3.Distance(first.WorldPosition, near.WorldPosition).ToString("0") +
                     " m from " + FleetShips.Describe(near) : "did not arrive in 40 s") + "; destroying it."));
            if (!gone && !first.DestroyRequested) first.Kill();
            float died = Time.unscaledTime;
            yield return new WaitForSeconds(1f);
            float left = SpawnerCount(countField, spawner);
            Log.Info("[autotest] Cruisers: with it gone the spawner counts " + left.ToString("R") + (left == 0f ? " (nothing, right)." : " (should be 0: that's room taken from the next one)."));

            // The next one: 30 s after the last one died, at the AI Director's next turn.
            OrbitObject next = null;
            until = Time.unscaledTime + 75f;
            while (next == null && Time.unscaledTime < until)
            {
                yield return new WaitForSeconds(1f);
                next = SpawnerUnit(unitsField, spawner, first);
            }
            if (next != null) Log.Info("[autotest] Cruisers: PASSED, the next cruiser came " + (Time.unscaledTime - died).ToString("0") + " s after the first one died.");
            else Log.Error("[autotest] Cruisers: FAILED, no next cruiser in 75 s; the spawner counts " + SpawnerCount(countField, spawner).ToString("R") + " of its budget " + spawner.TargetIntensity + ".");
            EndCruiserTest(director, tag, unitsField, spawner);
            _sequenceDone = true;
        }

        /// <summary>The intensity a spawner counts for the units it has out (NaN when it can't be read).</summary>
        private static float SpawnerCount(System.Reflection.FieldInfo countField, Gameplay.NPC.AI.Spawner spawner)
        {
            return GameMember.Read(countField, spawner, float.NaN);
        }

        /// <summary>A unit the spawner sent that is still there (other than one already seen), or null.</summary>
        private static OrbitObject SpawnerUnit(System.Reflection.FieldInfo unitsField, Gameplay.NPC.AI.Spawner spawner, OrbitObject except)
        {
            List<OrbitObject> units = GameMember.Read<List<OrbitObject>>(unitsField, spawner, null);
            if (units == null) return null;
            foreach (OrbitObject unit in units)
            {
                if (unit != null && !unit.IsDestroyed && !unit.DestroyRequested && !ReferenceEquals(unit, except)) return unit;
            }
            return null;
        }

        /// <summary>The cruiser test's spawner goes again, with the cruisers it still has out.</summary>
        private static void EndCruiserTest(Gameplay.NPC.AI.AIDirector director, ScriptableObjectRef tag, System.Reflection.FieldInfo unitsField, Gameplay.NPC.AI.Spawner spawner)
        {
            try
            {
                List<OrbitObject> units = GameMember.Read<List<OrbitObject>>(unitsField, spawner, null);
                if (units != null)
                {
                    foreach (OrbitObject unit in new List<OrbitObject>(units))
                    {
                        if (unit != null && !unit.IsDestroyed && !unit.DestroyRequested) unit.Kill();
                    }
                }
                director.DestroySpawner(tag);
                Log.Info("[autotest] Cruisers: the test's spawner and its cruisers are gone.");
            }
            catch (Exception e)
            {
                Log.Failure("[autotest] removing the cruiser test's spawner", e);
            }
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

        /// <summary>
        /// A camera tour of the PvP arena (AutoTestTour), to see what it looks like: a free camera over the whole arena, behind
        /// each team, at its centre, at every ship and every salvage spot, and at a supply drop called in for the tour, with a
        /// screenshot at each. The game's screens are hidden for the tour and the ships are shown as from open space.
        /// </summary>
        private static System.Collections.IEnumerator TourSequence()
        {
            var spots = new List<PvpArena.TourSpot>();
            Vector3 centre;
            Vector3 axis;
            Vector3 drop;
            string arena = PvpArena.TourArena(out centre, out axis, out drop, spots);
            if (arena == null)
            {
                Log.Warn("[autotest] Tour: this match has no PvP arena.");
                _sequenceDone = true;
                yield break;
            }
            Log.Info("[autotest] Tour of " + arena + ": centre " + centre.ToString("0") + ", BLUE to RED along " + axis.ToString("0.00") + ".");
            // The drop arrives through a void jump some seconds after it is due: it is the tour's last stop.
            PvpArena.DropNowForTest();
            var screens = new Dictionary<VisualElement, StyleEnum<DisplayStyle>>();
            HideScreens(screens);
            Spectator.ShowShipsFromSpace();
            var rig = new GameObject("__FleetTourCamera");
            var camera = rig.AddComponent<Cinemachine.CinemachineVirtualCamera>();
            Cinemachine.LensSettings lens = camera.m_Lens;
            lens.FieldOfView = 60f;
            lens.NearClipPlane = 1f;
            lens.FarClipPlane = 60000f;
            camera.m_Lens = lens;
            camera.Priority = 300;
            // Room sound and atmosphere follow the view, as for the spectator camera.
            rig.AddComponent<Gameplay.Atmosphere.RoomPoint>();
            Transform view = rig.transform;
            Vector3 side = Vector3.Cross(Vector3.up, axis).normalized;
            Vector3 blue = centre - axis * 1800f;
            Vector3 red = centre + axis * 1800f;

            // The view cuts over from the player's eyes first.
            PlaceView(view, centre + Vector3.up * 5200f, centre, axis);
            yield return new WaitForSeconds(3f);
            yield return TourShot(view, centre + Vector3.up * 5200f, centre, axis, screens, "tour-above",
                "the whole arena from 5.2 km straight above: BLUE's start at the bottom, RED's at the top");
            yield return TourShot(view, centre + side * 4500f + Vector3.up * 1500f, centre, Vector3.up, screens, "tour-side",
                "the arena from the side, 4.5 km out: BLUE's start on one side of the centre, RED's on the other");
            yield return TourShot(view, centre + (side - axis).normalized * 3500f + Vector3.up * 2500f, centre, Vector3.up, screens, "tour-diagonal",
                "the arena from high above BLUE's side, looking across to RED's");
            yield return TourShot(view, blue - axis * 500f + Vector3.up * 120f, centre, Vector3.up, screens, "tour-behind-blue",
                "from behind BLUE's start, looking towards the centre and RED");
            yield return TourShot(view, red + axis * 500f + Vector3.up * 120f, centre, Vector3.up, screens, "tour-behind-red",
                "from behind RED's start, looking towards the centre and BLUE");
            yield return TourShot(view, centre + Vector3.up * 60f, blue, Vector3.up, screens, "tour-centre-to-blue",
                "from the centre of the arena, looking towards BLUE's start");
            yield return TourShot(view, centre + Vector3.up * 60f, red, Vector3.up, screens, "tour-centre-to-red",
                "from the centre of the arena, looking towards RED's start");

            foreach (PlayerControlledShip ship in FleetShips.All.ToList())
            {
                if (ship == null) continue;
                Bounds bounds;
                Vector3 at = Extent(ship.gameObject, out bounds) ? bounds.center : ship.transform.position;
                // Ships are about 100 m long; their meshes' bounds take in far more (effects, interiors), so a set distance frames them.
                Vector3 from = at + (ship.transform.forward * 0.75f + ship.transform.right * 0.55f + Vector3.up * 0.35f).normalized * 170f;
                string name = FleetShips.Describe(ship);
                yield return TourShot(view, from, at, Vector3.up, screens, "tour-ship-" + name.Replace(' ', '-'), name + " from ahead and above");
            }

            foreach (PvpArena.TourSpot spot in spots)
            {
                GameObject found = NearestSpawned(spot.Prefab, spot.Position, 1500f);
                Bounds bounds = new Bounds();
                bool sized = found != null && Extent(found, out bounds);
                if (!sized) bounds = new Bounds(found != null ? found.transform.position : spot.Position, Vector3.one * 60f);
                float size = Mathf.Clamp(bounds.size.magnitude, 25f, 900f);
                Vector3 home = spot.Team == 0 ? blue : red;
                Vector3 toHome = home - bounds.center;
                toHome.y = 0f;
                Vector3 from = bounds.center + (toHome.normalized * 0.85f + Vector3.up * 0.5f).normalized * Mathf.Clamp(size * 1.2f, 40f, 420f);
                yield return TourShot(view, from, bounds.center, Vector3.up, screens, "tour-" + spot.Label.Replace(' ', '-'),
                    spot.Label + (found != null ? "" : " (not found: its planned spot)") + ", seen from its team's side (about " + size.ToString("0") + " m across)");
            }

            // The supply drop, once it has arrived (up to a minute after it was called in).
            GUIDUnion package = DataTable<DefaultAssetTable>.Instance.DefaultCarePackageAsset.AssetGuid;
            float until = Time.unscaledTime + 60f;
            GameObject crate = NearestSpawned(package, drop, 600f);
            while (crate == null && Time.unscaledTime < until)
            {
                yield return new WaitForSeconds(1f);
                crate = NearestSpawned(package, drop, 600f);
            }
            yield return new WaitForSeconds(6f);
            crate = NearestSpawned(package, drop, 2000f);
            Vector3 dropAt = crate != null ? crate.transform.position : drop;
            Vector3 dropSide = blue - dropAt;
            dropSide.y = 0f;
            yield return TourShot(view, dropAt + (dropSide.normalized * 0.8f + Vector3.up * 0.6f).normalized * 120f, dropAt, Vector3.up, screens, "tour-supply-drop",
                crate != null ? "the supply drop, arrived between the teams" : "the supply drop's spot (no drop arrived within a minute)");
            yield return TourShot(view, dropAt + side * 900f + Vector3.up * 300f, dropAt, Vector3.up, screens, "tour-supply-drop-wide",
                "the supply drop's spot from further out");

            UnityEngine.Object.Destroy(rig);
            foreach (KeyValuePair<VisualElement, StyleEnum<DisplayStyle>> screen in screens) screen.Key.style.display = screen.Value;
            Log.Info("[autotest] Tour done: " + _shots + " screenshots so far.");
            _sequenceDone = true;
        }

        private static System.Collections.IEnumerator TourShot(Transform view, Vector3 from, Vector3 at, Vector3 up,
            Dictionary<VisualElement, StyleEnum<DisplayStyle>> screens, string label, string what)
        {
            PlaceView(view, from, at, up);
            yield return new WaitForSeconds(1.5f);
            HideScreens(screens);
            yield return null;
            Screenshot(label);
            Log.Info("[autotest] Tour " + _shots.ToString("00") + ": " + what + "; camera at " + from.ToString("0") + ", looking at " + at.ToString("0") + ".");
            yield return new WaitForSeconds(0.3f);
        }

        private static void PlaceView(Transform view, Vector3 from, Vector3 at, Vector3 up)
        {
            Vector3 forward = at - from;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            view.SetPositionAndRotation(from, Quaternion.LookRotation(forward.normalized, up));
        }

        /// <summary>Hides every screen-space panel not hidden yet (one can open mid-tour), remembering how it was.</summary>
        private static void HideScreens(Dictionary<VisualElement, StyleEnum<DisplayStyle>> screens)
        {
            foreach (UIDocument document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
            {
                if (document == null || document.panelSettings == null || document.panelSettings.targetTexture != null) continue;
                VisualElement root = document.rootVisualElement;
                if (root == null || screens.ContainsKey(root)) continue;
                screens[root] = root.style.display;
                root.style.display = DisplayStyle.None;
            }
        }

        /// <summary>The bounds of an object's meshes; false when it has none.</summary>
        private static bool Extent(GameObject thing, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer renderer in thing.GetComponentsInChildren<Renderer>())
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any;
        }

        /// <summary>The spawned object of a prefab nearest a point, within a distance; null when there is none.</summary>
        private static GameObject NearestSpawned(GUIDUnion prefab, Vector3 near, float within)
        {
            GameObject best = null;
            float bestDistance = within;
            foreach (CG.AbstractCloneStarObject thing in UnityEngine.Object.FindObjectsOfType<CG.AbstractCloneStarObject>())
            {
                if (thing == null || thing.assetGuid != prefab) continue;
                float distance = Vector3.Distance(thing.transform.position, near);
                if (distance >= bestDistance) continue;
                best = thing.gameObject;
                bestDistance = distance;
            }
            return best;
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

        private static bool _leverJump;

        private static void StartJump()
        {
            _leverJump = Settings.AutoTestLeverJump.Value;
            _sequenceDone = false;
            Go(Step.Jumping);
            FleetRuntime.Instance.StartCoroutine(_leverJump ? LeverJumpSequence() : JumpSequence());
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
            int leadBefore = VoidJumpLink.LeaderSlot;
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever with every drive charged: it leads only if the game starts the jump " +
                     "(escape vector, power and a powered helm), otherwise the lead stays.");
            lastDrive.TryStartImpulseJump();
            yield return new WaitForSeconds(2f);
            VoidJumpSystem pulled = last.GetComponentInChildren<VoidJumpSystem>();
            bool started = pulled != null && pulled.ActiveState != null && !(pulled.ActiveState is VoidJumpInactive) && !(pulled.ActiveState is VoidJumpExitVectorSet);
            Log.Info("[autotest] Lead ship now " + (VoidJumpLink.LeaderSlot + 1) + " (expected " + ((started ? FleetShips.Slot(last) : leadBefore) + 1) + ": the jump " +
                     (started ? "started" : "didn't start") + ").");
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

        /// <summary>
        /// The co-op jump as players make it, led by the last ship (AutoTestLeverJump): every ship is powered and its drive
        /// charged; PLOT VECTOR pressed on the last ship's astral map gives every ship the vector; with the local player's own
        /// ship's drive short of full, the last ship's lever is refused and names that ship (the drive then fills up again and
        /// its state goes out to every player); the lever then starts the jump with the last ship leading and the others
        /// following it into the void; a destination picked on the last ship's map is every map's pick; and the lever again
        /// takes every ship there, side by side. Each step logs what every ship and map did.
        /// </summary>
        private static System.Collections.IEnumerator LeverJumpSequence()
        {
            System.Reflection.MethodInfo pull = GameMember.Method(typeof(VoidDriveModule), "OnJumpLeverPulled");
            List<int> alive = FleetShips.AliveSlots();
            PlayerControlledShip last = alive.Count > 1 ? FleetShips.BySlot(alive[alive.Count - 1]) : null;
            PlayerControlledShip own = FleetShips.LocalShip ?? (alive.Count > 0 ? FleetShips.BySlot(alive[0]) : null);
            VoidDriveModule lastDrive = last != null ? last.GetModule<VoidDriveModule>() : null;
            VoidDriveModule ownDrive = own != null ? own.GetModule<VoidDriveModule>() : null;
            VoidJumpSystem lastSystem = last != null ? last.GetComponentInChildren<VoidJumpSystem>() : null;
            if (pull == null || lastDrive == null || ownDrive == null || lastSystem == null)
            {
                Fail("the lever jump needs two living ships with void drives");
                yield break;
            }
            Log.Info("[autotest] Lever jump led by " + FleetShips.Describe(last) + ". Astral maps: " + AstralMapLink.DescribeForTest() + ".");

            // Every ship powered and charged, as its crew would: the game's own debug helpers, each ship in turn as "the player ship".
            foreach (PlayerControlledShip ship in FleetShips.All.ToList())
            {
                ForShip(ship, "Powering", () =>
                {
                    DebugInput.PowerShip();
                    DebugInput.PowerAllModules();
                });
            }
            yield return new WaitForSeconds(3f);
            foreach (PlayerControlledShip ship in FleetShips.All.ToList()) ForShip(ship, "Charging", DebugInput.ChargeEnginesOnce);
            yield return new WaitForSeconds(2f);
            Log.Info("[autotest] Powered and charged: " + DescribeDrives() + ".");

            // The escape vector, from the last ship's map: every ship takes it.
            Log.Info("[autotest] Pressing PLOT VECTOR on " + FleetShips.Describe(last) + "'s astral map: " + AstralMapLink.PressForTest(last, null) + ".");
            yield return new WaitForSeconds(2f);
            Log.Info("[autotest] Escape vector on every ship: " + EveryShipIn<VoidJumpExitVectorSet>() + " (" + DescribeDrives() + "); astral maps: " +
                     AstralMapLink.DescribeForTest() + ".");
            if (!EveryShipIn<VoidJumpExitVectorSet>())
            {
                Log.Warn("[autotest] The map didn't set the escape vector everywhere; setting it on " + FleetShips.Describe(last) + "'s jump system.");
                lastSystem.ExitVectorSet(true);
                yield return new WaitForSeconds(2f);
            }

            // A crewed ship's drive short of full: the lever names that ship. Its engines keep charging, so it fills up again.
            ownDrive.JumpCharge = 0.9f;
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever with " + FleetShips.Describe(own) + "'s drive at 90 %: it should be refused, " +
                     "naming " + FleetShips.Describe(own) + ".");
            pull.Invoke(lastDrive, null);
            yield return new WaitForSeconds(0.3f);
            Screenshot("lever-refused");
            float until = Time.unscaledTime + 15f;
            while (Time.unscaledTime < until && !ownDrive.IsFullyCharged) yield return new WaitForSeconds(0.25f);
            Log.Info("[autotest] " + FleetShips.Describe(own) + "'s drive full again: " + ownDrive.IsFullyCharged + "; the jump didn't start: " +
                     EveryShipIn<VoidJumpExitVectorSet>() + ".");
            yield return new WaitForSeconds(3.5f);

            // The jump: the last ship's lever, every drive charged.
            int leadBefore = VoidJumpLink.LeaderSlot;
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever with every drive charged: the jump should start, led by it.");
            pull.Invoke(lastDrive, null);
            yield return new WaitForSeconds(2f);
            bool started = !(lastSystem.ActiveState is VoidJumpInactive) && !(lastSystem.ActiveState is VoidJumpExitVectorSet);
            Log.Info("[autotest] Jump started: " + started + "; lead ship " + (VoidJumpLink.LeaderSlot + 1) + " (expected " +
                     ((started ? FleetShips.Slot(last) : leadBefore) + 1) + "); " + DescribeDrives() + ".");
            if (!started)
            {
                Log.Warn("[autotest] The lever didn't start the jump; making " + FleetShips.Describe(last) + " jump the debug way instead.");
                lastSystem.DebugTransitionToRotatingState();
            }
            until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until && !EveryShipTravelling()) yield return new WaitForSeconds(0.5f);
            Log.Info("[autotest] Every ship in the void: " + EveryShipTravelling() + " (" + DescribeDrives() + ").");
            Report("in the void");
            Screenshot("void");
            yield return new WaitForSeconds(4f);

            // A destination, picked on the last ship's map: every map should count it as the fleet's pick.
            GameSessionSector pick = AstralMapLink.PickableForTest(last);
            bool picked = pick != null && AstralMapLink.PressForTest(last, pick);
            Log.Info("[autotest] Picking " + (pick != null ? pick.Name + " (" + pick.Id + ")" : "a mission (none on the map)") + " on " + FleetShips.Describe(last) +
                     "'s astral map: " + picked + ".");
            yield return new WaitForSeconds(2f);
            Log.Info("[autotest] Astral maps after the pick: " + AstralMapLink.DescribeForTest() + ".");
            if (GameSessionSectorManager.Instance == null || GameSessionSectorManager.Instance.DestinationSector == null)
            {
                Log.Warn("[autotest] No destination was picked, so the ships stay in the void.");
                _sequenceDone = true;
                yield break;
            }

            // The lever again: every ship arrives at the destination, side by side.
            Log.Info("[autotest] Pulling " + FleetShips.Describe(last) + "'s lever again: every ship should arrive at the destination.");
            pull.Invoke(lastDrive, null);
            until = Time.unscaledTime + 60f;
            while (Time.unscaledTime < until && !(GameSessionManager.ActiveSector != null && EveryShipIn<VoidJumpInactive>())) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(4f);
            Log.Info("[autotest] Arrived: " + (GameSessionManager.ActiveSector != null && EveryShipIn<VoidJumpInactive>()) + ", in sector " +
                     (GameSessionManager.ActiveSector != null ? GameSessionManager.ActiveSector.Name + " (" + GameSessionManager.ActiveSector.Id + ")" : "none") +
                     " (picked " + pick.Id + ").");
            Report("arrived");
            Screenshot("arrived");
            _sequenceDone = true;
        }

        /// <summary>Runs one of the game's own helpers with a ship as "the player ship".</summary>
        private static void ForShip(PlayerControlledShip ship, string what, Action action)
        {
            ShipRedirect.BeginOverride(ship);
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Warn("[autotest] " + what + " " + FleetShips.Describe(ship) + " failed: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                ShipRedirect.EndOverride();
            }
        }

        /// <summary>Whether every living ship's jump is in that state.</summary>
        private static bool EveryShipIn<T>() where T : VoidJumpState
        {
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship == null || ship.StartedDeath) continue;
                VoidJumpSystem system = ship.GetComponentInChildren<VoidJumpSystem>();
                if (system == null || !(system.ActiveState is T)) return false;
            }
            return true;
        }

        /// <summary>Whether every living ship is travelling through the void.</summary>
        private static bool EveryShipTravelling()
        {
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                if (ship == null || ship.StartedDeath) continue;
                VoidJumpSystem system = ship.GetComponentInChildren<VoidJumpSystem>();
                if (system == null || !(system.ActiveState is VoidJumpTravellingStable || system.ActiveState is VoidJumpTravellingUnstable)) return false;
            }
            return true;
        }

        /// <summary>"ship 1: drive 100 %, powered drive/engine/helm True/True/True, jump VoidJumpInactive": every ship's drive and jump.</summary>
        private static string DescribeDrives()
        {
            var parts = new List<string>();
            foreach (PlayerControlledShip ship in FleetShips.All)
            {
                try
                {
                    VoidDriveModule drive = ship.GetModule<VoidDriveModule>();
                    ShipEngine engine = ship.GetModule<ShipEngine>();
                    Helm helm = ship.GetModule<Helm>();
                    VoidJumpSystem system = ship.GetComponentInChildren<VoidJumpSystem>();
                    parts.Add(FleetShips.Describe(ship) + ": drive " + (drive != null ? (drive.JumpCharge * 100f).ToString("0.0") + " %" : "none") + ", powered drive/engine/helm " +
                              (drive != null && drive.IsPowered) + "/" + (engine != null && engine.IsPowered) + "/" + (helm != null && helm.IsPowered) + ", jump " +
                              (system != null && system.ActiveState != null ? system.ActiveState.GetType().Name : "?"));
                }
                catch (Exception e)
                {
                    parts.Add(FleetShips.Describe(ship) + ": can't be described (" + e.GetType().Name + ")");
                }
            }
            return string.Join("; ", parts.ToArray());
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
                // The hub's ship has no platform or modules to report: one ship that can't be described doesn't end the report.
                try
                {
                    VoidJumpSystem system = ship.GetComponentInChildren<VoidJumpSystem>();
                    CloneTubeModule tubes = ship.GetModule<CloneTubeModule>();
                    text.AppendLine("  " + FleetShips.Describe(ship) + ": view " + FleetShips.ViewId(ship) + ", pos " + ship.Platform.Position + ", sector " +
                                    (ship.Sector != null ? ship.Sector.Id.ToString() : "none") + ", jump " + (system != null && system.ActiveState != null ? system.ActiveState.GetType().Name : "?") +
                                    ", hp " + ship.HitPoints.ToString("0") + ", clone tubes " + (tubes != null ? string.Join(",", tubes.CloneTubes.Select(t => t.Index.ToString()).ToArray()) : "none") +
                                    ", crew " + FleetShips.CrewCount(FleetShips.Slot(ship)) + ", team " + FleetShips.TeamOfSlot(FleetShips.Slot(ship)) + ", faction " + ship.faction +
                                    (ship.StartedDeath ? ", DYING" : ""));
                }
                catch (Exception e)
                {
                    text.AppendLine("  " + FleetShips.Describe(ship) + ": can't be described here (" + e.GetType().Name + ")");
                }
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
                if (FleetShips.Active) text.AppendLine("  astral maps: " + AstralMapLink.DescribeForTest());
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
            text.AppendLine("  crew list: " + CrewList.Describe());
            text.AppendLine("  chat: " + DescribeChat());
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

        private static readonly System.Reflection.FieldInfo ChatPanelField =
            typeof(Gameplay.Chat.TextChat).GetField("_chatUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>For the reports: whether the local text chat exists and shows, the game's UI toggle, and the spectator view.</summary>
        private static string DescribeChat()
        {
            try
            {
                Gameplay.Chat.TextChat chat = Gameplay.Chat.TextChat.Instance;
                var panel = chat != null && ChatPanelField != null ? ChatPanelField.GetValue(chat) as VisualElement : null;
                string shown = panel == null ? "no panel" : (panel.resolvedStyle.display == DisplayStyle.Flex && panel.panel != null ? "shown" : "hidden");
                return (chat != null ? "chat " + shown : "no chat") + ", UI toggle " + UI.MainHUD.MainHUDUI.UIToggleState + ", spectating " + Spectator.Active;
            }
            catch (Exception e)
            {
                return "unknown (" + e.Message + ")";
            }
        }

        private static bool _hubPreviewStarted;

        /// <summary>
        /// Self-test, alone in the hub: the crew lists as a big room shows them (stand-in rows made from the game's own row
        /// template): everyone with the key prompt, then BLUE and RED, then the whole BLUE list's window.
        /// </summary>
        private static System.Collections.IEnumerator HubCrewListSequence()
        {
            CrewList.PreviewForTest("everyone");
            yield return new WaitForSeconds(1f);
            Log.Info("[autotest] crew list: " + CrewList.Describe());
            Screenshot("hub-crew-list");
            yield return new WaitForSeconds(0.5f);
            CrewList.PreviewForTest("teams");
            yield return new WaitForSeconds(1f);
            Log.Info("[autotest] crew list: " + CrewList.Describe());
            Screenshot("hub-team-lists");
            yield return new WaitForSeconds(0.5f);
            PlayerListPanel.OpenPreview(PlayerListPanel.Kind.Blue, 12);
            yield return new WaitForSeconds(1f);
            Screenshot("player-list");
            yield return new WaitForSeconds(0.5f);
            PlayerListPanel.Close();
            CrewList.PreviewForTest(null);
            _sequenceDone = true;
        }

        /// <summary>Self-test, alone in a mission: a full crew's stand-in rows over the cards, stand-in heads, and the enemy cards.</summary>
        private static void CrewListPreview(bool show)
        {
            CrewList.PreviewForTest(show ? "own" : null);
            ShipCards.HeadsForTest = show ? 5 : 0;
            ShipCards.EnemiesInRangeForTest = show && FleetSession.IsPvP;
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
