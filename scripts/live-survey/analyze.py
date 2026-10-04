#!/usr/bin/env python3
"""Score the survey output: Chromium A vs B is the noise floor, Chromium A vs port is the finding."""
import json, re, os, sys, glob
from collections import Counter, defaultdict
from PIL import Image
import numpy as np

OUT = 'out'
BLOCK_RE = re.compile(r"(captcha|access denied|just a moment|verify you are (a )?human|robot check|attention required|"
                      r"unusual traffic|pardon our interruption|are you a robot|request blocked|403 forbidden|"
                      r"security check|bot detection|please enable js|enable javascript and cookies)", re.I)
WORD = re.compile(r"\w+", re.U)


def words(t):
    return set(w.lower() for w in WORD.findall(t or '') if len(w) > 1)


def jac(a, b):
    if not a and not b:
        return 1.0
    return len(a & b) / max(1, len(a | b))


def ratio(a, b):
    a, b = a or 0, b or 0
    if a == 0 and b == 0:
        return 1.0
    return min(a, b) / max(a, b)


def norm_title(t):
    return re.sub(r"\s+", " ", (t or '')).strip()


def boxes_agree(pa, pb):
    """Of A's boxes whose DOM path also exists in B, the share within 8px on x/y/w/h; plus path coverage."""
    ba = {p: (x, y, w, h) for p, x, y, w, h in pa.get('boxes', [])}
    bb = {p: (x, y, w, h) for p, x, y, w, h in pb.get('boxes', [])}
    if not ba:
        return None, None
    common = [p for p in ba if p in bb]
    cover = len(common) / len(ba)
    if not common:
        return 0.0, cover
    ok = sum(1 for p in common if all(abs(u - v) <= 8 for u, v in zip(ba[p], bb[p])))
    return ok / len(common), cover


_img_cache = {}


def img(domain, eng):
    k = (domain, eng)
    if k not in _img_cache:
        p = f'{OUT}/shots/{domain}.{eng}.png'
        if not os.path.exists(p):
            _img_cache[k] = None
        else:
            try:
                im = Image.open(p).convert('L').resize((160, 100), Image.BILINEAR)
                _img_cache[k] = np.asarray(im, dtype=np.float32)
            except Exception:
                _img_cache[k] = None
    return _img_cache[k]


def visdiff(domain, a, b):
    x, y = img(domain, a), img(domain, b)
    if x is None or y is None:
        return None
    return float(np.mean(np.abs(x - y)) / 255.0)


def blank(domain, eng):
    x = img(domain, eng)
    return x is not None and float(np.std(x)) < 3.0


def norm_err(m):
    m = re.sub(r"https?://\S+", "<url>", m)
    m = re.sub(r"\d+", "N", m)
    return m.strip()[:160]


def compare(domain, A, B, ea, eb):
    pa, pb = A.get('probe'), B.get('probe')
    if not pa or not pb:
        return None
    ba, cov = boxes_agree(pa, pb)
    return {
        'title_eq': norm_title(pa['title']) == norm_title(pb['title']),
        'elem': ratio(pa['elements'], pb['elements']),
        'text': jac(words(pa['domText']), words(pb['domText'])),
        'inner': jac(words(pa['innerText']), words(pb['innerText'])),
        'height': ratio(pa['scrollHeight'], pb['scrollHeight']),
        'visible': ratio(pa['visibleElements'], pb['visibleElements']),
        'links': ratio(pa['counts']['links'], pb['counts']['links']),
        'boxes': ba, 'box_cover': cov,
        'vis': visdiff(domain, ea, eb),
    }


