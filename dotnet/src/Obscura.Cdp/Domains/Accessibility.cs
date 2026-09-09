using System.Globalization;
using System.Text.Json.Nodes;
using Obscura.Dom;

namespace Obscura.Cdp.Domains;

/// <summary>CDP <c>Accessibility</c> domain: the synthesized accessibility tree.</summary>
public static class Accessibility
{
    /// <summary>Build a CDP AXValue for a role type.</summary>
    private static JsonObject AxValueRole(string role) =>
        new() { ["type"] = "role", ["value"] = role };

    /// <summary>Build a CDP AXValue for a string type.</summary>
    private static JsonObject AxValueString(string value) =>
        new() { ["type"] = "string", ["value"] = value };

    /// <summary>Build a CDP AXValue for a boolean type.</summary>
    private static JsonObject AxValueBoolean(bool value) =>
        new() { ["type"] = "boolean", ["value"] = value };

    /// <summary>Build a CDP AXValue for an integer type.</summary>
    private static JsonObject AxValueInteger(uint value) =>
        new() { ["type"] = "integer", ["value"] = value };

    public static async Task<DomainResult> HandleAsync(
        string method,
        JsonNode? parameters,
        CdpContext ctx,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = parameters;
        await Task.CompletedTask.ConfigureAwait(false);
        switch (method)
        {
            case "enable":
                return DomainResult.Empty();

            case "getFullAXTree":
            {
                if (ctx.GetSessionPage(sessionId) is not { } page)
                {
                    return DomainResult.Err("No page");
                }

                List<JsonObject> nodes = page.WithDom(BuildAxNodes) ?? [];
                var array = new JsonArray();
                foreach (JsonObject node in nodes)
                {
                    array.Add(node);
                }

                return DomainResult.Ok(new JsonObject { ["nodes"] = array });
            }

            default:
                return DomainResult.Empty();
        }
    }

    /// <summary>Walk the full DOM tree and produce the CDP Accessibility AXNode array.</summary>
    public static List<JsonObject> BuildAxNodes(DomTree dom)
    {
        ArgumentNullException.ThrowIfNull(dom);
        var nodes = new List<JsonObject>();
        uint idCounter = 0;
        // Map DOM NodeId -> AX string id, populated only for nodes actually in the AX tree.
        var domToAx = new Dictionary<uint, string>();

        NodeId document = dom.Document;

        // Collect all DOM nodes in tree order (root + descendants).
        var allDomIds = new List<NodeId> { document };
        allDomIds.AddRange(dom.Descendants(document));

        // First pass: assign AX ids only to nodes that will produce an AX node.
        var eligible = new List<NodeId>();
        foreach (NodeId domId in allDomIds)
        {
            // Quick check without a full BuildAxNode (just a role check).
            if (dom.GetNode(domId) is { } node)
            {
                string role = MapRole(node.Data);
                if (role.Length != 0)
                {
                    idCounter++;
                    domToAx[domId.Raw] = idCounter.ToString(CultureInfo.InvariantCulture);
                    eligible.Add(domId);
                }
            }
        }

        // Second pass: build an AXNode for each eligible node.
        foreach (NodeId domId in eligible)
        {
            if (BuildAxNode(dom, domId, domToAx) is { } ax)
            {
                nodes.Add(ax);
            }
        }

        return nodes;
    }

    private static JsonObject? BuildAxNode(
        DomTree dom,
        NodeId nodeId,
        Dictionary<uint, string> domToAx)
    {
        if (dom.GetNode(nodeId) is not { } node
            || !domToAx.TryGetValue(nodeId.Raw, out string? axId))
        {
            return null;
        }

        string role = MapRole(node.Data);
        // Skip non-relevant nodes (Document, Doctype, Comment, PI).
        if (role.Length == 0)
        {
            return null;
        }

        string? name = ComputeName(dom, node);
        string? value = ComputeValue(dom, node);
        List<JsonObject> properties = ComputeProperties(node);

        var childIds = new JsonArray();
        foreach (NodeId childId in dom.Children(nodeId))
        {
            if (domToAx.TryGetValue(childId.Raw, out string? childAxId))
            {
                childIds.Add(childAxId);
            }
        }

        // Resolve parentId: walk DOM ancestors until we find one in the AX tree.
        string? parentId = null;
        NodeId? current = nodeId;
        while (current is { } cursor)
        {
            NodeId? nextParent = dom.GetNode(cursor)?.Parent;
            if (nextParent is not { } parent)
            {
                break;
            }

            if (domToAx.TryGetValue(parent.Raw, out string? axParentId))
            {
                parentId = axParentId;
                break;
            }

            current = parent;
        }

        // Build the node with only the non-empty optional fields; per the CDP spec an optional
        // field is omitted when empty.
        var axNode = new JsonObject
        {
            ["nodeId"] = axId,
            ["ignored"] = false,
            ["role"] = AxValueRole(role),
        };

        if (parentId is not null)
        {
            axNode["parentId"] = parentId;
        }

        if (name is not null)
        {
            axNode["name"] = AxValueString(name);
        }

        if (value is not null)
        {
            axNode["value"] = AxValueString(value);
        }

        if (properties.Count != 0)
        {
            var propertyArray = new JsonArray();
            foreach (JsonObject property in properties)
            {
                propertyArray.Add(property);
            }

            axNode["properties"] = propertyArray;
        }

        if (childIds.Count != 0)
        {
            axNode["childIds"] = childIds;
        }

        axNode["backendDOMNodeId"] = nodeId.Raw;
        return axNode;
    }

