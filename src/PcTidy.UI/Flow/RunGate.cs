using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace PcTidy.UI.Flow;

/// <summary>
/// One long task at a time (layout spec §4.6 item 3). While a run, an undo, a resume or a roll back is going, every
/// Run, Preview and Simulate button in PC Tidy is disabled and says why, in text: <see cref="BusyReason"/>.
/// The shell's flow host opens a scope with <see cref="Begin"/> for each task; pages read <see cref="IsBusy"/> and
/// listen to <see cref="Changed"/>. A page that isn't built on the standard flow page can opt a button in with
/// <c>flow:RunGate.DisableWhileBusy="True"</c> (see <see cref="RunGateButtons"/>).
/// Scanning only reads and is never blocked by this.
/// </summary>
public sealed class RunGate
{
    /// <summary>The reason shown next to (and on) every button that waits, when the task has no short name.</summary>
    public const string BusyReason = "Available when the current task finishes";

    /// <summary>The app-wide gate the shell uses.</summary>
    public static RunGate Shared { get; } = new();

    private readonly object _lock = new();
    private readonly List<Scope> _active = new();

    /// <summary>True while a long task is going.</summary>
    public bool IsBusy { get { lock (_lock) return _active.Count > 0; } }

    /// <summary>The short name of what is going on right now ("Clean", "the undo"), or null when idle. The latest still-running task wins.</summary>
    public string? CurrentTask { get { lock (_lock) return _active.Count > 0 ? _active[^1].Task : null; } }

    /// <summary>The reason text while busy ("Available when Clean finishes"), null when idle.</summary>
    public string? Reason => CurrentTask is { } task ? ReasonFor(task) : IsBusy ? BusyReason : null;

    /// <summary>
    /// "Available when Clean finishes" for a short task name; <see cref="BusyReason"/> for an empty or long one (a
    /// progress title such as "Working through every section…" isn't a name).
    /// </summary>
    public static string ReasonFor(string? task)
    {
        var t = task?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 40 || t.Contains('…') || t.Contains(':')) return BusyReason;
        return $"Available when {t} finishes";
    }

    /// <summary>Raised when <see cref="IsBusy"/> flips (on the thread that started or ended the task).</summary>
    public event EventHandler? Changed;

    /// <summary>Marks a long task as started; dispose the result when it ends. Nested tasks are counted.</summary>
    /// <param name="task">A short name for the busy reason ("Clean", "the undo"), see <see cref="ReasonFor"/>.</param>
    public IDisposable Begin(string task)
    {
        var scope = new Scope(this, task);
        bool flipped;
        lock (_lock)
        {
            flipped = _active.Count == 0;
            _active.Add(scope);
        }
        if (flipped) Changed?.Invoke(this, EventArgs.Empty);
        return scope;
    }

    private void End(Scope scope)
    {
        bool flipped;
        lock (_lock)
        {
            if (!_active.Remove(scope)) return;
            flipped = _active.Count == 0;
        }
        if (flipped) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A button that would start something can do so only when it could anyway and nothing else is going.</summary>
    public bool Allows(bool couldRun) => couldRun && !IsBusy;

    private sealed class Scope(RunGate gate, string task) : IDisposable
    {
        private int _done;
        public string Task { get; } = task;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.End(this);
        }
    }

    // ---------------- attached property for pages that aren't standard flow pages ----------------

    /// <summary>
    /// <c>flow:RunGate.DisableWhileBusy="True"</c> on a Run, Preview or Simulate button: disabled (with
    /// <see cref="BusyReason"/> as its tooltip and help text) while <see cref="Shared"/> is busy, and back to what the
    /// page wanted afterwards. The page can keep setting IsEnabled as usual; the gate remembers the latest wish.
    /// </summary>
    public static readonly DependencyProperty DisableWhileBusyProperty = DependencyProperty.RegisterAttached(
        "DisableWhileBusy", typeof(bool), typeof(RunGate), new PropertyMetadata(false, (d, e) => RunGateButtons.Attach(d as FrameworkElement, (bool)e.NewValue)));

    public static bool GetDisableWhileBusy(DependencyObject d) => (bool)d.GetValue(DisableWhileBusyProperty);
    public static void SetDisableWhileBusy(DependencyObject d, bool value) => d.SetValue(DisableWhileBusyProperty, value);
}

