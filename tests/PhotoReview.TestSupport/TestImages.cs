namespace PhotoReview.TestSupport;

public static class TestImages
{
    // A valid, tiny PNG keeps decode fixtures portable while exercising WPF's real decoder.
    public static readonly byte[] PreviewPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