def chrome_state(A):
    p = A.get('probe')
    if A.get('fatal') or not p:
        return 'failed', A.get('fatal') or A.get('probeError') or A.get('navError')
    # Match the body text only on small pages: a full page can mention "captcha" in passing.
    txt = (p.get('title') or '') + (' ' + (p.get('innerText') or '')[:3000] if p['elements'] < 300 else '')
    if BLOCK_RE.search(txt) or A.get('status') in (401, 403, 429, 503):
        return 'blocked', f"status={A.get('status')} title={p.get('title')!r}"
    if p['elements'] < 25 and 'example.com' not in p['url']:
        return 'blocked', f"tiny DOM ({p['elements']} elements) status={A.get('status')} title={p.get('title')!r}"
    return 'ok', None


def analyse(path):
    d = json.load(open(path))
    dom = d['domain']
    A, B, O = d['chromeA'], d['chromeB'], d['obscura']
    r = {'domain': dom, 'url': d['url']}
    cs, why = chrome_state(A)
    r['chrome_state'], r['chrome_why'] = cs, why
    r['chrome_status'] = A.get('status')
    pa, po = A.get('probe') or {}, O.get('probe') or {}
    r['a'] = {k: pa.get(k) for k in ('url', 'title', 'elements', 'scrollHeight', 'visibleElements', 'inViewport')}
    r['o'] = {k: po.get(k) for k in ('url', 'title', 'elements', 'scrollHeight', 'visibleElements', 'inViewport')}
    r['a']['loadMs'], r['o']['loadMs'] = A.get('loadMs'), O.get('loadMs')
    r['a']['requests'], r['o']['requests'] = A.get('requests'), O.get('requests')
    r['o']['status'] = O.get('status')
    r['o']['navError'] = O.get('navError')
    r['o']['fatal'] = O.get('fatal')
    r['o']['probeError'] = O.get('probeError')
    r['o']['shotError'] = O.get('shotError')
    r['o']['processExit'] = O.get('processExit')
    r['o']['stderrTail'] = (O.get('stderrTail') or '')[-600:]
    r['a']['navError'] = A.get('navError')
    a_err = set(norm_err(m) for m in A.get('pageErrors', []) + B.get('pageErrors', []))
    o_only = [m for m in O.get('pageErrors', []) if norm_err(m) not in a_err]
    r['o_page_errors'] = O.get('pageErrors', [])
    r['o_only_errors'] = o_only
    r['a_page_errors'] = A.get('pageErrors', [])
    r['o_console_errors'] = O.get('consoleErrors', [])[:15]
    r['o_failed'] = O.get('failedSamples', [])
    r['o_requests_failed'] = O.get('requestsFailed')
    r['a_requests_failed'] = A.get('requestsFailed')
    r['bodyStyle'] = {'a': pa.get('bodyStyle'), 'o': po.get('bodyStyle')}
    r['counts'] = {'a': pa.get('counts'), 'o': po.get('counts')}

    devs = []
    if cs != 'ok':
        r['verdict'] = 'inconclusive'
        r['devs'] = devs
        return r
    noise = compare(dom, A, B, 'chromeA', 'chromeB') or {}
    dev = compare(dom, A, O, 'chromeA', 'obscura')
    r['noise'], r['dev'] = noise, dev
    # Chromium disagreeing with itself this much (one run blocked, one not) leaves no baseline.
    if noise and (noise['elem'] < 0.5 or noise['text'] < 0.3):
        r['chrome_state'] = 'unstable'
        r['chrome_why'] = f"Chromium's two runs disagree (elements ratio {noise['elem']:.2f}, text overlap {noise['text']:.2f})"
        r['verdict'] = 'inconclusive'
        r['devs'] = devs
        return r
    if O.get('fatal') or O.get('processExit') or not po:
        why = O.get('fatal') or O.get('probeError') or (f"process exited {O.get('processExit')}")
        devs.append(('failed', f'port failed: {why}'))
        r['verdict'] = 'broken'
        r['devs'] = devs
        return r
    if O.get('navError'):
        devs.append(('nav', f"navigation error: {O['navError'][:160]}"))
    if po['elements'] < 0.2 * pa['elements'] or (blank(dom, 'obscura') and not blank(dom, 'chromeA')):
        devs.append(('empty', f"near-empty page: {po['elements']} vs {pa['elements']} elements" +
                     (', blank screenshot' if blank(dom, 'obscura') else '')))

    def worse(k, thr, margin):
        n = noise.get(k)
        v = dev.get(k)
        if v is None:
            return False
        floor = thr if n is None else min(thr, n - margin)
        return v < floor

    if not dev['title_eq'] and noise.get('title_eq', True):
        devs.append(('title', f"title differs: {r['o']['title']!r} vs {r['a']['title']!r}"))
    if worse('elem', 0.8, 0.1):
        devs.append(('dom', f"DOM size {po['elements']} vs {pa['elements']} (noise ratio {noise.get('elem', 1):.2f})"))
    if worse('text', 0.7, 0.15):
        devs.append(('text', f"DOM text Jaccard {dev['text']:.2f} (noise {noise.get('text', 1):.2f})"))
    if worse('height', 0.85, 0.1):
        devs.append(('height', f"page height {po['scrollHeight']} vs {pa['scrollHeight']}"))
    if dev['boxes'] is not None and worse('boxes', 0.7, 0.2):
        devs.append(('layout', f"box agreement {dev['boxes']:.2f} (noise {noise.get('boxes') or 0:.2f}), path coverage {dev['box_cover']:.2f}"))
    if dev['vis'] is not None and dev['vis'] > max(0.08, (noise.get('vis') or 0) + 0.05):
        devs.append(('visual', f"screenshot mean diff {dev['vis']:.3f} (noise {noise.get('vis') or 0:.3f})"))
    if o_only:
        devs.append(('js', f"{len(o_only)} uncaught error(s) only in port: {o_only[0][:140]}"))
    r['devs'] = devs
    hard = {'failed', 'nav', 'empty', 'dom', 'text'}
    kinds = {k for k, _ in devs}
    if not devs:
        r['verdict'] = 'agree'
    elif 'empty' in kinds:
        r['verdict'] = 'broken'
    elif kinds & hard or len(kinds) >= 3:
        r['verdict'] = 'diverge'
    else:
        r['verdict'] = 'minor'
    # Severity for ranking: weight hard failures, then size of the gaps.
    sev = 0.0
    w = {'failed': 10, 'empty': 8, 'nav': 3, 'dom': 3, 'text': 3, 'height': 1.5, 'layout': 1.5, 'visual': 1.5, 'js': 1, 'title': 1}
    for k, _ in devs:
        sev += w.get(k, 1)
    sev += (1 - dev['elem']) * 3 + (1 - dev['text']) * 3 + (1 - dev['height']) + (dev['vis'] or 0) * 5
    r['severity'] = round(sev, 2)
    return r


if __name__ == '__main__':
    if len(sys.argv) > 1:
        OUT = sys.argv[1]
    rows = [analyse(p) for p in sorted(glob.glob(f'{OUT}/json/*.json'))]
    json.dump(rows, open('analysis.json', 'w'), indent=1, default=str)
    c = Counter(r['verdict'] for r in rows)
    print(len(rows), dict(c))
    for r in sorted(rows, key=lambda r: -r.get('severity', 0)):
        print(f"{r['verdict']:12} {r.get('severity', 0):6} {r['domain']:20} " + ' | '.join(f'{k}: {v}' for k, v in r['devs'])[:300])
    print('\n-- inconclusive --')
    for r in rows:
        if r['verdict'] == 'inconclusive':
            print(r['domain'], r['chrome_state'], r['chrome_why'])
    kinds = Counter(k for r in rows for k, _ in r['devs'])
    print('\n-- deviation kinds --', dict(kinds))
    errs = Counter()
    where = defaultdict(set)
    for r in rows:
        for m in set(norm_err(x) for x in r['o_only_errors']):
            errs[m] += 1
            where[m].add(r['domain'])
    print('\n-- port-only uncaught errors by site count --')
    for m, n in errs.most_common(40):
        print(n, m, sorted(where[m])[:6])
