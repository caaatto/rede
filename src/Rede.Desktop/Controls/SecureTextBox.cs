using System;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Rede.Core.Crypto;

namespace Rede.Desktop.Controls;

/// <summary>
/// Password input that never materializes the passphrase as a managed string.
/// Each keystroke is UTF-8 encoded directly into an mlock'd byte buffer.
/// Display shows masking dots. Extract the passphrase via
/// <see cref="ExtractPassphrase"/>, which returns a fresh byte[] and zeros
/// the internal buffer.
///
/// Limitations vs TextBox (by design):
/// - Clipboard paste IS supported (Ctrl+V / Cmd+V) for password managers.
///   The pasted string briefly exists as a managed string (same as keystroke
///   TextInput args) — unavoidable, but the window is microseconds.
/// - Copy and Cut are blocked — the passphrase never leaves this control.
/// - No cursor movement / no selection — the caret is always at the end.
/// - Backspace removes the trailing UTF-8 codepoint; Delete clears everything.
/// - Each TextInput event's string argument briefly exists on the GC heap for
///   the duration of the handler (1-2 chars per keystroke — negligible window).
/// </summary>
public class SecureTextBox : Border
{
    private const int MaxBytes = 4096;

    private byte[] _buffer = new byte[MaxBytes];
    private int _length;
    private int _charCount; // codepoint count, for display
    private SecureMemory.SecureHandle? _bufferLock;
    private bool _isRevealed;

    private readonly TextBlock _displayText;
    private readonly TextBlock _toggleIcon;
    private readonly SecureInputClient _inputClient;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#12121a"));
    private static readonly IBrush IdleBorder = new SolidColorBrush(Color.Parse("#2a2a3a"));
    private static readonly IBrush FocusBorder = new SolidColorBrush(Color.Parse("#8b5cf6"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#e6e6ed"));
    private static readonly IBrush WatermarkBrush = new SolidColorBrush(Color.Parse("#6b6b7c"));
    private static readonly IBrush ToggleIdleBrush = new SolidColorBrush(Color.Parse("#6b6b7c"));
    private static readonly IBrush ToggleHoverBrush = new SolidColorBrush(Color.Parse("#e6e6ed"));

    // Eye symbols (closed = hidden, open = revealed)
    private const string EyeClosed = "\u25C9"; // ◉ — password hidden
    private const string EyeOpen = "\u25CE";   // ◎ — password visible

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<SecureTextBox, string>(nameof(Watermark), "");

    public static readonly StyledProperty<bool> IsInputEnabledProperty =
        AvaloniaProperty.Register<SecureTextBox, bool>(nameof(IsInputEnabled), true);

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool IsInputEnabled
    {
        get => GetValue(IsInputEnabledProperty);
        set => SetValue(IsInputEnabledProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsInputEnabledProperty)
            Opacity = IsInputEnabled ? 1.0 : 0.4;
    }

    /// <summary>Raised when the user presses Enter while focused.</summary>
    public event EventHandler? EnterPressed;

    /// <summary>Raised whenever the buffer contents change.</summary>
    public event EventHandler? SecureTextChanged;

    public int CharCount => _charCount;
    public int ByteLength => _length;

    public SecureTextBox()
    {
        Focusable = true;
        Background = BgBrush;
        BorderBrush = IdleBorder;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10, 6);
        MinHeight = 32;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _displayText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            Foreground = TextBrush,
        };

        _toggleIcon = new TextBlock
        {
            Text = EyeClosed,
            FontSize = 16,
            Foreground = ToggleIdleBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(8, 0, 0, 0),
        };
        _toggleIcon.PointerPressed += OnTogglePressed;
        _toggleIcon.PointerEntered += (_, _) => _toggleIcon.Foreground = ToggleHoverBrush;
        _toggleIcon.PointerExited += (_, _) => _toggleIcon.Foreground = ToggleIdleBrush;

        var panel = new DockPanel();
        DockPanel.SetDock(_toggleIcon, Dock.Right);
        panel.Children.Add(_toggleIcon);
        panel.Children.Add(_displayText);
        Child = panel;

        _bufferLock = SecureMemory.Lock(_buffer);
        _inputClient = new SecureInputClient(this);
        UpdateDisplay();

        // Register with input method system so OnTextInput fires on all platforms
        AddHandler(TextInputMethodClientRequestedEvent, OnTextInputMethodClientRequested);

        WatermarkProperty.Changed.AddClassHandler<SecureTextBox>((x, _) => x.UpdateDisplay());
    }

