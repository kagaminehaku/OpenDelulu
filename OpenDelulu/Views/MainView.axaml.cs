using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenDelulu.ViewModels;

namespace OpenDelulu.Views;

public partial class MainView : UserControl
{
    const double NarrowWidth = 760;
    bool? _narrow;
    bool _autoLoaded;

    public MainView()
    {
        InitializeComponent();
    }

    TopLevel? _keyHost;

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _keyHost?.RemoveHandler(KeyDownEvent, OnShortcutKey);
        _keyHost = null;
    }

    void OnShortcutKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
            return;
        if (e.Key == Key.F11 && TopLevel.GetTopLevel(this) is Window window)
        {
            window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            e.Handled = true;
            return;
        }
        // Typing in a text box or using an open list keeps its keys.
        if (!vm.IsEmergency && (e.Source is TextBox || e.Source is ComboBox { IsDropDownOpen: true }))
            return;
        if (vm.HandleKey(e.Key))
            e.Handled = true;
    }

    void OnEmergencyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.IsEmergency = false;
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // On the window, tunnelling: shortcuts work with nothing focused and before a focused button takes Space/Enter.
        _keyHost?.RemoveHandler(KeyDownEvent, OnShortcutKey);
        _keyHost = TopLevel.GetTopLevel(this);
        _keyHost?.AddHandler(KeyDownEvent, OnShortcutKey, RoutingStrategies.Tunnel);
        if (DataContext is not MainViewModel vm)
            return;

        vm.PickFolder = async () =>
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null || !storage.CanPickFolder)
                return null;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.Translator.Get("picker.title"),
                AllowMultiple = false,
            });
            return folders.FirstOrDefault()?.TryGetLocalPath();
        };

        if (!_autoLoaded)
        {
            _autoLoaded = true;
            _ = vm.TryAutoLoadAsync();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            ApplyLayout(Bounds.Width < NarrowWidth);
    }

    /// <summary>Phones / portrait: stack browser above preview instead of side by side.</summary>
    void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow)
            return;
        _narrow = narrow;

        Body.ColumnDefinitions = narrow ? new ColumnDefinitions("*") : new ColumnDefinitions("340,4,*");
        Body.RowDefinitions = narrow ? new RowDefinitions("2*,3*") : new RowDefinitions("*");
        Grid.SetColumn(LeftPane, 0);
        Grid.SetRow(LeftPane, 0);
        Grid.SetColumn(PreviewPane, narrow ? 0 : 2);
        Grid.SetRow(PreviewPane, narrow ? 1 : 0);
        Splitter.IsVisible = !narrow;

        CharacterBody.ColumnDefinitions = narrow ? new ColumnDefinitions("*") : new ColumnDefinitions("300,*");
        CharacterBody.RowDefinitions = narrow ? new RowDefinitions("Auto,*") : new RowDefinitions("*");
        Grid.SetColumn(CharacterPreview, narrow ? 0 : 1);
        Grid.SetRow(CharacterPreview, narrow ? 1 : 0);
    }
}
