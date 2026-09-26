using PhotoReview.Core.Updates;

namespace PhotoReview.Core.Tests.Updates;

public sealed class UpdateUrlPolicyTests
{
    [Theory(DisplayName = "Project release URLs on github.com are allowed")]
    [InlineData("https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94")]
    [InlineData("https://github.com/ConanKLOP2/PhotoReview/releases")]
    [InlineData("https://github.com/ConanKLOP2/PhotoReview")]
    [InlineData("https://GITHUB.com/conanklop2/photoreview/releases/latest")]
    public void Validate_ProjectUrl_ReturnsUrl(string url) => Assert.NotNull(UpdateUrlPolicy.Validate(url));

    [Theory(DisplayName = "Anything that is not an https project URL is rejected")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://github.com/ConanKLOP2/PhotoReview/releases")]                 // not https
    [InlineData("https://github.com/Evil/PhotoReview/releases")]                       // other owner
    [InlineData("https://github.com/ConanKLOP2/PhotoReviewEvil/releases")]             // prefix trick
    [InlineData("https://github.com/ConanKLOP2/Other")]
    [InlineData("https://github.com.evil.example/ConanKLOP2/PhotoReview/releases")]    // host trick
    [InlineData("https://evil.example/https://github.com/ConanKLOP2/PhotoReview")]
    [InlineData("https://github.com@evil.example/ConanKLOP2/PhotoReview/releases")]    // userinfo trick
    [InlineData("https://user:pw@github.com/ConanKLOP2/PhotoReview/releases")]
    [InlineData("https://github.com:8443/ConanKLOP2/PhotoReview/releases")]
    [InlineData("https://github.com/ConanKLOP2/PhotoReview/../Evil/x")]                // dot-dot normalises away
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:")]
    [InlineData("javascript:alert(1)")]
    public void Validate_Other_ReturnsNull(string? url) => Assert.Null(UpdateUrlPolicy.Validate(url));
}
