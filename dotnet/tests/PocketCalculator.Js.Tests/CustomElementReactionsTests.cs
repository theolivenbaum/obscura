using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Custom element upgrades and reactions against Chromium's. The shim used to upgrade only what
/// define() found and to call connectedCallback from there: nothing created later was upgraded,
/// and insertion, removal and attribute changes never reached a callback, so YouTube (Polymer),
/// MSN (FAST) and Reddit (faceplate) never booted. Every expected log here is the one Chromium
/// 141.0.7390.37 (headless, Playwright) printed for the same script.
/// </summary>
public sealed class CustomElementReactionsTests
{
    // Logs every callback, labelled by id or data-l.
    private const string Prelude = """
        const L = [];
        const log = (s) => L.push(s);
        function lab(el) {
          if (!el || el.nodeType !== 1) return String(el);
          return el.id || (el.getAttribute('data-l') ? el.localName + '#' + el.getAttribute('data-l') : el.localName);
        }
        function errs(f) {
          try { const r = f(); return 'ok:' + (r && r.nodeType ? lab(r) : String(r)); }
          catch (e) { return (e && e.name) + ': ' + (e && e.message); }
        }
        function mk(tagLabel, observed, Base) {
          Base = Base || HTMLElement;
          return class extends Base {
            static get observedAttributes() { return observed; }
            constructor() {
              super();
              log('ctor ' + tagLabel + ' ' + lab(this) + ' conn=' + this.isConnected + ' kids=' + this.childNodes.length + ' attrs=' + this.attributes.length);
            }
            connectedCallback() { log('connected ' + lab(this) + ' next=' + lab(this.nextSibling && this.nextSibling.nodeType === 1 ? this.nextSibling : null)); }
            disconnectedCallback() { log('disconnected ' + lab(this) + ' conn=' + this.isConnected); }
            attributeChangedCallback(name, oldV, newV, ns) { log('attr ' + lab(this) + ' ' + name + ' ' + oldV + ' -> ' + newV + ' ns=' + ns); }
          };
        }
        const onError = (e) => log('onerror ' + (e.error ? e.error.name + ': ' + e.error.message : e.message));
        window.addEventListener('error', onError);
        """;

    private static string Run(string html, string body)
    {
        using var fixture = RuntimeFixture.Setup(html);
        return Run(fixture.Runtime, body);
    }

    private static string Run(PocketCalculatorJsRuntime runtime, string body) =>
        runtime.Evaluate("(() => {" + Prelude + body + "\nwindow.removeEventListener('error', onError);\nreturn L.join('\\n'); })()")!
            .GetValue<string>();

    private static string Lines(string text) => text.ReplaceLineEndings("\n").Trim();

    private const string Stage = "<html><body><div id=\"stage\"></div></body></html>";

    [Fact]
    public void DefineUpgradesTheDocumentInShadowIncludingTreeOrder()
    {
        var result = Run(
            """
            <html><body><div id="pre">
              <x-a id="a1" foo="1" bar="2" baz="3"><x-a id="a2"></x-a></x-a>
              <x-u id="u1"></x-u>
              <div id="host1"><template shadowrootmode="open"><x-a id="ashadow"></x-a></template><x-a id="alight"></x-a></div>
              <x-a id="a3"></x-a>
            </div></body></html>
            """,
            """
            const a1 = document.getElementById('a1');
            log('before ' + (a1.constructor === HTMLElement) + ' defined=' + a1.matches(':defined')
              + ' undefined=' + document.querySelectorAll('#pre :not(:defined)').length);
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            log('after same=' + (document.getElementById('a1') === a1) + ' inst=' + (a1 instanceof XA)
              + ' defined=' + a1.matches(':defined') + ' u1=' + document.getElementById('u1').matches(':defined'));
            """);
        Assert.Equal(Lines("""
            before true defined=false undefined=5
            ctor XA a1 conn=true kids=1 attrs=4
            attr a1 foo null -> 1 ns=null
            attr a1 bar null -> 2 ns=null
            connected a1 next=null
            ctor XA a2 conn=true kids=0 attrs=1
            connected a2 next=null
            ctor XA ashadow conn=true kids=0 attrs=1
            connected ashadow next=null
            ctor XA alight conn=true kids=0 attrs=1
            connected alight next=null
            ctor XA a3 conn=true kids=0 attrs=1
            connected a3 next=null
            after same=true inst=true defined=true u1=false
            """), result);
    }

