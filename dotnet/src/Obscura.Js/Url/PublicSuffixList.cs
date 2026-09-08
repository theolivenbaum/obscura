namespace Obscura.Js.Url;

/// <summary>
/// The registrable-domain ("eTLD+1") lookup that <c>op_document_domain_candidate</c> needs.
/// </summary>
/// <remarks>
/// <para>
/// The Rust engine uses the <c>psl</c> crate, which embeds the full Public Suffix List. This
/// implements the same matching algorithm - longest matching rule, wildcards, exceptions,
/// and the implicit <c>*</c> rule for unknown TLDs - over a curated rule set rather than the
/// full list. The <c>*</c> default means every single-label TLD (<c>com</c>, <c>org</c>,
/// <c>io</c>, ...) is already correct without being listed; the table below only has to carry
/// the multi-label suffixes, where a plain "strip one label" answer would be wrong.
/// </para>
/// <para>
/// A suffix that is missing from the table degrades in the permissive direction for
/// <c>document.domain</c> (it would allow relaxing one label further than the real list does),
/// so the table deliberately includes the private suffixes the Rust comment calls out.
/// </para>
/// </remarks>
public static class PublicSuffixList
{
    private static readonly HashSet<string> Rules = new(StringComparer.Ordinal)
    {
        // United Kingdom
        "co.uk", "org.uk", "me.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk", "ac.uk",
        "gov.uk", "nhs.uk", "police.uk", "mod.uk", "nic.uk",
        // Australia / New Zealand
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "asn.au", "id.au", "csiro.au",
        "co.nz", "net.nz", "org.nz", "govt.nz", "ac.nz", "geek.nz", "school.nz", "kiwi.nz",
        // Japan / Korea / China / Taiwan / Hong Kong
        "co.jp", "or.jp", "ne.jp", "ac.jp", "ad.jp", "ed.jp", "go.jp", "gr.jp", "lg.jp",
        "co.kr", "or.kr", "ne.kr", "re.kr", "pe.kr", "go.kr", "ac.kr",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn", "mil.cn",
        "com.tw", "net.tw", "org.tw", "edu.tw", "gov.tw", "idv.tw",
        "com.hk", "net.hk", "org.hk", "edu.hk", "gov.hk", "idv.hk",
        // India / South-East Asia
        "co.in", "net.in", "org.in", "gen.in", "firm.in", "ind.in", "ac.in", "edu.in",
        "gov.in", "mil.in", "nic.in", "res.in",
        "com.sg", "net.sg", "org.sg", "edu.sg", "gov.sg", "per.sg",
        "com.my", "net.my", "org.my", "edu.my", "gov.my", "mil.my", "name.my",
        "com.ph", "net.ph", "org.ph", "edu.ph", "gov.ph", "mil.ph",
        "com.vn", "net.vn", "org.vn", "edu.vn", "gov.vn", "ac.vn", "biz.vn", "info.vn",
        "co.id", "or.id", "web.id", "ac.id", "go.id", "sch.id", "my.id", "biz.id", "net.id",
        "co.th", "in.th", "ac.th", "go.th", "or.th", "net.th", "mi.th",
        // Middle East / Africa
        "co.il", "org.il", "net.il", "ac.il", "gov.il", "k12.il", "muni.il", "idf.il",
        "com.tr", "net.tr", "org.tr", "edu.tr", "gov.tr", "info.tr", "biz.tr",
        "com.sa", "net.sa", "org.sa", "edu.sa", "gov.sa", "med.sa", "sch.sa",
        "com.eg", "net.eg", "org.eg", "edu.eg", "gov.eg", "sci.eg",
        "com.ng", "net.ng", "org.ng", "edu.ng", "gov.ng",
        "co.za", "org.za", "net.za", "gov.za", "ac.za", "web.za", "edu.za",
        "com.pk", "net.pk", "org.pk", "edu.pk", "gov.pk",
        "com.kw", "com.qa", "com.om", "com.bh", "com.lb", "com.jo", "com.cy",
        // Europe
        "co.at", "or.at", "ac.at", "gv.at", "priv.at",
        "com.es", "org.es", "nom.es", "gob.es", "edu.es",
        "com.pt", "org.pt", "edu.pt", "gov.pt", "int.pt", "net.pt", "nome.pt", "publ.pt",
        "com.pl", "net.pl", "org.pl", "edu.pl", "gov.pl", "info.pl", "waw.pl", "biz.pl",
        "com.ua", "net.ua", "org.ua", "edu.ua", "gov.ua", "in.ua", "kiev.ua",
        "com.ru", "net.ru", "org.ru", "edu.ru", "gov.ru", "int.ru", "msk.ru", "spb.ru",
        "com.gr", "edu.gr", "net.gr", "org.gr", "gov.gr",
        "co.hu", "info.hu", "org.hu", "priv.hu", "sport.hu", "tm.hu",
        "com.hr", "from.hr", "iz.hr", "name.hr",
        "com.ro", "org.ro", "tm.ro", "nt.ro", "nom.ro", "info.ro", "rec.ro", "arts.ro",
        "com.de", "com.se", "org.se", "pp.se", "tm.se",
        "co.no", "priv.no", "co.dk",
        // Americas
        "com.br", "net.br", "org.br", "gov.br", "edu.br", "art.br", "blog.br", "eco.br",
        "com.ar", "net.ar", "org.ar", "edu.ar", "gob.ar", "int.ar", "mil.ar", "tur.ar",
        "com.mx", "net.mx", "org.mx", "edu.mx", "gob.mx",
        "com.co", "net.co", "org.co", "edu.co", "gov.co", "mil.co", "nom.co",
        "com.pe", "net.pe", "org.pe", "edu.pe", "gob.pe", "nom.pe", "mil.pe",
        "com.ve", "net.ve", "org.ve", "edu.ve", "gob.ve", "web.ve", "info.ve",
        "com.ec", "net.ec", "org.ec", "edu.ec", "gob.ec", "med.ec", "fin.ec",
        "com.uy", "net.uy", "org.uy", "edu.uy", "gub.uy", "mil.uy",
        "com.do", "net.do", "org.do", "edu.do", "gob.do", "gov.do",
        "com.gt", "net.gt", "org.gt", "edu.gt", "gob.gt", "ind.gt", "mil.gt",
        "com.bo", "net.bo", "org.bo", "edu.bo", "gob.bo", "gov.bo",
        "com.py", "net.py", "org.py", "edu.py", "gov.py",
        "com.cu", "com.ni", "com.pa", "com.sv", "com.hn", "com.pr",
        // United States
        "k12.ak.us", "k12.ca.us", "k12.ny.us", "ci.us", "cc.us", "lib.us", "state.us",
        // Common private suffixes: sites here are separate origins, and the Rust comment
        // calls out github.io specifically as a case a plain suffix strip would get wrong.
        "github.io", "githubusercontent.com", "gitlab.io", "netlify.app", "netlify.com",
        "vercel.app", "herokuapp.com", "herokussl.com", "appspot.com", "firebaseapp.com",
        "web.app", "pages.dev", "workers.dev", "blogspot.com", "wordpress.com",
        "azurewebsites.net", "cloudapp.net", "cloudfront.net", "s3.amazonaws.com",
        "elasticbeanstalk.com", "glitch.me", "now.sh", "surge.sh", "neocities.org",
        "readthedocs.io", "translate.goog", "pythonanywhere.com", "eu.org", "co.nl",
    };

