namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Registry that selects the appropriate <see cref="IRawContainerReader"/> for a given file header and extension.
/// </summary>
public sealed class RawContainerReaderRegistry
{
    private readonly List<IRawContainerReader> _readers = [];

    public RawContainerReaderRegistry()
    {
        // Standard TIFF-family readers
        Register(new Tiff.Cr2ContainerReader());
        Register(new Tiff.NefContainerReader());
        Register(new Tiff.ArwContainerReader());
        Register(new Tiff.DngContainerReader());
        Register(new Tiff.OrfContainerReader());
        Register(new Tiff.Rw2ContainerReader());
    }

    public RawContainerReaderRegistry(IEnumerable<IRawContainerReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers.AddRange(readers);
    }

    /// <summary>Registers a container reader.</summary>
    public void Register(IRawContainerReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _readers.Add(reader);
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