    [Fact]
    public void CreateElementIsSynchronousAndInsertionRemovalAndMovesReact()
    {
        var result = Run(Stage, """
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            const stage = document.getElementById('stage');
            const e = document.createElement('x-a');
            e.setAttribute('data-l', 'c1');
            log('after create inst=' + (e instanceof XA) + ' defined=' + e.matches(':defined'));
            e.setAttribute('foo', 'x');
            e.setAttribute('foo', 'x');
            e.setAttribute('qux', 'y');
            e.removeAttribute('nope');
            e.removeAttribute('foo');
            stage.appendChild(e);
            stage.appendChild(e);
            const d = document.createElement('div');
            stage.appendChild(d);
            d.appendChild(e);
            e.remove();
            const n = new XA();
            log('new inst=' + (n instanceof XA) + ' ln=' + n.localName + ' conn=' + n.isConnected);
            log('new HTMLElement ' + errs(() => new HTMLElement()));
            log('undefined class ' + errs(() => new (class extends HTMLElement {})()));
            """);
        Assert.Equal(Lines("""
            ctor XA x-a conn=false kids=0 attrs=0
            after create inst=true defined=true
            attr x-a#c1 foo null -> x ns=null
            attr x-a#c1 foo x -> x ns=null
            attr x-a#c1 foo x -> null ns=null
            connected x-a#c1 next=null
            disconnected x-a#c1 conn=true
            connected x-a#c1 next=null
            disconnected x-a#c1 conn=true
            connected x-a#c1 next=null
            disconnected x-a#c1 conn=false
            ctor XA x-a conn=false kids=0 attrs=0
            new inst=true ln=x-a conn=false
            new HTMLElement TypeError: Failed to construct 'HTMLElement': Illegal constructor
            undefined class TypeError: Failed to construct 'HTMLElement': Illegal constructor
            """), result);
    }

    [Fact]
    public void SubtreeInsertionAndRemovalReachEveryCustomDescendantInOrder()
    {
        var result = Run(Stage, """
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            const stage = document.getElementById('stage');
            const make = (l) => { const x = document.createElement('x-a'); x.setAttribute('data-l', l); return x; };
            const w = document.createElement('div');
            const p = make('p'), q = make('q'), r = make('r');
            const host = document.createElement('div');
            const sr = host.attachShadow({ mode: 'open' });
            sr.appendChild(make('s'));
            host.appendChild(make('t'));
            p.appendChild(q);
            w.appendChild(p);
            w.appendChild(host);
            w.appendChild(r);
            log('-- append w');
            stage.appendChild(w);
            log('-- remove w');
            w.remove();
            log('-- append frag');
            const f = document.createDocumentFragment();
            f.appendChild(p); f.appendChild(r);
            stage.appendChild(f);
            log('-- append(a, b)');
            stage.append(make('pa'), make('pb'));
            log('-- replaceChildren');
            stage.replaceChildren();
            log('-- shadow insert');
            stage.appendChild(host);
            sr.appendChild(make('s2'));
            log('-- textContent clear');
            stage.textContent = '';
            """);
        Assert.Equal(Lines("""
            ctor XA x-a conn=false kids=0 attrs=0
            ctor XA x-a conn=false kids=0 attrs=0
            ctor XA x-a conn=false kids=0 attrs=0
            ctor XA x-a conn=false kids=0 attrs=0
            ctor XA x-a conn=false kids=0 attrs=0
            -- append w
            connected x-a#p next=div
            connected x-a#q next=null
            connected x-a#s next=null
            connected x-a#t next=null
            connected x-a#r next=null
            -- remove w
            disconnected x-a#p conn=false
            disconnected x-a#q conn=false
            disconnected x-a#s conn=false
            disconnected x-a#t conn=false
            disconnected x-a#r conn=false
            -- append frag
            connected x-a#p next=x-a#r
            connected x-a#q next=null
            connected x-a#r next=null
            -- append(a, b)
            ctor XA x-a conn=false kids=0 attrs=0
            ctor XA x-a conn=false kids=0 attrs=0
            connected x-a#pa next=x-a#pb
            connected x-a#pb next=null
            -- replaceChildren
            disconnected x-a#p conn=false
            disconnected x-a#q conn=false
            disconnected x-a#r conn=false
            disconnected x-a#pa conn=false
            disconnected x-a#pb conn=false
            -- shadow insert
            connected x-a#s next=null
            connected x-a#t next=null
            ctor XA x-a conn=false kids=0 attrs=0
            connected x-a#s2 next=null
            -- textContent clear
            disconnected x-a#s conn=false
            disconnected x-a#s2 conn=false
            disconnected x-a#t conn=false
            """), result);
    }

