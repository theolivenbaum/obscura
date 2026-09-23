namespace PocketCalculator.Js.Runtime;

/// <summary>
/// Shared markdown extraction script, used by the <c>LP.getMarkdown</c> CDP
/// method and the CLI's <c>--dump markdown</c> mode.
/// </summary>
/// <remarks>
/// Lives here rather than in either caller so the CDP and CLI layers can both
/// use it without depending on each other. It is JavaScript rather than a
/// managed DOM walk on purpose: it must see the same live DOM page script sees.
/// Began as a byte-identical copy of <c>crates/obscura-js/src/markdown.rs</c>.
/// <para>
/// DEVIATION from that script, which copied text and URLs into the markdown
/// verbatim (SECURITY.md M10): page text reading <c>&lt;script&gt;</c> came out as
/// a literal tag, a <c>javascript:</c> link as a live link, and a <c>]</c> or
/// <c>)</c> in link text or a URL could close the link early and spoof another.
/// Now text escapes <c>&lt;</c> and <c>&gt;</c> as entities, inside code too (a backtick in
/// the text could otherwise close the code span); link text and image
/// alt text also backslash-escape <c>\</c>, <c>[</c> and <c>]</c>; a destination
/// percent-encodes whitespace, parentheses and angle brackets; and a link or image
/// whose URL has a scheme other than http, https or mailto keeps its text and drops
/// the URL. Output for text and URLs without those characters is unchanged, so the
/// two engines still agree on ordinary pages.
/// </para>
/// </remarks>
public static class MarkdownScript
{
    /// <summary>
    /// A JS expression that walks <c>document.body</c> and returns markdown.
    /// Must be evaluated against a page with a fully bootstrapped JS runtime.
    /// </summary>
    public const string HtmlToMarkdown = """
        (function() {
            function escText(s, inLink) {
                s = s.replace(/</g, '&lt;').replace(/>/g, '&gt;');
                return inLink ? s.replace(/[\\\[\]]/g, '\\$&') : s;
            }
            function safeUrl(url) {
                var bare = url.replace(/[\u0000-\u0020\u007f]/g, '');
                var scheme = /^([a-zA-Z][a-zA-Z0-9+.\-]*):/.exec(bare);
                if (scheme && !/^(https?|mailto)$/i.test(scheme[1])) return null;
                return url.trim().replace(/[\u0000-\u0020\u007f()<>]/g, function(c) {
                    return '%' + ('0' + c.charCodeAt(0).toString(16).toUpperCase()).slice(-2);
                });
            }
            function toMd(el, depth) {
                if (!el) return '';
                var out = '';
                if (el.nodeType === 3) return escText(el.textContent || '', depth > 0);
                if (el.nodeType !== 1) return '';
                var tag = (el.tagName || '').toLowerCase();
                var children = '';
                var cn = el.childNodes || [];
                var childDepth = tag === 'a' ? depth + 1 : depth;
                for (var i = 0; i < cn.length; i++) children += toMd(cn[i], childDepth);
                children = children.replace(/\n{3,}/g, '\n\n');
                switch(tag) {
                    case 'h1': return '\n# ' + children.trim() + '\n\n';
                    case 'h2': return '\n## ' + children.trim() + '\n\n';
                    case 'h3': return '\n### ' + children.trim() + '\n\n';
                    case 'h4': return '\n#### ' + children.trim() + '\n\n';
                    case 'h5': return '\n##### ' + children.trim() + '\n\n';
                    case 'h6': return '\n###### ' + children.trim() + '\n\n';
                    case 'p': return '\n' + children.trim() + '\n\n';
                    case 'br': return '\n';
                    case 'hr': return '\n---\n\n';
                    case 'strong': case 'b': return '**' + children + '**';
                    case 'em': case 'i': return '*' + children + '*';
                    case 'code': return '`' + children + '`';
                    case 'pre': return '\n```\n' + children + '\n```\n\n';
                    case 'blockquote': return '\n> ' + children.trim().replace(/\n/g, '\n> ') + '\n\n';
                    case 'a':
                        var href = safeUrl(el.getAttribute('href') || '');
                        if (href && children.trim()) return '[' + children.trim() + '](' + href + ')';
                        return children;
                    case 'img':
                        var src = safeUrl(el.getAttribute('src') || '');
                        var alt = escText(el.getAttribute('alt') || '', true);
                        if (src === null) return alt;
                        return '![' + alt + '](' + src + ')';
                    case 'ul': case 'ol':
                        return '\n' + children + '\n';
                    case 'li':
                        var parent = el.parentNode;
                        var isOrdered = parent && parent.tagName && parent.tagName.toLowerCase() === 'ol';
                        var bullet = isOrdered ? '1. ' : '- ';
                        return bullet + children.trim() + '\n';
                    case 'table': return '\n' + children + '\n';
                    case 'thead': case 'tbody': case 'tfoot': return children;
                    case 'tr':
                        var cells = [];
                        var tds = el.childNodes || [];
                        for (var j = 0; j < tds.length; j++) {
                            if (tds[j].nodeType === 1) cells.push(toMd(tds[j], depth).trim());
                        }
                        return '| ' + cells.join(' | ') + ' |\n';
                    case 'th': case 'td': return children;
                    case 'script': case 'style': case 'noscript': case 'link': case 'meta': return '';
                    case 'div': case 'section': case 'article': case 'main': case 'aside': case 'nav': case 'header': case 'footer':
                        return '\n' + children;
                    case 'span': return children;
                    default: return children;
                }
            }
            var body = document.body || document.documentElement;
            var md = toMd(body, 0);
            md = md.replace(/\n{3,}/g, '\n\n').trim();
            return md;
        })()
        """;
}