/// <summary>The behaviour behind <see cref="RunGate.DisableWhileBusyProperty"/>.</summary>
public static class RunGateButtons
{
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("RunGateState", typeof(State), typeof(RunGateButtons));

    internal static void Attach(FrameworkElement? element, bool on)
    {
        if (element is null) return;
        var state = element.GetValue(StateProperty) as State;
        if (on && state is null)
        {
            state = new State(element, RunGate.Shared);
            element.SetValue(StateProperty, state);
            state.Connect();
        }
        else if (!on && state is not null)
        {
            state.Disconnect();
            element.ClearValue(StateProperty);
        }
    }

    /// <summary>Gates one element on a given gate (tests use their own gate). Returns a handle that undoes it.</summary>
    public static IDisposable Gate(FrameworkElement element, RunGate gate)
    {
        var state = new State(element, gate);
        state.Connect();
        return state;
    }

    private sealed class State : IDisposable
    {
        private static readonly DependencyPropertyDescriptor EnabledDescriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(UIElement));
        private readonly FrameworkElement _element;
        private readonly RunGate _gate;
        private bool _forcing, _setting, _connected;
        private object? _wanted; // the page's own local IsEnabled value (DependencyProperty.UnsetValue = none)
        private object? _oldTip, _oldHelp;

        public State(FrameworkElement element, RunGate gate)
        {
            _element = element;
            _gate = gate;
        }

        public void Connect()
        {
            if (_connected) return;
            _connected = true;
            _gate.Changed += OnGateChanged;
            EnabledDescriptor.AddValueChanged(_element, OnEnabledChanged);
            // Elements that leave the tree drop their subscriptions (no leaks); coming back re-applies the state.
            _element.Unloaded += OnUnloaded;
            _element.Loaded += OnLoaded;
            Apply();
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;
            _gate.Changed -= OnGateChanged;
            EnabledDescriptor.RemoveValueChanged(_element, OnEnabledChanged);
            _element.Unloaded -= OnUnloaded;
            _element.Loaded -= OnLoaded;
            Release();
        }

        public void Dispose() => Disconnect();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _gate.Changed -= OnGateChanged;
            _gate.Changed += OnGateChanged;
            Apply();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => _gate.Changed -= OnGateChanged;

        private void OnGateChanged(object? sender, EventArgs e)
        {
            if (_element.Dispatcher.CheckAccess()) Apply();
            else _element.Dispatcher.BeginInvoke(Apply);
        }

        private void Apply()
        {
            if (_gate.IsBusy) Force();
            else Release();
        }

        private void Force()
        {
            if (_forcing) return;
            _wanted = _element.ReadLocalValue(UIElement.IsEnabledProperty);
            _oldTip = _element.ReadLocalValue(FrameworkElement.ToolTipProperty);
            _oldHelp = _element.ReadLocalValue(AutomationProperties.HelpTextProperty);
            _forcing = true;
            Set(() =>
            {
                _element.IsEnabled = false;
                var reason = _gate.Reason ?? RunGate.BusyReason;
                _element.ToolTip = reason;
                ToolTipService.SetShowOnDisabled(_element, true);
                AutomationProperties.SetHelpText(_element, reason);
            });
        }

        private void Release()
        {
            if (!_forcing) return;
            _forcing = false;
            Set(() =>
            {
                Restore(UIElement.IsEnabledProperty, _wanted);
                Restore(FrameworkElement.ToolTipProperty, _oldTip);
                Restore(AutomationProperties.HelpTextProperty, _oldHelp);
            });
        }

        private void Restore(DependencyProperty p, object? value)
        {
            if (value is null || value == DependencyProperty.UnsetValue) _element.ClearValue(p);
            else _element.SetValue(p, value);
        }

        /// <summary>The page set IsEnabled while the gate holds it: remember the wish, keep it disabled.</summary>
        private void OnEnabledChanged(object? sender, EventArgs e)
        {
            if (!_forcing || _setting || !_element.IsEnabled) return;
            _wanted = _element.ReadLocalValue(UIElement.IsEnabledProperty);
            Set(() => _element.IsEnabled = false);
        }

        private void Set(Action change)
        {
            _setting = true;
            try { change(); }
            finally { _setting = false; }
        }
    }
}
