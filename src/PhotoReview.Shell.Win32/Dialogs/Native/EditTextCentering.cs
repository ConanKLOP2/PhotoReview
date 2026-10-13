using System.Runtime.InteropServices;
using PhotoReview.Shell.Interop;
using N = PhotoReview.Shell.Interop.DialogChromeNative;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>
/// A single-line EDIT draws its text at the TOP of its client area and ignores <c>EM_SETRECT</c> (multiline only), so a box taller than one
/// text line (the number / key boxes, ~44 px at 150 %) had its text stuck to the top edge. The box is kept at its full height and its
/// non-client area is enlarged on top (<c>WM_NCCALCSIZE</c>) by the amount that moves the digits / capital letters (cap height = ascent
/// minus internal leading) to the vertical middle; the extra band is painted with the control's own background (<c>WM_NCPAINT</c>).
/// The caret, selection and hit test follow the shrunken client area by themselves.
/// </summary>
internal static unsafe class EditTextCentering
{
    private const nuint SubclassId = 0x45444354; // "EDCT"
    private const string Prop = "PhotoReview.EditCentering";
    private const uint WmCtlColorStatic = 0x0138;
    private const uint EsReadOnly = 0x0800;

    /// <summary>Subclass <paramref name="edit"/> (idempotent) and make its frame recalculate. False when it could not be attached.</summary>
    internal static bool Attach(nint edit)
    {
        if (edit == 0 || !User32.IsWindow(edit))
        {
            return false;
        }

        if (N.GetProp(edit, Prop) == 0)
        {
            if (!CommonControls.SetWindowSubclass(edit, &Proc, SubclassId, 0))
            {
                return false;
            }

            N.SetProp(edit, Prop, 1);
        }

        Refresh(edit);
        return true;
    }

    /// <summary>Recompute the frame (font or size changed).</summary>
    internal static void Refresh(nint edit)
        => User32.SetWindowPos(edit, 0, 0, 0, 0, 0,
            WindowMessages.SwpNoMove | WindowMessages.SwpNoSize | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate | WindowMessages.SwpFrameChanged);

    /// <summary>Rows the client top moves down so that the cap-height ink is centred in a client area <paramref name="clientHeight"/> high.</summary>
    internal static int TopPadding(nint edit, int clientHeight)
    {
        var font = (nint)User32.SendMessage(edit, DialogConstants.WmGetFont, 0, 0);
        var hdc = User32.GetDC(edit);
        if (hdc == 0)
        {
            return 0;
        }

        var metrics = stackalloc int[15];
        var old = font != 0 ? Gdi32.SelectObject(hdc, font) : 0;
        var ok = Gdi32.GetTextMetrics(hdc, metrics);
        if (old != 0)
        {
            Gdi32.SelectObject(hdc, old);
        }

        _ = User32.ReleaseDC(edit, hdc);
        if (!ok)
        {
            return 0;
        }

        int height = metrics[0], ascent = metrics[1], internalLeading = metrics[3];
        var capHeight = Math.Max(1, ascent - internalLeading);
        // The system draws the line at client top + 1, so the ink starts at 1 + internalLeading below the client top. After moving the
        // client top down by p the gap above the ink is p + 1 + internalLeading; centred when it equals (clientHeight - capHeight) / 2.
        var padding = ((clientHeight - capHeight) / 2) - 1 - internalLeading;
        // The shrunken client area must still hold one full line (plus the 1 px the system leaves above it).
        var limit = clientHeight - (height + 2);
        return Math.Clamp(padding, 0, Math.Max(0, limit));
    }

    [UnmanagedCallersOnly]
    private static nint Proc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            switch (message)
            {
                case WindowMessages.WmNcCalcSize:
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    // wParam != 0: lParam -> NCCALCSIZE_PARAMS whose first RECT now is the client area; otherwise lParam -> that RECT.
                    var client = (Rect*)lParam;
                    if (client != null && client->Bottom > client->Top)
                    {
                        client->Top += TopPadding(hwnd, client->Bottom - client->Top);
                    }

                    return result;
                }

                case N.WmNcPaint:
                {
                    var result = CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
                    PaintBand(hwnd);
                    return result;
                }

                case N.WmNcDestroy:
                    CommonControls.RemoveWindowSubclass(hwnd, &Proc, id);
                    N.RemoveProp(hwnd, Prop);
                    break;
            }
        }
        catch (Exception)
        {
            // An exception must not cross the unmanaged boundary; the box only keeps its top-aligned text.
        }

        return CommonControls.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>Paint the band between the border and the (moved) client area with the colour the dialog gives the edit box.</summary>
    private static void PaintBand(nint hwnd)
    {
        Rect window = default;
        Rect client = default;
        if (!User32.GetWindowRect(hwnd, &window) || !User32.GetClientRect(hwnd, &client))
        {
            return;
        }

        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        var edge = (width - clientWidth) / 2; // border thickness (symmetric)
        var clientTop = height - clientHeight - edge; // client top in window coordinates (the bottom edge is not enlarged)
        if (clientTop <= edge)
        {
            return;
        }

        var hdc = N.GetWindowDC(hwnd);
        if (hdc == 0)
        {
            return;
        }

        var style = (uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle);
        var message = (style & EsReadOnly) != 0 ? WmCtlColorStatic : DialogConstants.WmCtlColorEdit;
        // The dialog's WM_CTLCOLOR* answers with the brush of the box background; the page viewport may sit between the box and the dialog.
        nint brush = 0;
        for (var parent = User32.GetParent(hwnd); parent != 0 && brush == 0; parent = User32.GetParent(parent))
        {
            brush = (nint)User32.SendMessage(parent, message, hdc, hwnd);
        }

        if (brush != 0)
        {
            var band = new Rect { Left = edge, Top = edge, Right = width - edge, Bottom = clientTop };
            _ = User32.FillRect(hdc, &band, brush);
        }

        _ = User32.ReleaseDC(hwnd, hdc);
    }
}