    private void OnTextInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        e.Client = _inputClient;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        BorderBrush = FocusBorder;
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        BorderBrush = IdleBorder;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!IsInputEnabled) { e.Handled = true; return; }
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;

        var bytes = Encoding.UTF8.GetByteCount(e.Text);
        if (_length + bytes > MaxBytes) return;

        Encoding.UTF8.GetBytes(e.Text, 0, e.Text.Length, _buffer, _length);
        _length += bytes;
        foreach (var ch in e.Text)
            if (!char.IsLowSurrogate(ch)) _charCount++;

        UpdateDisplay();
        SecureTextChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsInputEnabled) return;

        // Block Copy (Ctrl+C) and Cut (Ctrl+X) — passphrase must never leave this control
        var mod = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta);
        if (mod != KeyModifiers.None && (e.Key == Key.C || e.Key == Key.X))
        {
            e.Handled = true;
            return;
        }

        // Paste (Ctrl+V / Cmd+V) — for password managers
        if (mod != KeyModifiers.None && e.Key == Key.V)
        {
            e.Handled = true;
            _ = PasteFromClipboardAsync();
            return;
        }

        switch (e.Key)
        {
            case Key.Back:
                e.Handled = true;
                RemoveLastCodepoint();
                break;
            case Key.Delete:
                e.Handled = true;
                Clear();
                break;
            case Key.Enter:
                e.Handled = true;
                EnterPressed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private async System.Threading.Tasks.Task PasteFromClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

#pragma warning disable CS0618 // IClipboard.GetTextAsync is marked obsolete but TryGetTextAsync requires IAsyncDataTransfer
        var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(text)) return;

        var bytes = Encoding.UTF8.GetByteCount(text);
        if (_length + bytes > MaxBytes) return;

        Encoding.UTF8.GetBytes(text, 0, text.Length, _buffer, _length);
        _length += bytes;
        foreach (var ch in text)
            if (!char.IsLowSurrogate(ch)) _charCount++;

        UpdateDisplay();
        SecureTextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveLastCodepoint()
    {
        if (_length == 0) return;
        int newLen = _length - 1;
        // Walk back over UTF-8 continuation bytes (10xxxxxx)
        while (newLen > 0 && (_buffer[newLen] & 0xC0) == 0x80)
            newLen--;
        for (int i = newLen; i < _length; i++) _buffer[i] = 0;
        _length = newLen;
        if (_charCount > 0) _charCount--;
        UpdateDisplay();
        SecureTextChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        for (int i = 0; i < _length; i++) _buffer[i] = 0;
        _length = 0;
        _charCount = 0;
        _isRevealed = false;
        _toggleIcon.Text = EyeClosed;
        UpdateDisplay();
        SecureTextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Return a fresh byte[] copy of the current passphrase and zero the internal
    /// buffer. Caller owns the returned array and is responsible for locking and
    /// zeroing it.
    /// </summary>
    public byte[] ExtractPassphrase()
    {
        var copy = new byte[_length];
        Buffer.BlockCopy(_buffer, 0, copy, 0, _length);
        Clear();
        return copy;
    }

    /// <summary>
    /// Return a copy of the current bytes WITHOUT clearing the internal buffer.
    /// Used for passphrase-confirm comparison on register.
    /// </summary>
    public byte[] PeekPassphrase()
    {
        var copy = new byte[_length];
        Buffer.BlockCopy(_buffer, 0, copy, 0, _length);
        return copy;
    }

    private void OnTogglePressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _isRevealed = !_isRevealed;
        _toggleIcon.Text = _isRevealed ? EyeOpen : EyeClosed;
        UpdateDisplay();
        Focus();
    }

    private void UpdateDisplay()
    {
        if (_charCount == 0 && !string.IsNullOrEmpty(Watermark))
        {
            _displayText.Text = Watermark;
            _displayText.Foreground = WatermarkBrush;
        }
        else if (_isRevealed && _length > 0)
        {
            _displayText.Text = Encoding.UTF8.GetString(_buffer, 0, _length);
            _displayText.Foreground = TextBrush;
        }
        else
        {
            _displayText.Text = new string('\u25CF', _charCount);
            _displayText.Foreground = TextBrush;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CryptographicOperations.ZeroMemory(_buffer);
        _length = 0;
        _charCount = 0;
        _bufferLock?.Dispose();
        _bufferLock = null;
    }

    /// <summary>
    /// Minimal TextInputMethodClient that tells the platform IME this control accepts text.
    /// Does NOT expose any buffer content (security: passphrase stays internal).
    /// </summary>
    private sealed class SecureInputClient : TextInputMethodClient
    {
        private readonly SecureTextBox _owner;
        public SecureInputClient(SecureTextBox owner) => _owner = owner;

        public override Visual TextViewVisual => _owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => false;
        public override string? SurroundingText => null;
        public override TextSelection Selection { get => default; set { } }
        public override Rect CursorRectangle => default;
        public override void SetPreeditText(string? text) { }
    }
}
