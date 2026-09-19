using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BeaverBuddies.Panel
{
    public enum Quality { Unknown, Good, Fair, Poor, Silent }

    public enum StatusKind { InSync, CatchingUp, WaitingForHost, Unstable, Desynced, Disconnected }

    public static class PingQuality
    {
        public const double GoodMaxMs = 80;
        public const double FairMaxMs = 160;
        /// <summary>No reply for this long means the player has stopped responding, whatever their last ping was.</summary>
        public const double SilentSeconds = 5;

        public static Quality Classify(double? rttMs, double? silenceSeconds)
        {
            if (silenceSeconds != null && silenceSeconds.Value >= SilentSeconds) return Quality.Silent;
            if (rttMs == null) return Quality.Unknown;
            if (rttMs.Value <= GoodMaxMs) return Quality.Good;
            return rttMs.Value <= FairMaxMs ? Quality.Fair : Quality.Poor;
        }
    }

    /// <summary>Simulation ticks per second, measured over a short sliding window so it neither flickers nor lags.</summary>
    public sealed class TickRateMeter
    {
        const double WindowSeconds = 3;
        const double MinSpanSeconds = .5;
        readonly Queue<(double Time, long Ticks)> samples = new Queue<(double, long)>();

        /// <summary>Records the current tick and returns the rate, or null until there is enough history.</summary>
        public double? Sample(long ticks, double nowSeconds)
        {
            // A lower tick count means a new map or session: start again rather than report a negative rate.
            if (samples.Count > 0 && ticks < samples.Last().Ticks) samples.Clear();
            samples.Enqueue((nowSeconds, ticks));
            while (samples.Count > 2 && nowSeconds - samples.Peek().Time > WindowSeconds) samples.Dequeue();
            var oldest = samples.Peek();
            double span = nowSeconds - oldest.Time;
            if (span < MinSpanSeconds) return null;
            return (ticks - oldest.Ticks) / span;
        }

        public void Reset() => samples.Clear();
    }

    public sealed class PanelPlayer
    {
        public int Id;
        public string Name = "";
        public bool IsYou, IsHost;
        public double? RttMs;
        public double? SilenceSeconds;
        public string Transport = "";
        /// <summary>Host only: how many ticks behind the host this guest last reported being. Null if unknown.</summary>
        public int? TicksBehind;
    }

    /// <summary>Everything the panel needs, gathered by the game and free of any game types.</summary>
    public sealed class PanelInputs
    {
        public bool IsHost, Stopped, Desynced, WaitingForHost;
        public int TicksBehind;
        public double? HostSilenceSeconds;
        public double? TickRate;
        public float Speed;
        /// <summary>Host only: percent of the chosen speed the host is running at. Below 100 while easing off for a guest.</summary>
        public int HostPacingPercent = 100;
        public bool HostPacingHolding;
        public List<PanelPlayer> Players = new List<PanelPlayer>();
    }

    public sealed class PanelRow
    {
        public string Name = "", Tag = "", PingText = "";
        public Quality Quality;
    }

    /// <summary>The finished text and states the view shows. No layout, no colors: just what to say.</summary>
    public sealed class PanelModel
    {
        public StatusKind Status;
        public string StatusText = "", Role = "", Summary = "";
        public Quality SummaryQuality;
        public List<PanelRow> Rows = new List<PanelRow>();
        public string TickRateText = "", SpeedText = "";
        /// <summary>Only for guests: how far this game is behind the host. Null for the host.</summary>
        public string BehindText;
        /// <summary>How the players are connected ("Direct", "Steam"), or null when unknown.</summary>
        public string LinkText;
        /// <summary>Only for the host: how far the slowest guest is behind. Null for a guest or when unknown.</summary>
        public string GuestsBehindText;
        /// <summary>Only for the host while it is easing off so a guest can keep up. Null otherwise.</summary>
        public string PacingText;
    }

    public static class PanelModelBuilder
    {
        /// <summary>A guest this many ticks behind the host is shown as catching up.</summary>
        public const int CatchingUpTicks = 3;

        /// <param name="t">Translates a key with format arguments (the game's localization).</param>
        public static PanelModel Build(PanelInputs input, Func<string, object[], string> t)
        {
            var model = new PanelModel();
            model.Role = t(input.IsHost ? "BeaverBuddies.Panel.Host" : "BeaverBuddies.Panel.Guest", Array.Empty<object>());

            var others = input.Players.Where(p => !p.IsYou && !p.IsHost).ToList();
            var silent = input.Players.Any(p => !p.IsYou && !p.IsHost && Classify(p) == Quality.Silent);
            bool hostSilent = !input.IsHost && input.HostSilenceSeconds != null && input.HostSilenceSeconds.Value >= PingQuality.SilentSeconds;

            // The most urgent thing wins.
            if (input.Stopped) model.Status = StatusKind.Disconnected;
            else if (input.Desynced) model.Status = StatusKind.Desynced;
            else if (hostSilent || silent) model.Status = StatusKind.Unstable;
            else if (!input.IsHost && input.WaitingForHost) model.Status = StatusKind.WaitingForHost;
            else if (!input.IsHost && input.TicksBehind >= CatchingUpTicks) model.Status = StatusKind.CatchingUp;
            else model.Status = StatusKind.InSync;
            model.StatusText = t(StatusKey(model.Status), new object[] { input.TicksBehind });

            foreach (var player in OrderedPlayers(input.Players))
            {
                // The host has no ping to itself, and a guest's ping is to the host, so neither has one to show.
                bool noPing = player.IsHost || (input.IsHost && player.IsYou);
                model.Rows.Add(new PanelRow
                {
                    Name = player.Name,
                    Tag = Tag(player, t),
                    PingText = noPing ? "" : PingText(player.RttMs, player.SilenceSeconds, t),
                    // These rows are present, so they read as healthy unless the host has gone quiet.
                    Quality = noPing ? (player.IsHost && hostSilent ? Quality.Silent : Quality.Good) : Classify(player),
                });
            }

            // The pill shows one number: the worst ping the host sees, or this guest's own ping.
            var measured = input.IsHost ? others : input.Players.Where(p => p.IsYou).ToList();
            double? headline = measured.Where(p => p.RttMs != null).Select(p => (double?)p.RttMs).DefaultIfEmpty(null).Max();
            double? headlineSilence = measured.Select(p => p.SilenceSeconds).Where(s => s != null).DefaultIfEmpty(null).Max();
            if (!input.IsHost) headlineSilence = input.HostSilenceSeconds;
            model.SummaryQuality = model.Status == StatusKind.Disconnected ? Quality.Silent : PingQuality.Classify(headline, headlineSilence);
            int count = input.Players.Count;
            string people = t(count == 1 ? "BeaverBuddies.Panel.PlayersOne" : "BeaverBuddies.Panel.PlayersMany", new object[] { count });
            model.Summary = model.Status == StatusKind.Disconnected
                ? t("BeaverBuddies.Panel.StatusDisconnected", Array.Empty<object>())
                : headline == null ? people : people + "  " + PingText(headline, null, t);

            model.TickRateText = input.TickRate == null
                ? Measuring(t)
                : t("BeaverBuddies.Panel.TickRateValue", new object[] { input.TickRate.Value.ToString("0.0", CultureInfo.InvariantCulture) });
            model.SpeedText = input.Speed <= 0
                ? t("BeaverBuddies.Panel.Paused", Array.Empty<object>())
                : t("BeaverBuddies.Panel.SpeedValue", new object[] { input.Speed.ToString("0.#", CultureInfo.InvariantCulture) });
            if (!input.IsHost)
                model.BehindText = t(input.TicksBehind == 1 ? "BeaverBuddies.Panel.TicksOne" : "BeaverBuddies.Panel.TicksMany",
                    new object[] { input.TicksBehind });

            if (input.IsHost)
            {
                int? worst = input.Players.Where(p => !p.IsYou && !p.IsHost && p.TicksBehind != null)
                    .Select(p => p.TicksBehind).DefaultIfEmpty(null).Max();
                if (worst != null)
                    model.GuestsBehindText = t(worst == 1 ? "BeaverBuddies.Panel.TicksOne" : "BeaverBuddies.Panel.TicksMany",
                        new object[] { worst.Value });
                if (input.HostPacingHolding)
                    model.PacingText = t("BeaverBuddies.Panel.PacingHolding", new object[0]);
                else if (input.HostPacingPercent < 100)
                    model.PacingText = t("BeaverBuddies.Panel.PacingValue", new object[] { input.HostPacingPercent });
            }

            // The host lists how each guest reaches it; a guest shows only how it reaches the host.
            var linked = input.IsHost ? input.Players.Where(p => !p.IsYou && !p.IsHost) : input.Players.Where(p => p.IsYou);
            var transports = linked.Select(p => p.Transport).Where(x => !string.IsNullOrEmpty(x)).Distinct().Select(x => LinkName(x, t)).ToList();
            model.LinkText = transports.Count == 0 ? null : string.Join(", ", transports);
            return model;
        }

        static Quality Classify(PanelPlayer p) => PingQuality.Classify(p.RttMs, p.SilenceSeconds);

        static IEnumerable<PanelPlayer> OrderedPlayers(IEnumerable<PanelPlayer> players) =>
            players.OrderBy(p => p.IsHost ? 0 : 1).ThenBy(p => p.Id);

        static string Tag(PanelPlayer p, Func<string, object[], string> t)
        {
            var tags = new List<string>();
            if (p.IsYou) tags.Add(t("BeaverBuddies.Panel.You", Array.Empty<object>()));
            if (p.IsHost) tags.Add(t("BeaverBuddies.Panel.Host", Array.Empty<object>()));
            return string.Join(" / ", tags);
        }

        static string PingText(double? rtt, double? silence, Func<string, object[], string> t)
        {
            if (silence != null && silence.Value >= PingQuality.SilentSeconds) return t("BeaverBuddies.Panel.NoResponse", Array.Empty<object>());
            return rtt == null ? Measuring(t) : t("BeaverBuddies.Panel.PingValue", new object[] { ((int)Math.Round(rtt.Value)).ToString(CultureInfo.InvariantCulture) });
        }

        static string Measuring(Func<string, object[], string> t) => t("BeaverBuddies.Panel.Measuring", Array.Empty<object>());

        static string LinkName(string transport, Func<string, object[], string> t)
        {
            if (transport == "Direct") return t("BeaverBuddies.Panel.LinkDirect", Array.Empty<object>());
            if (transport == "Steam") return t("BeaverBuddies.Panel.LinkSteam", Array.Empty<object>());
            return transport;
        }

        static string StatusKey(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.CatchingUp: return "BeaverBuddies.Panel.StatusCatchingUp";
                case StatusKind.WaitingForHost: return "BeaverBuddies.Panel.StatusWaiting";
                case StatusKind.Unstable: return "BeaverBuddies.Panel.StatusUnstable";
                case StatusKind.Desynced: return "BeaverBuddies.Panel.StatusDesynced";
                case StatusKind.Disconnected: return "BeaverBuddies.Panel.StatusDisconnected";
                default: return "BeaverBuddies.Panel.StatusInSync";
            }
        }
    }
}
