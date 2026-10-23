using System;
using CG.Client.UserData;
using CG.Game;
using CG.Space;
using Photon.Pun;
using UI.Core.Audio;
using UnityEngine.UIElements;

namespace VoidCrewFleet
{
    /// <summary>
    /// The host's choice once a ship of the fleet is lost: carry on with the ships that are left, or take everyone back to the
    /// hub to start again. RETURN TO HUB is in the game's escape menu (the host's, once a ship is lost), made like the menu's
    /// own buttons, and asks with the game's own confirmation popup. It ends the mission the way the game ends a failed one,
    /// so everyone goes through the end screen back to the hub.
    /// </summary>
    internal static class FleetReturn
    {
        public const string Feature = "Return to hub";

        private static object _lostIn;
        private static Button _button;

        /// <summary>Every client: a ship of this mission was lost (a player who takes over as host later knows it too).</summary>
        public static void OnShipLost(PlayerControlledShip ship)
        {
            _lostIn = GameSessionManager.ActiveSession;
        }

        /// <summary>
        /// A ship of this mission is lost: this client saw one start dying, or the ships the host published at the start
        /// include one that is gone or dying (for a host who joined after the loss).
        /// </summary>
        private static bool ShipLostThisMission()
        {
            if (_lostIn != null && ReferenceEquals(_lostIn, GameSessionManager.ActiveSession)) return true;
            return FleetShips.PublishedShipLost();
        }

        /// <summary>Host: the fleet carries on after a loss; say what the host can do.</summary>
        public static void ShipWasLost()
        {
            _lostIn = GameSessionManager.ActiveSession;
            if (PhotonNetwork.IsMasterClient)
                Notice.Show("You're the host: carry on with the ships that are left, or end the mission for everyone with RETURN TO HUB in the escape menu.");
        }

        /// <summary>The host may end the mission now: a fleet mission that has lost a ship and hasn't ended.</summary>
        public static bool Available
        {
            get
            {
                try
                {
                    if (!PhotonNetwork.IsMasterClient || GameSessionManager.InHub || FleetSession.ShipCount < 2) return false;
                    if (!ShipLostThisMission()) return false;
                    ClientGame game = ClientGame.Current;
                    return game != null && game.GameState != ClientGameState.Ended;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>The escape menu opens: RETURN TO HUB, above the menu's MAIN MENU button, for the host when it applies.</summary>
        [GamePatch(Feature, "EscapeMenu", "OnEnter", PatchKind.Postfix)]
        private static void EscapeMenuPostfix(EscapeMenu __instance)
        {
            try
            {
                bool show = Available;
                if (show && (_button == null || _button.panel == null)) _button = MakeButton(__instance);
                if (_button != null) _button.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            }
            catch (Exception e)
            {
                Log.Failure("Adding RETURN TO HUB to the escape menu", e);
            }
        }

        private static Button MakeButton(EscapeMenu menu)
        {
            VisualElement root = menu != null && menu.UIDoc != null ? menu.UIDoc.rootVisualElement : null;
            Button mainMenu = root != null ? root.Q<Button>("button_main_menu") : null;
            if (mainMenu == null || mainMenu.parent == null)
            {
                Log.WarnOnce("escape menu button", "The escape menu has no MAIN MENU button to put RETURN TO HUB beside (a game update?).");
                return null;
            }
            var button = new Button { name = "button_fleet_return_to_hub", text = "RETURN TO HUB" };
            foreach (string name in mainMenu.GetClasses()) button.AddToClassList(name);
            button.clicked += Ask;
            mainMenu.parent.Insert(mainMenu.parent.IndexOf(mainMenu), button);
            try
            {
                UIAudioProvider.AddSounds(button);
            }
            catch
            {
                // a silent button then
            }
            return button;
        }

        private static void Ask()
        {
            if (!Available || MenuScreenController.Instance == null) return;
            MenuScreenController.Instance.ShowConfirmationPopup("RETURN TO HUB",
                "END THIS MISSION FOR EVERYONE AND GO BACK TO THE HUB TO START AGAIN?", ReturnToHub);
        }

        private static void ReturnToHub()
        {
            if (!Available) return;
            try
            {
                // The confirmation popup has closed itself; the escape menu is still open under it.
                if (MenuScreenController.IsOpen()) MenuScreenController.Instance.CloseTop();
            }
            catch
            {
                // the end screen replaces it anyway
            }
            Log.Info("The host ends the mission: everyone returns to the hub.");
            Notice.ShowAll("The host ended the mission: everyone returns to the hub.");
            ClientGame.Current.EndGame((byte)QuestResult.Defeat);
        }

        /// <summary>Self-test: RETURN TO HUB is in the escape menu and showing.</summary>
        public static bool ButtonShown
        {
            get { return _button != null && _button.panel != null && _button.resolvedStyle.display == DisplayStyle.Flex; }
        }

        /// <summary>Self-test: the confirmation popup RETURN TO HUB opens.</summary>
        public static void AskForTest()
        {
            Ask();
        }

        /// <summary>Self-test: what confirming RETURN TO HUB does, without the menus.</summary>
        public static void ReturnNow()
        {
            ReturnToHub();
        }
    }
}
