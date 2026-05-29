using System.Windows;
using System.Windows.Controls;
using Zexus.Models;
using Zexus.ViewModels;

namespace Zexus.Views.Converters
{
    /// <summary>
    /// Picks the right DataTemplate (User / Agent / System) for an ItemsControl
    /// bound to <see cref="ChatViewModel.Messages"/>.
    /// </summary>
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate AgentTemplate { get; set; }
        public DataTemplate SystemTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is MessageViewModel msg)
            {
                switch (msg.Role)
                {
                    case MessageRole.User: return UserTemplate;
                    case MessageRole.Assistant: return AgentTemplate;
                    case MessageRole.System: return SystemTemplate;
                }
            }
            return base.SelectTemplate(item, container);
        }
    }
}