    /// <summary>Map an HTML element tag to an ARIA role value.</summary>
    private static string MapRole(NodeData data)
    {
        switch (data)
        {
            case DocumentData:
                return "RootWebArea";
            case TextData:
                return "StaticText";
            case DoctypeData or CommentData or ProcessingInstructionData:
                return string.Empty;
            case ElementData element:
            {
                string tag = element.Name.Local;
                List<Obscura.Dom.Attribute> attrs = element.Attrs;

                // Check the explicit role attribute first.
                if (FindAttribute(attrs, "role") is { } roleAttr)
                {
                    return roleAttr.Value switch
                    {
                        "button" => "button",
                        "link" => "link",
                        "heading" => "heading",
                        "textbox" or "searchbox" => "textbox",
                        "checkbox" => "checkbox",
                        "radio" => "radio",
                        "listbox" => "listbox",
                        "combobox" => "combobox",
                        "list" => "list",
                        "listitem" => "listitem",
                        "navigation" => "navigation",
                        "banner" => "banner",
                        "main" => "main",
                        "complementary" => "complementary",
                        "contentinfo" => "contentinfo",
                        "form" => "form",
                        "table" => "table",
                        "row" => "row",
                        "cell" or "gridcell" => "cell",
                        "img" => "image",
                        "dialog" => "dialog",
                        "alert" => "alert",
                        "tab" => "tab",
                        "tablist" => "tablist",
                        "tabpanel" => "tabpanel",
                        "menu" => "menu",
                        "menuitem" => "menuitem",
                        "toolbar" => "toolbar",
                        "separator" => "separator",
                        // presentation/none roles get the role but content is still in tree.
                        "presentation" or "none" => "presentation",
                        _ => "generic",
                    };
                }

                switch (tag)
                {
                    case "a":
                        return FindAttribute(attrs, "href") is not null ? "link" : "generic";
                    case "button":
                    case "summary":
                        return "button";
                    case "input":
                    {
                        string typeAttr = FindAttribute(attrs, "type")?.Value ?? "text";
                        return typeAttr switch
                        {
                            "submit" or "reset" or "button" or "image" => "button",
                            "checkbox" => "checkbox",
                            "radio" => "radio",
                            "range" => "slider",
                            "number" => "spinbutton",
                            "search" => "searchbox",
                            _ => "textbox",
                        };
                    }

                    case "textarea":
                        return "textbox";
                    case "select":
                        return FindAttribute(attrs, "multiple") is not null
                            || FindAttribute(attrs, "size") is not null
                            ? "listbox"
                            : "combobox";
                    case "h1":
                    case "h2":
                    case "h3":
                    case "h4":
                    case "h5":
                    case "h6":
                        return "heading";
                    case "img":
                    case "svg":
                        return "image";
                    case "ul":
                    case "ol":
                    case "menu":
                        return "list";
                    case "li":
                        return "listitem";
                    case "table":
                        return "table";
                    case "tr":
                        return "row";
                    case "td":
                    case "th":
                        return "cell";
                    case "nav":
                        return "navigation";
                    case "header":
                        return "banner";
                    case "main":
                        return "main";
                    case "footer":
                        return "contentinfo";
                    case "form":
                        return "form";
                    case "dialog":
                        return "dialog";
                    case "hr":
                        return "separator";
                    case "label":
                        return "LabelText";
                    case "article":
                        return "article";
                    case "aside":
                        return "complementary";
                    case "section":
                        return "region";
                    case "figure":
                        return "figure";
                    case "figcaption":
                        return "StaticText";
                    case "p":
                    case "div":
                    case "span":
                    case "pre":
                    case "blockquote":
                    case "code":
                    case "em":
                    case "strong":
                    case "b":
                    case "i":
                    case "u":
                    case "s":
                    case "small":
                    case "sub":
                    case "sup":
                    case "mark":
                    case "del":
                    case "ins":
                        return "generic";
                    case "iframe":
                        return "Iframe";
                    default:
                        return "generic";
                }
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>Compute the accessible name for a node.</summary>
    private static string? ComputeName(DomTree dom, Node node)
    {
        if (node.Data is ElementData element)
        {
            List<Obscura.Dom.Attribute> attrs = element.Attrs;

            // aria-label takes highest priority.
            if (FindAttribute(attrs, "aria-label") is { } label)
            {
                return label.Value;
            }

            // aria-labelledby.
            if (FindAttribute(attrs, "aria-labelledby") is { } labelledBy)
            {
                var name = new System.Text.StringBuilder();
                foreach (string idText in labelledBy.Value.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (dom.GetElementById(idText) is { } refId)
                    {
                        name.Append(dom.TextContent(refId));
                        name.Append(' ');
                    }
                }

                string trimmed = name.ToString().Trim();
                if (trimmed.Length != 0)
                {
                    return trimmed;
                }
            }

            // alt attribute for images.
            if (FindAttribute(attrs, "alt") is { Value.Length: > 0 } alt)
            {
                return alt.Value;
            }

            // title attribute.
            if (FindAttribute(attrs, "title") is { Value.Length: > 0 } title)
            {
                return title.Value;
            }

            // placeholder.
            if (FindAttribute(attrs, "placeholder") is { Value.Length: > 0 } placeholder)
            {
                return placeholder.Value;
            }
        }

        // For text nodes, the name is the text content.
        if (node.Data is TextData text)
        {
            string trimmed = text.Contents.Trim();
            if (trimmed.Length != 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>Compute the accessible value for a node (for example the current input value).</summary>
    private static string? ComputeValue(DomTree dom, Node node)
    {
        if (node.Data is ElementData element)
        {
            string tag = element.Name.Local;
            // For native form controls, return the value attribute.
            if (tag is "input" or "textarea" or "select")
            {
                return FindAttribute(element.Attrs, "value")?.Value;
            }

            if (IsContentEditingHost(dom, node))
            {
                return dom.TextContent(node.Id);
            }
        }

        return null;
    }

    private static bool? ContentEditableKeyword(Node node)
    {
        if (node.Data is not ElementData element)
        {
            return null;
        }

        if (FindAttribute(element.Attrs, "contenteditable") is not { } attr)
        {
            return null;
        }

        string value = attr.Value;
        if (value.Length == 0
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("plaintext-only", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static bool IsEffectivelyContentEditable(DomTree dom, NodeId nodeId)
    {
        NodeId? current = nodeId;
        while (current is { } id)
        {
            if (dom.GetNode(id) is not { } node)
            {
                return false;
            }

            if (ContentEditableKeyword(node) is { } editable)
            {
                return editable;
            }

            current = node.Parent;
        }

        return false;
    }

    private static bool IsContentEditingHost(DomTree dom, Node node) =>
        IsEffectivelyContentEditable(dom, node.Id)
        && (node.Parent is not { } parent || !IsEffectivelyContentEditable(dom, parent));

    /// <summary>Compute accessibility properties for a node.</summary>
    private static List<JsonObject> ComputeProperties(Node node)
    {
        if (node.Data is not ElementData element)
        {
            return [];
        }

        string tag = element.Name.Local;
        List<Obscura.Dom.Attribute> attrs = element.Attrs;
        var props = new List<JsonObject>();

        // focusable
        bool focusable = tag is "a" or "button" or "input" or "select" or "textarea"
                or "details" or "summary"
            || attrs.Exists(a => a.Name.Local is "tabindex" or "contenteditable");
        if (focusable)
        {
            props.Add(new JsonObject
            {
                ["name"] = "focusable",
                ["value"] = AxValueBoolean(true),
            });
        }

        // editable
        if (tag is "input" or "textarea"
            || attrs.Exists(a => a.Name.Local == "contenteditable"
                && !string.Equals(a.Value, "false", StringComparison.Ordinal)))
        {
            props.Add(new JsonObject
            {
                ["name"] = "editable",
                ["value"] = AxValueBoolean(true),
            });
        }

        // checked for checkboxes/radios
        if (attrs.Exists(a => a.Name.Local == "checked"))
        {
            props.Add(new JsonObject
            {
                ["name"] = "checked",
                ["value"] = AxValueBoolean(true),
            });
        }

        // disabled
        if (attrs.Exists(a => a.Name.Local == "disabled"))
        {
            props.Add(new JsonObject
            {
                ["name"] = "disabled",
                ["value"] = AxValueBoolean(true),
            });
        }

        // level for headings
        if (tag.Length > 1
            && tag[0] == 'h'
            && uint.TryParse(
                tag.AsSpan(1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint level)
            && level is >= 1 and <= 6)
        {
            props.Add(new JsonObject
            {
                ["name"] = "level",
                ["value"] = AxValueInteger(level),
            });
        }

        // required
        if (attrs.Exists(a => a.Name.Local is "required" or "aria-required"))
        {
            props.Add(new JsonObject
            {
                ["name"] = "required",
                ["value"] = AxValueBoolean(true),
            });
        }

        // multiline for textarea
        if (tag == "textarea")
        {
            props.Add(new JsonObject
            {
                ["name"] = "multiline",
                ["value"] = AxValueBoolean(true),
            });
        }

        return props;
    }

    private static Obscura.Dom.Attribute? FindAttribute(List<Obscura.Dom.Attribute> attrs, string local)
    {
        for (int i = 0; i < attrs.Count; i++)
        {
            if (string.Equals(attrs[i].Name.Local, local, StringComparison.Ordinal))
            {
                return attrs[i];
            }
        }

        return null;
    }
}
