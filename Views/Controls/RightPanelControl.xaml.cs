using System.Windows.Controls;

namespace Zexus.Views.Controls
{
    /// <summary>
    /// Right Panel: Model Health (top) + Final Results (bottom). All data flows
    /// through <see cref="ViewModels.RightPanelViewModel"/> — no code-behind logic.
    /// </summary>
    public partial class RightPanelControl : UserControl
    {
        public RightPanelControl()
        {
            InitializeComponent();
        }
    }
}
