using PhotoReview.App;

internal static class ImageCacheKeyTests
{
    public static void Run(string root, List<string> failures)
    {
        var path = System.IO.Path.Combine(root, "cache-identity.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        var preview = ImageCacheKey.Create(path, false, 2400);
        var same = ImageCacheKey.Create(path, false, 2400);
        var resized = ImageCacheKey.Create(path, false, 3200);
        var original = ImageCacheKey.Create(path, true, 0);
        Check(preview == same, "Cache key reuses unchanged source/mode/width", failures);
        Check(preview != resized && preview != original, "Cache key separates decode width and Original mode", failures);

        File.WriteAllBytes(path, [4, 5, 6, 7]);
        var replacement = ImageCacheKey.Create(path, false, 2400);
        Check(preview != replacement && !preview.MatchesCurrentSource(), "Cache key rejects a replacement at the same path", failures);
        File.Delete(path);
        Check(!replacement.MatchesCurrentSource(), "Cache key rejects a removed source", failures);
    }

    private static void Check(bool condition, string name, List<string> failures)
    {
        if (!condition) failures.Add(name);
    }
}
