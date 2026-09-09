using Xunit;

namespace Obscura.Browser.Tests;

/// <summary>
/// The xUnit port of <c>crates/obscura-browser/tests/input_accept.rs</c>.
/// </summary>
public sealed class InputAcceptTests
{
    [Fact]
    public async Task InputAcceptReflectsTheContentAttribute()
    {
        BrowserContext context = BrowserContext.WithStorageAndNetwork(
            "input-accept", null, false, null, null, true);
        using var page = new Page("input-accept-page", context);
        await page.NavigateAsync(
            "data:text/html,<input id=upload type=file accept='image/png,image/jpeg'>");

        PageFixtures.AssertJson(
            """
            {
                "initial": "image/png,image/jpeg",
                "property": "image/webp",
                "attribute": "image/webp"
            }
            """,
            page.Evaluate(
                """
                (() => {
                    const input = document.getElementById('upload');
                    const initial = input.accept;
                    input.accept = 'image/webp';
                    return {
                        initial,
                        property: input.accept,
                        attribute: input.getAttribute('accept'),
                    };
                })()
                """));
    }
}
