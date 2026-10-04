using PocketCalculator.Js.Runtime;
using Xunit;

namespace PocketCalculator.Js.Tests;

/// <summary>
/// Slot assignment (assignedNodes, assignedElements, assign, assignedSlot, slotchange) and
/// declarative shadow roots, against Chromium. FAST (msn.com) reads assignedNodes; Polymer
/// (youtube.com) builds inside declarative and attached roots. Every expected value was printed
/// by Chromium 141.0.7390.37 (headless, Playwright) for the same script.
/// </summary>
public sealed class ShadowSlotTests
{
    private static string Eval(PocketCalculatorJsRuntime runtime, string expression) =>
        runtime.Evaluate(expression)!.GetValue<string>();

    private const string Names = """
        globalThis.names = (l) => Array.from(l, (n) => n.nodeType === 1 ? (n.localName + (n.id ? '#' + n.id : '')) : '#' + n.nodeType + ':' + n.data).join(',');
        """;

    [Fact]
    public void NamedAssignmentAndFlattening()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", Names + """
            const host = document.createElement('div');
            host.innerHTML = '<span slot="a">A</span>text<b>B</b><!--c--><i slot="zz">Z</i>';
            document.body.appendChild(host);
            const sr = host.attachShadow({ mode: 'open' });
            sr.innerHTML = '<slot name="a" id="sa"><em>fa</em></slot><slot id="sd"><u>fallback</u></slot><slot name="none" id="sn"><s>fb</s>t2</slot><div><slot id="inner" name="a"></slot></div>';
            const $ = (id) => sr.getElementById(id);
            globalThis.r1 = [names($('sa').assignedNodes()), names($('sa').assignedNodes({ flatten: true })),
              names($('sd').assignedNodes()), names($('sd').assignedElements()), names($('sn').assignedNodes()),
              names($('sn').assignedNodes({ flatten: true })), names($('sn').assignedElements({ flatten: true })),
              names($('inner').assignedNodes()),
              host.children[0].assignedSlot === $('sa'), host.childNodes[1].assignedSlot === $('sd'),
              host.querySelector('i').assignedSlot, host.childNodes[3].assignedSlot, $('sa').assignedSlot].join('|');
            // A slot assigned to another host's slot flattens to what it was assigned.
            const H = document.createElement('div'); H.innerHTML = '<p id=p1>1</p><p slot=a id=p2>2</p>'; document.body.append(H);
            const HS = H.attachShadow({ mode: 'open' }); HS.innerHTML = '<div id=ih><slot name=a id=mid><b id=midfb>mf</b></slot>txt</div>';
            const IS = HS.getElementById('ih').attachShadow({ mode: 'open' }); IS.innerHTML = '<slot id=deep></slot>';
            const deep = IS.getElementById('deep'), mid = HS.getElementById('mid');
            const r2 = [names(deep.assignedNodes()), names(deep.assignedNodes({ flatten: true })),
              names(deep.assignedElements({ flatten: true })), mid.assignedSlot === deep, H.children[1].assignedSlot === mid];
            H.children[1].removeAttribute('slot');
            r2.push(names(deep.assignedNodes({ flatten: true })));
            globalThis.result2 = r2.join('|');
            // A closed root hides its slot from assignedSlot; a slot outside a shadow tree has no nodes.
            const C = document.createElement('div'); C.innerHTML = '<span>c</span>'; document.body.append(C);
            const CS = C.attachShadow({ mode: 'closed' }); CS.innerHTML = '<slot id=cs></slot>';
            const loose = document.createElement('slot'); loose.appendChild(document.createElement('q'));
            let bad;
            try { $('sd').assignedNodes(5); } catch (e) { bad = e.constructor.name + ': ' + e.message; }
            globalThis.r3 = [C.firstChild.assignedSlot, names(CS.getElementById('cs').assignedNodes()),
              names(loose.assignedNodes()), names(loose.assignedNodes({ flatten: true })), bad].join('|');
            """);
        Assert.Equal("span|span|#3:text,b|b||s,#3:t2|s||true|true|||", Eval(rt, "r1"));
        Assert.Equal("slot#mid,#3:txt|p#p2,#3:txt|p#p2|true|true|b#midfb,#3:txt", Eval(rt, "result2"));
        Assert.Equal(
            "|span|||TypeError: Failed to execute 'assignedNodes' on 'HTMLSlotElement': The provided value is not of type 'AssignedNodesOptions'.",
            Eval(rt, "r3"));
    }

    [Fact]
    public void ManualAssignment()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", Names + """
            const M = document.createElement('div'); M.innerHTML = '<a id=ma>1</a><b slot=x id=mb>2</b>t'; document.body.append(M);
            const MS = M.attachShadow({ mode: 'open', slotAssignment: 'manual' }); MS.innerHTML = '<slot id=m1 name=x></slot><slot id=m2></slot>';
            const m1 = MS.getElementById('m1'), m2 = MS.getElementById('m2');
            const r = [names(m1.assignedNodes()), names(m2.assignedNodes()), MS.slotAssignment];
            m2.assign(M.children[1], M.children[0], M.lastChild);
            r.push(names(m2.assignedNodes()), M.children[0].assignedSlot.id, M.lastChild.assignedSlot.id);
            m1.assign(M.children[0]);
            r.push(names(m1.assignedNodes()), names(m2.assignedNodes()));
            m1.assign(document.body, M.children[1]);
            r.push(names(m1.assignedNodes()));
            for (const v of [5, document.createComment('x')]) {
              try { m1.assign(v); r.push('no'); } catch (e) { r.push(e.constructor.name + ': ' + e.message); }
            }
            const moved = M.children[1];
            M.removeChild(moved);
            r.push(names(m1.assignedNodes()));
            M.appendChild(moved);
            r.push(names(m1.assignedNodes()));
            globalThis.result = r.join('|');
            """);
        Assert.Equal(
            "||manual|b#mb,a#ma,#3:t|m2|m2|a#ma|b#mb,#3:t|b#mb|"
            + "TypeError: Failed to execute 'assign' on 'HTMLSlotElement': The provided value is not of type '(Element or Text)'.|"
            + "TypeError: Failed to execute 'assign' on 'HTMLSlotElement': The provided value is not of type '(Element or Text)'.||b#mb",
            Eval(rt, "result"));
    }

    [Fact]
    public void ManualAssignmentIsWhatTheRenderLayerSlots()
    {
        using var fixture = RuntimeFixture.Setup("""
            <html><body><div id=m><span id=a1 style="display:inline-block;width:30px;height:10px">A</span><span id=a2 style="display:inline-block;width:40px;height:10px">B</span></div></body></html>
            """);
        Assert.Equal("0x0|40x10|null|ms", Eval(fixture.Runtime, """
            (() => {
              const ms = document.getElementById('m').attachShadow({ mode: 'open', slotAssignment: 'manual' });
              ms.innerHTML = '<slot id=ms></slot>';
              ms.getElementById('ms').assign(document.getElementById('a2'));
              const r = (id) => { const b = document.getElementById(id).getBoundingClientRect(); return b.width + 'x' + b.height; };
              const a1 = document.getElementById('a1'), a2 = document.getElementById('a2');
              return [r('a1'), r('a2'), String(a1.assignedSlot), a2.assignedSlot.id].join('|');
            })()
            """));
    }

    [Fact]
    public async Task SlotchangeFiresAtTheMicrotaskCheckpoint()
    {
        using var fixture = RuntimeFixture.Setup("<html><body></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.log = [];
            const host = document.createElement('div');
            host.innerHTML = '<span slot="a">A</span>text<b>B</b><i slot="zz">Z</i>';
            document.body.appendChild(host);
            const sr = host.attachShadow({ mode: 'open' });
            sr.innerHTML = '<slot name="a" id="sa"></slot><slot id="sd"></slot><slot name="none" id="sn"></slot>';
            for (const s of sr.querySelectorAll('slot')) s.addEventListener('slotchange', (e) => log.push(e.target.id + ':' + e.bubbles + ':' + e.composed + ':' + e.isTrusted));
            sr.addEventListener('slotchange', (e) => log.push('root:' + e.target.id));
            log.push('sync');
            queueMicrotask(() => log.push('microtask'));
            setTimeout(() => {
              log.push('-- append');
              host.appendChild(document.createElement('p'));
              host.appendChild(document.createElement('p'));
              setTimeout(() => {
                log.push('-- slot attr');
                host.children[0].slot = 'zz';
                host.querySelector('i').setAttribute('slot', 'a');
                setTimeout(() => {
                  log.push('-- rename');
                  sr.getElementById('sn').name = 'a';
                  setTimeout(() => {
                    log.push('-- remove');
                    sr.getElementById('sa').remove();
                    setTimeout(() => {
                      log.push('-- text');
                      host.childNodes[1].data = 'changed';
                      setTimeout(() => log.push('end'), 0);
                    }, 0);
                  }, 0);
                }, 0);
              }, 0);
            }, 0);
            """);
        await rt.RunEventLoopBoundedAsync(500);
        Assert.Equal(
            "sync,sa:true:false:true,root:sa,sd:true:false:true,root:sd,microtask,-- append,sd:true:false:true,root:sd,"
            + "-- slot attr,sa:true:false:true,root:sa,-- rename,-- remove,sa:true:false:true,sn:true:false:true,root:sn,-- text,end",
            Eval(rt, "log.join(',')"));
    }

    [Fact]
    public async Task EventListenerObjectsReceiveEvents()
    {
        // FAST registers its elements as listener objects (`addEventListener('slotchange', this)`);
        // the shim called every listener as a function and threw.
        using var fixture = RuntimeFixture.Setup("<html><body><div id=d></div></body></html>");
        var rt = fixture.Runtime;
        rt.ExecuteScript("setup", """
            globalThis.seen = [];
            const listener = { handleEvent(e) { seen.push((this === listener) + ':' + e.type); } };
            const d = document.getElementById('d');
            d.addEventListener('x', listener);
            document.addEventListener('x', listener);
            d.addEventListener('x', null);
            d.dispatchEvent(new Event('x', { bubbles: true }));
            const host = document.createElement('div');
            document.body.append(host);
            const sr = host.attachShadow({ mode: 'open' });
            sr.innerHTML = '<slot></slot>';
            sr.firstChild.addEventListener('slotchange', listener);
            host.append(document.createElement('p'));
            """);
        await rt.RunEventLoopBoundedAsync(100);
        Assert.Equal("true:x,true:x,true:slotchange", Eval(rt, "seen.join(',')"));
    }

    [Fact]
    public void DeclarativeShadowRootIsTheParentOfItsChildren()
    {
        // x-in is upgraded by define() inside the declarative root before script reads
        // host.shadowRoot, so its parentNode is the first wrapper of that root: it used to be a
        // #document.
        using var fixture = RuntimeFixture.Setup("""
            <html><body><div id=h><template shadowrootmode="open"><x-in id=xi></x-in><span id=s>in</span></template><b>light</b></div>
            <div id=h2><template shadowrootmode="open" shadowrootdelegatesfocus shadowrootclonable shadowrootserializable><i>c</i></template></div>
            </body></html>
            """);
        var rt = fixture.Runtime;
        rt.ExecuteScript("define", """
            globalThis.L = [];
            customElements.define('x-in', class extends HTMLElement {
              connectedCallback() {
                const p = this.parentNode;
                L.push([p && p.nodeType, p && p.nodeName, p instanceof ShadowRoot, p && p.host && p.host.id, this.getRootNode() === p, p && p.mode].join('|'));
              }
            });
            """);
        Assert.Equal("11|#document-fragment|true|h|true|open", Eval(rt, "L.join(',')"));
        Assert.Equal("true|false|false|false|named|0|true|true|true", Eval(rt, """
            (() => {
              const sr = document.getElementById('h').shadowRoot, s2 = document.getElementById('h2').shadowRoot;
              return [sr.getElementById('s').parentNode === sr, sr.clonable, sr.serializable, sr.delegatesFocus,
                sr.slotAssignment, document.querySelectorAll('template').length, s2.clonable, s2.serializable,
                s2.delegatesFocus].join('|');
            })()
            """));
    }
}
