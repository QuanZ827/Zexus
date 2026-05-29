using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zexus.Models;
using Zexus.ViewModels;

namespace Zexus.Views.Controls
{
    /// <summary>
    /// Input bar: textbox + send button + image-paste preview. Key handling
    /// (Enter to send, Ctrl+V to paste an image) stays as code-behind because both
    /// are pure UI events — the VM only sees the resulting <see cref="ChatViewModel.InputText"/>
    /// changes and <see cref="ChatViewModel.PendingImages"/> additions.
    /// </summary>
    public partial class InputBarControl : UserControl
    {
        private const int MAX_IMAGES_PER_MESSAGE = 3;
        private ChatViewModel _vm;

        public InputBarControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_vm != null)
                _vm.PendingImages.CollectionChanged -= OnPendingImagesChanged;

            _vm = e.NewValue as ChatViewModel;

            if (_vm != null)
            {
                _vm.PendingImages.CollectionChanged += OnPendingImagesChanged;
                RebuildImagePreview();
            }
        }

        private void OnPendingImagesChanged(object sender, NotifyCollectionChangedEventArgs e)
            => RebuildImagePreview();

        /// <summary>Focus the input box (called by ChatWindow when the user opens a new chat).</summary>
        public void FocusInput() => MessageInput.Focus();

        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                if (_vm?.SendCommand?.CanExecute(null) == true)
                    _vm.SendCommand.Execute(null);
            }
        }

        /// <summary>
        /// Intercepts Ctrl+V — if the clipboard contains an image and we're under
        /// MAX_IMAGES_PER_MESSAGE, encodes it as PNG and adds it to PendingImages.
        ///
        /// Silent no-ops (fall through to default paste):
        ///   - Clipboard has no image
        ///   - Already 3 pending images
        ///   - PngBitmapEncoder produces empty bytes
        /// </summary>
        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.V) return;
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (!Clipboard.ContainsImage()) return;
            if (_vm == null || _vm.PendingImages.Count >= MAX_IMAGES_PER_MESSAGE) return;

            BitmapSource bitmapSource;
            try { bitmapSource = Clipboard.GetImage(); }
            catch { return; }

            if (bitmapSource == null) return;

            byte[] pngBytes;
            try { pngBytes = ConvertToPngBytes(bitmapSource); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Zexus] Image paste encode failed: {ex.Message}");
                return;
            }

            if (pngBytes == null || pngBytes.Length == 0) return;

            _vm.PendingImages.Add(new ImageAttachment { Data = pngBytes });
            e.Handled = true; // critical — without this WPF would also paste the bitmap as garbage text
        }

        /// <summary>
        /// Encode a clipboard BitmapSource to PNG bytes, downscaling first if either
        /// dimension exceeds 2048px. Vision APIs tile internally, so don't compress harder
        /// than this — over-compression loses quality without saving meaningful tokens.
        /// </summary>
        private static byte[] ConvertToPngBytes(BitmapSource source)
        {
            const int MAX_DIM = 2048;
            if (source.PixelWidth > MAX_DIM || source.PixelHeight > MAX_DIM)
            {
                double scale = Math.Min(
                    (double)MAX_DIM / source.PixelWidth,
                    (double)MAX_DIM / source.PixelHeight);
                source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Rebuild the ImagePreviewPanel from PendingImages. Each entry shows a 48px-tall
        /// thumbnail plus an "x" remove button. When the list is empty, hide the whole bar.
        /// </summary>
        private void RebuildImagePreview()
        {
            ImagePreviewPanel.Children.Clear();

            if (_vm == null || _vm.PendingImages.Count == 0)
            {
                ImagePreviewBar.Visibility = Visibility.Collapsed;
                return;
            }

            ImagePreviewBar.Visibility = Visibility.Visible;

            for (int i = 0; i < _vm.PendingImages.Count; i++)
            {
                int capturedIndex = i;
                var imgData = _vm.PendingImages[i].Data;

                // DecodePixelHeight=48 lets WPF discard the high-res pixels so a 4K paste
                // doesn't keep a 4K BitmapSource alive in memory.
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgData);
                bmp.DecodePixelHeight = 48;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                var thumb = new Image
                {
                    Source = bmp,
                    Height = 48,
                    Margin = new Thickness(0, 0, 4, 0)
                };

                var removeBtn = new TextBlock
                {
                    Text = "x",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(-8, 0, 8, 0),
                    ToolTip = "Remove image"
                };
                removeBtn.MouseLeftButtonDown += (s, evt) =>
                {
                    if (_vm != null && capturedIndex < _vm.PendingImages.Count)
                        _vm.PendingImages.RemoveAt(capturedIndex);
                    evt.Handled = true;
                };

                ImagePreviewPanel.Children.Add(thumb);
                ImagePreviewPanel.Children.Add(removeBtn);
            }
        }
    }
}
