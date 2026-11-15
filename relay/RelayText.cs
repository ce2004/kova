using System;
using System.Globalization;

namespace KovaRelay
{
    /// <summary>
    /// What the relay says about its hosts and controlling PCs, in words. Kept apart from the window (OptionsForm.cs)
    /// because the relay on Linux has no window but says the same things, and the self-test checks them on both.
    /// </summary>
    internal static class RelayText
    {
        public static string CountsText(RelaySnapshot s) =>
            (s.Hosts switch { 0 => "No hosts are connected.", 1 => "1 host is connected.", _ => $"{s.Hosts} hosts are connected." }) + " " +
            (s.ControllingPcs switch
            {
                0 => "No controlling PCs are connected.",
                1 => "1 controlling PC is connected.",
                _ => $"{s.ControllingPcs} controlling PCs are connected.",
            });

        /// <summary>"Host connected from 203.0.113.5 since 14:02", with the date when it is not today, and its controlling PCs.</summary>
        public static string RowText(HostRow r)
        {
            DateTime local = r.SinceUtc.ToLocalTime();
            string since = local.Date == DateTime.Now.Date
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);
            string text = $"Host connected from {r.Address} since {since}";
            return r.ControllingPcs switch
            {
                0 => text,
                1 => text + ", 1 controlling PC",
                _ => text + $", {r.ControllingPcs} controlling PCs",
            };
        }
    }
}
