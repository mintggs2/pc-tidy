using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Advisor;
using PcTidy.Features.Learning.Model;
using PcTidy.UI.Flow;

namespace PcTidy.App.Composition;

/// <summary>
/// The flow pages' view of "Learns from me" (<see cref="IFlowLearning"/>), forwarding to the Learning section's
/// <see cref="ILearningAdvisor"/> when one is registered. Looked up on every call, so it works whether or not Learning
/// is part of this build, and does nothing while learning is switched off in Settings. Never throws into a flow.
/// </summary>
public sealed class LearningBridge : IFlowLearning
{
    private readonly IServiceProvider _services;

    public LearningBridge(IServiceProvider services) => _services = services;

    private ILearningAdvisor? Advisor
    {
        get
        {
            if (_services.GetService(typeof(AppSettings)) is AppSettings { LearningEnabled: false }) return null;
            try { return _services.GetService(typeof(ILearningAdvisor)) as ILearningAdvisor; }
            catch (Exception ex) { Shell.CrashHandler.Write(ex, "Learning advisor", terminating: false); return null; }
        }
    }

    public bool IsUntickedByDefault(string featureId, string groupId) => Try(a => a.IsUntickedByDefault(featureId, groupId), false);

    public string? WhyUnticked(string featureId, string groupId) => Try(a => a.SkipAdvice(featureId, groupId)?.Reason, null);

    public void ResetSkip(string featureId, string groupId) => Try(a => { a.ResetSkip(featureId, groupId); return true; }, false);

    public void RecordDecisions(IReadOnlyList<GroupDecision> decisions, IReadOnlyList<RunRecord> runs)
    {
        if (decisions.Count == 0 || runs.All(r => r.Mode != ExecutionMode.Real)) return;
        Try(a =>
        {
            if (a.IsPaused) return false;
            foreach (var d in decisions)
            {
                var run = runs.FirstOrDefault(r => string.Equals(r.FeatureId, d.FeatureId, StringComparison.OrdinalIgnoreCase)) ?? runs[0];
                a.RecordDecision(d.FeatureId, d.GroupId, d.Accepted ? LearningDecision.Accept : LearningDecision.Reject, run.RunId, d.GroupTitle, d.Example);
            }
            return true;
        }, false);
    }

    public void RecordUndo(IReadOnlyList<RunRecord> runs)
        => Try(a => { foreach (var r in runs) a.RecordUndo(r.RunId); return true; }, false);

    /// <summary>Opening a folder through PC Tidy counts as a visit (frequent folders).</summary>
    public void RecordFolderVisit(string folder) => Try(a => { a.RecordFolderVisit(folder); return true; }, false);

    private T Try<T>(Func<ILearningAdvisor, T> call, T fallback)
    {
        if (Advisor is not { } advisor) return fallback;
        try { return call(advisor); }
        catch (Exception ex)
        {
            // Learning is a nice-to-have: a broken learning file must never stop a clean-up.
            Shell.CrashHandler.Write(ex, "Learning", terminating: false);
            return fallback;
        }
    }
}
