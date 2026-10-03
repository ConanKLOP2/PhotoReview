namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Registry that selects the appropriate <see cref="IRawContainerReader"/> for a given file header and extension.
/// </summary>
public sealed class RawContainerReaderRegistry
{
    private readonly List<IRawContainerReader> _readers = [];

    public RawContainerReaderRegistry()
    {
        // Standard TIFF-family readers, then CR3 (BMFF) and RAF
        _readers.AddRange(
        [
            new Tiff.Cr2ContainerReader(),
            new Tiff.NefContainerReader(),
            new Tiff.ArwContainerReader(),
            new Tiff.DngContainerReader(),
            new Tiff.OrfContainerReader(),
            new Tiff.Rw2ContainerReader(),
            new Bmff.Cr3ContainerReader(),
            new Raf.RafContainerReader(),
        ]);
    }

    public RawContainerReaderRegistry(IEnumerable<IRawContainerReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers.AddRange(readers);
    }

    /// <summary>
    /// Finds the first registered reader whose <see cref="IRawContainerReader.CanRead"/> returns true.
    /// Returns null if no registered reader recognizes the container.
    /// </summary>
    public IRawContainerReader? FindReader(ReadOnlySpan<byte> first64Bytes, string extension)
    {
        for (var i = 0; i < _readers.Count; i++)
        {
            if (_readers[i].CanRead(first64Bytes, extension))
            {
                return _readers[i];
            }
        }

        return null;
    }
}