    [Fact]
    public void MarkupInsertionUpgradesNewElementsBeforeDisconnectingOldOnes()
    {
        var result = Run(Stage, """
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            const stage = document.getElementById('stage');
            stage.innerHTML = '<x-a data-l="i1" foo="1" qux="2"><x-a data-l="i2"></x-a><span><x-a data-l="i3" bar="b"></x-a></span></x-a><x-a data-l="i4"></x-a>';
            log('-- innerHTML replace');
            stage.innerHTML = '<x-a data-l="i5"></x-a>';
            log('-- outerHTML');
            stage.firstChild.outerHTML = '<x-a data-l="i6"></x-a><b></b>';
            log('-- insertAdjacentHTML');
            stage.firstChild.insertAdjacentHTML('afterend', '<x-a data-l="j1"><x-a data-l="j2"></x-a></x-a>');
            stage.insertAdjacentHTML('afterbegin', '<x-a data-l="j0"></x-a>');
            stage.textContent = '';
            """);
        Assert.Equal(Lines("""
            ctor XA x-a#i1 conn=true kids=2 attrs=3
            attr x-a#i1 foo null -> 1 ns=null
            connected x-a#i1 next=x-a#i4
            ctor XA x-a#i2 conn=true kids=0 attrs=1
            connected x-a#i2 next=span
            ctor XA x-a#i3 conn=true kids=0 attrs=2
            attr x-a#i3 bar null -> b ns=null
            connected x-a#i3 next=null
            ctor XA x-a#i4 conn=true kids=0 attrs=1
            connected x-a#i4 next=null
            -- innerHTML replace
            ctor XA x-a#i5 conn=true kids=0 attrs=1
            connected x-a#i5 next=null
            disconnected x-a#i1 conn=false
            disconnected x-a#i2 conn=false
            disconnected x-a#i3 conn=false
            disconnected x-a#i4 conn=false
            -- outerHTML
            ctor XA x-a#i6 conn=true kids=0 attrs=1
            connected x-a#i6 next=b
            disconnected x-a#i5 conn=false
            -- insertAdjacentHTML
            ctor XA x-a#j1 conn=true kids=1 attrs=1
            connected x-a#j1 next=b
            ctor XA x-a#j2 conn=true kids=0 attrs=1
            connected x-a#j2 next=null
            ctor XA x-a#j0 conn=true kids=0 attrs=1
            connected x-a#j0 next=x-a#i6
            disconnected x-a#j0 conn=false
            disconnected x-a#i6 conn=false
            disconnected x-a#j1 conn=false
            disconnected x-a#j2 conn=false
            """), result);
    }

    [Fact]
    public void DisconnectedParsingUpgradesExceptInertDocuments()
    {
        var result = Run(Stage, """
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            const stage = document.getElementById('stage');
            const d = document.createElement('div');
            d.innerHTML = '<x-a data-l="k1" foo="z"><x-a data-l="k2"></x-a></x-a>';
            log('k1 inst=' + (d.firstChild instanceof XA) + ' defined=' + d.firstChild.matches(':defined'));
            log('-- append');
            stage.appendChild(d);
            const tpl = document.createElement('template');
            tpl.innerHTML = '<x-a data-l="k4"></x-a>';
            log('k4 inst=' + (tpl.content.firstChild instanceof XA) + ' clone=' + (tpl.content.cloneNode(true).firstChild instanceof XA));
            log('-- importNode');
            const imp = document.importNode(tpl.content, true);
            log('k4 imported inst=' + (imp.firstChild instanceof XA));
            log('-- range fragment');
            const fr = document.createRange().createContextualFragment('<x-a data-l="k5"></x-a>');
            log('k5 inst=' + (fr.firstChild instanceof XA));
            const pd = new DOMParser().parseFromString('<x-a data-l="k6"></x-a>', 'text/html');
            log('k6 inst=' + (pd.body.firstChild instanceof XA));
            """);
        Assert.Equal(Lines("""
            ctor XA x-a#k1 conn=false kids=1 attrs=2
            attr x-a#k1 foo null -> z ns=null
            ctor XA x-a#k2 conn=false kids=0 attrs=1
            k1 inst=true defined=true
            -- append
            connected x-a#k1 next=null
            connected x-a#k2 next=null
            k4 inst=false clone=false
            -- importNode
            ctor XA x-a#k4 conn=false kids=0 attrs=1
            k4 imported inst=true
            -- range fragment
            ctor XA x-a#k5 conn=false kids=0 attrs=1
            k5 inst=true
            k6 inst=false
            """), result);
    }

