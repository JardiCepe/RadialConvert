using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using Forms = System.Windows.Forms;

namespace RadialConvert;

public partial class MainWindow : Window
{
    #region Win32 Native API (Mouse Input & Cursor Position)

    private const int VK_LBUTTON = 0x01;
    private const int DRAG_MOVEMENT_THRESHOLD_PX = 6;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    #endregion

    private sealed class AppConfig
    {
        public string Theme { get; set; } = "Cami";
    }

    // Temporizadores para monitoreo de ratón y activación por arrastre sostenido (500 ms)
    private readonly DispatcherTimer _mouseMonitorTimer;
    private readonly DispatcherTimer _dwellActivationTimer;

    private bool _wasLeftMouseDown;
    private bool _hasDragStarted;
    private POINT _mouseDownInitialPos;

    private bool _isProcessingDrop;
    private bool _isApplyingThemeSelection;
    private bool _isWheelOpen;
    private bool _isClosingAnimationRunning;
    private string _currentTheme = "Cami";

    // Bandeja del sistema (System Tray)
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayCamiMenuItem;
    private Forms.ToolStripMenuItem? _trayJardiMenuItem;

    // Instancia precalentada de Microsoft Word para conversiones instantáneas sin arranque en frío
    private dynamic? _wordApp = null;
    private readonly object _wordLock = new();

    private static string ConfigFilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    static MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Inicializar icono de bandeja del sistema (System Tray)
        InitializeSystemTray();

        // Cargar y aplicar el tema persistido ("Cami" o "Jardi")
        LoadSavedTheme();

        // Precalentar la instancia de Word en segundo plano sin bloquear la UI
        Task.Run(PrewarmWordInstance);

        // Temporizador de 500 ms para desplegar la rueda tras mantener un arrastre sostenido
        _dwellActivationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _dwellActivationTimer.Tick += DwellActivationTimer_Tick;

        // Polling ligero del botón izquierdo del ratón (sin dependencia de SHIFT)
        _mouseMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _mouseMonitorTimer.Tick += MouseMonitorTimer_Tick;
        _mouseMonitorTimer.Start();

