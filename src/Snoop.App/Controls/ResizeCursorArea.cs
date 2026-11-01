using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Snoop.App.Controls;

/// <summary>
/// A panel that shows the left-right resize cursor while the pointer is over it. Wrap a drag handle
/// (a Thumb) in it so people can see the handle can be dragged. WinUI only lets an element set its
/// own cursor (ProtectedCursor), hence this small subclass.
/// </summary>
public partial class ResizeCursorArea : Grid
{
    public ResizeCursorArea()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
