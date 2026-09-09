using System.Text.Json.Nodes;

namespace Obscura.Mcp;

/// <summary>
/// The <c>tools/list</c> payload, byte for byte the JSON <c>handle_tools_list</c>
/// builds in <c>crates/obscura-mcp/src/lib.rs</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tool names, their argument schemas and their result shapes are the wire
/// contract an MCP client is written against, so this is a transcription of the
/// Rust <c>json!</c> literal rather than a rebuild: key order, descriptions and
/// number literals all have to survive. <c>2.0</c> stays a float and <c>0</c>
/// stays an integer because <c>serde_json</c> prints them that way.
/// </para>
/// <para>
/// The render half is unconditional here. The Rust crate gates
/// <c>browser_screenshot</c> / <c>browser_pdf</c> behind <c>#[cfg(feature =
/// "render")]</c>; the C# port always compiles rendering in, so both are always
/// advertised.
/// </para>
/// </remarks>
internal static class ToolSchemas
{
    /// <summary>A fresh copy of the tool list, so a caller cannot mutate the source.</summary>
    internal static JsonArray All()
    {
        var tools = (JsonArray)JsonNode.Parse(BaseTools)!;
        foreach (var tool in (JsonArray)JsonNode.Parse(RenderTools)!)
        {
            tools.Add(tool!.DeepClone());
        }

        return tools;
    }

