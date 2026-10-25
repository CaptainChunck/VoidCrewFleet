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
using Gameplay.Chat;
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
    /// mission: once their character dies they watch; a player who came back to a match whose team has no free seat, or who
    /// found every crew full (WAITING; in co-op too, where all ships are their team's), spawns when one opens. Both watch from
    /// a third-person camera around a ship:
    /// - Q / E (also the left and right mouse buttons, the arrow keys or a gamepad's shoulder buttons) switch ships; the
    ///   mouse (or the right stick) looks around; the wheel zooms. None of them while the player types in the text chat.
    /// - A dead player watches every remaining ship, their own team's first; a waiting player only their own team's ships,
    ///   and one not on a team yet (they could still end up on either) none: they look out into space far above the fleet
    ///   until they get a seat. So nothing they see helps the enemy once they play.
    /// - A dead player first sees their ship's end as the game shows it (its death camera and fade); then the view opens.
    /// The camera is a Cinemachine virtual camera on the game's own camera rig (as the game's own debug camera is), above
    /// the character's and the pilot's cameras and below a ship's death camera. The game's HUD and markers stay hidden while
    /// they watch (closing a menu doesn't bring them back), but not its text chat: spectators talk among themselves. A
    /// waiting player's loading screen is lifted. A label in the game's HUD style says who is watched; for a spectator
    /// without a character, who has no chat, it also shows Fleet's notices. A waiting player who gets a seat has the HUD
    /// back once their character is there.
    /// </summary>
    internal static class Spectator
    {
        public const string Feature = "Spectators";

        private const int CameraPriority = 100;
        private const float DeathSceneSeconds = 15f;

        /// <summary>How far above the middle of the fleet a waiting player without a team looks out from (no ship to make out).</summary>
        private const float NeutralHeight = 20000f;

        /// <summary>How often the view reads the living ships again (FleetShips makes a new list for each read).</summary>
        private const float ShipReadSeconds = 0.25f;

        /// <summary>How long a notice stays over the label (the last NoticeFadeSeconds of it fading out), and how many show at once.</summary>
        private const float NoticeSeconds = 10f;
        private const float NoticeFadeSeconds = 2f;
        private const int NoticeLines = 3;

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
        private static float _nextPrepare;
        private static bool _endHandled;
        private static UIDocument _document;
        private static Label _title;
        private static Label _hint;
        private static bool _labelUnderMenu;

        /// <summary>The game's HUD canvas the view switched off, switched on again with the rest of the HUD (RestoreHudWhenPlaying).</summary>
        private static Canvas _hiddenCanvas;

        /// <summary>The view has ended: the game's HUD comes back once a living character is there (a waiting player who got a seat).</summary>
        private static bool _restoreHud;

        /// <summary>The living ships' slots, lowest first, as last read (ReadShips).</summary>
        private static readonly List<int> LivingSlots = new List<int>();

        /// <summary>When LivingSlots was last read (Time.unscaledTime).</summary>
        private static float _shipsReadAt = float.NegativeInfinity;

        /// <summary>The ships the local player may watch, found once a frame from LivingSlots (FindShips).</summary>
        private static readonly List<int> ShipList = new List<int>();

        /// <summary>The point the camera circles (Target), and the ship it was found for.</summary>
        private static Transform _target;
        private static PlayerControlledShip _targetShip;

        // What the label says, as last written: it is only written again when one of these changes.
        private static bool _labelSet;
        private static CrewState _labelState;
        private static int _labelTeam;
        private static int _labelSlot;
        private static int _labelShips;

        // Fleet's notices over the label, for a spectator without a text chat: the lines (oldest first) and when each goes.
        private static readonly Label[] NoticeLabels = new Label[NoticeLines];
        private static readonly float[] NoticeUntil = new float[NoticeLines];
        private static bool _noticesShown;

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
                if (_active)
                {
                    Follow(entry);
                    FadeNotices();
                }
                else
                {
                    if (_restoreHud) RestoreHudWhenPlaying();
                    Prepare();
                }
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
            ReadShips(true);
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
            _target = null;
            _targetShip = null;
            ClearNotices();
            ShowLabel(false);
            // The HUD comes back once a living character is there: a waiting player who got a seat. A dead player's stays off
            // to the end of the mission, as after the game's own ship loss.
            _restoreHud = CrewsSet && !GameSessionManager.InHub;
            Log.Info("Spectating ended.");
        }

        /// <summary>
        /// The game's screens over the view: a dead player's HUD and death cover (the HUD canvas stays off, as the ship's
        /// death sequence left it), or a waiting player's loading screen and queue panel. The HUD is hidden without the game's
        /// UI toggle, which goes back on (the ship's death sequence switched it off): the text chat follows it, and the game
        /// lets nobody open the chat while it is off. KeepHudHidden and the patches below keep the toggle's other screens off.
        /// </summary>
        private static void HideGameScreens(CrewState state)
        {
            try
            {
                // With a menu open, the game publishes the toggle itself as the menu closes, if the toggle is on.
                if (MenuScreenController.IsOpen()) MainHUDUI.UIToggleState = true;
                else if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnUIToggle.Publish(true);
            }
            catch (Exception e)
            {
                Log.Failure("Switching the game's text chat back on for the spectator view", e);
            }
            try
            {
                HUD_LayoutContainer layout = HUD_LayoutContainer.Instance;
                Canvas canvas = layout != null ? layout.GetComponentInParent<Canvas>() : null;
                if (canvas != null)
                {
                    canvas.enabled = false;
                    _hiddenCanvas = canvas;
                }
                MainHUDUI hud = MainHUDUI.Instance;
                KeepHudHidden(hud != null ? hud.GetComponent<UIDocument>() : null);
                if (ViewEventBus.Instance != null) ViewEventBus.Instance.OnLocalPlayerSpawnQueueChanged.Publish(false);
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
        /// The HUD's own document hidden (its panels, missions, hints, subtitles and reticle), object outlines off, and the
        /// voice panel showing who speaks: the game's HUD as with its UI toggle off, though the toggle is on.
        /// </summary>
        private static void KeepHudHidden(UIDocument hud)
        {
            VisualElement root = hud != null ? hud.rootVisualElement : null;
            if (root != null) root.style.display = DisplayStyle.None;
            Knife.HDRPOutline.Core.OutlineObject.ForceAllDisabled = true;
            // The game shows the voice panel whenever its HUD (with its own speaking marks) is off.
            if (ViewEventBus.Instance != null) ViewEventBus.Instance.VoipPanelConditionsChanged.Publish(VoipPanelController.VoipPanelShowStates.MainHUDDisabled, true);
        }

        /// <summary>
        /// After the view: once a living character's HUD is up (past its boot sequence, after which the game switches it on
        /// itself), what the view kept off comes back: the HUD canvas, and the HUD, the markers and the rest through the game's
        /// UI toggle. A dead player's character stays dead, so their HUD stays off until they leave the mission.
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
            // A new character's HUD is off until its boot sequence ends.
            MainHUDUI hud = MainHUDUI.Instance;
            if (hud != null && !hud.enabled) return;
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
        /// The game switches its HUD back on with its UI toggle: when a menu closes, with the toggle-UI key, and as the view
        /// opens. While the view is open the HUD stays hidden, and the toggle stays on for the text chat.
        /// </summary>
        [GamePatch(Feature, "UI.MainHUD.MainHUDUI", "EnableUI", PatchKind.Postfix, Optional = true)]
        private static void HudEnablePostfix(UIDocument ___doc)
        {
            if (!_active) return;
            try
            {
                KeepHudHidden(___doc);
            }
            catch (Exception e)
            {
                Log.Failure("Keeping the game's HUD hidden for the spectator view", e);
            }
        }

        /// <summary>
        /// The game's other screens that follow its UI toggle stay off while the view is open, as they were with the toggle
        /// off: the HUD's context info (which shows and hides the HUD's whole document, also as menus open and close), the
        /// markers, the turret's and the pilot's screens, and the HUD's toggled objects. The text chat, which follows the
        /// toggle too, is left to it.
        /// </summary>
        [GamePatch(Feature, "ContextInfoDisplay", "EnableUI", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "UI.ObjectMarker.ObjectMarkerViewer", "EnableUI", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "UI.PilotHUD.WeaponHUD", "EnableUI", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "UI.PilotHUD.PilotHUD", "EnableUI", PatchKind.Prefix, Optional = true)]
        [GamePatch(Feature, "Code.Gameplay.Player.HUD.HUD_ToggleBehavior", "EnableUI", PatchKind.Prefix, Optional = true)]
        private static void ScreenTogglePrefix(ref bool enable)
        {
            // Nothing here can throw: it only reads the view's state.
            if (_active) enable = false;
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
        /// Reads the living ships into LivingSlots when the last read is ShipReadSeconds old, or at once: FleetShips makes a new
        /// list for each read, which the view would otherwise do every frame. A ship that starts dying leaves the view within
        /// that time.
        /// </summary>
        private static void ReadShips(bool now)
        {
            float time = Time.unscaledTime;
            if (!now && time - _shipsReadAt < ShipReadSeconds) return;
            _shipsReadAt = time;
            LivingSlots.Clear();
            foreach (int slot in FleetShips.AliveSlots()) LivingSlots.Add(slot);
        }

        /// <summary>
        /// The ships this player may watch, into ShipList: living ones, their own team's first. A waiting player only watches
        /// their own team's, and one not on a team yet none.
        /// </summary>
        private static void FindShips(CrewEntry entry)
        {
            ShipList.Clear();
            if (entry.State == CrewState.Waiting && entry.Team < 0) return;
            foreach (int slot in LivingSlots)
            {
                if (FleetShips.TeamOfSlot(slot) == entry.Team) ShipList.Add(slot);
            }
            if (entry.State == CrewState.Waiting) return;
            foreach (int slot in LivingSlots)
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

        /// <summary>
        /// The point the camera circles around a ship: its helm's outside camera (where the pilot's third-person view turns),
        /// else the ship's own centre. Looked up once for each ship watched.
        /// </summary>
        private static Transform Target(PlayerControlledShip ship)
        {
            if (ship == _targetShip && _target != null) return _target;
            _targetShip = ship;
            _target = ship.transform;
            try
            {
                Helm helm = ship.GetModule<Helm>();
                if (helm != null && helm.ExternalCamera != null) _target = helm.ExternalCamera.transform;
            }
            catch
            {
                // the ship's own centre then
            }
            return _target;
        }

        /// <summary>The middle of the living ships; false when there are none.</summary>
        private static bool FleetMiddle(out Vector3 middle)
        {
            middle = Vector3.zero;
            int count = 0;
            foreach (int slot in LivingSlots)
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

        /// <summary>
        /// The player is typing in the game's text chat. The chat's TextingEnabled is set and cleared with its input line; its
        /// _chatOpened is not cleared when a message is sent (only by Escape), so it would keep the view's controls off.
        /// </summary>
        private static bool Typing
        {
            get
            {
                TextChat chat = TextChat.Instance;
                return chat != null && chat.TextingEnabled;
            }
        }

        private static void Follow(CrewEntry entry)
        {
            ReadShips(false);
            FindShips(entry);
            if (ShipList.Count == 0)
            {
                if (_watching >= 0) Watch(-1, false);
                // No ship left to watch (the match is ending), unless they look out into space until they are on a team.
                if (entry.State != CrewState.Waiting || entry.Team >= 0) return;
            }
            else if (!ShipList.Contains(_watching)) Watch(ShipList[0], true);

            // Switching ships and looking around: not while a menu is open (its clicks are for its buttons), nor while the
            // player types in the text chat (the keys and the mouse are for their message).
            int step = 0;
            bool menu = MenuScreenController.IsOpen();
            // The label steps aside for the game's menus (it is drawn above them).
            if (menu != _labelUnderMenu)
            {
                _labelUnderMenu = menu;
                ShowLabel(!menu);
            }
            bool busy = menu || Typing;
            Keyboard keyboard = busy ? null : Keyboard.current;
            Mouse mouse = busy ? null : Mouse.current;
            Gamepad pad = busy ? null : Gamepad.current;
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
                Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
                _rig.transform.SetPositionAndRotation(Target(ship).position - rotation * Vector3.forward * _distance, rotation);
            }
            else if (_watching >= 0)
            {
                // The watched ship is gone since the ships were last read: they are read again next frame, and the view stays
                // where it is meanwhile.
                _shipsReadAt = float.NegativeInfinity;
                return;
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
            if (show) MakeLabel();
            if (_document != null && _document.rootVisualElement != null)
                _document.rootVisualElement.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// In a fleet mission, ahead of the view: its camera and label, made hidden, so opening the view doesn't make them too
        /// (the label's overlay alone holds up a frame by about 20 ms). Only once the game itself is up (the overlay copies its
        /// screens' settings, not a loading screen's), tried again a second later.
        /// </summary>
        private static void Prepare()
        {
            if (_document != null && _rig != null) return;
            if (Time.unscaledTime < _nextPrepare || !FleetShips.Active || GameSessionManager.InHub) return;
            _nextPrepare = Time.unscaledTime + 1f;
            GameStateMachine machine = GameStateMachine.Instance;
            if (machine == null || !(machine.CurrentState is GSIngame)) return;
            EnsureRig();
            if (_document != null) return;
            MakeLabel();
            if (_document != null && _document.rootVisualElement != null) _document.rootVisualElement.style.display = DisplayStyle.None;
        }

        /// <summary>The label's overlay, made once.</summary>
        private static void MakeLabel()
        {
            if (_document == null)
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
                    // Fleet's notices for a spectator without a text chat (ShowNotice), above the label. Player names in them
                    // show as typed, not as markup.
                    for (int i = 0; i < NoticeLines; i++)
                    {
                        Label line = Ui.HudLabel("", 20, Color.white);
                        line.enableRichText = false;
                        line.style.maxWidth = 1100;
                        line.style.unityTextAlign = TextAnchor.UpperCenter;
                        line.style.marginBottom = 6;
                        line.style.display = DisplayStyle.None;
                        NoticeLabels[i] = line;
                        box.Add(line);
                    }
                    _title = Ui.HudLabel("", 30, Ui.GameOrange);
                    _title.style.unityFontStyleAndWeight = FontStyle.Bold;
                    _hint = Ui.HudLabel("", 20, Ui.GameBrightGrey);
                    _hint.style.marginTop = 4;
                    box.Add(_title);
                    box.Add(_hint);
                    root.Add(box);
                }
            }
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
                : "WAITING FOR A PLACE" + (entry.Team >= 0 && FleetSession.IsPvP ? " ON THE " + PvpTeams.TeamName(entry.Team) + " TEAM" : "") + " · WATCHING " + watched;
            _hint.text = ships > 1 ? "Q / E: OTHER SHIP     MOUSE: LOOK AROUND     WHEEL: ZOOM" : "MOUSE: LOOK AROUND     WHEEL: ZOOM";
        }

        // ---------------------------------------------------------------- notices without a chat

        /// <summary>
        /// One of Fleet's notices for a spectator without a text chat (the chat is part of the character: a waiting player, or
        /// one back after their ship was lost, has none): a line over the label for NoticeSeconds, the newest at the bottom.
        /// Nothing while the view is closed.
        /// </summary>
        public static void ShowNotice(string message)
        {
            if (!_active || string.IsNullOrEmpty(message) || NoticeLabels[NoticeLines - 1] == null) return;
            try
            {
                for (int i = 0; i < NoticeLines - 1; i++)
                {
                    NoticeLabels[i].text = NoticeLabels[i + 1].text;
                    NoticeUntil[i] = NoticeUntil[i + 1];
                }
                NoticeLabels[NoticeLines - 1].text = message.ToUpperInvariant();
                NoticeUntil[NoticeLines - 1] = Time.unscaledTime + NoticeSeconds;
                _noticesShown = true;
                FadeNotices();
            }
            catch (Exception e)
            {
                Log.Failure("Showing a notice to a spectator", e);
            }
        }

        /// <summary>Every frame while notices show: each fades out at the end of its time, then goes.</summary>
        private static void FadeNotices()
        {
            if (!_noticesShown) return;
            float now = Time.unscaledTime;
            bool any = false;
            for (int i = 0; i < NoticeLines; i++)
            {
                Label line = NoticeLabels[i];
                if (line == null) continue;
                float left = NoticeUntil[i] - now;
                line.style.display = left > 0f ? DisplayStyle.Flex : DisplayStyle.None;
                line.style.opacity = Mathf.Clamp01(left / NoticeFadeSeconds);
                if (left > 0f) any = true;
            }
            _noticesShown = any;
        }

        /// <summary>No notices left over for the next time the view opens.</summary>
        private static void ClearNotices()
        {
            for (int i = 0; i < NoticeLines; i++)
            {
                NoticeUntil[i] = 0f;
                if (NoticeLabels[i] != null) NoticeLabels[i].style.display = DisplayStyle.None;
            }
            _noticesShown = false;
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

        /// <summary>An occlusion controller's hull parts, read without a reflection call; null when the field is missing.</summary>
        private static readonly AccessTools.FieldRef<LocalPlayerOcclusionController, List<IOcclusionNode>> OcclusionNodes =
            FieldRef<LocalPlayerOcclusionController, List<IOcclusionNode>>(OcclusionNodesField);

        /// <summary>
        /// The occlusion controllers whose hull parts are shown as from open space, by instance ID, with how many parts each
        /// had then: a controller is done again only when it is new or its parts have changed.
        /// </summary>
        private static readonly Dictionary<int, int> ShownFromSpace = new Dictionary<int, int>();

        /// <summary>A reader for a field of a game object (no reflection call per read); null when the field is missing.</summary>
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

        /// <summary>
        /// The ships' hidden parts (interior walls, exterior plates) are shown or hidden for where the local player is. A
        /// spectator is in open space as far as that goes (and may have no character, which the game's code would trip on).
        /// Their parts are set for that once, as the view opens (ShowShipsFromSpace) or when a controller is new to it; after
        /// that the game's calls are just skipped.
        /// </summary>
        [GamePatch(Feature, "CG.Graphics.LocalPlayerOcclusionController", "UpdateOcclusion", PatchKind.Prefix)]
        private static bool OcclusionPrefix(LocalPlayerOcclusionController __instance)
        {
            if (!_active && LocalPlayer.Instance != null)
            {
                // The game's own rules again: the next time, the parts are set afresh.
                if (ShownFromSpace.Count > 0) ShownFromSpace.Clear();
                return true;
            }
            try
            {
                List<IOcclusionNode> nodes = OcclusionNodes != null ? OcclusionNodes(__instance) : null;
                int shown;
                if (nodes != null && (!ShownFromSpace.TryGetValue(__instance.GetInstanceID(), out shown) || shown != nodes.Count)) ShowFromSpace(__instance);
            }
            catch (Exception e)
            {
                Log.Failure("Showing the ships to a spectator", e);
            }
            return false;
        }

        /// <summary>
        /// When the view opens: every ship's hidden parts as from open space, at once. The controllers only update on the
        /// local player's own moves, so a player who died aboard a ship would otherwise watch with that room's walls and plates.
        /// The controllers are looked for on the ships, not by a search of the whole scene (15-20 ms in a fleet mission).
        /// </summary>
        internal static void ShowShipsFromSpace()
        {
            try
            {
                ShownFromSpace.Clear();
                // Most parts register with this one (OnShipLost).
                LocalPlayerOcclusionController main = LocalPlayerOcclusionController.Instance;
                if (main != null) ShowFromSpace(main);
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    LocalPlayerOcclusionController controller = ship != null ? ship.GetComponentInChildren<LocalPlayerOcclusionController>() : null;
                    if (controller != null && controller != main) ShowFromSpace(controller);
                }
            }
            catch (Exception e)
            {
                Log.Failure("Showing the ships to a spectator", e);
            }
        }

        /// <summary>One occlusion controller's hidden parts as the game shows them to a player in open space (remembered in ShownFromSpace).</summary>
        private static void ShowFromSpace(LocalPlayerOcclusionController controller)
        {
            try
            {
                List<IOcclusionNode> nodes = OcclusionNodes != null && controller != null ? OcclusionNodes(controller) : null;
                if (nodes == null) return;
                foreach (IOcclusionNode node in nodes) node.ToggleRenderers(!node.HideOnLocalPlayerIsInSpace);
                ShownFromSpace[controller.GetInstanceID()] = nodes.Count;
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
                if (current == null || FleetShips.OwnerOf(current) != dying || OcclusionNodes == null) return;
                PropertyInfo instance = typeof(LocalPlayerOcclusionController).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                MethodInfo setter = instance != null ? instance.GetSetMethod(true) : null;
                if (setter == null) return;
                foreach (PlayerControlledShip ship in FleetShips.All)
                {
                    if (ship == null || ship == dying || ship.StartedDeath) continue;
                    LocalPlayerOcclusionController controller = ship.GetComponentInChildren<LocalPlayerOcclusionController>(true);
                    if (controller == null) continue;
                    setter.Invoke(null, new object[] { controller });
                    List<IOcclusionNode> nodes = OcclusionNodes(current);
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
