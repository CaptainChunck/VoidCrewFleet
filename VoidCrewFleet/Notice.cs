using System;
using Gameplay.Chat;

namespace VoidCrewFleet
{
    /// <summary>Messages the player must see wherever they are: a line in their own text chat, plus the log.</summary>
    internal static class Notice
    {
        private const string Sender = "Fleet";

        public static void Show(string message)
        {
            Log.Info(message);
            try
            {
                if (TextChat.Instance != null) TextChat.Instance.AddLog(new LogEntry(Sender, message));
            }
            catch (Exception e)
            {
                Log.Verbose("Could not show a notice in the chat: " + e.Message);
            }
            // The PvP team screen covers the chat.
            PvpTeamScreen.Flash(message);
        }

        /// <summary>Host: shows a notice to everyone in the room.</summary>
        public static void ShowAll(string message)
        {
            Show(message);
            Net.ToOthers(Msg.Notice, message);
        }

        public static void Listen()
        {
            Net.On(Msg.Notice, (sender, data) =>
            {
                if (!Net.FromHost(sender)) return;
                string text = Net.Get<string>(data, 0, null);
                if (!string.IsNullOrEmpty(text)) Show(text);
            });
        }
    }
}
