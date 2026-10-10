using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// A shadow root's adopted sheets are not its children in Chromium, so replacing the children
/// keeps the styling; and a <c>:host(...) descendant</c> rule in the shadow tree matches (msn.com
/// cards). Values are Chromium 141's.
/// </summary>
public sealed class ShadowAdoptedStylesTests
{
    [Fact]
    public void AdoptedSheetsSurviveReplacingTheShadowRootsChildren()
    {
        using var fixture = RuntimeFixture.Setup(
            """<html><head></head><body><x-a id="a" immersive></x-a><x-a id="b"></x-a><x-a id="c"></x-a></body></html>""");
        var result = fixture.Runtime.Evaluate("""
            (() => {
              const sheet = new CSSStyleSheet();
              sheet.replaceSync(':host { display: block } .m { width: 20px; height: 10px } :host([immersive]) .m { width: 30px }');
              const make = (id) => {
                const root = document.getElementById(id).attachShadow({ mode: 'open' });
                root.adoptedStyleSheets = [sheet];
                return root;
              };
              const a = make('a');
              a.innerHTML = '<div class="m"></div>';
              const b = make('b');
              b.textContent = 'x';
              b.replaceChildren(document.createElement('div'));
              b.firstElementChild.className = 'm';
              const c = make('c');
              c.innerHTML = '<div class="m"></div>';
              const width = (root) => getComputedStyle(root.querySelector('.m')).width;
              return [width(a), width(b), width(c),
                getComputedStyle(document.getElementById('a')).display,
                a.adoptedStyleSheets.length];
            })()
            """);

        Assert.Equal("""["30px","20px","20px","block",1]""", result?.ToJsonString());
    }
}
