using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
/// - No clipboard paste — the clipboard would hand us a managed string that
///   we cannot zero, defeating the purpose.
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

    private readonly TextBlock _displayText;

    private static readonly IBrush BgBrush = new SolidColorBrush(Color.Parse("#12121a"));
    private static readonly IBrush IdleBorder = new SolidColorBrush(Color.Parse("#2a2a3a"));
    private static readonly IBrush FocusBorder = new SolidColorBrush(Color.Parse("#8b5cf6"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#e6e6ed"));
    private static readonly IBrush WatermarkBrush = new SolidColorBrush(Color.Parse("#6b6b7c"));

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
        Child = _displayText;

        _bufferLock = SecureMemory.Lock(_buffer);
        UpdateDisplay();

        WatermarkProperty.Changed.AddClassHandler<SecureTextBox>((x, _) => x.UpdateDisplay());
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

    private void UpdateDisplay()
    {
        if (_charCount == 0 && !string.IsNullOrEmpty(Watermark))
        {
            _displayText.Text = Watermark;
            _displayText.Foreground = WatermarkBrush;
        }
        else
        {
            _displayText.Text = new string('\u25CF', _charCount);
            _displayText.Foreground = TextBrush;
        }
    }
}