    private const string BaseTools =
        """
        [
          {
            "name": "browser_navigate",
            "description": "Navigate to a URL and wait for the page to load",
            "inputSchema": {
              "type": "object",
              "properties": {
                "url": {
                  "type": "string",
                  "description": "URL to navigate to"
                },
                "waitUntil": {
                  "type": "string",
                  "enum": [
                    "load",
                    "domcontentloaded",
                    "networkidle0"
                  ],
                  "description": "Navigation wait condition (default: load)"
                }
              },
              "required": [
                "url"
              ]
            }
          },
          {
            "name": "browser_snapshot",
            "description": "Get the current page content as text (title, URL, and readable body text)",
            "inputSchema": {
              "type": "object",
              "properties": {
                "max_chars": {
                  "type": "number",
                  "minimum": 0,
                  "description": "Truncate readable body text to this many characters (default: 4000)"
                }
              },
              "additionalProperties": false
            }
          },
          {
            "name": "browser_click",
            "description": "Click an element. Pass `ref` (preferred, from browser_snapshot / browser_interactive_elements) OR a `selector`.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "ref": {
                  "type": "string",
                  "description": "Element ref like 'e3' from a recent snapshot"
                },
                "selector": {
                  "type": "string",
                  "description": "CSS selector (fallback if ref unavailable)"
                }
              }
            }
          },
          {
            "name": "browser_fill",
            "description": "Set the value of an input element. Pass `ref` (preferred) OR `selector`.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "ref": {
                  "type": "string"
                },
                "selector": {
                  "type": "string"
                },
                "value": {
                  "type": "string",
                  "description": "Value to set"
                }
              },
              "required": [
                "value"
              ]
            }
          },
          {
            "name": "browser_type",
            "description": "Type text into an input element (appends to existing value). Pass `ref` (preferred) OR `selector`.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "ref": {
                  "type": "string"
                },
                "selector": {
                  "type": "string"
                },
                "text": {
                  "type": "string",
                  "description": "Text to type"
                }
              },
              "required": [
                "text"
              ]
            }
          },
          {
            "name": "browser_press_key",
            "description": "Dispatch a keyboard event on an element or the document",
            "inputSchema": {
              "type": "object",
              "properties": {
                "key": {
                  "type": "string",
                  "description": "Key name (e.g. Enter, Tab, Escape)"
                },
                "selector": {
                  "type": "string",
                  "description": "CSS selector (optional, defaults to document)"
                }
              },
              "required": [
                "key"
              ]
            }
          },
          {
            "name": "browser_select_option",
            "description": "Select an option from a <select> element",
            "inputSchema": {
              "type": "object",
              "properties": {
                "selector": {
                  "type": "string",
                  "description": "CSS selector of the <select> element"
                },
                "value": {
                  "type": "string",
                  "description": "Value or text of the option to select"
                }
              },
              "required": [
                "selector",
                "value"
              ]
            }
          },
          {
            "name": "browser_evaluate",
            "description": "Evaluate a JavaScript expression in the page context and return the result",
            "inputSchema": {
              "type": "object",
              "properties": {
                "expression": {
                  "type": "string",
                  "description": "JavaScript expression to evaluate"
                }
              },
              "required": [
                "expression"
              ]
            }
          },
          {
            "name": "browser_wait_for",
            "description": "Wait for a CSS selector to appear in the DOM",
            "inputSchema": {
              "type": "object",
              "properties": {
                "selector": {
                  "type": "string",
                  "description": "CSS selector to wait for"
                },
                "timeout": {
                  "type": "number",
                  "description": "Timeout in seconds (default: 30)"
                }
              },
              "required": [
                "selector"
              ]
            }
          },
          {
            "name": "browser_network_requests",
            "description": "Return the list of network requests made by the current page",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_console_messages",
            "description": "Return the console messages logged by the current page",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_close",
            "description": "Close the current browser page and reset state",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_markdown",
            "description": "Extract the current page as Markdown (headings, paragraphs, lists, links, code blocks). Use this instead of browser_snapshot when you want token-dense structured content rather than plain text.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "max_chars": {
                  "type": "number",
                  "description": "Truncate to this many characters (default 4000)"
                }
              }
            }
          },
          {
            "name": "browser_links",
            "description": "List every anchor link on the current page as one JSON object per line: {text, href}. Use when you need to enumerate where to navigate next.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "limit": {
                  "type": "number",
                  "description": "Max number of links to return (default 100)"
                },
                "internal_only": {
                  "type": "boolean",
                  "description": "If true, only return links on the same origin as the current page"
                }
              }
            }
          },
          {
            "name": "browser_interactive_elements",
            "description": "List every clickable / typeable element on the current page with a stable ref ID and a brief description. Use this BEFORE clicking or filling so you can refer to elements by ref instead of guessing a CSS selector. Refs look like 'e3' and stay valid until the next navigation.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "limit": {
                  "type": "number",
                  "description": "Max number of elements (default 100)"
                }
              }
            }
          },
          {
            "name": "browser_back",
            "description": "Navigate back in the page history (equivalent to the browser back button).",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_forward",
            "description": "Navigate forward in the page history.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_reload",
            "description": "Reload the current page.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_get_cookies",
            "description": "Return all cookies in the browser's cookie jar as one JSON object per line.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "domain": {
                  "type": "string",
                  "description": "Filter to cookies on this domain (default: all)"
                }
              }
            }
          },
          {
            "name": "browser_set_cookie",
            "description": "Add or replace a cookie in the jar. Use this to skip a login flow when you already have a session token.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "name": {
                  "type": "string"
                },
                "value": {
                  "type": "string"
                },
                "domain": {
                  "type": "string",
                  "description": "e.g. example.com or .example.com"
                },
                "path": {
                  "type": "string",
                  "description": "default '/'"
                },
                "secure": {
                  "type": "boolean"
                },
                "http_only": {
                  "type": "boolean"
                }
              },
              "required": [
                "name",
                "value",
                "domain"
              ]
            }
          },
          {
            "name": "browser_clear_cookies",
            "description": "Wipe every cookie from the jar.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_wait_for_text",
            "description": "Wait until a substring appears anywhere in the rendered page text. Use when you want to wait for a result message or notification rather than a specific selector.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "text": {
                  "type": "string"
                },
                "timeout": {
                  "type": "number",
                  "description": "Seconds (default 30)"
                }
              },
              "required": [
                "text"
              ]
            }
          },
          {
            "name": "browser_detect_forms",
            "description": "List every <form> on the page with its action URL, method, and a description of each input/textarea/select. Use to understand a form's structure before filling it in.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_fill_form",
            "description": "Fill multiple inputs in one call. `fields` is an array of {ref?, selector?, value, type?}. type='text' (default) sets value, type='check'/'uncheck' toggles checkboxes, type='select' picks an option by value or visible text. Saves N round-trips vs N browser_fill calls.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "fields": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "ref": {
                        "type": "string"
                      },
                      "selector": {
                        "type": "string"
                      },
                      "value": {
                        "type": "string"
                      },
                      "type": {
                        "type": "string",
                        "enum": [
                          "text",
                          "check",
                          "uncheck",
                          "select"
                        ]
                      }
                    }
                  }
                },
                "submit_ref": {
                  "type": "string",
                  "description": "Optional: click this element after filling (e.g. submit button ref)"
                },
                "submit_selector": {
                  "type": "string"
                }
              },
              "required": [
                "fields"
              ]
            }
          },
          {
            "name": "browser_scroll",
            "description": "Scroll the page or an element. `direction` is 'top'|'bottom'|'up'|'down'|'left'|'right' (default 'down'). `amount` in pixels (default viewport height). Use 'bottom' to trigger infinite-scroll loaders.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "direction": {
                  "type": "string",
                  "enum": [
                    "top",
                    "bottom",
                    "up",
                    "down",
                    "left",
                    "right"
                  ]
                },
                "amount": {
                  "type": "number",
                  "description": "Pixels (default: one viewport)"
                },
                "ref": {
                  "type": "string",
                  "description": "Optional element to scroll into view"
                },
                "selector": {
                  "type": "string"
                }
              }
            }
          },
          {
            "name": "browser_get_attribute",
            "description": "Read an attribute of an element (href, src, value, class, data-*, etc.). Returns the raw attribute value as a string, or empty string if missing.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "ref": {
                  "type": "string"
                },
                "selector": {
                  "type": "string"
                },
                "attribute": {
                  "type": "string",
                  "description": "Attribute name (e.g. href, value, src)"
                }
              },
              "required": [
                "attribute"
              ]
            }
          },
          {
            "name": "browser_count",
            "description": "Count how many elements on the page match a CSS selector. Cheap existence / pagination probe.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "selector": {
                  "type": "string"
                }
              },
              "required": [
                "selector"
              ]
            }
          },
          {
            "name": "browser_extract",
            "description": "Extract a structured object from the page given a map of {field_name: css_selector}. Returns one JSON object with each field set to the matching element's text content (or attribute via 'selector@attr' syntax, e.g. 'a@href'). For list extraction, append '[]' to the field name (e.g. 'rows[]') and the value will be an array.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "schema": {
                  "type": "object",
                  "description": "Map of field_name to CSS selector. Suffix selector with '@attr' for attribute, suffix field name with '[]' for array."
                }
              },
              "required": [
                "schema"
              ]
            }
          },
          {
            "name": "browser_tab_new",
            "description": "Open a new tab (isolated browser page). Returns the tab ID; subsequent tool calls operate on the most recently opened or browser_tab_switch'd tab.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "url": {
                  "type": "string",
                  "description": "Optional URL to navigate the new tab to"
                }
              }
            }
          },
          {
            "name": "browser_tab_list",
            "description": "List all open tabs with their ID, URL, title, and which one is active.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_tab_switch",
            "description": "Switch the active tab. All subsequent tool calls (snapshot, click, etc.) target this tab.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "tab_id": {
                  "type": "string"
                }
              },
              "required": [
                "tab_id"
              ]
            }
          },
          {
            "name": "browser_tab_close",
            "description": "Close a tab by ID (default: the active tab). If you close the active tab, the next remaining tab becomes active.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "tab_id": {
                  "type": "string"
                }
              }
            }
          },
          {
            "name": "browser_search",
            "description": "Find substring matches in the visible page text. Returns each match with its surrounding context. Use this to confirm content exists before scraping or to locate a section.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string"
                },
                "case_sensitive": {
                  "type": "boolean"
                },
                "limit": {
                  "type": "number",
                  "description": "Max matches to return (default 10)"
                },
                "context_chars": {
                  "type": "number",
                  "description": "Chars on each side of the match (default 80)"
                }
              },
              "required": [
                "query"
              ]
            }
          },
          {
            "name": "browser_storage_state",
            "description": "Export the full authentication / session state (cookies + localStorage + sessionStorage) as a JSON object. Save this to skip a login on a subsequent run via browser_set_storage_state.",
            "inputSchema": {
              "type": "object",
              "properties": {}
            }
          },
          {
            "name": "browser_set_storage_state",
            "description": "Restore session state previously returned by browser_storage_state. Pass the JSON object. Use to bring an authenticated session back without re-logging in.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "state": {
                  "type": "object",
                  "description": "{cookies: [...], origins: [{origin, localStorage: [...], sessionStorage: [...]}]}"
                }
              },
              "required": [
                "state"
              ]
            }
          }
        ]
        """;