    [Fact]
    public void CloneNodeUpgradesTheFinishedCopy()
    {
        var result = Run(Stage, """
            const XA = mk('XA', ['foo', 'bar']);
            customElements.define('x-a', XA);
            const src = document.createElement('x-a'); src.setAttribute('data-l', 'cl'); src.setAttribute('foo', 'f');
            const inner = document.createElement('x-a'); inner.setAttribute('data-l', 'cl2');
            src.appendChild(inner);
            log('-- clone');
            const c = src.cloneNode(true);
            log('clone inst=' + (c instanceof XA) + ' child inst=' + (c.firstChild instanceof XA));
            log('-- clone shallow');
            src.cloneNode(false);
            """);
        Assert.Equal(Lines("""
            ctor XA x-a conn=false kids=0 attrs=0
            attr x-a#cl foo null -> f ns=null
            ctor XA x-a conn=false kids=0 attrs=0
            -- clone
            ctor XA x-a#cl conn=false kids=1 attrs=2
            attr x-a#cl foo null -> f ns=null
            ctor XA x-a#cl2 conn=false kids=0 attrs=1
            clone inst=true child inst=true
            -- clone shallow
            ctor XA x-a#cl conn=false kids=0 attrs=2
            attr x-a#cl foo null -> f ns=null
            """), result);
    }

    [Fact]
    public void AttributeChangedCallbackFollowsEveryAttributePath()
    {
        var result = Run(Stage, """
            const XO = mk('XO', ['foo', 'id', 'class', 'data-x', 'title', 'hidden', 'p', 'lang']);
            customElements.define('x-o', XO);
            const e = document.createElement('x-o');
            e.setAttribute('foo', '1');
            e.setAttribute('foo', '1');
            e.setAttribute('FOO', '2');
            e.toggleAttribute('hidden');
            e.toggleAttribute('hidden');
            e.toggleAttribute('hidden', false);
            e.setAttributeNS('urn:x', 'q:p', 'nsv');
            e.setAttributeNS(null, 'p', 'plain');
            e.removeAttributeNS('urn:x', 'p');
            e.id = 'o1';
            e.className = 'c1';
            e.classList.add('c2');
            e.classList.remove('c1');
            e.dataset.x = 'dx';
            e.title = 'tt';
            e.hidden = true;
            e.lang = 'en';
            e.getAttributeNode('foo').value = '3';
            const nat = document.createAttribute('foo');
            nat.value = '4';
            e.setAttributeNode(nat);
            e.removeAttributeNode(nat);
            e.attributes.removeNamedItem('title');
            e.removeAttribute('nonexistent');
            """);
        Assert.Equal(Lines("""
            ctor XO x-o conn=false kids=0 attrs=0
            attr x-o foo null -> 1 ns=null
            attr x-o foo 1 -> 1 ns=null
            attr x-o foo 1 -> 2 ns=null
            attr x-o hidden null ->  ns=null
            attr x-o hidden  -> null ns=null
            attr x-o p null -> nsv ns=urn:x
            attr x-o p null -> plain ns=null
            attr x-o p nsv -> null ns=urn:x
            attr o1 id null -> o1 ns=null
            attr o1 class null -> c1 ns=null
            attr o1 class c1 -> c1 c2 ns=null
            attr o1 class c1 c2 -> c2 ns=null
            attr o1 data-x null -> dx ns=null
            attr o1 title null -> tt ns=null
            attr o1 hidden null ->  ns=null
            attr o1 lang null -> en ns=null
            attr o1 foo 2 -> 3 ns=null
            attr o1 foo 3 -> 4 ns=null
            attr o1 foo 4 -> null ns=null
            attr o1 title tt -> null ns=null
            """), result);
    }

