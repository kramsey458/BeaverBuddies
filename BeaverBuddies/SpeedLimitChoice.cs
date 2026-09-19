namespace BeaverBuddies
{
    // Who decides whether the game's large colony speed limit applies.
    //
    // Timberborn slows its own speed settings as the population grows (GameSpeedThrottler): in a big colony,
    // speed 7 runs at about 3.4. Every computer applies that scaling locally. If the host removed it and a guest
    // did not, the host would run twice as fast as the guest, and the guest's catch-up speed is scaled down too,
    // so it could never recover. So in a session the host's choice is the only one that counts: it is sent when a
    // guest joins and every player uses it for that session.
    public static class SpeedLimitChoice
    {
        /// <param name="inSession">A multiplayer session is active on this computer.</param>
        /// <param name="isGuest">This computer joined someone else's session.</param>
        /// <param name="sessionValue">The host's choice for this session, or null if not known (yet).</param>
        /// <param name="ownSetting">This player's own setting.</param>
        public static bool IsRemoved(bool inSession, bool isGuest, bool? sessionValue, bool ownSetting)
        {
            if (!inSession)
            {
                return ownSetting;
            }
            if (sessionValue != null)
            {
                return sessionValue.Value;
            }
            // A guest that has not heard from the host keeps the game's default, which is what a host running an
            // older build uses. A host that has not started the session yet uses what it is about to send.
            return !isGuest && ownSetting;
        }
    }
}
