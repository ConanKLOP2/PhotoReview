using System.Threading.Tasks;

namespace PhotoReview.App.Coordinators;

/// <summary>
/// AR02a: production seam for the move override that <see cref="PhotoReview.Core.FileActions.FileActionService"/>
/// and <see cref="PhotoReview.Core.FileActions.UndoService"/> already accept as a constructor
/// parameter (<c>Func&lt;string, string, Task&gt;? moveOverride</c>). Registering an
/// <see cref="IMoveOverride"/> in DI lets a test override the physical move step without a
/// second composition root; production registers none, so the services move files for real.
/// </summary>
public interface IMoveOverride
{
    Task MoveAsync(string source, string destination);
}