    [Fact]
    public void ReactionsRunPerElementQueueBeforeTheMethodReturns()
    {
        var result = Run(Stage, """
            customElements.define('x-r', class extends HTMLElement {
              static get observedAttributes() { return ['v']; }
              connectedCallback() {
                log('R connected ' + lab(this) + ' next=' + lab(this.nextElementSibling));
                if (this.getAttribute('data-l') === 'r1') {
                  const other = document.querySelector('[data-l="r2"]');
                  other.setAttribute('v', '1');
                  log('R after set');
                }
              }
              attributeChangedCallback(n, o, v) { log('R attr ' + lab(this) + ' ' + n + ' ' + o + ' -> ' + v); }
            });
            const stage = document.getElementById('stage');
            const r1 = document.createElement('x-r'); r1.setAttribute('data-l', 'r1');
            const r2 = document.createElement('x-r'); r2.setAttribute('data-l', 'r2');
            stage.append(r1, r2);
            stage.textContent = '';
            log('-- innerHTML order');
            stage.innerHTML = '<x-r data-l="r1"></x-r><x-r data-l="r2" v="0"></x-r>';
            stage.textContent = '';
            log('-- nested insert');
            customElements.define('x-ins', class extends HTMLElement {
              connectedCallback() {
                log('INS connected ' + lab(this));
                const c = document.createElement('x-r'); c.setAttribute('data-l', 'insc');
                this.appendChild(c);
                log('INS appended child');
              }
            });
            stage.appendChild(document.createElement('x-ins'));
            """);
        Assert.Equal(Lines("""
            R connected x-r#r1 next=x-r#r2
            R connected x-r#r2 next=null
            R attr x-r#r2 v null -> 1
            R after set
            -- innerHTML order
            R connected x-r#r1 next=x-r#r2
            R after set
            R attr x-r#r2 v null -> 1
            R connected x-r#r2 next=null
            -- nested insert
            INS connected x-ins
            R connected x-r#insc next=null
            INS appended child
            """), result);
    }

    [Fact]
    public void ConstructorViolationsAreReportedAndLeaveFailedElements()
    {
        var result = Run(Stage, """
            customElements.define('x-attr', class extends HTMLElement { constructor() { super(); this.setAttribute('a', '1'); } });
            customElements.define('x-child', class extends HTMLElement { constructor() { super(); this.appendChild(document.createElement('span')); } });
            customElements.define('x-throw', class extends HTMLElement { constructor() { super(); throw new Error('boom'); } connectedCallback() { log('throw connected'); } });
            customElements.define('x-other', class extends HTMLElement { constructor() { super(); return document.createElement('div'); } });
            customElements.define('x-nosuper', class extends HTMLElement { constructor() { return document.createElement('span'); } });
            for (const n of ['x-attr', 'x-child', 'x-throw', 'x-other', 'x-nosuper']) {
              let el = null;
              const res = errs(() => (el = document.createElement(n)));
              log('create ' + n + ' ' + res + (el ? ' ctor=' + el.constructor.name + ' defined=' + el.matches(':defined') : ''));
            }
            log('-- via innerHTML');
            const stage = document.getElementById('stage');
            stage.innerHTML = '<x-attr data-l="v1"></x-attr><x-child data-l="v2"></x-child><x-throw data-l="v3"></x-throw><x-other data-l="v4"></x-other>';
            for (const el of stage.children) log(lab(el) + ' attrs=' + el.attributes.length + ' kids=' + el.childNodes.length + ' defined=' + el.matches(':defined'));
            stage.appendChild(stage.querySelector('[data-l="v3"]'));
            """);
        Assert.Equal(Lines("""
            onerror NotSupportedError: Failed to execute 'createElement' on 'Document': The result must not have attributes
            create x-attr ok:x-attr ctor=HTMLUnknownElement defined=false
            onerror NotSupportedError: Failed to execute 'createElement' on 'Document': The result must not have children
            create x-child ok:x-child ctor=HTMLUnknownElement defined=false
            onerror Error: boom
            create x-throw ok:x-throw ctor=HTMLUnknownElement defined=false
            onerror NotSupportedError: Failed to execute 'createElement' on 'Document': The result must have the same localName
            create x-other ok:x-other ctor=HTMLUnknownElement defined=false
            onerror NotSupportedError: Failed to execute 'createElement' on 'Document': The result must have the same localName
            create x-nosuper ok:x-nosuper ctor=HTMLUnknownElement defined=false
            -- via innerHTML
            onerror Error: boom
            onerror TypeError: custom element constructors must call super() first and must not return a different object
            x-attr#v1 attrs=2 kids=0 defined=true
            x-child#v2 attrs=1 kids=1 defined=true
            x-throw#v3 attrs=1 kids=0 defined=false
            x-other#v4 attrs=1 kids=0 defined=false
            """), result);
    }

