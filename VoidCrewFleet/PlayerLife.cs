using CG.Game.Player;
using CG.Ship.Object;

namespace VoidCrewFleet
{
    /// <summary>
    /// Whether a character is dead, as every client sees it. The game's Player.IsDead is only set on the dying player's own
    /// client (CustomCharacterHealth.Die runs there), so other players' dead characters read as alive everywhere else; the
    /// death RPC (Player.NetworkNotifyDeath, to everyone) sets Player.State to Dead on every client, and late joiners get it
    /// with the character's hit points. The game's own anti-personnel turret reads State too.
    /// </summary>
    internal static class PlayerLife
    {
        public static bool IsDead(Player player)
        {
            if (player == null) return true;
            if (player.State == LifeState.Dead) return true;
            return player == LocalPlayer.Instance && player.IsDead;
        }
    }
}
