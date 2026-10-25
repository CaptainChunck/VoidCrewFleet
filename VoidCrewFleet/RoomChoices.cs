using System;
using System.Collections.Generic;

namespace VoidCrewFleet
{
    /// <summary>
    /// Host: its own copy of room properties that only the host writes (HubFleet's fleet choices), with nothing of the game,
    /// Unity or Photon in it (it is tested offline). While this player hosts the room the copy is the truth, not the room's:
    /// the room's property only changes a round trip after the host's own write, and any player can write a room property. A
    /// choice the host hasn't written is the room's as it was when the copy was made (Adopt), so a player who takes over as
    /// host carries on with the room's choices. PutBack names the choices whose room copy keeps differing once the host's own
    /// write has had time to come back (RoomCopyCheck, one per choice): someone else wrote them, and the host writes its own
    /// again.
    /// </summary>
    internal sealed class RoomChoices
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        private readonly Dictionary<string, float> _writtenAt = new Dictionary<string, float>();
        private readonly Dictionary<string, RoomCopyCheck> _checks = new Dictionary<string, RoomCopyCheck>();
        private readonly List<string> _putBack = new List<string>();

        /// <summary>The room the copy is for; null before the first copy, and once this player stops hosting.</summary>
        public string Room { get; private set; }

        /// <summary>Forgets the copy: this player isn't the host (any more), or left the room.</summary>
        public void Forget()
        {
            Room = null;
            _values.Clear();
            _writtenAt.Clear();
            _checks.Clear();
        }

        /// <summary>
        /// A new copy for a room: each of these choices as the room holds it now (roomValue: the room's value, or null when it
        /// has none). now: the clock (unscaled seconds), which counts as the time each was written.
        /// </summary>
        public void Adopt(string room, IEnumerable<string> keys, Func<string, object> roomValue, float now)
        {
            Forget();
            Room = room;
            foreach (string key in keys)
            {
                _values[key] = roomValue(key);
                _writtenAt[key] = now;
            }
        }

        /// <summary>The host writes a choice (null removes it): its copy has the new value at once.</summary>
        public void Write(string key, object value, float now)
        {
            _values[key] = value;
            _writtenAt[key] = now;
        }

        /// <summary>Whether the copy has this choice (it may be null: none).</summary>
        public bool Knows(string key)
        {
            return _values.ContainsKey(key);
        }

        /// <summary>The host's value of a choice; false (with null) when it has none.</summary>
        public bool TryGet(string key, out object value)
        {
            return _values.TryGetValue(key, out value) && value != null;
        }

        /// <summary>The host's value of a choice, null for none (what PutBack's choices are written back with).</summary>
        public object Value(string key)
        {
            object value;
            return _values.TryGetValue(key, out value) ? value : null;
        }

        /// <summary>
        /// The choices whose room copy should be written again now (the list is reused: read it before the next call). Each
        /// counts as written now. roomValue: the room's value of a choice, or null; held: Photon holds incoming messages right
        /// now (RoomCopyCheck). Values are compared with Equals, so a number of another type (an int where the host wrote a
        /// byte) differs too.
        /// </summary>
        public List<string> PutBack(Func<string, object> roomValue, float now, bool held)
        {
            _putBack.Clear();
            foreach (KeyValuePair<string, object> choice in _values)
            {
                RoomCopyCheck check;
                if (!_checks.TryGetValue(choice.Key, out check)) _checks[choice.Key] = check = new RoomCopyCheck();
                if (check.PutBack(!Equals(roomValue(choice.Key), choice.Value), _writtenAt[choice.Key], now, held)) _putBack.Add(choice.Key);
            }
            foreach (string key in _putBack) _writtenAt[key] = now;
            return _putBack;
        }
    }
}
