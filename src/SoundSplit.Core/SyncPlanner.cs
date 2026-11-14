using System;
using System.Collections.Generic;
using System.Linq;

namespace SoundSplit.Core;

/// <summary>What the microphone measured for one speaker.</summary>
/// <param name="Id">Device id.</param>
/// <param name="DirectLatency">
/// Seconds from "Windows mixed this sound for the device" to "the microphone heard it". This is
/// how late the device is when Windows plays to it directly.
/// </param>
/// <param name="StreamLatency">
/// Seconds from "the app's own stream reached this point" to "the microphone heard it". This is
/// how late the device is, beyond what its own clock admits to, when the app feeds it.
/// </param>
public sealed record SpeakerMeasurement(string Id, double DirectLatency, double StreamLatency);

/// <param name="LeaderId">The device Windows should play to directly (it cannot be delayed, so it must be the slowest).</param>
/// <param name="Delays">For every other device: how long to hold audio back so it lands together with the leader.</param>
/// <param name="Residuals">For every other device: how far off it still is (0 when it can be matched exactly).</param>
public sealed record SyncPlan(
    string LeaderId,
    IReadOnlyDictionary<string, double> Delays,
    IReadOnlyDictionary<string, double> Residuals)
{
    public double WorstResidual => Residuals.Count == 0 ? 0 : Residuals.Values.Max();
}

public static class SyncPlanner
{
    /// <summary>
    /// Chooses which device Windows plays to directly and how long to delay the others.
    ///
    /// Sound Windows plays directly cannot be held back, only the copies the app sends can. So
    /// the slowest speaker has to be the direct one, and every faster speaker is delayed to meet
    /// it. Copying also costs a little time (<paramref name="minDelay"/>), so a speaker that is
    /// only very slightly faster than the leader ends up a few milliseconds late; that leftover
    /// is reported as its residual.
    /// </summary>
    /// <param name="preferredLeader">Kept as leader when it works exactly, to avoid switching the Windows default needlessly.</param>
    public static SyncPlan Plan(IReadOnlyList<SpeakerMeasurement> speakers, double minDelay, string? preferredLeader = null)
    {
        if (speakers.Count == 0) throw new ArgumentException("No speakers.", nameof(speakers));

        SpeakerMeasurement? best = null;
        double bestWorst = double.PositiveInfinity;
        foreach (var leader in speakers)
        {
            double worst = 0;
            foreach (var s in speakers)
            {
                if (ReferenceEquals(s, leader)) continue;
                double ideal = leader.DirectLatency - s.StreamLatency;
                worst = Math.Max(worst, Math.Max(0, minDelay - ideal));
            }

            bool better = worst < bestWorst - 1e-4
                || (Math.Abs(worst - bestWorst) <= 1e-4 && leader.Id == preferredLeader);
            if (best == null || better)
            {
                best = leader;
                bestWorst = worst;
            }
        }

        var delays = new Dictionary<string, double>();
        var residuals = new Dictionary<string, double>();
        foreach (var s in speakers)
        {
            if (ReferenceEquals(s, best)) continue;
            double ideal = best!.DirectLatency - s.StreamLatency;
            double delay = Math.Max(minDelay, ideal);
            delays[s.Id] = delay;
            residuals[s.Id] = delay - ideal;
        }
        return new SyncPlan(best!.Id, delays, residuals);
    }
}
