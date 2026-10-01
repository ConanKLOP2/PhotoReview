namespace PhotoReview.Imaging.Raw;

/// <summary>
/// Camera RAW container format identification.
/// </summary>
public enum RawFormat
{
    Unknown,
    Cr2,
    Cr3,
    Nef,
    /// <summary>Nikon Coolpix NRW: no container reader (LibRaw decodes it); kept for the LibRaw preview-fallback mapping.</summary>
    Nrw,
    Arw,
    Dng,
    Raf,
    Orf,
    Rw2,
    /// <summary>Pentax PEF: no container reader (LibRaw decodes it); kept for the LibRaw preview-fallback mapping.</summary>
    Pef
}
