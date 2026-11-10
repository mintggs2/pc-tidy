using PcTidy.Core;

namespace PcTidy.UI.Flow;

/// <summary>Starting async work from a click handler without losing its errors.</summary>
public static class FlowTasks
{
    /// <summary>
    /// Runs <paramref name="task"/> to completion and shows a calm message if it fails, instead of letting the
    /// exception vanish (or crash the app through the unobserved-task handler). Cancellation is not an error.
    /// </summary>
    public static async void Forget(this Task task, IFlowHost? host, string title = "That didn't finish")
    {
        try { await task; }
        catch (OperationCanceledException) { /* the user stopped it */ }
        catch (Exception ex)
        {
            try { host?.ShowMessage(title, ex.Message, isError: true); }
            catch { /* the window is closing: nothing left to tell */ }
        }
    }
}
