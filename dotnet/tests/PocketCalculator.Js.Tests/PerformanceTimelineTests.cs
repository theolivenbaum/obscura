using System.Text.Json.Nodes;
using PocketCalculator.Dom;
using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// The Performance Timeline surfaces that have no counterpart in the Rust engine:
/// resource timing (<c>op_resource_timings</c>) and the <c>@font-face</c> status
/// behind <c>document.fonts.check()</c> (<c>op_font_resource_loaded</c>).
/// </summary>
/// <remarks>
/// Both are PORT ADDITIONS. <c>bootstrap.js</c> is shared, so it treats either op as
/// optional and behaves exactly as it did before when the host does not bind it -
/// no resource entries, and a CSS-connected face that stays <c>unloaded</c>.
/// </remarks>
public sealed class PerformanceTimelineTests
{
    private static void AssertJson(string expected, JsonNode? actual) =>
        Assert.Equal(JsonNode.Parse(expected)?.ToJsonString() ?? "null", actual?.ToJsonString() ?? "null");

    private static double TimeOrigin(PocketCalculatorJsRuntime runtime) =>
        runtime.Evaluate("performance.timeOrigin")!.GetValue<double>();

    private static void Record(
        PocketCalculatorJsRuntime runtime,
        string url,
        string initiatorType,
        double startOffsetMs,
        double endOffsetMs,
        long decoded = 0,
        long encoded = 0,
        int status = 200,
        string contentType = "")
    {
        double origin = TimeOrigin(runtime);
        runtime.RecordResourceTiming(
            url,
            initiatorType,
            status,
            origin + startOffsetMs,
            origin + endOffsetMs,
            decoded,
            encoded,
            contentType);
    }

    [Fact]
    public void ResourceEntriesReportTheMeasuredPhasesAndLeaveTheRestAtZero()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;

        Assert.Equal(0d, rt.Evaluate("performance.getEntriesByType('resource').length")!.GetValue<double>());

        Record(
            rt,
            "https://cdn.example.com/app.js",
            "script",
            startOffsetMs: 120,
            endOffsetMs: 170,
            decoded: 4096,
            encoded: 1024,
            contentType: "text/javascript");

