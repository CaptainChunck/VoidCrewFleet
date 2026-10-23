using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CG.Client;
using CG.Client.Audio;
using CG.Client.Player.HUD;
using CG.Client.UI;
using CG.Game;
using CG.Game.Player;
using CG.GameLoopStateMachine;
using CG.GameLoopStateMachine.GameStates;
using CG.Graphics;
using CG.Ship.Modules;
using CG.Space;
using Cinemachine;
using Gameplay.Atmosphere;
using HarmonyLib;
using Photon.Pun;
using ToolClasses;
using UI.MainHUD;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace VoidCrewFleet
{
    /// <summary>
    /// Spectators of a fleet mission (co-op or Fleet Battle). A player whose ship was destroyed (DEAD) is never re-cloned this
    /// mission: once their character dies they watch; a player who came back to a match whose team has no free seat
    /// (WAITING, PvP) spawns when one opens. Both watch from a third-person camera around a ship:
    /// - Q / E (also the left and right mouse buttons, the arrow keys or a gamepad's shoulder buttons) switch ships; the
    ///   mouse (or the right stick) looks around; the wheel zooms.
    /// - A dead player watches every remaining ship, their own team's first; a waiting player only their own team's ships,
    ///   and one not on a team yet (they could still end up on either) none: they look out into space far above the fleet
    ///   until they get a seat. So nothing they see helps the enemy once they play.
    /// - A dead player first sees their ship's end as the game shows it (its death camera and fade); then the view opens.
    /// The camera is a Cinemachine virtual camera on the game's own camera rig (as the game's own debug camera is), above
    /// the character's and the pilot's cameras and below a ship's death camera. The game's HUD stays hidden while they watch
    /// (closing a menu doesn't bring it back), and a waiting player's loading screen is lifted; a label in the game's HUD
    /// style says who is watched. A waiting player who gets a seat has the HUD back once their character is there.
    /// </summary>
    internal static class Spectator
    {
        public const string Feature = "Spectators";

        private const int CameraPriority = 100;
        private const float DeathSceneSeconds = 15f;

        /// <summary>How far above the middle of the fleet a waiting player without a team looks out from (no ship to make out).</summary>
        private const float NeutralHeight = 20000f;

        private static readonly MethodInfo GsIngameEnded = GameMember.Method(typeof(GSIngame), "OnGameEnded");
        private static readonly FieldInfo ForceRespawnButton = GameMember.Field(typeof(MainMenu), "_forceRespawnButton");
        private static readonly MethodInfo PlayerEnteredRoomMethod = GameMember.Method(typeof(LocalPlayer), "PlayerEnteredRoom");
        private static readonly MethodInfo EnteredRoomMethod = AccessTools.Method(typeof(Spectator), "EnteredRoom");

        private static GameObject _rig;
        private static CinemachineVirtualCamera _camera;
        private static bool _active;
        private static int _watching = -1;
        private static float _yaw;
        private static float _pitch = 12f;
        private static float _distance = 110f;
        private static float _deadSince = -1f;
        private static bool _endHandled;
        private static UIDocument _document;
        private static Label _title;
        private static Label _hint;
        private static bool _labelUnderMenu;

        /// <summary>The game's HUD canvas the view switched off, for a waiting player who gets a seat.</summary>
        private static Canvas _hiddenCanvas;

        /// <summary>A waiting player got a seat: the game's HUD comes back once their character is there.</summary>
        private static bool _restoreHud;

        /// <summary>The ships the local player may watch, found once a frame (FindShips).</summary>
        private static readonly List<int> ShipList = new List<int>();

        // What the label says, as last written: it is only written again when one of these changes.
        private static bool _labelSet;
        private static CrewState _labelState;
        private static int _labelTeam;
        private static int _labelSlot;
        private static int _labelShips;

        /// <summary>The local player is watching right now.</summary>
        public static bool Active
        {
            get { return _active; }
        }

        private static int LocalActor
        {
            get { return FleetShips.LocalActor; }
        }

        /// <summary>
        /// The mission's crews are set, so players can be spectators: a co-op mission with several ships, or a Fleet Battle
        /// whose teams are confirmed.
        /// </summary>
        public static bool CrewsSet
        {
            get
            {
                FleetPlan plan = FleetSession.Current;
                if (plan == null || plan.ShipCount < 2) return false;
                if (plan.Mode == FleetMode.Coop) return true;
                return plan.Mode == FleetMode.PvP && PvpTeams.Locked;
            }
        }

        /// <summary>
        /// What a player is doing for the others: still playing while their character lives (as every client sees it), even
        /// when their ship is gone; else their spectator state (Dead or Waiting).
        /// </summary>
        public static CrewState WatchState(int actor)
        {
            CrewEntry entry;
            if (!CrewsSet || !FleetShips.TryGetEntry(actor, out entry) || !entry.IsSpectator) return CrewState.Playing;
            Player character = actor == LocalActor ? (Player)LocalPlayer.Instance : FleetShips.CharacterOf(actor);
            return character != null && !PlayerLife.IsDead(character) ? CrewState.Playing : entry.State;
        }

        // ---------------------------------------------------------------- every frame

        public static void Frame()
        {
            try
            {
                CrewEntry entry;
                bool want = ShouldWatch(out entry);
                if (want && !_active) Begin(entry);
                else if (!want && _active) End();
                if (_active) Follow(entry);
                else if (_restoreHud) RestoreHudWhenPlaying();
                HandleMatchEnd();
            }
            catch (Exception e)
            {
                Log.Failure("Running the spectator view", e);
            }
        }

        private static bool ShouldWatch(out CrewEntry entry)
        {
            entry = default(CrewEntry);
            if (!CrewsSet || GameSessionManager.InHub || !FleetRuntime.GameReady)
            {
                _deadSince = -1f;
                return false;
            }
            ClientGame game = ClientGame.Current;
            if (game == null || game.GameState == ClientGameState.Ended) return false;
            if (!FleetShips.TryGetEntry(LocalActor, out entry) || !entry.IsSpectator)
            {
                _deadSince = -1f;
                return false;
            }
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null)
            {
                // No character (a player who joined while their team was full, or came back after their ship was lost): the
                // view opens once the game has asked the host for a spawn, which keeps them in the host's queue.
                GameStateMachine machine = GameStateMachine.Instance;
                var spawning = machine != null ? machine.CurrentState as GSSpawn : null;
                return spawning != null && spawning.requestedSpawn;
            }
            // A spectator with a living character (aboard another ship when theirs was lost) plays on until they die.
            if (!me.IsDead) return false;
            if (_deadSince < 0f) _deadSince = Time.unscaledTime;
            // Their ship's end plays out first (its death camera and fade), for as long as it lasts.
            PlayerControlledShip lost = FleetShips.BySlot(entry.Slot);
            return lost == null || Time.unscaledTime - _deadSince > DeathSceneSeconds;
        }

        private static void Begin(CrewEntry entry)
        {
            _active = true;
            _restoreHud = false;
            _labelSet = false;
            EnsureRig();
            _rig.SetActive(true);
            _camera.Priority = CameraPriority;
            _watching = -1;
            FindShips(entry);
            Watch(ShipList.Count > 0 ? ShipList[0] : -1, true);
            try
            {
                if (Singleton<FadeController>.Instance != null) Singleton<FadeController>.Instance.FadeToClear();
            }
            catch (Exception e)
            {
                Log.Failure("Fading the spectator view in", e);
            }
            HideGameScreens(entry.State);
            ShowShipsFromSpace();
            try
            {
                if (SnapshotController.Instance != null) SnapshotController.Instance.OnSpawnSequenceCompleted();
            }
            catch
            {
                // The muffled "dying" mix stays: harmless.
            }
            _labelUnderMenu = false;
            ShowLabel(true);
            Log.Info("Spectating (" + entry.State + "): " + (_watching >= 0 ? FleetShips.Describe(FleetShips.BySlot(_watching)) : "no ship until they are on a team") + ".");
        }

        private static void End()
        {
            _active = false;
            _deadSince = -1f;
            _labelSet = false;
            if (_rig != null)
            {
                _camera.Priority = 0;
                _rig.SetActive(false);
            }
            FleetShips.Watched = null;
            ShowLabel(false);
            // A waiting player who got a seat plays now.
            CrewEntry entry;
            _restoreHud = CrewsSet && !GameSessionManager.InHub && FleetShips.TryGetEntry(LocalActor, out entry) && !entry.IsSpectator;
            Log.Info("Spectating ended.");
        }

        /// <summary>
        /// The game's screens over the view: a dead player's HUD and death cover (the HUD canvas stays off, as the ship's
        /// death sequence left it), or a waiting player's loading screen and queue panel.
        /// </summary>
        private static void HideGameScreens(CrewState state)
        {
            try
            {
                HUD_LayoutContainer layout = UnityEngine.Object.FindObjectOfType<HUD_LayoutContainer>();
                Canvas canvas = layout != null ? layout.GetComponentInParent<Canvas>() : null;
                if (canvas != null)
                {
                    canvas.enabled = false;
                    _hiddenCanvas = canvas;
                }
                if (ViewEventBus.Instance != null)
                {
                    ViewEventBus.Instance.OnUIToggle.Publish(false);
                    ViewEventBus.Instance.OnLocalPlayerSpawnQueueChanged.Publish(false);
                }
                Scene loading = SceneManager.GetSceneByBuildIndex(CloneStarConstants.LoadingScreenSceneIndex);
                if (loading.IsValid() && loading.isLoaded)
                {
                    foreach (GameObject root in loading.GetRootGameObjects()) root.SetActive(false);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Lifting the game's screens for the spectator view", e);
            }
        }

        /// <summary>
        /// A waiting player who got a seat: once their character is there, the HUD the view hid comes back (nothing in the
        /// game shows it again by itself, short of closing a menu).
        /// </summary>
        private static void RestoreHudWhenPlaying()
        {
            if (GameSessionManager.InHub || !CrewsSet)
            {
                _restoreHud = false;
                _hiddenCanvas = null;
                return;
            }
            LocalPlayer me = LocalPlayer.Instance;
            if (me == null || me.IsDead) return;
            _restoreHud = false;
            try
            {
                if (_hiddenCanvas != null) _hiddenCanvas.enabled = true;
                _hiddenCanvas = null;
                if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnUIToggle.Publish(true);
            }
            catch (Exception e)
            {
                Log.Failure("Bringing the game's HUD back after the spectator view", e);
            }
        }

        /// <summary>
        /// The game shows its HUD again when the last menu closes (its handler turns it back on when its toggle state says
        /// so, and opening a menu sets that state). While the view is open, closing a menu leaves the HUD hidden.
        /// </summary>
        [GamePatch(Feature, "UI.MainHUD.MainHUDUI", "OnMenuVisibilityChanged", PatchKind.Prefix, Optional = true)]
        private static void MenuVisibilityPrefix(bool menuVisible)
        {
            if (_active && !menuVisible) MainHUDUI.UIToggleState = false;
        }

        // ---------------------------------------------------------------- the camera

        private static void EnsureRig()
        {
            if (_rig != null) return;
            _rig = new GameObject("__FleetSpectatorCamera");
            _rig.layer = 0;
            UnityEngine.Object.DontDestroyOnLoad(_rig);
            _camera = _rig.AddComponent<CinemachineVirtualCamera>();
            LensSettings lens = _camera.m_Lens;
            lens.FieldOfView = 70f;
            lens.NearClipPlane = 0.1f;
            lens.FarClipPlane = 60000f;
            _camera.m_Lens = lens;
            _camera.Priority = 0;
            // Room-based sound and atmosphere follow the view, as for the game's own debug camera.
            _rig.AddComponent<RoomPoint>();
            _rig.SetActive(false);
        }

        /// <summary>
        /// The ships this player may watch, into ShipList: living ones, their own team's first. A waiting player only watches
        /// their own team's, and one not on a team yet none.
        /// </summary>
        private static void FindShips(CrewEntry entry)
        {
            ShipList.Clear();
            if (entry.State == CrewState.Waiting && entry.Team < 0) return;
            List<int> alive = FleetShips.AliveSlots();
            foreach (int slot in alive)
            {
                if (FleetShips.TeamOfSlot(slot) == entry.Team) ShipList.Add(slot);
            }
            if (entry.State == CrewState.Waiting) return;
            foreach (int slot in alive)
            {
                if (FleetShips.TeamOfSlot(slot) != entry.Team) ShipList.Add(slot);
            }
        }

        private static void Watch(int slot, bool turnBehind)
        {
            _watching = slot;
            PlayerControlledShip ship = FleetShips.BySlot(slot);
            FleetShips.Watched = ship;
            if (ship != null && turnBehind) _yaw = ship.transform.eulerAngles.y;
        }

        /// <summary>The middle of the living ships; false when there are none.</summary>
        private static bool FleetMiddle(out Vector3 middle)
        {
            middle = Vector3.zero;
            int count = 0;
            foreach (int slot in FleetShips.AliveSlots())
            {
                PlayerControlledShip ship = FleetShips.BySlot(slot);
                if (ship == null) continue;
                middle += ship.transform.position;
                count++;
            }
            if (count == 0) return false;
            middle /= count;
            return true;
        }

        private static void Follow(CrewEntry entry)
        {
            FindShips(entry);
            if (ShipList.Count == 0)
            {
                if (_watching >= 0) Watch(-1, false);
                // No ship left to watch (the match is ending), unless they look out into space until they are on a team.
                if (entry.State != CrewState.Waiting || entry.Team >= 0) return;
            }
            else if (!ShipList.Contains(_watching)) Watch(ShipList[0], true);

            // Switching ships and looking around (not while a menu is open: its clicks are for its buttons).
            int step = 0;
            bool menu = MenuScreenController.IsOpen();
            // The label steps aside for the game's menus (it is drawn above them).
            if (menu != _labelUnderMenu)
            {
                _labelUnderMenu = menu;
                ShowLabel(!menu);
            }
            Keyboard keyboard = menu ? null : Keyboard.current;
            Mouse mouse = menu ? null : Mouse.current;
            Gamepad pad = menu ? null : Gamepad.current;
            if (keyboard != null && (keyboard.eKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame)) step = 1;
            if (keyboard != null && (keyboard.qKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame)) step = -1;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) step = 1;
            if (mouse != null && mouse.rightButton.wasPressedThisFrame) step = -1;
            if (pad != null && pad.rightShoulder.wasPressedThisFrame) step = 1;
            if (pad != null && pad.leftShoulder.wasPressedThisFrame) step = -1;
            if (step != 0 && ShipList.Count > 1)
            {
                int index = (ShipList.IndexOf(_watching) + step + ShipList.Count) % ShipList.Count;
                Watch(ShipList[index], true);
            }

            // Looking around and zooming.
            Vector2 look = Vector2.zero;
            if (mouse != null) look += mouse.delta.ReadValue() * 0.12f;
            if (pad != null) look += pad.rightStick.ReadValue() * (120f * Time.unscaledDeltaTime);
            _yaw += look.x;
            _pitch = Mathf.Clamp(_pitch - look.y, -75f, 75f);
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) _distance = Mathf.Clamp(_distance * (1f - Mathf.Sign(scroll) * 0.1f), 40f, 400f);
            }

            PlayerControlledShip ship = FleetShips.BySlot(_watching);
            if (ship != null)
            {
                Transform target = ship.transform;
                try
                {
                    Helm helm = ship.GetModule<Helm>();
                    if (helm != null && helm.ExternalCamera != null) target = helm.ExternalCamera.transform;
                }
                catch
                {
                    // the ship's own centre then
                }
                Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
                _rig.transform.SetPositionAndRotation(target.position - rotation * Vector3.forward * _distance, rotation);
            }
            else
            {
                Vector3 middle;
                if (!FleetMiddle(out middle)) return;
                // Level or looking up, never down at the fleet.
                _rig.transform.SetPositionAndRotation(middle + Vector3.up * NeutralHeight, Quaternion.Euler(-Mathf.Abs(_pitch), _yaw, 0f));
            }
            UpdateLabel(entry, ship != null ? _watching : -1);
        }

        // ---------------------------------------------------------------- the label

        private static void ShowLabel(bool show)
        {
            if (show && _document == null)
            {
                _document = Ui.CreateOverlay("Void Crew Fleet spectator", 4000);
                if (_document != null && _document.rootVisualElement != null)
                {
                    VisualElement root = _document.rootVisualElement;
                    root.pickingMode = PickingMode.Ignore;
                    Ui.UseGameStyles(root);
                    var box = new VisualElement { pickingMode = PickingMode.Ignore };
                    box.style.position = Position.Absolute;
                    box.style.left = 0;
                    box.style.right = 0;
                    box.style.bottom = 70;
                    box.style.alignItems = Align.Center;
                    _title = Ui.HudLabel("", 30, Ui.GameOrange);
                    _title.style.unityFontStyleAndWeight = FontStyle.Bold;
                    _hint = Ui.HudLabel("", 20, Ui.GameBrightGrey);
                    _hint.style.marginTop = 4;
                    box.Add(_title);
                    box.Add(_hint);
                    root.Add(box);
                }
            }
            if (_document != null && _document.rootVisualElement != null)
                _document.rootVisualElement.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>The label for the watched ship (slot -1: none, a waiting player without a team); written only when it changes.</summary>
        private static void UpdateLabel(CrewEntry entry, int slot)
        {
            if (_title == null) return;
            int ships = ShipList.Count;
            if (_labelSet && _labelState == entry.State && _labelTeam == entry.Team && _labelSlot == slot && _labelShips == ships) return;
            _labelSet = true;
            _labelState = entry.State;
            _labelTeam = entry.Team;
            _labelSlot = slot;
            _labelShips = ships;
            if (slot < 0)
            {
                _title.text = "WAITING FOR A PLACE";
                _hint.text = "MOUSE: LOOK AROUND";
                return;
            }
            string watched = FleetSlots.Name(slot, FleetSession.IsPvP);
            FleetPlan plan = FleetSession.Current;
            ShipPlan shipPlan = plan != null ? plan.Ship(slot) : null;
            if (shipPlan != null && !shipPlan.Type.IsNull) watched += " (" + FleetSlots.TypeName(shipPlan.Type) + ")";
            _title.text = entry.State == CrewState.Dead
                ? "YOUR SHIP IS LOST · WATCHING " + watched
                : "WAITING FOR A PLACE" + (entry.Team >= 0 ? " ON THE " + PvpTeams.TeamName(entry.Team) + " TEAM" : "") + " · WATCHING " + watched;
            _hint.text = ships > 1 ? "Q / E: OTHER SHIP     MOUSE: LOOK AROUND     WHEEL: ZOOM" : "MOUSE: LOOK AROUND     WHEEL: ZOOM";
        }

        // ---------------------------------------------------------------- staying a spectator

        /// <summary>A player whose ship is lost can't start re-cloning (the host would never grant it anyway).</summary>
        [GamePatch(Feature, "ClonetubeRespawn", "CanStartAbility", PatchKind.Postfix)]
        private static void CanRespawnPostfix(ref bool __result)
        {
            if (__result && LocalShipLost()) __result = false;
        }

        /// <summary>The local player's ship was destroyed this mission (DEAD: no respawn), whether or not their character lives.</summary>
        private static bool LocalShipLost()
        {
            CrewEntry entry;
            return CrewsSet && FleetShips.TryGetEntry(LocalActor, out entry) && entry.State == CrewState.Dead;
        }

        /// <summary>
        /// The escape menu's FORCE RESPAWN is off for a spectator, and for a player whose ship is lost (alive aboard another
        /// ship, pressing it would only make them a spectator at once). The game's own check reads the local character, which
        /// a waiting spectator doesn't have.
        /// </summary>
        [GamePatch(Feature, "MainMenu", "TryEnableRespawn", PatchKind.Prefix)]
        private static bool TryEnableRespawnPrefix(MainMenu __instance)
        {
            if (LocalPlayer.Instance != null && !_active && !LocalShipLost()) return true;
            DisableForceRespawn(__instance);
            return false;
        }

        /// <summary>The button can come back on while the menu is open (when its cooldown ends): then pressing it does nothing for them.</summary>
        [GamePatch(Feature, "MainMenu", "ForceRespawn", PatchKind.Prefix, Optional = true)]
        private static bool ForceRespawnPrefix(MainMenu __instance)
        {
            if (LocalPlayer.Instance != null && !_active && !LocalShipLost()) return true;
            DisableForceRespawn(__instance);
            return false;
        }

        private static void DisableForceRespawn(MainMenu menu)
        {
            try
            {
                var button = ForceRespawnButton != null ? ForceRespawnButton.GetValue(menu) as UnityEngine.UIElements.Button : null;
                if (button != null) button.SetEnabled(false);
            }
            catch
            {
                // nothing to switch off
            }
        }

        /// <summary>Leaving a match destroys the local character, which a waiting spectator doesn't have.</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "DestroyLocalCharacter", PatchKind.Prefix)]
        private static bool DestroyLocalCharacterPrefix()
        {
            return LocalPlayer.Instance != null;
        }

        /// <summary>
        /// The host spawning a newcomer passes them to the local character (LocalPlayer.PlayerEnteredRoom), which raises the
        /// Opsive event that sends a newcomer every character's and pooled object's state. A host without a character (a
        /// spectator who became host) would throw there, half-way through the spawn, and the newcomer's character would never
        /// be switched on for the host. The call goes through EnteredRoom, which raises the same event without a character.
        /// </summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "SpawnPlayer", PatchKind.Transpiler, Optional = true)]
        private static IEnumerable<CodeInstruction> EnteredRoomTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && PlayerEnteredRoomMethod != null &&
                    Equals(instruction.operand, PlayerEnteredRoomMethod))
                {
                    var call = new CodeInstruction(OpCodes.Call, EnteredRoomMethod);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);
                    yield return call;
                    replaced++;
                    continue;
                }
                yield return instruction;
            }
            if (replaced == 0) Log.Warn("Spectators: SpawnSystem.SpawnPlayer no longer passes a newcomer to the local character; the game may have changed.");
        }

        /// <summary>LocalPlayer.PlayerEnteredRoom, also for a host without a character (all it does is raise the Opsive event).</summary>
        private static void EnteredRoom(LocalPlayer local, Photon.Realtime.Player owner, GameObject character)
        {
            if (local != null) local.PlayerEnteredRoom(owner, character);
            else Opsive.Shared.Events.EventHandler.ExecuteEvent("OnPlayerEnteredRoom", owner, character);
        }

        /// <summary>A waiting spectator stays in the spawn state as long as it takes: no "the host didn't answer" timeout.</summary>
        [GamePatch(Feature, "CG.GameLoopStateMachine.GameStates.GSSpawn", "UpdateState", PatchKind.Prefix)]
        private static void SpawnStatePrefix(ref float ___timeout)
        {
            if (_active || WaitingForSeat()) ___timeout = 0f;
        }

        private static bool WaitingForSeat()
        {
            CrewEntry entry;
            return CrewsSet && FleetShips.TryGetEntry(LocalActor, out entry) && entry.IsSpectator;
        }

        /// <summary>No "you are in the queue" panel over the spectator view.</summary>
        [GamePatch(Feature, "CG.Game.Player.SpawnSystem", "NotifyQueuedPlayer", PatchKind.Prefix)]
        private static bool QueuedPrefix()
        {
            return !_active && !WaitingForSeat();
        }

        /// <summary>
        /// A spectator without a character (waiting, or back after their ship was lost) is still in the spawn state, which
        /// ignores the end of the mission: end it here the way the game does (its in-game state's own end handler, which
        /// needs no character).
        /// </summary>
        private static void HandleMatchEnd()
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.GameState != ClientGameState.Ended)
            {
                _endHandled = false;
                return;
            }
            if (_endHandled || LocalPlayer.Instance != null || FleetSession.ShipCount < 2) return;
            GameStateMachine machine = GameStateMachine.Instance;
            if (machine == null || !(machine.CurrentState is GSSpawn) || GsIngameEnded == null) return;
            _endHandled = true;
            try
            {
                Scene loading = SceneManager.GetSceneByBuildIndex(CloneStarConstants.LoadingScreenSceneIndex);
                if (loading.IsValid() && loading.isLoaded) SceneManager.UnloadSceneAsync(loading);
                GsIngameEnded.Invoke(machine.GetState<GSIngame>(), new object[] { ClientGameState.Ended });
                Log.Info("The mission ended while this player watched without a character.");
            }
            catch (Exception e)
            {
                Log.Failure("Ending the match for a waiting spectator", e);
            }
        }

        // ---------------------------------------------------------------- what the view shows

        private static readonly FieldInfo OcclusionNodesField = GameMember.Field(typeof(LocalPlayerOcclusionController), "_occlusionNodes");

        /// <summary>
        /// The ships' hidden parts (interior walls, exterior plates) are shown or hidden for where the local player is. A
        /// spectator is in open space as far as that goes (and may have no character, which the game's code would trip on).
        /// </summary>
        [GamePatch(Feature, "CG.Graphics.LocalPlayerOcclusionController", "UpdateOcclusion", PatchKind.Prefix)]
        private static bool OcclusionPrefix(LocalPlayerOcclusionController __instance)
        {
            if (!_active && LocalPlayer.Instance != null) return true;
            ShowFromSpace(__instance);
            return false;
        }

        /// <summary>
        /// When the view opens: every ship's hidden parts as from open space, at once. The controllers only update on the
        /// local player's own moves, so a player who died aboard a ship would otherwise watch with that room's walls and plates.
        /// </summary>
        internal static void ShowShipsFromSpace()
        {
            try
            {
                foreach (LocalPlayerOcclusionController controller in UnityEngine.Object.FindObjectsOfType<LocalPlayerOcclusionController>()) ShowFromSpace(controller);
            }
            catch (Exception e)
            {
                Log.Failure("Showing the ships to a spectator", e);
            }
        }

        /// <summary>One occlusion controller's hidden parts as the game shows them to a player in open space.</summary>
        private static void ShowFromSpace(LocalPlayerOcclusionController controller)
        {
            try
            {
                var nodes = OcclusionNodesField != null ? OcclusionNodesField.GetValue(controller) as List<IOcclusionNode> : null;
                if (nodes == null) return;
                foreach (IOcclusionNode node in nodes) node.ToggleRenderers(!node.HideOnLocalPlayerIsInSpace);
            }
            catch (Exception e)
            {
                Log.Failure("Showing the ships to a spectator", e);
            }
        }

        /// <summary>
        /// Every ship's hull parts register with one occlusion controller, the last ship's to load. When that ship is lost,
        /// another ship's controller takes them all over, or the remaining ships' parts would stop showing and hiding.
        /// </summary>
        public static void OnShipLost(PlayerControlledShip dying)
        {
            try
            {
                LocalPlayerOcclusionController current = LocalPlayerOcclusionController.Instance;
                if (current == null || FleetShips.OwnerOf(current) != dying || OcclusionNodesField == null) return;
                PropertyInfo instance = typeof(LocalPlayerOcclusionController).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                MethodInfo setter = instance != null ? instance.GetSetMethod(true) : null;
                if (setter == null) return;
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship == null || ship == dying || ship.StartedDeath) continue;
                    LocalPlayerOcclusionController controller = ship.GetComponentInChildren<LocalPlayerOcclusionController>(true);
                    if (controller == null) continue;
                    setter.Invoke(null, new object[] { controller });
                    var nodes = OcclusionNodesField.GetValue(current) as List<IOcclusionNode>;
                    int moved = 0;
                    if (nodes != null)
                    {
                        foreach (IOcclusionNode node in nodes.ToList())
                        {
                            var component = node as Component;
                            if (component == null || FleetShips.OwnerOf(component) == dying) continue;
                            controller.Register(node);
                            moved++;
                        }
                    }
                    Log.Info("The hull parts of the remaining ships (" + moved + ") now answer to " + FleetShips.Describe(ship) + "'s occlusion controller.");
                    return;
                }
            }
            catch (Exception e)
            {
                Log.Failure("Handing the ships' hull parts to another occlusion controller", e);
            }
        }
    }
}
