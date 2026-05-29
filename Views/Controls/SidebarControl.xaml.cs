using System.Windows.Controls;
using Zexus.ViewModels;

namespace Zexus.Views.Controls
{
    /// <summary>
    /// 56px icon rail on the left edge. All actions bound to <see cref="SidebarViewModel"/>
    /// commands — no event handlers in this code-behind.
    /// </summary>
    public partial class SidebarControl : UserControl
    {
        public SidebarControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is SidebarViewModel vm)
            {
                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(SidebarViewModel.IsDarkTheme))
                        ThemeIcon.Text = vm.IsDarkTheme ? "☀" : "☽";
                };
                ThemeIcon.Text = vm.IsDarkTheme ? "☀" : "☽";
            }
        }
    }
}
