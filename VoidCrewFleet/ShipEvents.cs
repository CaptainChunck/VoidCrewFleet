using System;
using System.Collections.Generic;
using CG.Space;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// Which ship an event belongs to. Many of the game's events say nothing about the ship they come from ("a power
    /// breaker flipped", "the jump drive is charging"), because the game has one ship; with several ships every listener
    /// reacts to all of them. Patches around the code that raises such an event say which ship is raising it (Begin/End, in
    /// a prefix and finalizer, the prefix telling its finalizer through __state that it began one), and patches on the
    /// listeners skip events of another ship:
    /// - a listener that is part of a ship (lights, alarms, effects) reacts only to its own ship's events;
    /// - a listener for the local player's screen or ears (voice lines, HUD, 2D sounds) reacts only to the ship the local
    ///   player belongs to right now (FleetShips.ViewShip: in co-op the ship they are aboard or crew, in PvP their team's).
    /// With one ship nothing is ever skipped.
    /// </summary>
    internal static class ShipEvents
    {
        private static readonly List<PlayerControlledShip> Stack = new List<PlayerControlledShip>();

        /// <summary>The ship whose event is being raised right now, or null when unknown.</summary>
        public static PlayerControlledShip Current
        {
            get { return Stack.Count > 0 ? Stack[Stack.Count - 1] : null; }
        }

        /// <summary>
        /// The event raised from here on (until End) belongs to this ship; null when it can't be told (every listener then
        /// reacts, as with one ship). Always pair with End, as ShipRedirect.BeginOverride is paired.
        /// </summary>
        public static void Begin(PlayerControlledShip ship)
        {
            Stack.Add(ship);
        }

        /// <summary>Begin with the ship that owns a component (a module, a system, a part of the ship); always pushes one entry.</summary>
        public static void BeginFor(Component component)
        {
            PlayerControlledShip ship = null;
            try
            {
                if (FleetShips.Active && component != null) ship = FleetShips.OwnerOf(component);
            }
            catch (Exception e)
            {
                Log.Failure("Reading which ship an event comes from", e);
            }
            Stack.Add(ship);
        }

        /// <summary>Ends the latest Begin. One more than were begun (a patch out of step) is ignored, and logged once.</summary>
        public static void End()
        {
            if (Stack.Count == 0)
            {
                Log.WarnOnce("ship event underflow", "A patch ended a ship's event it never began; ignored (a Fleet patch is out of step).");
                return;
            }
            Stack.RemoveAt(Stack.Count - 1);
        }

        /// <summary>True when the event being raised belongs to another ship than this ship part's.</summary>
        public static bool ForeignTo(Component listener)
        {
            PlayerControlledShip source = Current;
            if (source == null || listener == null || !FleetShips.Active) return false;
            PlayerControlledShip own = FleetShips.OwnerOf(listener);
            return own != null && own != source;
        }

        /// <summary>True when the event being raised belongs to another ship than the one the local player belongs to.</summary>
        public static bool ForeignToLocal()
        {
            return ForeignToLocal(Current);
        }

        /// <summary>True when a ship is not the one the local player belongs to right now (with several ships only).</summary>
        public static bool ForeignToLocal(PlayerControlledShip ship)
        {
            if (ship == null || !FleetShips.Active) return false;
            // A Fleet Battle player waiting for a place on no team yet hears no ship: any one would be a team's.
            if (FleetSession.IsPvP && Spectator.Active && FleetShips.TeamOfActor(FleetShips.LocalActor) < 0) return true;
            PlayerControlledShip mine = FleetShips.ViewShip;
            return mine != null && mine != ship;
        }
    }
}
