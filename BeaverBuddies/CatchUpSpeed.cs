using System;

namespace BeaverBuddies
{
    // Decides how fast a player's game runs while it is behind the newest tick it has received.
    //
    // This only changes how quickly a player works through ticks it already holds. Which events
    // run on which tick is decided by the host, so the speed chosen here cannot change what
    // anyone simulates.
    //
    // The original rule sped a guest up only once it was more ticks behind than the game speed:
    // more than 1 tick at speed 1, but more than 7 ticks at speed 7. Both players run at the same
    // nominal speed, so every hitch on the guest added lag that nothing recovered until it passed
    // that mark. At speed 7 a guest therefore sat 3 to 5 ticks behind, about 0.3 to 0.4 s before
    // it saw the result of its own actions, on top of the network delay.
    //
    // Now a guest also starts catching up once it is more than BufferTicks behind, whatever the
    // game speed, and keeps going until it is within ReleaseTicks. The original rule still
    // applies, so a guest is never slower to catch up than it was.
    public static class CatchUpSpeed
    {
        // Lag a guest settles at. Not zero: a guest with nothing queued has to wait for the host's
        // next heartbeat before every tick, which stutters.
        public const int BufferTicks = 2;

        // Once catching up, continue until this close. Each speed change notifies every animated
        // building, so the rule is built to change speed a few times per hitch, not every tick.
        public const int ReleaseTicks = 1;

        // The original cap on catch-up speed.
        public const float MaxSpeed = 10;

        public static float For(float targetSpeed, int ticksBehind, float currentSpeed)
        {
            // The original rule, unchanged. It also covers a paused game (target 0), where a guest
            // that is behind still has to run to reach the tick the host paused on.
            float speed = targetSpeed;
            if (ticksBehind > targetSpeed)
            {
                speed = Math.Min(ticksBehind, MaxSpeed);
            }
            if (targetSpeed <= 0)
            {
                return speed;
            }

            bool catchingUp = currentSpeed > targetSpeed;
            if (ticksBehind > (catchingUp ? ReleaseTicks : BufferTicks))
            {
                // Whole steps above the chosen speed. The lag naturally flickers by one tick as the host's
                // tick arrives and ours finishes, so while catching up the speed only ever rises; it drops
                // back once, at the release mark. Otherwise it would flip on every tick.
                float boosted = targetSpeed + Math.Max(1, ticksBehind - BufferTicks);
                if (catchingUp)
                {
                    boosted = Math.Max(boosted, currentSpeed);
                }
                speed = Math.Max(speed, Math.Min(boosted, MaxSpeed));
            }
            return speed;
        }
    }
}