    [Fact]
    public void DefineValidatesAsChromiumDoes()
    {
        var result = Run(Stage, """
            const XA = class extends HTMLElement {};
            customElements.define('x-a', XA);
            log('noctor ' + errs(() => customElements.define('x-q', {})));
            log('arrow ' + errs(() => customElements.define('x-q', () => {})));
            for (const n of ['xq', 'X-q', 'font-face', '1-q', 'x-Q', '-q', 'x-q!']) log('name ' + n + ' ' + errs(() => customElements.define(n, class extends HTMLElement {})));
            log('dup ' + errs(() => customElements.define('x-a', class extends HTMLElement {})));
            log('reuse ' + errs(() => customElements.define('x-a2', XA)));
            log('extends custom ' + errs(() => customElements.define('x-e1', class extends HTMLElement {}, { extends: 'x-a' })));
            log('extends unknown ' + errs(() => customElements.define('x-e2', class extends HTMLElement {}, { extends: 'foo' })));
            log('extends div ok ' + errs(() => customElements.define('x-e3', class extends HTMLDivElement {}, { extends: 'div' })));
            log('get ' + (customElements.get('x-a') === XA) + ' ' + customElements.get('x-none') + ' getName ' + customElements.getName(XA) + ' ' + customElements.getName(class {}));
            log('getName noctor ' + errs(() => customElements.getName({})));
            log('keys ' + Object.keys(customElements).length);
            """);
        Assert.Equal(Lines("""
            noctor TypeError: Failed to execute 'define' on 'CustomElementRegistry': parameter 2 is not of type 'Function'.
            arrow TypeError: Failed to execute 'define' on 'CustomElementRegistry': constructor argument is not a constructor
            name xq SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "xq" is not a valid custom element name
            name X-q SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "X-q" is not a valid custom element name
            name font-face SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "font-face" is not a valid custom element name
            name 1-q SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "1-q" is not a valid custom element name
            name x-Q SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "x-Q" is not a valid custom element name
            name -q SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "-q" is not a valid custom element name
            name x-q! SyntaxError: Failed to execute 'define' on 'CustomElementRegistry': "x-q!" is not a valid custom element name
            dup NotSupportedError: Failed to execute 'define' on 'CustomElementRegistry': the name "x-a" has already been used with this registry
            reuse NotSupportedError: Failed to execute 'define' on 'CustomElementRegistry': this constructor has already been used with this registry
            extends custom NotSupportedError: Failed to execute 'define' on 'CustomElementRegistry': "x-a" is a valid custom element name
            extends unknown NotSupportedError: Failed to execute 'define' on 'CustomElementRegistry': "foo" is an HTMLUnknownElement
            extends div ok ok:undefined
            get true undefined getName x-a null
            getName noctor TypeError: Failed to execute 'getName' on 'CustomElementRegistry': parameter 1 is not of type 'Function'.
            keys 0
            """), result);
    }

