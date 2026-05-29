using System;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Zexus.ViewModels
{
    /// <summary>
    /// Base class for all Zexus ViewModels. Extends <see cref="ObservableObject"/>
    /// from CommunityToolkit.Mvvm with a Dispatcher helper so AgentService callbacks
    /// (which run on background threads) can safely update bindable properties.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
        protected static Dispatcher UiDispatcher => Application.Current?.Dispatcher;

        /// <summary>
        /// Run <paramref name="action"/> on the UI thread. Safe to call from any thread.
        /// Used by AgentService event handlers (OnStreamingText, OnToolExecuting, etc.)
        /// which fire on whatever thread the LLM client is running on.
        /// </summary>
        protected void RunOnUi(Action action)
        {
            if (action == null) return;
            var d = UiDispatcher;
            if (d != null && !d.CheckAccess())
                d.BeginInvoke(action);
            else
                action();
        }
    }
}
