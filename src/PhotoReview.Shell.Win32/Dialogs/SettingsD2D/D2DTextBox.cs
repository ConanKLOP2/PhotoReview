using System.Text;

namespace PhotoReview.Shell.Win32.Dialogs.SettingsD2D;

/// <summary>Kẹp giấy (seam: test không đụng clipboard thật của người dùng).</summary>
internal interface ID2DClipboard
{
    string? GetText();

    void SetText(string text);
}

/// <summary>
/// SPIKE c2: logic một dòng nhập TỰ VẼ (không HWND): văn bản, caret, vùng chọn, hoàn tác/làm lại, dán/cắt/chép, lọc ký tự số
/// (như <c>ES_NUMBER</c>), cuộn ngang. Việc vẽ + đo chữ do khung gọi (<c>measure</c> = độ rộng px của một chuỗi).
/// </summary>
internal sealed class D2DTextBox
{
    private const int UndoLimit = 100;

    private readonly List<(string Text, int Caret)> _undo = [];
    private readonly List<(string Text, int Caret)> _redo = [];
    private string _text = string.Empty;

    public D2DTextBox(bool numericOnly, int maxLength = 9, bool allowDecimal = false)
    {
        NumericOnly = numericOnly;
        MaxLength = maxLength;
        AllowDecimal = allowDecimal;
    }

    public bool NumericOnly { get; }

    /// <summary>Cho phép một dấu thập phân (cỡ chữ 12,5): Win32 <c>ES_NUMBER</c> không có kiểu này nên c1 phải tự lọc.</summary>
    public bool AllowDecimal { get; }

    public int MaxLength { get; }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            Caret = Anchor = _text.Length;
            _undo.Clear();
            _redo.Clear();
            ScrollX = 0;
        }
    }

    /// <summary>Vị trí caret (chỉ số UTF-16, 0..Length).</summary>
    public int Caret { get; private set; }

    /// <summary>Đầu kia của vùng chọn (= Caret khi không chọn).</summary>
    public int Anchor { get; private set; }

    /// <summary>Cuộn ngang (px) của chữ trong ô.</summary>
    public double ScrollX { get; set; }

    /// <summary>Số lần từ chối một ký tự/đoạn dán không hợp lệ (ô số).</summary>
    public int RejectedCount { get; private set; }

    public bool HasSelection => Caret != Anchor;

    public int SelectionStart => Math.Min(Caret, Anchor);

    public int SelectionEnd => Math.Max(Caret, Anchor);

    public string SelectedText => _text.Substring(SelectionStart, SelectionEnd - SelectionStart);

    /// <summary>Bất kỳ thay đổi nội dung (để khung báo "Changed").</summary>
    public event EventHandler? TextChanged;

    public bool TypeChar(char c)
    {
        if (char.IsControl(c))
        {
            return false;
        }

        if (NumericOnly && !IsAllowed(c, _text.Remove(SelectionStart, SelectionEnd - SelectionStart)))
        {
            RejectedCount++;
            return false;
        }

        return Replace(c.ToString());
    }

    public bool Insert(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var cleaned = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                continue;
            }

            if (NumericOnly && !IsAllowed(c, _text.Remove(SelectionStart, SelectionEnd - SelectionStart) + cleaned))
            {
                RejectedCount++;
                return false; // như ES_NUMBER: cả đoạn dán bị từ chối
            }

            cleaned.Append(c);
        }

        return Replace(cleaned.ToString());
    }

    public bool Backspace(bool word = false)
    {
        if (HasSelection)
        {
            return Replace(string.Empty);
        }

        if (Caret == 0)
        {
            return false;
        }

        Anchor = word ? PreviousWord(Caret) : Caret - 1;
        return Replace(string.Empty);
    }

    public bool Delete(bool word = false)
    {
        if (HasSelection)
        {
            return Replace(string.Empty);
        }

        if (Caret >= _text.Length)
        {
            return false;
        }

        Anchor = word ? NextWord(Caret) : Caret + 1;
        return Replace(string.Empty);
    }

    public void MoveTo(int index, bool extend)
    {
        Caret = Math.Clamp(index, 0, _text.Length);
        if (!extend)
        {
            Anchor = Caret;
        }
    }

    public void MoveLeft(bool extend, bool word)
    {
        if (!extend && HasSelection)
        {
            MoveTo(SelectionStart, false);
            return;
        }

        MoveTo(word ? PreviousWord(Caret) : Caret - 1, extend);
    }

    public void MoveRight(bool extend, bool word)
    {
        if (!extend && HasSelection)
        {
            MoveTo(SelectionEnd, false);
            return;
        }

        MoveTo(word ? NextWord(Caret) : Caret + 1, extend);
    }

    public void SelectAll()
    {
        Anchor = 0;
        Caret = _text.Length;
    }

    public bool Copy(ID2DClipboard clipboard)
    {
        if (!HasSelection)
        {
            return false;
        }

        clipboard.SetText(SelectedText);
        return true;
    }

    public bool Cut(ID2DClipboard clipboard) => Copy(clipboard) && Replace(string.Empty);

    public bool Paste(ID2DClipboard clipboard) => clipboard.GetText() is { Length: > 0 } text && Insert(text);

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Add((_text, Caret));
        var (text, caret) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _text = text;
        Caret = Anchor = caret;
        TextChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Add((_text, Caret));
        var (text, caret) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _text = text;
        Caret = Anchor = caret;
        TextChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Chỉ số ký tự gần nhất với <paramref name="x"/> (px, gốc = đầu chữ chưa cuộn); <paramref name="measure"/> đo tiền tố.</summary>
    public int HitTest(double x, Func<string, double> measure)
    {
        if (_text.Length == 0 || x <= 0)
        {
            return 0;
        }

        var previous = 0d;
        for (var i = 1; i <= _text.Length; i++)
        {
            var width = measure(_text[..i]);
            if (x < (previous + width) / 2)
            {
                return i - 1;
            }

            previous = width;
        }

        return _text.Length;
    }

    private bool IsAllowed(char c, string existing)
        => char.IsAsciiDigit(c) || (AllowDecimal && c is '.' or ',' && existing.IndexOfAny(['.', ',']) < 0);

    private bool Replace(string with)
    {
        var start = SelectionStart;
        var end = SelectionEnd;
        var next = string.Concat(_text.AsSpan(0, start), with, _text.AsSpan(end));
        if (next.Length > MaxLength)
        {
            RejectedCount++;
            return false;
        }

        if (next == _text)
        {
            return false;
        }

        Push(_undo, (_text, Caret));
        _redo.Clear();
        _text = next;
        Caret = Anchor = start + with.Length;
        TextChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static void Push(List<(string Text, int Caret)> stack, (string Text, int Caret) entry)
    {
        stack.Add(entry);
        if (stack.Count > UndoLimit)
        {
            stack.RemoveAt(0);
        }
    }

    private int PreviousWord(int from)
    {
        var i = from;
        while (i > 0 && !char.IsLetterOrDigit(_text[i - 1]))
        {
            i--;
        }

        while (i > 0 && char.IsLetterOrDigit(_text[i - 1]))
        {
            i--;
        }

        return i;
    }

    private int NextWord(int from)
    {
        var i = from;
        while (i < _text.Length && char.IsLetterOrDigit(_text[i]))
        {
            i++;
        }

        while (i < _text.Length && !char.IsLetterOrDigit(_text[i]))
        {
            i++;
        }

        return i;
    }
}