    [Fact]
    public void CustomizedBuiltInsUpgradeAndSerializeTheirIsValue()
    {
        var result = Run(
            """<html><body><button is="x-btn" id="btn1">B</button><div id="stage"></div></body></html>""",
            """
            const btn1 = document.getElementById('btn1');
            log('btn1 before defined=' + btn1.matches(':defined'));
            const XB = class extends HTMLButtonElement {
              constructor() { super(); log('XB ctor ' + lab(this) + ' ln=' + this.localName); }
              connectedCallback() { log('XB connected ' + lab(this)); }
            };
            customElements.define('x-btn', XB, { extends: 'button' });
            log('btn1 inst=' + (btn1 instanceof XB) + ' defined=' + btn1.matches(':defined'));
            const b2 = document.createElement('button', { is: 'x-btn' });
            log('b2 inst=' + (b2 instanceof XB) + ' html=' + b2.outerHTML + ' attr=' + b2.getAttribute('is'));
            const b3 = new XB();
            log('b3 inst=' + (b3 instanceof XB) + ' html=' + b3.outerHTML);
            const b4 = document.createElement('div', { is: 'x-btn' });
            log('b4 ctor=' + b4.constructor.name + ' html=' + b4.outerHTML);
            const stage = document.getElementById('stage');
            stage.innerHTML = '<button is="x-btn" data-l="b5"></button>';
            log('b5 inst=' + (stage.firstChild instanceof XB));
            const XD = class extends HTMLElement {};
            log('extends mismatch new ' + errs(() => { customElements.define('x-mis', XD, { extends: 'p' }); return new XD(); }));
            """);
        Assert.Equal(Lines("""
            btn1 before defined=false
            XB ctor btn1 ln=button
            XB connected btn1
            btn1 inst=true defined=true
            XB ctor button ln=button
            b2 inst=true html=<button is="x-btn"></button> attr=null
            XB ctor button ln=button
            b3 inst=true html=<button is="x-btn"></button>
            b4 ctor=HTMLDivElement html=<div is="x-btn"></div>
            XB ctor button#b5 ln=button
            XB connected button#b5
            b5 inst=true
            extends mismatch new TypeError: Failed to construct 'HTMLElement': Illegal constructor: localName does not match the HTML element interface
            """), result);
    }

    [Fact]
    public void FormAssociatedCallbacksFollowFormOwnerDisabledStateAndReset()
    {
        var result = Run(
            """<html><body><form id="f1"><fieldset id="fs1"></fieldset></form></body></html>""",
            """
            customElements.define('x-f', class extends HTMLElement {
              static formAssociated = true;
              constructor() { super(); this.i = this.attachInternals(); }
              formAssociatedCallback(f) { log('FA ' + lab(this) + ' form=' + (f ? f.id : null)); }
              formDisabledCallback(d) { log('FD ' + lab(this) + ' ' + d); }
              formResetCallback() { log('FR ' + lab(this)); }
              connectedCallback() { log('F connected ' + lab(this)); }
            });
            const e = document.createElement('x-f'); e.setAttribute('data-l', 'f');
            const fs = document.getElementById('fs1');
            fs.appendChild(e);
            fs.disabled = true;
            fs.disabled = false;
            document.getElementById('f1').reset();
            e.remove();
            """);
        Assert.Equal(Lines("""
            F connected x-f#f
            FA x-f#f form=f1
            FD x-f#f true
            FD x-f#f false
            FR x-f#f
            FA x-f#f form=null
            """), result);
    }

    [Fact]
    public void WhenDefinedSharesOnePromiseAndResolvesWithTheConstructor()
    {
        using var fixture = RuntimeFixture.Setup(Stage);
        var sync = Run(fixture.Runtime, """
            window.__wd = [];
            const p1 = customElements.whenDefined('x-w');
            log('same promise ' + (p1 === customElements.whenDefined('x-w')));
            p1.then((c) => window.__wd.push('resolved ' + c.name));
            customElements.whenDefined('nope').catch((e) => window.__wd.push('rejected ' + e.name));
            class XW extends HTMLElement {}
            customElements.define('x-w', XW);
            log('defined resolves anew ' + (customElements.whenDefined('x-w') !== p1));
            """);
        Assert.Equal("same promise true\ndefined resolves anew true", sync);
        var later = fixture.Runtime.Evaluate("window.__wd.sort().join('|')")!.GetValue<string>();
        Assert.Equal("rejected SyntaxError|resolved XW", later);
    }

    [Fact]
    public void DefinedPseudoClassStylesUndefinedElements()
    {
        var result = Run(
            """
            <html><head><style>x-p:not(:defined) { display: none } x-p:defined { display: flex }</style></head>
            <body><x-p id="p1"></x-p><x-u id="u1"></x-u></body></html>
            """,
            """
            const p1 = document.getElementById('p1');
            log('before ' + getComputedStyle(p1).display + ' ' + document.querySelectorAll(':not(:defined)').length);
            customElements.define('x-p', class extends HTMLElement {});
            log('after ' + getComputedStyle(p1).display + ' ' + document.querySelectorAll(':not(:defined)').length
              + ' ' + document.querySelector(':not(:defined)').id);
            """);
        Assert.Equal("before none 2\nafter flex 1 u1", result);
    }
}
