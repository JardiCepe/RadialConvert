using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace RadialConvert;

public partial class ImageOrderWindow : Window
{
    public sealed class ImagePageItem : INotifyPropertyChanged
    {
        private int _pageNumber;

        public string FilePath { get; init; } = string.Empty;
        public string ShortFileName { get; init; } = string.Empty;
        public ImageSource? Thumbnail { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }

        public string DimensionsLabel => PixelWidth > 0 && PixelHeight > 0
            ? $"{PixelWidth} × {PixelHeight} px"
            : string.Empty;

        public int PageNumber
        {
            get => _pageNumber;
            set
            {
                if (_pageNumber != value)
                {
                    _pageNumber = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PageLabel));
                }
            }
        }

        public string PageLabel => $"Pág. {PageNumber}";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly ObservableCollection<ImagePageItem> _items = new();
    private readonly string _sourceDirectory;
    private System.Windows.Point _dragStartPoint;
    private ImagePageItem? _draggedItem;
    private bool _isGeneratingPdf;

    public string? GeneratedPdfPath { get; private set; }

    public ImageOrderWindow(IEnumerable<string> imagePaths, string activeTheme)
    {
        InitializeComponent();

        ApplyModalTheme(activeTheme);

        List<string> validPaths = imagePaths
            .Where(File.Exists)
            .ToList();

        _sourceDirectory = validPaths.Count > 0
            ? (Path.GetDirectoryName(validPaths[0]) ?? AppDomain.CurrentDomain.BaseDirectory)
            : AppDomain.CurrentDomain.BaseDirectory;

        // Nombre por defecto: nombre de la primera imagen + "_unidas.pdf"
        string firstBaseName = validPaths.Count > 0
            ? Path.GetFileNameWithoutExtension(validPaths[0])
            : "imagenes";
        OutputFileNameTextBox.Text = $"{firstBaseName}_unidas.pdf";

        int index = 1;
        foreach (string path in validPaths)
        {
            (ImageSource? thumb, int width, int height) = LoadThumbnailAndDimensions(path);
            _items.Add(new ImagePageItem
            {
                FilePath = path,
                ShortFileName = Path.GetFileName(path),
                Thumbnail = thumb,
                PixelWidth = width,
                PixelHeight = height,
                PageNumber = index++
            });
        }

        ImagesItemsControl.ItemsSource = _items;
        RefreshHeaderAndPageNumbers();
    }

    private void ApplyModalTheme(string themeName)
    {
        bool isJardi = string.Equals(themeName, "Jardi", StringComparison.OrdinalIgnoreCase);

        if (isJardi)
        {
            // Tema "Jardi" (Dark Liquid Glass)
            RadialGradientBrush jardiBrush = new()
            {
                GradientOrigin = new System.Windows.Point(0.35, 0.3),
                Center = new System.Windows.Point(0.5, 0.5),
                RadiusX = 0.65,
                RadiusY = 0.65
            };
            jardiBrush.GradientStops.Add(new GradientStop(ParseColor("#D9242428"), 0.0));
            jardiBrush.GradientStops.Add(new GradientStop(ParseColor("#CC141417"), 0.65));
            jardiBrush.GradientStops.Add(new GradientStop(ParseColor("#BF0B0B0D"), 1.0));

            MainGlassBorder.Background = jardiBrush;
            ListContainerBorder.Background = new SolidColorBrush(ParseColor("#660A0A0C"));
            OutputFileNameTextBox.Background = new SolidColorBrush(ParseColor("#73121215"));
            CreatePdfButton.Background = new SolidColorBrush(ParseColor("#CC3B82F6"));
        }
        else
        {
            // Tema "Cami" (Pink Liquid Glass)
            LinearGradientBrush camiBrush = new()
            {
                StartPoint = new System.Windows.Point(0.1, 0.0),
                EndPoint = new System.Windows.Point(0.9, 1.0)
            };
            camiBrush.GradientStops.Add(new GradientStop(ParseColor("#CCFCE7F3"), 0.0));
            camiBrush.GradientStops.Add(new GradientStop(ParseColor("#BFE879F9"), 0.5));
            camiBrush.GradientStops.Add(new GradientStop(ParseColor("#B3EC4899"), 1.0));

            MainGlassBorder.Background = camiBrush;
            ListContainerBorder.Background = new SolidColorBrush(ParseColor("#4D1F121A"));
            OutputFileNameTextBox.Background = new SolidColorBrush(ParseColor("#591F121A"));
            CreatePdfButton.Background = new SolidColorBrush(ParseColor("#CCDB2777"));
        }
    }

    private static (ImageSource? Thumbnail, int Width, int Height) LoadThumbnailAndDimensions(string imagePath)
    {
        try
        {
            int width = 0;
            int height = 0;

            IImageInfo? info = SixLabors.ImageSharp.Image.Identify(imagePath);
            if (info != null)
            {
                width = info.Width;
                height = info.Height;
            }

            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext == ".webp")
            {
                using FileStream fs = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using SixLabors.ImageSharp.Image webp = SixLabors.ImageSharp.Image.Load(fs);
                if (width == 0 || height == 0)
                {
                    width = webp.Width;
                    height = webp.Height;
                }

                using MemoryStream ms = new();
                webp.Save(ms, new PngEncoder());
                ms.Position = 0;

                BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return (bmp, width, height);
            }
            else
            {
                using FileStream fs = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                BitmapImage bmp = new();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                return (bmp, width, height);
            }
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    private void RefreshHeaderAndPageNumbers()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].PageNumber = i + 1;
        }

        TitleTextBlock.Text = $"Organizar páginas del PDF ({_items.Count} {(_items.Count == 1 ? "imagen" : "imágenes")})";
        CreatePdfButton.IsEnabled = _items.Count > 0 && !_isGeneratingPdf;
    }

    #region Reordering Controls (Buttons ▲ / ▼, Remove ✕, and Drag & Drop)

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ImagePageItem item)
        {
            int index = _items.IndexOf(item);
            if (index > 0)
            {
                _items.Move(index, index - 1);
                RefreshHeaderAndPageNumbers();
            }
        }
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ImagePageItem item)
        {
            int index = _items.IndexOf(item);
            if (index >= 0 && index < _items.Count - 1)
            {
                _items.Move(index, index + 1);
                RefreshHeaderAndPageNumbers();
            }
        }
    }

    private void RemoveItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ImagePageItem item)
        {
            _items.Remove(item);
            RefreshHeaderAndPageNumbers();

            if (_items.Count == 0)
            {
                DialogResult = false;
                Close();
            }
        }
    }

    private void ItemCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Evitar iniciar Drag & Drop si el clic fue sobre un botón (▲, ▼, ✕)
        if (IsClickInsideButton(e.OriginalSource as DependencyObject))
            return;

        _dragStartPoint = e.GetPosition(null);
        _draggedItem = (sender as FrameworkElement)?.Tag as ImagePageItem;
    }

    private void ItemCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null)
            return;

        System.Windows.Point currentPos = e.GetPosition(null);
        Vector diff = _dragStartPoint - currentPos;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is DependencyObject dragSource)
            {
                DragDrop.DoDragDrop(dragSource, _draggedItem, DragDropEffects.Move);
                _draggedItem = null;
            }
        }
    }

    private void ItemCard_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ImagePageItem)) && sender is Border cardBorder)
        {
            cardBorder.BorderBrush = Brushes.White;
            cardBorder.BorderThickness = new Thickness(1.6);
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void ItemCard_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border cardBorder)
        {
            cardBorder.BorderBrush = new SolidColorBrush(ParseColor("#45FFFFFF"));
            cardBorder.BorderThickness = new Thickness(1.0);
        }
        e.Handled = true;
    }

    private void ItemCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border cardBorder)
        {
            cardBorder.BorderBrush = new SolidColorBrush(ParseColor("#45FFFFFF"));
            cardBorder.BorderThickness = new Thickness(1.0);
        }

        if (e.Data.GetDataPresent(typeof(ImagePageItem)) &&
            e.Data.GetData(typeof(ImagePageItem)) is ImagePageItem sourceItem &&
            (sender as FrameworkElement)?.Tag is ImagePageItem targetItem &&
            !ReferenceEquals(sourceItem, targetItem))
        {
            int oldIndex = _items.IndexOf(sourceItem);
            int newIndex = _items.IndexOf(targetItem);

            if (oldIndex >= 0 && newIndex >= 0)
            {
                _items.Move(oldIndex, newIndex);
                RefreshHeaderAndPageNumbers();
            }
        }
        e.Handled = true;
    }

    private static bool IsClickInsideButton(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    #endregion

    #region Window Controls & Multi-Page PDF Generation (PdfSharp v1.50.5147)

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_isGeneratingPdf)
        {
            e.Handled = true;
            DialogResult = false;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isGeneratingPdf)
            return;

        DialogResult = false;
        Close();
    }

    private async void CreatePdfButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isGeneratingPdf || _items.Count == 0)
            return;

        string requestedFileName = OutputFileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(requestedFileName))
        {
            requestedFileName = $"{Path.GetFileNameWithoutExtension(_items[0].FilePath)}_unidas.pdf";
        }

        if (!requestedFileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            requestedFileName += ".pdf";
        }

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            requestedFileName = requestedFileName.Replace(invalidChar, '_');
        }

        string targetOutputPath = GetUniqueMergedPdfPath(_sourceDirectory, requestedFileName);
        List<string> orderedPaths = _items.Select(i => i.FilePath).ToList();

        _isGeneratingPdf = true;
        CreatePdfButton.IsEnabled = false;
        CreatePdfButton.Content = "Creando PDF...";
        HintStatusText.Text = "Generando páginas con PdfSharp...";

        try
        {
            await Task.Run(() => GenerateMergedPdf(orderedPaths, targetOutputPath));

            GeneratedPdfPath = targetOutputPath;
            SystemSounds.Asterisk.Play();
            HintStatusText.Text = "✅ ¡PDF creado con éxito!";
            await Task.Delay(350);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _isGeneratingPdf = false;
            CreatePdfButton.IsEnabled = true;
            CreatePdfButton.Content = "Crear PDF";
            HintStatusText.Text = "Error al generar el PDF";

            MessageBox.Show(
                $"No se pudo crear el documento PDF combinado:\n\n{ex.Message}",
                "RadialConvert - Error al crear PDF",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Crea un único documento PDF iterando las imágenes en el orden decidido por el usuario.
    /// Cada página toma las dimensiones originales exactas de su imagen correspondiente.
    /// </summary>
    public static void GenerateMergedPdf(IReadOnlyList<string> orderedImagePaths, string outputPath)
    {
        List<string> tempFilesToClean = new();

        try
        {
            using PdfDocument document = new();

            foreach (string imagePath in orderedImagePaths)
            {
                if (!File.Exists(imagePath))
                    continue;

                string ext = Path.GetExtension(imagePath).ToLowerInvariant();
                int width;
                int height;
                string fileForPdfSharp = imagePath;

                if (ext == ".webp")
                {
                    IImageInfo? imageInfo = SixLabors.ImageSharp.Image.Identify(imagePath);
                    if (imageInfo != null)
                    {
                        width = imageInfo.Width;
                        height = imageInfo.Height;
                    }
                    else
                    {
                        using SixLabors.ImageSharp.Image webpImg = SixLabors.ImageSharp.Image.Load(imagePath);
                        width = webpImg.Width;
                        height = webpImg.Height;
                    }

                    string tempPng = Path.Combine(Path.GetTempPath(), $"radialconvert_page_{Guid.NewGuid():N}.png");
                    tempFilesToClean.Add(tempPng);

                    using (FileStream inputStream = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (SixLabors.ImageSharp.Image webpImage = SixLabors.ImageSharp.Image.Load(inputStream))
                    {
                        webpImage.Save(tempPng, new PngEncoder());
                    }

                    fileForPdfSharp = tempPng;
                }
                else
                {
                    IImageInfo? info = SixLabors.ImageSharp.Image.Identify(imagePath);
                    if (info != null)
                    {
                        width = info.Width;
                        height = info.Height;
                    }
                    else
                    {
                        using XImage probeImg = XImage.FromFile(imagePath);
                        width = probeImg.PixelWidth;
                        height = probeImg.PixelHeight;
                    }
                }

                PdfPage page = document.AddPage();
                page.Width = XUnit.FromPoint(Math.Max(1, width));
                page.Height = XUnit.FromPoint(Math.Max(1, height));

                using XGraphics gfx = XGraphics.FromPdfPage(page);
                using XImage xImg = XImage.FromFile(fileForPdfSharp);
                gfx.DrawImage(xImg, 0, 0, page.Width, page.Height);
            }

            if (document.PageCount == 0)
            {
                throw new InvalidOperationException("No se encontraron imágenes válidas para agregar al PDF.");
            }

            document.Save(outputPath);
        }
        finally
        {
            foreach (string tempFile in tempFilesToClean)
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }
    }

    private static string GetUniqueMergedPdfPath(string directory, string desiredFileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(desiredFileName);
        string candidatePath = Path.Combine(directory, $"{baseName}.pdf");

        int counter = 2;
        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(directory, $"{baseName}_{counter}.pdf");
            counter++;
        }

        return candidatePath;
    }

    private static System.Windows.Media.Color ParseColor(string hex) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    #endregion
}

