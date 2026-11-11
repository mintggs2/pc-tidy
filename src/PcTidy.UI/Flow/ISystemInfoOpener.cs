using System.Windows;

namespace PcTidy.UI.Flow;

/// <summary>
/// Opens the read-only System info window (layout spec §4.5, built by Insights). Home's "System info" button uses it
/// when the shell's services have one; otherwise the shell looks for
/// <c>PcTidy.Features.Insights.SystemInfoWindow.Show(Window owner, IServiceProvider services)</c> and hides the button
/// when neither exists. Showing it never changes anything.
/// </summary>
public interface ISystemInfoOpener
{
    void Show(Window owner, IServiceProvider services);
}