        AssertJson(
            """
            [1,"https://cdn.example.com/app.js","resource","script",120,50,120,170,
             200,4096,1024,1324,"text/javascript","h2",
             [0,0,0,0,0,0,0,0,0,0]]
            """,
            rt.Evaluate(
                """
                return (() => {
                    const entries = performance.getEntriesByType('resource');
                    const e = entries[0];
                    return [
                        entries.length, e.name, e.entryType, e.initiatorType,
                        e.startTime, e.duration, e.fetchStart, e.responseEnd,
                        e.responseStatus, e.decodedBodySize, e.encodedBodySize,
                        e.transferSize, e.contentType, e.nextHopProtocol,
                        // Every phase the transport does not instrument.
                        [e.redirectStart, e.redirectEnd, e.workerStart,
                         e.domainLookupStart, e.domainLookupEnd,
                         e.connectStart, e.connectEnd, e.secureConnectionStart,
                         e.requestStart, e.responseStart]
                    ];
                })()
                """));
    }

    [Fact]
    public void ResourceEntriesAreRealPerformanceResourceTimingObjectsWithToJson()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Record(rt, "http://example.com/a.css", "link", 10, 20, decoded: 64, encoded: 64);

        AssertJson(
            """
            [true,true,true,"http://example.com/a.css","link","resource",10,10,"http/1.1"]
            """,
            rt.Evaluate(
                """
                return (() => {
                    const e = performance.getEntriesByType('resource')[0];
                    const json = e.toJSON();
                    return [
                        e instanceof PerformanceResourceTiming,
                        e instanceof PerformanceEntry,
                        typeof e.toJSON === 'function',
                        json.name, json.initiatorType, json.entryType,
                        json.startTime, json.duration, json.nextHopProtocol
                    ];
                })()
                """));
    }

    [Fact]
    public void GetEntriesIncludesResourcesAlongsideTheNavigationEntryInStartTimeOrder()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Record(rt, "http://example.com/late.png", "img", 300, 310);
        Record(rt, "http://example.com/early.js", "script", 40, 50);

        AssertJson(
            """
            [3,["navigation","resource","resource"],
             ["http://example.com/test","http://example.com/early.js","http://example.com/late.png"],
             1,2]
            """,
            rt.Evaluate(
                """
                return (() => {
                    const all = performance.getEntries();
                    return [
                        all.length,
                        all.map(e => e.entryType),
                        all.map(e => e.name),
                        performance.getEntriesByType('navigation').length,
                        performance.getEntriesByName('http://example.com/late.png').length
                            + performance.getEntriesByName('http://example.com/early.js', 'resource').length
                    ];
                })()
                """));
    }

    [Fact]
    public void ClearResourceTimingsDropsWhatIsBufferedAndLaterResourcesStillArrive()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Record(rt, "http://example.com/one.js", "script", 10, 20);
        Record(rt, "http://example.com/two.js", "script", 20, 30);
        Assert.Equal(2d, rt.Evaluate("performance.getEntriesByType('resource').length")!.GetValue<double>());

        rt.Evaluate("performance.clearResourceTimings()");
        Assert.Equal(0d, rt.Evaluate("performance.getEntriesByType('resource').length")!.GetValue<double>());

        Record(rt, "http://example.com/three.js", "script", 40, 50);
        AssertJson(
            """[1,"http://example.com/three.js"]""",
            rt.Evaluate(
                """
                return (() => {
                    const r = performance.getEntriesByType('resource');
                    return [r.length, r[0].name];
                })()
                """));
    }

    [Fact]
    public void SetResourceTimingBufferSizeCapsTheBufferUntilItIsCleared()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript(
            "resource-timing-buffer",
            """
            globalThis.__bufferFull = 0;
            performance.setResourceTimingBufferSize(2);
            performance.onresourcetimingbufferfull = () => { __bufferFull += 1; };
            """);

        for (int i = 0; i < 5; i++)
        {
            Record(rt, $"http://example.com/{i}.js", "script", 10 + i, 20 + i);
        }

        AssertJson(
            """[2,["http://example.com/0.js","http://example.com/1.js"],1]""",
            rt.Evaluate(
                """
                return (() => {
                    const r = performance.getEntriesByType('resource');
                    return [r.length, r.map(e => e.name), __bufferFull];
                })()
                """));

        rt.ExecuteScript(
            "resource-timing-reset",
            "performance.setResourceTimingBufferSize(100); performance.clearResourceTimings();");
        Record(rt, "http://example.com/after.js", "script", 90, 95);
        AssertJson(
            """[1,"http://example.com/after.js"]""",
            rt.Evaluate(
                """
                return (() => {
                    const r = performance.getEntriesByType('resource');
                    return [r.length, r[0].name];
                })()
                """));
    }

    /// <summary>
    /// A document owns its own timeline. The Page builds a whole new runtime per
    /// navigation, so the shim's buffer goes with the realm; what this pins is the
    /// other half - installing a document drops the host's own record list, so the
    /// same-runtime paths (frame realms, resume) cannot leak the previous
    /// document's subresources into the next one.
    /// </summary>
    [Fact]
    public void InstallingADocumentDropsTheHostsResourceRecords()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        Record(rt, "http://example.com/first.js", "script", 10, 20);
        Assert.Equal(1d, rt.Evaluate("performance.getEntriesByType('resource').length")!.GetValue<double>());
        Assert.Single(rt.ResourceTimings);

        rt.SetDom(HtmlParsing.ParseHtml("<html><body>second</body></html>"));
        Assert.Empty(rt.ResourceTimings);

        using var next = RuntimeFixture.Setup("<html><body>second</body></html>");
        Assert.Equal(
            0d,
            next.Runtime.Evaluate("performance.getEntriesByType('resource').length")!.GetValue<double>());
    }

    private const string WebfontDocument =
        """
        <html><head><style>
            @font-face {
                font-family: "Loaded Webfont";
                src: url("/fonts/loaded.woff2") format("woff2");
            }
            @font-face {
                font-family: "Pending Webfont";
                src: url("/fonts/pending.woff2") format("woff2");
            }
        </style></head><body></body></html>
        """;

    [Fact]
    public void FontsCheckIsTrueForAnAtFontFaceTheRendererHasBytesFor()
    {
        using var fixture = RuntimeFixture.Setup(WebfontDocument);
        var rt = fixture.Runtime;

        // Before anything arrives both faces are unloaded, exactly as in Chromium for
        // a webfont nothing has requested yet.
        AssertJson(
            """[2,["unloaded","unloaded"],false,false,true]""",
            rt.Evaluate(
                """
                return (() => {
                    const faces = Array.from(document.fonts);
                    return [
                        faces.length,
                        faces.map(f => f.status),
                        document.fonts.check('13px "Loaded Webfont"'),
                        document.fonts.check('13px "Pending Webfont"'),
                        // A family with no @font-face falls through to the local
                        // families, which is a match.
                        document.fonts.check('13px Arial')
                    ];
                })()
                """));

        rt.SeedRenderResource("http://example.com/fonts/loaded.woff2", [0x77, 0x4f, 0x46, 0x32]);

        AssertJson(
            """[["loaded","unloaded"],true,false]""",
            rt.Evaluate(
                """
                return (() => {
                    const faces = Array.from(document.fonts);
                    return [
                        faces.map(f => f.status),
                        document.fonts.check('13px "Loaded Webfont"'),
                        document.fonts.check('13px "Pending Webfont"')
                    ];
                })()
                """));
    }

    [Fact]
    public void ADataUrlFontFaceIsLoadedWithoutAskingTheHost()
    {
        using var fixture = RuntimeFixture.Setup(
            """
            <html><head><style>
                @font-face { font-family: Inline; src: url(data:font/woff2;base64,d09GMg==); }
            </style></head><body></body></html>
            """);

        AssertJson(
            """["loaded",true]""",
            fixture.Runtime.Evaluate(
                """
                return (() => {
                    const face = Array.from(document.fonts)[0];
                    return [face.status, document.fonts.check('13px Inline')];
                })()
                """));
    }

    [Fact]
    public void AScriptCreatedFaceStillNeedsLoadAndIsNotAnsweredByTheHost()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        // Seeding the bytes must not make a script-created face report loaded: only a
        // CSS-connected face is one the renderer downloads on the page's behalf.
        rt.SeedRenderResource("http://example.com/fonts/script.woff2", [0x77, 0x4f, 0x46, 0x32]);

        AssertJson(
            """["unloaded",false]""",
            rt.Evaluate(
                """
                return (() => {
                    const face = new FontFace("Scripted", "url('/fonts/script.woff2')");
                    document.fonts.add(face);
                    return [face.status, document.fonts.check('13px Scripted')];
                })()
                """));
    }
}