    private static readonly HashSet<string> Wildcards = new(StringComparer.Ordinal)
    {
        "sch.uk", "ck", "er", "jm", "kh", "mm", "pg", "compute.amazonaws.com",
        "compute-1.amazonaws.com", "us-east-1.amazonaws.com",
    };

    private static readonly HashSet<string> Exceptions = new(StringComparer.Ordinal)
    {
        "www.ck",
    };

    /// <summary>
    /// The registrable domain of <paramref name="host"/> (its public suffix plus one label),
    /// or null when the host <em>is</em> a public suffix or has no registrable part.
    /// </summary>
    public static string? RegistrableDomain(string host)
    {
        if (host.Length == 0)
        {
            return null;
        }

        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return null;
        }

        // Exception rules win outright: the public suffix is the rule minus its first label.
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels, i, labels.Length - i);
            if (Exceptions.Contains(candidate))
            {
                var suffixLabels = labels.Length - i - 1;
                return TakeLast(labels, suffixLabels + 1);
            }
        }

        // Longest matching normal or wildcard rule; "*" is the implicit default.
        var suffixLength = 1;
        for (var i = 0; i < labels.Length; i++)
        {
            var candidate = string.Join('.', labels, i, labels.Length - i);
            if (Rules.Contains(candidate))
            {
                suffixLength = Math.Max(suffixLength, labels.Length - i);
            }

            if (i > 0 && Wildcards.Contains(candidate))
            {
                suffixLength = Math.Max(suffixLength, labels.Length - i + 1);
            }
        }

        return suffixLength >= labels.Length ? null : TakeLast(labels, suffixLength + 1);
    }

    private static string TakeLast(string[] labels, int count) =>
        string.Join('.', labels, labels.Length - count, count);
}