    private const string RenderTools =
        """
        [
          {
            "name": "browser_screenshot",
            "description": "Capture the current rendered viewport as a PNG image. Width and height default to the page's current CSS viewport.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "width": {
                  "type": "number",
                  "exclusiveMinimum": 0,
                  "maximum": 32768,
                  "description": "Optional CSS-pixel capture width"
                },
                "height": {
                  "type": "number",
                  "exclusiveMinimum": 0,
                  "maximum": 32768,
                  "description": "Optional CSS-pixel capture height"
                }
              },
              "additionalProperties": false
            }
          },
          {
            "name": "browser_pdf",
            "description": "Export the current rendered document as a paginated raster PDF using print media and bounded PDF defaults.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "landscape": {
                  "type": "boolean"
                },
                "print_background": {
                  "type": "boolean"
                },
                "scale": {
                  "type": "number",
                  "minimum": 0.1,
                  "maximum": 2.0
                },
                "paper_width": {
                  "type": "number",
                  "exclusiveMinimum": 0,
                  "maximum": 200,
                  "description": "Paper width in inches"
                },
                "paper_height": {
                  "type": "number",
                  "exclusiveMinimum": 0,
                  "maximum": 200,
                  "description": "Paper height in inches"
                },
                "margin_top": {
                  "type": "number",
                  "minimum": 0,
                  "description": "Top margin in inches"
                },
                "margin_bottom": {
                  "type": "number",
                  "minimum": 0,
                  "description": "Bottom margin in inches"
                },
                "margin_left": {
                  "type": "number",
                  "minimum": 0,
                  "description": "Left margin in inches"
                },
                "margin_right": {
                  "type": "number",
                  "minimum": 0,
                  "description": "Right margin in inches"
                }
              },
              "additionalProperties": false
            }
          }
        ]
        """;
}
