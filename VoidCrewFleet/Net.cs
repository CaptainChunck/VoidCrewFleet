using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;

namespace VoidCrewFleet
{
    /// <summary>Kinds of message the plugin sends; the first element of every event's content.</summary>
    internal enum Msg : byte
    {
        /// <summary>Host to everyone: show a notice. Data: text.</summary>
        Notice = 1,

        /// <summary>Client to host, in the hub: how many ships the game's own missions use (1-3). Data: int.</summary>
        HubShipCount = 2,

        /// <summary>Client to host, in the hub: a ship 2-6's loadout. Data: slot, GUID hex.</summary>
        HubShipLoadout = 3,

        /// <summary>Client to host, in the hub: choose a fleet mission variant. Data: byte (HubVariant).</summary>
        HubVariant = 4,

        /// <summary>Host only, in the hub: force a ship's type. Data: slot, ship GUID hex ("" for any type).</summary>
        HubShipType = 5,

        /// <summary>Host only, in the hub: Fleet Battle's ships per team. Data: int (1-3).</summary>
        HubShipsPerTeam = 6,

        /// <summary>Host only, in the hub: Fleet Battle's teams get the same ship types. Data: bool.</summary>
        HubMirror = 7,

        /// <summary>Client to host, lobby screen: the ship this player wants to crew. Data: slot.</summary>
        CrewRequest = 10,

        /// <summary>Client to host, lobby screen (host only): move a player to a ship, or off every ship (-1). Data: actor, slot.</summary>
        CrewMove = 11,

        /// <summary>
        /// Attacker to the players around and the victim: a punch, to show on the attacker's model; the victim's client takes
        /// the damage. Data: arm (byte, 0 left, 1 right), victim's actor number (-1 for none), damage, attacker position.
        /// </summary>
        FistSwing = 20,

        /// <summary>Host to the victim's client: an enemy ship's turret hit you. Data: damage, turret position.</summary>
        TurretHit = 21,

        /// <summary>Client to host: an enemy completed an enhancement panel. Data: panel view id.</summary>
        Sabotage = 30,

        /// <summary>Client to host: plug the held hacking terminal into an enemy airlock's outside panel. Data: terminal view, ship view, airlock index.</summary>
        HackPlug = 40,

        /// <summary>Host to the hacker: the terminal is in the panel; destroy the one in your hands. Data: terminal view.</summary>
        HackTaken = 41,

        /// <summary>Client to host: the ship's own crew pulls a terminal out of its airlock panel. Data: ship view, airlock index.</summary>
        HackPull = 42
    }

    /// <summary>
    /// The plugin's own network messages, all on one Photon event code. The game uses event codes 0-10, Opsive's PUN
    /// add-on 155-160 and Photon itself 200 and up, so 173 is free. Senders are checked by the handlers: only the host
    /// may send host messages, and a client's request is only ever about that client.
    /// </summary>
    internal static class Net
    {
        public const byte EventCode = 173;

        private static readonly Dictionary<Msg, Action<int, object[]>> Handlers = new Dictionary<Msg, Action<int, object[]>>();
        private static bool _listening;

        /// <summary>Registers a handler. Listening itself starts once the game has set up Photon (FleetRuntime).</summary>
        public static void On(Msg type, Action<int, object[]> handler)
        {
            Handlers[type] = handler;
        }

        public static void StartListening()
        {
            if (_listening) return;
            try
            {
                PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
                _listening = true;
            }
            catch (Exception e)
            {
                Log.Failure("Listening for Void Crew Fleet messages", e);
            }
        }

        /// <summary>To the host. On the host itself the handler runs right away (sender = the host's actor number).</summary>
        public static void ToHost(Msg type, params object[] data)
        {
            if (PhotonNetwork.IsMasterClient)
            {
                Dispatch(type, PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1, data ?? new object[0]);
                return;
            }
            Send(type, data, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient });
        }

        public static void ToAll(Msg type, params object[] data)
        {
            Send(type, data, new RaiseEventOptions { Receivers = ReceiverGroup.All });
        }

        public static void ToOthers(Msg type, params object[] data)
        {
            Send(type, data, new RaiseEventOptions { Receivers = ReceiverGroup.Others });
        }

        public static void ToActor(int actor, Msg type, params object[] data)
        {
            if (PhotonNetwork.LocalPlayer != null && PhotonNetwork.LocalPlayer.ActorNumber == actor)
            {
                Dispatch(type, actor, data ?? new object[0]);
                return;
            }
            Send(type, data, new RaiseEventOptions { TargetActors = new[] { actor } });
        }

        /// <summary>To these players other than the local one.</summary>
        public static void ToOtherActors(IEnumerable<int> actors, Msg type, params object[] data)
        {
            int me = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
            var targets = new List<int>();
            foreach (int actor in actors)
            {
                if (actor != me && actor > 0 && !targets.Contains(actor)) targets.Add(actor);
            }
            if (targets.Count > 0) Send(type, data, new RaiseEventOptions { TargetActors = targets.ToArray() });
        }

        private static void Send(Msg type, object[] data, RaiseEventOptions options)
        {
            try
            {
                if (!PhotonNetwork.InRoom) return;
                var content = new object[(data != null ? data.Length : 0) + 1];
                content[0] = (byte)type;
                if (data != null) Array.Copy(data, 0, content, 1, data.Length);
                PhotonNetwork.RaiseEvent(EventCode, content, options, SendOptions.SendReliable);
            }
            catch (Exception e)
            {
                Log.Failure("Sending a Void Crew Fleet message (" + type + ")", e);
            }
        }

        private static void OnEvent(EventData photonEvent)
        {
            if (photonEvent == null || photonEvent.Code != EventCode) return;
            var content = photonEvent.CustomData as object[];
            if (content == null || content.Length == 0 || !(content[0] is byte)) return;
            var data = new object[content.Length - 1];
            Array.Copy(content, 1, data, 0, data.Length);
            Dispatch((Msg)(byte)content[0], photonEvent.Sender, data);
        }

        private static void Dispatch(Msg type, int sender, object[] data)
        {
            Action<int, object[]> handler;
            if (!Handlers.TryGetValue(type, out handler)) return;
            try
            {
                handler(sender, data);
            }
            catch (Exception e)
            {
                Log.Failure("Handling a Void Crew Fleet message (" + type + ")", e);
            }
        }

        /// <summary>True when the sender of a message is the room's host.</summary>
        public static bool FromHost(int sender)
        {
            Player master = PhotonNetwork.MasterClient;
            return master != null && master.ActorNumber == sender;
        }

        public static T Get<T>(object[] data, int index, T fallback)
        {
            if (data == null || index < 0 || index >= data.Length || !(data[index] is T)) return fallback;
            return (T)data[index];
        }
    }
}
