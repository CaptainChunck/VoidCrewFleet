namespace VoidCrewFleet
{
    /// <summary>
    /// Host: tells when to put its own value of a room property back (PvpTeams.KeepRoomCopies), one per property, with nothing
    /// of the game, Unity or Photon in it (it is tested offline). The room's copy comes back a round trip after the host's own
    /// write, and later still while Photon holds incoming messages (it does while the host moves to a mission, right after
    /// Fleet writes its crews) or long loading frames hold them up (a 3v3 Fleet Battle's six ships spawning took longer than
    /// EchoSeconds). So a difference only counts as someone else's write (any player can write a room property) once the
    /// room has shown the host's own value since its last write, or NoEchoSeconds have passed without it; no sooner than
    /// EchoSeconds after that write and after Photon's messages flow again; and once it has lasted LastingSeconds.
    /// </summary>
    internal sealed class RoomCopyCheck
    {
        public const float EchoSeconds = 3f;
        public const float LastingSeconds = 1f;

        /// <summary>How long the host waits for its own write to show in the room before a difference counts anyway.</summary>
        public const float NoEchoSeconds = 20f;

        /// <summary>The last time Photon was seen holding incoming messages.</summary>
        private float _heldAt = float.NegativeInfinity;

        private bool _differs;
        private float _differsSince;

        /// <summary>The host's write this check last saw, and whether the room has shown the host's value since then.</summary>
        private float _write = float.NaN;
        private bool _echoed;

        /// <summary>
        /// True when the host should write its own value again now. differs: the room's copy isn't the host's; writtenAt: when
        /// the host last wrote it; now: the clock (unscaled seconds); held: Photon holds incoming messages right now.
        /// </summary>
        public bool PutBack(bool differs, float writtenAt, float now, bool held)
        {
            if (writtenAt != _write)
            {
                _write = writtenAt;
                _echoed = false;
            }
            if (!differs) _echoed = true;
            if (held) _heldAt = now;
            if (!differs || now - writtenAt < (_echoed ? EchoSeconds : NoEchoSeconds) || now - _heldAt < EchoSeconds)
            {
                _differs = false;
                return false;
            }
            if (!_differs)
            {
                _differs = true;
                _differsSince = now;
                return false;
            }
            if (now - _differsSince < LastingSeconds) return false;
            _differs = false;
            return true;
        }
    }
}
