using System.Globalization;
using System.Text.Json.Nodes;

using Obscura.Browser;
using Obscura.Dom;

using BrowserPage = Obscura.Browser.Page;
using DomAttribute = Obscura.Dom.Attribute;

namespace Obscura.Cdp.Domains;

/// <summary>
/// The CDP <c>DOM</c> domain: the node tree, selector queries, node resolution
/// and box models.
/// </summary>
public static class Dom
{
    /// <summary>
    /// Hard cap on how deep a getDocument/describeNode response may nest, independent
    /// of the requested <c>depth</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DOM.getDocument{depth:-1}</c> arrives here as <c>uint.MaxValue</c>, which on a
    /// pathologically deep DOM (trivially scriptable, unbounded by the parser) produces a
    /// value nested that far. Even built without recursion, the serializer's own
    /// recursion over that nesting overflows the stack. Bounding the depth keeps the
    /// response safe to serialize. Real DOMs are shallow (deep React trees are a few
    /// hundred), so this only truncates pathological nesting, which beats crashing the
    /// worker. Mirrors DOMSnapshot's node guard (issue #341).
    /// </para>
    /// <para>
    /// Clients needing a deeper subtree re-request it with DOM.requestChildNodes or
    /// describeNode on a specific node.
    /// </para>
    /// </remarks>
    internal const uint MaxSerializeDepth = 256;

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        await Task.CompletedTask.ConfigureAwait(false);
        try
        {
            return HandleCore(method, parameters, ctx, sessionId);
        }
        catch (DomainError error)
        {
            return DomainResult.Err(error.Message);
        }
    }

    private static DomainResult HandleCore(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        switch (method)
        {
            case "enable":
                return DomainResult.Empty();

            case "getDocument":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId) ?? throw new DomainError("No page");
                long depth = parameters.Get("depth").AsI64() ?? 2;
                JsonNode? root = null;
                bool hasDom = page.WithDom(dom =>
                {
                    root = SerializeNode(dom, dom.Document, unchecked((uint)depth), 0);
                    return true;
                });
                if (!hasDom)
                {
                    throw new DomainError("No DOM loaded");
                }

                return DomainResult.Ok(new JsonObject { ["root"] = root });
            }

            case "querySelector":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId) ?? throw new DomainError("No page");
                string selector = parameters.Get("selector").AsString()
                    ?? throw new DomainError("selector required");
                ulong result = page.WithDom(dom =>
                    dom.TryQuerySelector(selector, out NodeId? found, out _) && found is { } id
                        ? (ulong)id.Raw
                        : 0UL);
                return DomainResult.Ok(new JsonObject { ["nodeId"] = result });
            }

            case "querySelectorAll":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId) ?? throw new DomainError("No page");
                string selector = parameters.Get("selector").AsString()
                    ?? throw new DomainError("selector required");
                var ids = new JsonArray();
                List<NodeId>? found = page.WithDom(dom =>
                    dom.TryQuerySelectorAll(selector, out List<NodeId> results, out _) ? results : []);
                foreach (NodeId id in found ?? [])
                {
                    ids.Add((ulong)id.Raw);
                }

                return DomainResult.Ok(new JsonObject { ["nodeIds"] = ids });
            }

            case "getOuterHTML":
            {
                BrowserPage page = ctx.GetSessionPage(sessionId) ?? throw new DomainError("No page");
                ulong nodeId = parameters.Get("nodeId").AsU64()
                    ?? parameters.Get("backendNodeId").AsU64()
                    ?? throw new DomainError("nodeId required");
                string html = page.WithDom(dom => dom.OuterHtml(NodeId.New((uint)nodeId))) ?? string.Empty;
                return DomainResult.Ok(new JsonObject { ["outerHTML"] = html });
            }

            case "describeNode":
            {
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                long depth = parameters.Get("depth").AsI64() ?? 0;

                ulong nodeId;
                if ((parameters.Get("nodeId").AsU64()
                    ?? parameters.Get("backendNodeId").AsU64()) is { } explicitId)
                {
                    nodeId = explicitId;
                }
                else if (parameters.Get("objectId").AsString() is { } objectId)
                {
                    string code =
                        $"(function() {{ var o = globalThis.__obscura_objects[{CdpUtil.ObjectIdLiteral(objectId)}]; "
                        + "if (!o) return -1; return (typeof o._nid === 'number') ? o._nid : -1; })()";
                    double? resolved = page.Evaluate(code).AsF64();
                    nodeId = resolved is { } value and >= 0 ? (ulong)value : 0UL;
                }
                else
                {
                    throw new DomainError("nodeId or objectId required");
                }

                JsonNode? node = null;
                page.WithDom(dom =>
                {
                    node = SerializeNode(dom, NodeId.New((uint)nodeId), unchecked((uint)depth), 0);
                    return true;
                });
                return DomainResult.Ok(new JsonObject { ["node"] = node });
            }

            case "resolveNode":
            {
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                ulong nodeId;
                if ((parameters.Get("nodeId").AsU64()
                    ?? parameters.Get("backendNodeId").AsU64()) is { } explicitId)
                {
                    nodeId = explicitId;
                }
                else if (parameters.Get("objectId").AsString() is { } objectId)
                {
                    string code =
                        $"(function() {{ var o = globalThis.__obscura_objects[{CdpUtil.ObjectIdLiteral(objectId)}]; "
                        + "return (o && typeof o._nid === 'number') ? o._nid : -1; })()";
                    double? resolved = page.Evaluate(code).AsF64();
                    nodeId = resolved is { } value and >= 0 ? (ulong)value : 0UL;
                }
                else
                {
                    throw new DomainError("nodeId or objectId required");
                }

                string jsCode =
                    "(function() {"
                    + $"var nid = {nodeId.ToString(CultureInfo.InvariantCulture)};"
                    + "var node = null;"
                    + "if (globalThis._cache && globalThis._cache.has(nid)) {"
                    + "node = globalThis._cache.get(nid);"
                    + "} else {"
                    + "var t = +Deno.core.ops.op_dom('node_type', String(nid), '', globalThis.__obscura_frameId >>> 0);"
                    + "if (t === 1) node = new Element(nid);"
                    + "else if (t === 9) node = globalThis.document;"
                    + "else node = new Node(nid);"
                    + "if (globalThis._cache) globalThis._cache.set(nid, node);"
                    + "}"
                    + "return node;"
                    + "})()";

                if (page.Js is not { } js)
                {
                    throw new DomainError("No JS runtime");
                }

                Obscura.Js.Runtime.RemoteObjectInfo info;
                try
                {
                    info = js.StoreObjectWithMeta(jsCode);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return DomainResult.Ok(new JsonObject
                    {
                        ["object"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["subtype"] = "node",
                            ["className"] = "HTMLElement",
                            ["objectId"] = $"node-{nodeId.ToString(CultureInfo.InvariantCulture)}",
                        },
                    });
                }

                return DomainResult.Ok(new JsonObject
                {
                    ["object"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["subtype"] = "node",
                        ["className"] = info.ClassName.Length == 0 ? "HTMLElement" : info.ClassName,
                        ["description"] = info.Description,
                        ["objectId"] = info.ObjectId
                            ?? $"node-{nodeId.ToString(CultureInfo.InvariantCulture)}",
                    },
                });
            }

            case "setAttributeValue":
                return DomainResult.Empty();

            case "removeNode":
                return DomainResult.Empty();

            case "focus":
            {
                // No layout engine, but obscura's JS focus() sets document.activeElement,
                // which Input.dispatchKeyEvent targets. CDP clients (browser-use) focus an
                // input via DOM.focus before typing; without this their keystrokes land on
                // nothing and the field stays empty.
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                ulong nodeId = ResolveNodeId(page, parameters);
                string code =
                    $"(function() {{ var el = globalThis._wrap && globalThis._wrap({nodeId}); "
                    + "if (el && typeof el.focus === 'function') { el.focus(); return true; } return false; })()";
                page.Evaluate(code);
                return DomainResult.Empty();
            }

            case "scrollIntoViewIfNeeded":
            {
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                ulong nodeId = ResolveNodeId(page, parameters);
                // Obscura has no layout viewport to move, but the JS shim records this
                // element for the hit testing used by subsequent input events.
                string code =
                    $"(function() {{ var el = globalThis._wrap && globalThis._wrap({nodeId}); "
                    + "if (!el || typeof el.scrollIntoView !== 'function') return false; "
                    + "el.scrollIntoView(); return true; })()";
                bool didScroll = page.Evaluate(code).AsBool() ?? false;
                if (!didScroll)
                {
                    throw new DomainError(
                        $"node {nodeId} could not be resolved to a scrollable element");
                }

                return DomainResult.Empty();
            }

            case "setFileInputFiles":
            {
                // Puppeteer's ElementHandle.uploadFile / Playwright's setInputFiles drive an
                // <input type=file> through this CDP call (issue #359). Read each local file,
                // then hand its bytes (base64) to the JS layer, which builds real File
                // objects and fires input+change like a real selection so page code can
                // read/upload them.
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                // setFileInputFiles reads local files and hands their bytes to page JS.
                // Anyone who can reach the CDP port (default localhost, but Docker images
                // bind 0.0.0.0) could otherwise read any file the process can read - the
                // same threat as Page.navigate to file://, so it honours the same opt-in
                // and is off by default.
                if (!page.Context.AllowFileAccess)
                {
                    throw new DomainError(
                        "DOM.setFileInputFiles is disabled. Restart with `obscura serve --allow-file-access` to enable local file uploads.");
                }

                ulong nodeId = ResolveNodeId(page, parameters);
                List<string> paths = [];
                foreach (JsonNode? entry in JsonExt.AsJsonArray(parameters.Get("files")) ?? [])
                {
                    if (entry.AsString() is { } path)
                    {
                        paths.Add(path);
                    }
                }

                var specs = new JsonArray();
                foreach (string path in paths)
                {
                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                        or ArgumentException or NotSupportedException)
                    {
                        throw new DomainError(
                            $"setFileInputFiles: cannot read '{path}': {exception.Message}");
                    }

                    string name = Path.GetFileName(path);
                    specs.Add(new JsonObject
                    {
                        ["name"] = name.Length == 0 ? "file" : name,
                        ["type"] = GuessMime(path),
                        ["b64"] = Convert.ToBase64String(bytes),
                    });
                }

                string specsJson = CdpJson.Serialize(specs);
                string code =
                    $"(function() {{ var el = globalThis._wrap && globalThis._wrap({nodeId}); "
                    + $"if (el && globalThis.__obscura_setInputFiles) {{ globalThis.__obscura_setInputFiles(el, {specsJson}); return true; }} return false; }})()";
                page.Evaluate(code);
                return DomainResult.Empty();
            }

            case "getBoxModel":
            {
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                ulong nodeId;
                try
                {
                    nodeId = ResolveNodeId(page, parameters);
                }
                catch (DomainError)
                {
                    return DomainResult.Ok(null);
                }

                string code =
                    "(function() {"
                    + $"var el = globalThis._wrap && globalThis._wrap({nodeId});"
                    + "if (!el || typeof el.getBoundingClientRect !== 'function') return null;"
                    + "var r = el.getBoundingClientRect();"
                    + "return [r.left, r.top, r.right, r.top, r.right, r.bottom, r.left, r.bottom,"
                    + "r.width, r.height];"
                    + "})()";
                JsonNode? value = page.Evaluate(code);
                List<double> numbers = [];
                foreach (JsonNode? item in JsonExt.AsJsonArray(value) ?? [])
                {
                    if (item.AsF64() is { } number)
                    {
                        numbers.Add(number);
                    }
                }

                JsonArray quad;
                double width;
                double height;
                if (numbers.Count >= 10)
                {
                    quad = [];
                    for (int i = 0; i < 8; i++)
                    {
                        quad.Add(CoordValue(numbers[i]));
                    }

                    width = numbers[8];
                    height = numbers[9];
                }
                else
                {
                    quad = DefaultQuad();
                    width = 100.0;
                    height = 20.0;
                }

                return DomainResult.Ok(new JsonObject
                {
                    ["model"] = new JsonObject
                    {
                        ["content"] = quad.DeepClone(),
                        ["padding"] = quad.DeepClone(),
                        ["border"] = quad.DeepClone(),
                        ["margin"] = quad,
                        ["width"] = CoordValue(width),
                        ["height"] = CoordValue(height),
                    },
                });
            }

            case "getContentQuads":
            {
                BrowserPage page = ctx.GetSessionPageMut(sessionId) ?? throw new DomainError("No page");
                ulong nodeId;
                try
                {
                    nodeId = ResolveNodeId(page, parameters);
                }
                catch (DomainError)
                {
                    return DomainResult.Ok(null);
                }

                string code =
                    "(function() {"
                    + $"var el = globalThis._wrap && globalThis._wrap({nodeId});"
                    + "if (!el || typeof el.getBoundingClientRect !== 'function') return null;"
                    + "var r = el.getBoundingClientRect();"
                    + "return [r.left, r.top, r.right, r.top, r.right, r.bottom, r.left, r.bottom];"
                    + "})()";
                JsonNode? value = page.Evaluate(code);
                List<double> numbers = [];
                foreach (JsonNode? item in JsonExt.AsJsonArray(value) ?? [])
                {
                    if (item.AsF64() is { } number)
                    {
                        numbers.Add(number);
                    }
                }

                JsonArray quad;
                if (numbers.Count == 8)
                {
                    quad = [];
                    foreach (double number in numbers)
                    {
                        quad.Add(CoordValue(number));
                    }
                }
                else
                {
                    quad = DefaultQuad();
                }

                return DomainResult.Ok(new JsonObject { ["quads"] = new JsonArray(quad) });
            }

            default:
                return DomainResult.Err($"Unknown DOM method: {method}");
        }
    }

    private static JsonArray DefaultQuad() => [8, 8, 108, 8, 108, 28, 8, 28];

    /// <summary>Rust's <c>str::to_ascii_uppercase</c>: non-ASCII is left alone.</summary>
    private static string AsciiUpper(string value)
    {
        Span<char> buffer = value.Length <= 64 ? stackalloc char[value.Length] : new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        }

        return new string(buffer);
    }

    /// <summary>Rust's <c>str::to_ascii_lowercase</c>: non-ASCII is left alone.</summary>
    private static string AsciiLower(string value)
    {
        Span<char> buffer = value.Length <= 64 ? stackalloc char[value.Length] : new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        return new string(buffer);
    }

    /// <summary>
    /// Resolve a DOM <c>nodeId</c> from CDP params. Honors <c>nodeId</c>,
    /// <c>backendNodeId</c>, and <c>objectId</c> in that order.
    /// </summary>
    /// <remarks>
    /// Playwright commonly passes only <c>objectId</c> (returned by a prior
    /// <c>DOM.resolveNode</c>); without this fallback those requests silently default to
    /// node 0 and click the wrong element.
    /// </remarks>
    internal static ulong ResolveNodeId(BrowserPage page, JsonNode? parameters)
    {
        if (parameters.Get("nodeId").AsU64() is { } nodeId)
        {
            return nodeId;
        }

        if (parameters.Get("backendNodeId").AsU64() is { } backendNodeId)
        {
            return backendNodeId;
        }

        if (parameters.Get("objectId").AsString() is { } objectId)
        {
            string code =
                $"(function() {{ var o = globalThis.__obscura_objects && globalThis.__obscura_objects[{CdpUtil.ObjectIdLiteral(objectId)}]; "
                + "return (o && typeof o._nid === 'number') ? o._nid : -1; })()";
            double? result = page.Evaluate(code).AsF64();
            long resolved = result is { } value ? (long)value : -1L;
            if (resolved < 0)
            {
                throw new DomainError(
                    $"objectId {objectId} could not be resolved to a node");
            }

            return (ulong)resolved;
        }

        throw new DomainError("nodeId, backendNodeId, or objectId required");
    }

    /// <summary>
    /// Serialize a CDP box-model coordinate the way Chrome does: an integral double
    /// (e.g. <c>256.0</c>) becomes a JSON integer (<c>256</c>), while a genuinely
    /// fractional value (<c>206.0390625</c>) stays a float.
    /// </summary>
    /// <remarks>
    /// The Rust engine's serializer always writes an <c>f64</c> with a decimal point, so
    /// <c>256.0</c> reached strict CDP clients (Hermes Agent) which deserialize the quad
    /// as <c>i64</c> and reject it, breaking every click-by-coordinate flow. Chrome only
    /// widens fractional coordinates, so mirroring that keeps those clients working
    /// (issue #576).
    /// </remarks>
    internal static JsonNode? CoordValue(double value)
    {
        // Collapse to an integer only when the value is exactly integral and fits an
        // i64 losslessly; NaN/inf and out-of-range doubles fall through unchanged.
        if (double.IsFinite(value) && Math.Truncate(value) == value
            && value >= long.MinValue && value <= long.MaxValue)
        {
            return JsonValue.Create((long)value);
        }

        return JsonValue.Create(value);
    }

    /// <summary>
    /// Build the CDP Node object for a single node (without its <c>children</c> array),
    /// returning it together with that node's child ids. <c>null</c> for a missing node.
    /// </summary>
    private static (JsonObject Value, List<NodeId> Children)? NodeValue(DomTree dom, NodeId nodeId)
    {
        Node? node = dom.GetNode(nodeId);
        if (node is null)
        {
            return null;
        }

        List<NodeId> childrenIds = dom.Children(nodeId);
        var result = new JsonObject
        {
            ["nodeId"] = (ulong)nodeId.Raw,
            ["backendNodeId"] = (ulong)nodeId.Raw,
            ["childNodeCount"] = childrenIds.Count,
        };

        switch (node.Data)
        {
            case DocumentData:
                result["nodeType"] = 9;
                result["nodeName"] = "#document";
                result["localName"] = "";
                result["nodeValue"] = "";
                result["documentURL"] = "";
                result["baseURL"] = "";
                result["xmlVersion"] = "";
                break;
            case DoctypeData doctype:
                result["nodeType"] = 10;
                result["nodeName"] = doctype.Name;
                result["localName"] = "";
                result["nodeValue"] = "";
                result["publicId"] = doctype.PublicId;
                result["systemId"] = doctype.SystemId;
                break;
            case ElementData element:
            {
                result["nodeType"] = 1;
                result["nodeName"] = AsciiUpper(element.Name.Local);
                result["localName"] = element.Name.Local;
                result["nodeValue"] = "";
                var attributes = new JsonArray();
                foreach (DomAttribute attribute in element.Attrs)
                {
                    attributes.Add(attribute.Name.Local);
                    attributes.Add(attribute.Value);
                }

                result["attributes"] = attributes;
                break;
            }

            case TextData text:
                result["nodeType"] = 3;
                result["nodeName"] = "#text";
                result["localName"] = "";
                result["nodeValue"] = text.Contents;
                break;
            case CommentData comment:
                result["nodeType"] = 8;
                result["nodeName"] = "#comment";
                result["localName"] = "";
                result["nodeValue"] = comment.Contents;
                break;
            case ProcessingInstructionData instruction:
                result["nodeType"] = 7;
                result["nodeName"] = instruction.Target;
                result["localName"] = "";
                result["nodeValue"] = instruction.Data;
                break;
        }

        return (result, childrenIds);
    }

    /// <summary>
    /// Serialize a node and its descendants into the CDP Node tree, iteratively.
    /// </summary>
    /// <remarks>
    /// The requested <paramref name="maxDepth"/> is clamped to
    /// <paramref name="currentDepth"/> + <see cref="MaxSerializeDepth"/> so a
    /// <c>depth:-1</c> request on a very deep DOM cannot produce a value that overflows
    /// the stack when it is later serialized. An explicit heap worklist keeps the builder
    /// itself off the call stack.
    /// </remarks>
    internal static JsonNode? SerializeNode(DomTree dom, NodeId nodeId, uint maxDepth, uint currentDepth)
    {
        uint clamped = Math.Min(
            maxDepth,
            currentDepth > uint.MaxValue - MaxSerializeDepth ? uint.MaxValue : currentDepth + MaxSerializeDepth);

        if (NodeValue(dom, nodeId) is not { } root)
        {
            return null;
        }

        var stack = new List<Frame>
        {
            new()
            {
                Value = root.Value,
                Children = root.Children,
                Next = 0,
                Built = [],
                Depth = currentDepth,
                Expand = currentDepth < clamped && root.Children.Count > 0,
            },
        };

        while (true)
        {
            Frame top = stack[^1];
            NodeId? nextChild = null;
            if (top.Expand && top.Next < top.Children.Count)
            {
                nextChild = top.Children[top.Next];
                top.Next++;
            }

            if (nextChild is { } childId)
            {
                uint childDepth = stack[^1].Depth + 1;
                if (NodeValue(dom, childId) is { } child)
                {
                    stack.Add(new Frame
                    {
                        Value = child.Value,
                        Children = child.Children,
                        Next = 0,
                        Built = [],
                        Depth = childDepth,
                        Expand = childDepth < clamped && child.Children.Count > 0,
                    });
                }
                else
                {
                    // Missing child: match the recursive behavior of emitting null.
                    stack[^1].Built.Add(null);
                }

                continue;
            }

            // This node's children are all built; finalize and fold into the parent.
            Frame frame = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (frame.Built.Count > 0)
            {
                var children = new JsonArray();
                foreach (JsonNode? built in frame.Built)
                {
                    children.Add(built);
                }

                frame.Value["children"] = children;
            }

            if (stack.Count == 0)
            {
                return frame.Value;
            }

            stack[^1].Built.Add(frame.Value);
        }
    }

    private sealed class Frame
    {
        internal required JsonObject Value { get; init; }

        internal required List<NodeId> Children { get; init; }

        internal int Next { get; set; }

        internal required List<JsonNode?> Built { get; init; }

        internal required uint Depth { get; init; }

        internal required bool Expand { get; init; }
    }

    /// <summary>
    /// Best-effort MIME type from a file extension, for the File objects created by
    /// DOM.setFileInputFiles. Defaults to application/octet-stream.
    /// </summary>
    internal static string GuessMime(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.StartsWith('.'))
        {
            extension = extension[1..];
        }

        return AsciiLower(extension) switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            "pdf" => "application/pdf",
            "txt" => "text/plain",
            "html" or "htm" => "text/html",
            "css" => "text/css",
            "js" or "mjs" => "text/javascript",
            "json" => "application/json",
            "xml" => "application/xml",
            "csv" => "text/csv",
            "zip" => "application/zip",
            "gz" => "application/gzip",
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream",
        };
    }
}