        Closed += OnWindowClosed;
    }

    #region System Tray Icon & Context Menu

    private void InitializeSystemTray()
    {
        Forms.ContextMenuStrip contextMenu = new();

        _trayCamiMenuItem = new Forms.ToolStripMenuItem("Tema actual: Cami (Rosa)")
        {
            CheckOnClick = false
        };
        _trayCamiMenuItem.Click += (_, _) =>
        {
            Dispatcher.Invoke(() => ApplyTheme("Cami", savePreference: true));
        };

        _trayJardiMenuItem = new Forms.ToolStripMenuItem("Tema actual: Jardi (Negro)")
        {
            CheckOnClick = false
        };
        _trayJardiMenuItem.Click += (_, _) =>
        {
            Dispatcher.Invoke(() => ApplyTheme("Jardi", savePreference: true));
        };

        Forms.ToolStripSeparator separator = new();

        Forms.ToolStripMenuItem exitMenuItem = new("Salir de RadialConvert");
        exitMenuItem.Click += (_, _) =>
        {
            Dispatcher.Invoke(Close);
        };

        contextMenu.Items.Add(_trayCamiMenuItem);
        contextMenu.Items.Add(_trayJardiMenuItem);
        contextMenu.Items.Add(separator);
        contextMenu.Items.Add(exitMenuItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = CreateCustomTrayIcon(),
            Text = "RadialConvert — Arrastra un archivo por 3s para convertir",
            ContextMenuStrip = contextMenu,
            Visible = true
        };
    }

    private static System.Drawing.Icon CreateCustomTrayIcon()
    {
        try
        {
            using System.Drawing.Bitmap bmp = new(32, 32);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                using System.Drawing.SolidBrush outerBrush = new(System.Drawing.Color.FromArgb(235, 244, 114, 182));
                using System.Drawing.Pen borderPen = new(System.Drawing.Color.White, 2f);
                g.FillEllipse(outerBrush, 2, 2, 28, 28);
                g.DrawEllipse(borderPen, 2, 2, 28, 28);

                using System.Drawing.SolidBrush innerBrush = new(System.Drawing.Color.FromArgb(230, 31, 18, 26));
                g.FillEllipse(innerBrush, 10, 10, 12, 12);
            }

            IntPtr hIcon = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    private void UpdateTrayMenuChecks()
    {
        bool isJardi = string.Equals(_currentTheme, "Jardi", StringComparison.OrdinalIgnoreCase);
        if (_trayCamiMenuItem != null)
        {
            _trayCamiMenuItem.Checked = !isJardi;
        }
        if (_trayJardiMenuItem != null)
        {
            _trayJardiMenuItem.Checked = isJardi;
        }
    }

    #endregion

    #region Theme Persistence & Liquid Glass Theme Switching ("Cami" / "Jardi")

    private void LoadSavedTheme()
    {
        string themeToApply = "Cami";
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                AppConfig? config = JsonSerializer.Deserialize<AppConfig>(json);
                if (!string.IsNullOrWhiteSpace(config?.Theme) &&
                    (config.Theme.Equals("Jardi", StringComparison.OrdinalIgnoreCase) ||
                     config.Theme.Equals("Cami", StringComparison.OrdinalIgnoreCase)))
                {
                    themeToApply = config.Theme.Equals("Jardi", StringComparison.OrdinalIgnoreCase) ? "Jardi" : "Cami";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadialConvert] No se pudo leer config.json: {ex.Message}");
        }

        ApplyTheme(themeToApply, savePreference: false);
    }

    private void SaveThemePreference(string themeName)
    {
        try
        {
            AppConfig config = new() { Theme = themeName };
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFilePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadialConvert] No se pudo guardar config.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica inmediatamente el tema "Cami" (Pink Liquid Glass) o "Jardi" (Dark Liquid Glass)
    /// a la rueda, cuadrantes, sombras, centro y menú de la bandeja del sistema.
    /// </summary>
    private void ApplyTheme(string themeName, bool savePreference = true)
    {
        bool isJardi = string.Equals(themeName, "Jardi", StringComparison.OrdinalIgnoreCase);
        _currentTheme = isJardi ? "Jardi" : "Cami";

        if (isJardi)
        {
            // Tema "Jardi" (Dark Liquid Glass)
            OuterGlassFrame.Background = (Brush)FindResource("JardiWheelBackgroundBrush");
            OuterGlassFrame.BorderBrush = (Brush)FindResource("JardiSpecularBorderBrush");
            WheelDropShadow.BlurRadius = 28;
            WheelDropShadow.Opacity = 0.55;
            WheelDropShadow.Color = Colors.Black;

            CenterHubBorder.Background = CreateBrush("#66141414");
            CenterHubBorder.BorderBrush = (Brush)FindResource("JardiSpecularBorderBrush");
        }
        else
        {
            // Tema "Cami" (Pink Liquid Glass)
            OuterGlassFrame.Background = (Brush)FindResource("CamiWheelBackgroundBrush");
            OuterGlassFrame.BorderBrush = (Brush)FindResource("CamiSpecularBorderBrush");
            WheelDropShadow.BlurRadius = 28;
            WheelDropShadow.Opacity = 0.45;
            WheelDropShadow.Color = Colors.Black;

            CenterHubBorder.Background = CreateBrush("#4D1F121A");
            CenterHubBorder.BorderBrush = (Brush)FindResource("CamiSpecularBorderBrush");
        }

        ResetAllSectorsVisuals();
        UpdateThemeSelectorIndicators();
        UpdateTrayMenuChecks();

        if (savePreference)
        {
            SaveThemePreference(_currentTheme);
        }
    }

    private void UpdateThemeSelectorIndicators()
    {
        bool isJardi = _currentTheme == "Jardi";
        BtnThemeCami.BorderBrush = CreateBrush(isJardi ? "#66FFFFFF" : "#FFFFFFFF");
        BtnThemeCami.BorderThickness = new Thickness(isJardi ? 1.0 : 1.8);

        BtnThemeJardi.BorderBrush = CreateBrush(isJardi ? "#FFFFFFFF" : "#66FFFFFF");
        BtnThemeJardi.BorderThickness = new Thickness(isJardi ? 1.8 : 1.0);
    }

    private async void BtnThemeCami_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await SelectThemeWithFeedbackAsync("Cami");
    }

    private async void BtnThemeJardi_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await SelectThemeWithFeedbackAsync("Jardi");
    }

    private async Task SelectThemeWithFeedbackAsync(string selectedTheme)
    {
        if (_isApplyingThemeSelection)
            return;

        _isApplyingThemeSelection = true;
        try
        {
            ApplyTheme(selectedTheme, savePreference: true);
            CenterIcon.Text = "✨";
            CenterStatusText.Text = $"Tema {selectedTheme}";
            await Task.Delay(320);
        }
        finally
        {
            _isApplyingThemeSelection = false;
            HideWheelAnimated();
        }
    }

    private void ThemeButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
        {
            string tag = card.Tag?.ToString() ?? string.Empty;
            bool isJardiCard = string.Equals(tag, "Jardi", StringComparison.OrdinalIgnoreCase);

            AnimateThemeCardHover(
                card,
                isHovering: true,
                hoverBgHex: isJardiCard ? "#99262626" : "#99F472B6",
                normalBgHex: isJardiCard ? "#80141414" : "#73F472B6");

            CenterIcon.Text = "🎨";
            CenterStatusText.Text = tag;
        }
    }

    private void ThemeButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border card)
        {
            string tag = card.Tag?.ToString() ?? string.Empty;
            bool isJardiCard = string.Equals(tag, "Jardi", StringComparison.OrdinalIgnoreCase);

            AnimateThemeCardHover(
                card,
                isHovering: false,
                hoverBgHex: isJardiCard ? "#99262626" : "#99F472B6",
                normalBgHex: isJardiCard ? "#80141414" : "#73F472B6");

            if (!_isApplyingThemeSelection)
            {
                CenterIcon.Text = "🎨";
                CenterStatusText.Text = _currentTheme;
            }
        }
    }

    private void AnimateThemeCardHover(Border card, bool isHovering, string hoverBgHex, string normalBgHex)
    {
        Duration duration = new(TimeSpan.FromMilliseconds(150));
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };

        double targetScale = isHovering ? 1.05 : 1.0;
        System.Windows.Media.Color targetBg = ParseColor(isHovering ? hoverBgHex : normalBgHex);
        bool isCurrentThemeCard = string.Equals(card.Tag?.ToString(), _currentTheme, StringComparison.OrdinalIgnoreCase);
        System.Windows.Media.Color targetBorder = isHovering || isCurrentThemeCard
            ? Colors.White
            : ParseColor("#66FFFFFF");

        if (card.RenderTransform is not ScaleTransform scaleTransform || scaleTransform.IsFrozen)
        {
            scaleTransform = new ScaleTransform(1.0, 1.0);
            card.RenderTransform = scaleTransform;
        }

        if (card.Background is not SolidColorBrush bgBrush || bgBrush.IsFrozen)
        {
            bgBrush = new SolidColorBrush(ParseColor(normalBgHex));
            card.Background = bgBrush;
        }

        if (card.BorderBrush is not SolidColorBrush borderBrush || borderBrush.IsFrozen)
        {
            borderBrush = new SolidColorBrush(targetBorder);
            card.BorderBrush = borderBrush;
        }

        DoubleAnimation scaleAnim = new(targetScale, duration) { EasingFunction = ease };
        ColorAnimation bgAnim = new(targetBg, duration);
        ColorAnimation borderAnim = new(targetBorder, duration);

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
    }

    #endregion

    #region Prewarmed Word Instance Management

    private void PrewarmWordInstance()
    {
        lock (_wordLock)
        {
            if (_wordApp != null)
                return;

            try
            {
                Type? wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                    return;

                dynamic? app = Activator.CreateInstance(wordType);
                if (app != null)
                {
                    app.Visible = false;
                    app.DisplayAlerts = 0; // wdAlertsNone = 0
                    app.ScreenUpdating = false;
                    _wordApp = app;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RadialConvert] No se pudo precalentar Word: {ex.Message}");
            }
        }
    }

    private dynamic EnsureWordInstance()
    {
        lock (_wordLock)
        {
            if (_wordApp != null)
            {
                try
                {
                    // Verificar que el servidor RPC de la instancia precalentada siga activo
                    _ = _wordApp.Version;
                    _wordApp.Visible = false;
                    _wordApp.DisplayAlerts = 0;
                    _wordApp.ScreenUpdating = false;
                    return _wordApp;
                }
                catch
                {
                    // Si el proceso de Word fue cerrado externamente, recrear la instancia
                    try { Marshal.FinalReleaseComObject(_wordApp); } catch { }
                    _wordApp = null;
                }
            }

            Type? wordType = Type.GetTypeFromProgID("Word.Application")
                ?? throw new InvalidOperationException(
                    "Microsoft Word no está instalado en este equipo. Se requiere Word para esta conversión.");

            dynamic app = Activator.CreateInstance(wordType)
                ?? throw new InvalidOperationException("No se pudo iniciar Microsoft Word en segundo plano.");

            app.Visible = false;
            app.DisplayAlerts = 0; // wdAlertsNone = 0
            app.ScreenUpdating = false;
            _wordApp = app;
            return _wordApp;
        }
    }

    private void ShutdownWordInstance()
    {
        lock (_wordLock)
        {
            if (_wordApp != null)
            {
                try
                {
                    const int wdDoNotSaveChanges = 0;
                    _wordApp.Quit(wdDoNotSaveChanges);
                }
                catch
                {
                    // Ignorar si el proceso ya fue terminado
                }

                try
                {
                    Marshal.ReleaseComObject(_wordApp);
                }
                catch
                {
                }

                _wordApp = null;
            }
        }
    }

    #endregion

    #region Dwell Drag Trigger Logic (500 ms Sustained Drag — No Shift Required)

    private void MouseMonitorTimer_Tick(object? sender, EventArgs e)
    {
        if (_isProcessingDrop || _isApplyingThemeSelection)
            return;

        bool isLeftMouseDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        if (isLeftMouseDown)
        {
            if (!_wasLeftMouseDown)
            {
                // El botón izquierdo acaba de presionarse: registrar posición inicial
                _wasLeftMouseDown = true;
                _hasDragStarted = false;
                GetCursorPos(out _mouseDownInitialPos);
            }
            else if (!_hasDragStarted && !_isWheelOpen)
            {
                // Verificar si el cursor se ha movido lo suficiente para considerar un arrastre
                if (GetCursorPos(out POINT currentPos))
                {
                    int dx = Math.Abs(currentPos.X - _mouseDownInitialPos.X);
                    int dy = Math.Abs(currentPos.Y - _mouseDownInitialPos.Y);

                    if (dx >= DRAG_MOVEMENT_THRESHOLD_PX || dy >= DRAG_MOVEMENT_THRESHOLD_PX)
                    {
                        _hasDragStarted = true;
                        _dwellActivationTimer.Stop();
                        _dwellActivationTimer.Start();
                    }
                }
            }
        }
        else
        {
            // Si el botón izquierdo se suelta en cualquier momento:
            // cancelar y reiniciar el temporizador de 500 ms inmediatamente
            if (_dwellActivationTimer.IsEnabled)
            {
                _dwellActivationTimer.Stop();
            }

            _wasLeftMouseDown = false;
            _hasDragStarted = false;

            // Si la rueda estaba abierta y se soltó el ratón fuera de un sector (sin Drop activo), ocultarla
            if (_isWheelOpen && !_isProcessingDrop)
            {
                HideWheelAnimated();
            }
        }
    }

    private void DwellActivationTimer_Tick(object? sender, EventArgs e)
    {
        _dwellActivationTimer.Stop();

        if (_isProcessingDrop || _isWheelOpen)
            return;

        // Confirmar que el botón izquierdo sigue presionado al cumplirse los 500 ms
        bool isLeftMouseStillDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (isLeftMouseStillDown)
        {
            ShowWheelAtCursorAnimated();
        }
    }

    /// <summary>
    /// Posiciona la rueda en las coordenadas actuales del cursor y ejecuta la animación fluida de entrada:
    /// Opacidad 0 -> 1 en 200ms y Escala 0.8 -> 1.0 con BackEase.
    /// </summary>
    private void ShowWheelAtCursorAnimated()
    {
        if (!GetCursorPos(out POINT pt))
            return;

        _isWheelOpen = true;
        _isClosingAnimationRunning = false;

        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }
        else
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            dpiScaleX = dpi.DpiScaleX;
            dpiScaleY = dpi.DpiScaleY;
        }

        double cursorDipX = pt.X / dpiScaleX;
        double cursorDipY = pt.Y / dpiScaleY;

        Left = cursorDipX - (Width / 2.0);
        Top = cursorDipY - (Height / 2.0);

        ThemeSelectorGrid.Visibility = Visibility.Collapsed;
        ConversionSectorsGrid.Visibility = Visibility.Visible;
        ResetAllSectorsVisuals();
        CenterIcon.Text = "✦";
        CenterStatusText.Text = "Arrastra aquí";

        // Cancelar cualquier animación previa en curso
        BeginAnimation(OpacityProperty, null);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        Opacity = 0.0;
        WheelScaleTransform.ScaleX = 0.8;
        WheelScaleTransform.ScaleY = 0.8;
        Visibility = Visibility.Visible;

        Duration duration = new(TimeSpan.FromMilliseconds(200));
        BackEase scaleEase = new()
        {
            EasingMode = EasingMode.EaseOut,
            Amplitude = 0.25
        };
        CubicEase opacityEase = new()
        {
            EasingMode = EasingMode.EaseOut
        };

        DoubleAnimation opacityIn = new(0.0, 1.0, duration)
        {
            EasingFunction = opacityEase
        };
        DoubleAnimation scaleXIn = new(0.8, 1.0, duration)
        {
            EasingFunction = scaleEase
        };
        DoubleAnimation scaleYIn = new(0.8, 1.0, duration)
        {
            EasingFunction = scaleEase
        };

        BeginAnimation(OpacityProperty, opacityIn);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXIn);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYIn);
    }

    /// <summary>
    /// Desvanece la rueda de 1 a 0 en 150ms con ligera contracción de escala y luego establece Visibility = Hidden.
    /// </summary>
    private void HideWheelAnimated()
    {
        if (!_isWheelOpen || _isClosingAnimationRunning)
            return;

        _isWheelOpen = false;
        _isClosingAnimationRunning = true;
        ResetAllSectorsVisuals();

        Duration duration = new(TimeSpan.FromMilliseconds(150));
        CubicEase easeIn = new() { EasingMode = EasingMode.EaseIn };

        DoubleAnimation opacityOut = new(Opacity, 0.0, duration)
        {
            EasingFunction = easeIn
        };
        DoubleAnimation scaleOut = new(WheelScaleTransform.ScaleX, 0.86, duration)
        {
            EasingFunction = easeIn
        };

        opacityOut.Completed += (_, _) =>
        {
            // Solo ocultar si no se volvió a abrir durante los 150ms
            if (!_isWheelOpen)
            {
                Visibility = Visibility.Hidden;
            }
            _isClosingAnimationRunning = false;
        };

        BeginAnimation(OpacityProperty, opacityOut);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        WheelScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOut);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _mouseMonitorTimer.Stop();
        _dwellActivationTimer.Stop();

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        // Liberar la instancia precalentada de Word al cerrar la ventana
        ShutdownWordInstance();
    }

    #endregion

    #region Drag & Drop & Dynamic Theme-Aware Hover Microinteractions

    private static string GetSectorDisplayName(string? sectorTag) => sectorTag switch
    {
        "DOCX_TO_PDF" => "DOCX ➔ PDF",
        "PDF_TO_DOCX" => "PDF ➔ DOCX",
        "WEBP_TO_PNG" => "WEBP ➔ PNG",
        "IMG_TO_PDF" => "IMG ➔ PDF",
        _ => sectorTag ?? "Arrastra aquí"
    };

    private void Sector_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border sectorBorder)
        {
            AnimateSectorHover(sectorBorder, isHovering: true);
        }
    }

    private void Sector_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border sectorBorder)
        {
            AnimateSectorHover(sectorBorder, isHovering: false);
        }
    }

    private void Sector_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Border sectorBorder)
            {
                AnimateSectorHover(sectorBorder, isHovering: true);
                string sectorTag = sectorBorder.Tag?.ToString() ?? string.Empty;
                CenterIcon.Text = "✨";
                CenterStatusText.Text = GetSectorDisplayName(sectorTag);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Sector_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Sector_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border sectorBorder)
        {
            AnimateSectorHover(sectorBorder, isHovering: false);
            CenterIcon.Text = "✦";
            CenterStatusText.Text = "Arrastra aquí";
        }
        e.Handled = true;
    }

    /// <summary>
    /// Aplica la microinteracción Liquid Glass según el tema activo ("Cami" o "Jardi"):
    /// Escala 1.0 -> 1.05, borde a blanco puro (#FFFFFFFF) y elevación de fondo (#733B1D30 en Cami / #80262626 en Jardi).
    /// </summary>
    private void AnimateSectorHover(Border sector, bool isHovering)
    {
        Duration duration = new(TimeSpan.FromMilliseconds(150));
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };

        bool isJardi = _currentTheme == "Jardi";

        string normalBgHex = isJardi ? "#66141414" : "#401F121A";
        string hoverBgHex = isJardi ? "#80262626" : "#733B1D30";
        string normalBorderHex = isJardi ? "#33FFFFFF" : "#40FFFFFF";

        double targetScale = isHovering ? 1.05 : 1.0;
        System.Windows.Media.Color targetBg = ParseColor(isHovering ? hoverBgHex : normalBgHex);
        System.Windows.Media.Color targetBorder = isHovering
            ? Colors.White
            : ParseColor(normalBorderHex);

        if (sector.RenderTransform is not ScaleTransform scaleTransform || scaleTransform.IsFrozen)
        {
            scaleTransform = new ScaleTransform(1.0, 1.0);
            sector.RenderTransform = scaleTransform;
        }

        if (sector.Background is not SolidColorBrush bgBrush || bgBrush.IsFrozen)
        {
            bgBrush = new SolidColorBrush(ParseColor(normalBgHex));
            sector.Background = bgBrush;
        }

        if (sector.BorderBrush is not SolidColorBrush borderBrush || borderBrush.IsFrozen)
        {
            borderBrush = new SolidColorBrush(ParseColor(normalBorderHex));
            sector.BorderBrush = borderBrush;
        }

        DoubleAnimation scaleAnim = new(targetScale, duration) { EasingFunction = ease };
        ColorAnimation bgAnim = new(targetBg, duration);
        ColorAnimation borderAnim = new(targetBorder, duration);

        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
    }

    private void ResetAllSectorsVisuals()
    {
        AnimateSectorHover(SectorDocxToPdf, false);
        AnimateSectorHover(SectorPdfToDocx, false);
        AnimateSectorHover(SectorWebpToPng, false);
        AnimateSectorHover(SectorImgToPdf, false);
    }

    private static System.Windows.Media.Color ParseColor(string hex) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush CreateBrush(string hex) =>
        new(ParseColor(hex));

    private async void Sector_Drop(object sender, DragEventArgs e)
    {
        _dwellActivationTimer.Stop();

        if (sender is Border sectorBorder)
        {
            AnimateSectorHover(sectorBorder, false);
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            HideWheelAnimated();
            return;
        }

        string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (files == null || files.Length == 0)
        {
            HideWheelAnimated();
            return;
        }

        string sectorMode = (sender as FrameworkElement)?.Tag?.ToString()?.ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(sectorMode))
        {
            HideWheelAnimated();
            return;
        }

        _isProcessingDrop = true;
        try
        {
            // Caso especial para el sector IMG ➔ PDF:
            // Filtrar solo imágenes válidas (.png, .jpg, .jpeg, .webp, .bmp).
            // - Si hay 1 imagen: conversión directa sin diálogos extra.
            // - Si hay 2 o más imágenes: abrir ImageOrderWindow modal con el tema activo ("Cami" o "Jardi").
            if (sectorMode == "IMG_TO_PDF")
            {
                List<string> validImages = files
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
                    .ToList();

                if (validImages.Count == 0)
                {
                    CenterIcon.Text = "⚠️";
                    CenterStatusText.Text = "Solo imágenes";
                    await Task.Delay(700);
                    MessageBox.Show(
                        "El sector 'IMG ➔ PDF' solo acepta imágenes (.png, .jpg, .jpeg, .webp, .bmp).",
                        "RadialConvert - Formato no válido para este sector",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (validImages.Count >= 2)
                {
                    HideWheelAnimated();
                    ImageOrderWindow orderModal = new(validImages, _currentTheme);
                    orderModal.ShowDialog();
                    return;
                }

                // Exactamente 1 imagen válida: proceder con la conversión directa
                files = validImages.ToArray();
            }

            int convertedCount = 0;
            string? errorMessage = null;
            string? shortStatusError = null;

            // Validar extensión antes de procesar
            foreach (string file in files)
            {
                if (!ValidateExtensionForSector(file, sectorMode, out string validationMsg, out string shortError))
                {
                    errorMessage = validationMsg;
                    shortStatusError = shortError;
                    break;
                }
            }

            if (errorMessage != null)
            {
                CenterIcon.Text = "⚠️";
                CenterStatusText.Text = shortStatusError ?? "Formato inválido";
                await Task.Delay(700);
                MessageBox.Show(
                    errorMessage,
                    "RadialConvert - Formato no válido para este sector",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            CenterIcon.Text = "⏳";
            CenterStatusText.Text = "Convirtiendo...";

            await Task.Run(() =>
            {
                foreach (string file in files)
                {
                    try
                    {
                        ExecuteSectorConversion(file, sectorMode);
                        convertedCount++;
                    }
                    catch (IOException ioEx)
                    {
                        errorMessage = $"El archivo '{Path.GetFileName(file)}' está en uso o bloqueado por otro programa.\n\nDetalle: {ioEx.Message}";
                        shortStatusError = "Archivo en uso";
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                        shortStatusError = "Error al convertir";
                    }
                }
            });

            if (convertedCount > 0 && errorMessage == null)
            {
                CenterIcon.Text = "✅";
                CenterStatusText.Text = "¡Completado!";
                await Task.Delay(500);
            }
            else
            {
                CenterIcon.Text = "⚠️";
                CenterStatusText.Text = shortStatusError ?? "Error";
                Debug.WriteLine($"[RadialConvert] Error: {errorMessage}");
                await Task.Delay(450);
                MessageBox.Show(
                    errorMessage ?? "No se pudo completar la conversión del archivo.",
                    "RadialConvert - Aviso de conversión",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception unexpectedEx)
        {
            Debug.WriteLine($"[RadialConvert] Excepción no controlada en Drop: {unexpectedEx.Message}");
        }
        finally
        {
            _isProcessingDrop = false;
            HideWheelAnimated();
        }
    }

    #endregion

    #region Validation & Conversion Engine (Prewarmed Word COM, ImageSharp, PdfSharp)

    private static bool ValidateExtensionForSector(
        string filePath,
        string sectorMode,
        out string detailedMessage,
        out string shortStatus)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string fileName = Path.GetFileName(filePath);

        switch (sectorMode)
        {
            case "DOCX_TO_PDF":
                if (ext is ".docx" or ".doc")
                {
                    detailedMessage = string.Empty;
                    shortStatus = string.Empty;
                    return true;
                }
                shortStatus = "Solo .docx / .doc";
                detailedMessage = $"El sector 'DOCX ➔ PDF' solo acepta documentos de Word (.docx o .doc).\n\nArchivo soltado: {fileName} ({ext})";
                return false;

            case "PDF_TO_DOCX":
                if (ext is ".pdf")
                {
                    detailedMessage = string.Empty;
                    shortStatus = string.Empty;
                    return true;
                }
                shortStatus = "Solo archivos .pdf";
                detailedMessage = $"El sector 'PDF ➔ DOCX' solo acepta documentos .pdf.\n\nArchivo soltado: {fileName} ({ext})";
                return false;

            case "WEBP_TO_PNG":
                if (ext is ".webp" or ".jpg" or ".jpeg")
                {
                    detailedMessage = string.Empty;
                    shortStatus = string.Empty;
                    return true;
                }
                shortStatus = "Solo .webp / .jpg";
                detailedMessage = $"El sector 'WEBP ➔ PNG' solo acepta imágenes .webp o .jpg.\n\nArchivo soltado: {fileName} ({ext})";
                return false;

            case "IMG_TO_PDF":
                if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
                {
                    detailedMessage = string.Empty;
                    shortStatus = string.Empty;
                    return true;
                }
                shortStatus = "Solo imágenes";
                detailedMessage = $"El sector 'IMG ➔ PDF' solo acepta imágenes (.png, .jpg, .jpeg, .webp, .bmp).\n\nArchivo soltado: {fileName} ({ext})";
                return false;

            default:
                shortStatus = "Sector desconocido";
                detailedMessage = $"Modo de conversión desconocido: {sectorMode}";
                return false;
        }
    }

    private void ExecuteSectorConversion(string sourcePath, string sectorMode)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("El archivo de origen no existe.", sourcePath);

        switch (sectorMode)
        {
            case "DOCX_TO_PDF":
                ConvertDocxToPdf(sourcePath);
                break;

            case "PDF_TO_DOCX":
                ConvertPdfToDocx(sourcePath);
                break;

            case "WEBP_TO_PNG":
                ConvertWebpOrJpgToPng(sourcePath);
                break;

            case "IMG_TO_PDF":
                ConvertImageToPdf(sourcePath);
                break;

            default:
                throw new NotSupportedException($"Sector no soportado: {sectorMode}");
        }
    }

    /// <summary>
    /// Sector 1 (Superior): DOCX ➔ PDF
    /// Reutiliza la instancia precalentada _wordApp para exportar instantáneamente a .pdf y cierra solo el documento.
    /// </summary>
    private void ConvertDocxToPdf(string sourcePath)
    {
        string outputPath = GetSafeOutputPath(sourcePath, ".pdf");

        lock (_wordLock)
        {
            dynamic wordApp = EnsureWordInstance();
            dynamic? documents = null;
            dynamic? doc = null;

            try
            {
                documents = wordApp.Documents;
                doc = documents.Open(
                    FileName: Path.GetFullPath(sourcePath),
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                // wdExportFormatPDF = 17
                const int wdExportFormatPDF = 17;
                doc.ExportAsFixedFormat(
                    OutputFileName: Path.GetFullPath(outputPath),
                    ExportFormat: wdExportFormatPDF,
                    OpenAfterExport: false);
            }
            catch (COMException comEx)
            {
                throw new InvalidOperationException(
                    $"Error de Microsoft Word al exportar '{Path.GetFileName(sourcePath)}' a PDF (verifica que el archivo no esté bloqueado).\n\nDetalle: {comEx.Message}",
                    comEx);
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(false); } catch { }
                    try { Marshal.ReleaseComObject(doc); } catch { }
                }

                if (documents != null)
                {
                    try { Marshal.ReleaseComObject(documents); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Sector 2 (Derecho): PDF ➔ DOCX
    /// Reutiliza la instancia precalentada _wordApp para abrir el PDF (Word Reflow) y guardarlo como .docx, cerrando solo el documento.
    /// </summary>
    private void ConvertPdfToDocx(string sourcePath)
    {
        string outputPath = GetSafeOutputPath(sourcePath, ".docx");

        lock (_wordLock)
        {
            dynamic wordApp = EnsureWordInstance();
            dynamic? documents = null;
            dynamic? doc = null;

            try
            {
                documents = wordApp.Documents;
                doc = documents.Open(
                    FileName: Path.GetFullPath(sourcePath),
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    Visible: false);

                // WdSaveFormat.wdFormatXMLDocument = 16 (.docx)
                const int wdFormatXMLDocument = 16;
                doc.SaveAs2(
                    FileName: Path.GetFullPath(outputPath),
                    FileFormat: wdFormatXMLDocument);
            }
            catch (COMException comEx)
            {
                throw new InvalidOperationException(
                    $"Error de Microsoft Word al convertir '{Path.GetFileName(sourcePath)}' a DOCX.\n\nDetalle: {comEx.Message}",
                    comEx);
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(false); } catch { }
                    try { Marshal.ReleaseComObject(doc); } catch { }
                }

                if (documents != null)
                {
                    try { Marshal.ReleaseComObject(documents); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Sector 3 (Inferior): WEBP ➔ PNG
    /// Carga la imagen .webp (o .jpg) con SixLabors.ImageSharp y la guarda como .png en la misma carpeta.
    /// </summary>
    private static void ConvertWebpOrJpgToPng(string sourcePath)
    {
        string outputPath = GetSafeOutputPath(sourcePath, ".png");

        using FileStream inputStream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using SixLabors.ImageSharp.Image image = SixLabors.ImageSharp.Image.Load(inputStream);
        image.Save(outputPath, new PngEncoder());
    }

    /// <summary>
    /// Sector 4 (Izquierdo): IMG ➔ PDF
    /// Usa PdfSharp (v1.50.5147) y SixLabors.ImageSharp (v2.1.4) sin librerías de licencia comercial:
    /// - Si es .webp: obtiene dimensiones con Image.Identify(sourcePath), exporta un PNG temporal y lo carga con XImage.FromFile.
    /// - Si es .png, .jpg, .jpeg o .bmp: obtiene dimensiones y lo abre directamente con XImage.FromFile(sourcePath).
    /// - Configura page.Width y page.Height al tamaño exacto de la imagen y dibuja a página completa.
    /// </summary>
    private static void ConvertImageToPdf(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"))
        {
            throw new NotSupportedException($"Formato de imagen no soportado para PDF: {ext}");
        }

        string outputPath = GetSafeOutputPath(sourcePath, ".pdf");
        string? tempPngPath = null;

        try
        {
            int width;
            int height;
            string imageFileForPdfSharp = sourcePath;

            if (ext == ".webp")
            {
                // Leer dimensiones exactas con ImageSharp (Image.Identify) y exportar temporalmente a PNG
                IImageInfo? imageInfo = SixLabors.ImageSharp.Image.Identify(sourcePath);
                if (imageInfo != null)
                {
                    width = imageInfo.Width;
                    height = imageInfo.Height;
                }
                else
                {
                    using SixLabors.ImageSharp.Image webpImg = SixLabors.ImageSharp.Image.Load(sourcePath);
                    width = webpImg.Width;
                    height = webpImg.Height;
                }

                tempPngPath = Path.Combine(Path.GetTempPath(), $"radialconvert_{Guid.NewGuid():N}.png");
                using (FileStream inputStream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (SixLabors.ImageSharp.Image webpImage = SixLabors.ImageSharp.Image.Load(inputStream))
                {
                    webpImage.Save(tempPngPath, new PngEncoder());
                }

                imageFileForPdfSharp = tempPngPath;
            }
            else
            {
                // Para JPG/PNG/BMP estándar, abrir directamente con XImage.FromFile
                IImageInfo? info = SixLabors.ImageSharp.Image.Identify(sourcePath);
                if (info != null)
                {
                    width = info.Width;
                    height = info.Height;
                }
                else
                {
                    using XImage probeImg = XImage.FromFile(sourcePath);
                    width = probeImg.PixelWidth;
                    height = probeImg.PixelHeight;
                }
            }

            using PdfDocument document = new();
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(Math.Max(1, width));
            page.Height = XUnit.FromPoint(Math.Max(1, height));

            using (XGraphics gfx = XGraphics.FromPdfPage(page))
            using (XImage img = XImage.FromFile(imageFileForPdfSharp))
            {
                gfx.DrawImage(img, 0, 0, page.Width, page.Height);
            }

            document.Save(outputPath);
        }
        catch (IOException ioEx)
        {
            throw new IOException(
                $"No se pudo acceder al archivo de imagen '{Path.GetFileName(sourcePath)}' porque está en uso.",
                ioEx);
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Error al convertir la imagen '{Path.GetFileName(sourcePath)}' a PDF: {ex.Message}",
                ex);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPngPath) && File.Exists(tempPngPath))
            {
                try { File.Delete(tempPngPath); } catch { /* Ignorar errores de limpieza temporal */ }
            }
        }
    }

    /// <summary>
    /// Obtiene una ruta en la misma carpeta del archivo original evitando sobrescribir si ya existe.
    /// </summary>
    private static string GetSafeOutputPath(string sourcePath, string targetExtension)
    {
        string directory = Path.GetDirectoryName(sourcePath) ?? AppDomain.CurrentDomain.BaseDirectory;
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
        string candidatePath = Path.Combine(directory, $"{fileNameWithoutExt}{targetExtension}");

        int counter = 1;
        while (string.Equals(candidatePath, sourcePath, StringComparison.OrdinalIgnoreCase) || File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(directory, $"{fileNameWithoutExt}_converted{(counter > 1 ? $"_{counter}" : "")}{targetExtension}");
            counter++;
        }

        return candidatePath;
    }

    #endregion
}