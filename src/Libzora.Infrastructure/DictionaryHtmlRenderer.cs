using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Libzora.Core;

namespace Libzora.Infrastructure;

public enum DictionaryEntryStyleMode
{
    BuiltIn,
    Original
}

public sealed record DictionaryHtmlPalette(string Paper, string Ink, string Muted, string Soft, string Border);

public static class DictionaryHtmlRenderer
{
    public static string Render(
        IReadOnlyList<DictionaryEntry> entries,
        DictionaryHtmlPalette? palette = null,
        DictionaryEntryStyleMode styleMode = DictionaryEntryStyleMode.Original)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var useBuiltIn = styleMode == DictionaryEntryStyleMode.BuiltIn;
        var body = new StringBuilder();
        foreach (var entry in entries)
        {
            var document = new HtmlDocument();
            if (entry.Html is not null)
            {
                document.LoadHtml(entry.Html);
            }
            else if (useBuiltIn)
            {
                document.LoadHtml(BuildPlainEntryHtml(entry));
            }
            else
            {
                document.LoadHtml($"<pre>{WebUtility.HtmlEncode(entry.Definition)}</pre>");
            }

            foreach (var node in document.DocumentNode.Descendants().ToArray())
            {
                if (node.Name is "script" or "link" or "iframe" or "object" or "embed" or "base" or "meta" or "form"
                    || (useBuiltIn && node.Name == "style"))
                { node.Remove(); continue; }
                foreach (var attribute in node.Attributes.ToArray())
                {
                    if (attribute.Name.Equals("onclick", StringComparison.OrdinalIgnoreCase))
                    {
                        var action = Regex.Match(attribute.Value, @"^\w+\.(toggle|showAtLink|toggleImg)\(this\);?$");
                        if (action.Success) node.SetAttributeValue("data-dictionary-toggle", action.Groups[1].Value);
                    }
                    if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                        || attribute.Name is "srcdoc" or "action" or "formaction"
                        || (useBuiltIn && IsPresentationalAttribute(attribute.Name)))
                    {
                        node.Attributes.Remove(attribute);
                    }
                }
                if (node.Name == "a")
                {
                    var href = WebUtility.HtmlDecode(node.GetAttributeValue("href", ""));
                    if (href.StartsWith("entry://#", StringComparison.OrdinalIgnoreCase))
                    {
                        node.SetAttributeValue("href", href[8..]);
                    }
                    else if (href.StartsWith("entry://", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetTerm = Uri.UnescapeDataString(href[8..].Split('#')[0]);
                        if (targetTerm.Length > 0)
                            node.SetAttributeValue("data-dictionary-term", targetTerm);
                    }
                    else if (!href.StartsWith('#'))
                    {
                        node.Attributes.Remove("href");
                    }
                    // Audio is stored separately in MDD, not in the MDX HTML.
                    if (href.StartsWith("sound:", StringComparison.OrdinalIgnoreCase)) node.Remove();
                }
                if (node.Name == "img" && !node.GetAttributeValue("src", "").StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    node.Remove();
            }
            body.Append("<section class='libzora-dictionary-entry'><header class='libzora-dictionary-name'>")
                .Append(WebUtility.HtmlEncode(entry.DictionaryName))
                .Append("</header><div class='libzora-dictionary-body'>")
                .Append(document.DocumentNode.InnerHtml)
                .Append("</div></section>");
        }
        var css = useBuiltIn
            ? string.Empty
            : string.Join("\n", entries.Select(entry => entry.Stylesheet).Where(css => css is not null).Distinct())
                .Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
        var effectiveCss = useBuiltIn || string.IsNullOrWhiteSpace(css) ? BuiltInEntryCss : css;
        palette ??= new("#ffffff", "#292d29", "#606a60", "#e8ece6", "#cdd4c9");
        static string Color(string value) => Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value : throw new ArgumentException("Invalid dictionary palette color.");
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; script-src 'nonce-{{nonce}}'; base-uri 'none'; form-action 'none'">
            <style>:root{--paper:{{Color(palette.Paper)}};--ink:{{Color(palette.Ink)}};--muted:{{Color(palette.Muted)}};--soft:{{Color(palette.Soft)}};--line:{{Color(palette.Border)}}}
            html,body{margin:0;padding:0;background:var(--paper);color:var(--ink);scrollbar-color:var(--line) var(--paper);-webkit-text-size-adjust:100%}
            body{padding:12px 14px 16px;font:14.5px/1.62 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Hiragino Sans GB","Microsoft YaHei",sans-serif;overflow-wrap:anywhere}
            section.libzora-dictionary-entry+section.libzora-dictionary-entry{border-top:1px solid var(--line);margin-top:16px;padding-top:14px}
            .libzora-dictionary-name{display:inline-flex;align-items:center;font-size:11px;font-weight:600;letter-spacing:0.03em;color:var(--muted);background:var(--soft);border:1px solid var(--line);border-radius:4px;padding:2px 7px;margin-bottom:10px}
            pre{white-space:pre-wrap;font:inherit;margin:0}img{max-width:100%}
            </style><style>{{effectiveCss}}</style></head><body>{{body}}
            <script nonce="{{nonce}}">
            document.addEventListener('click',event=>{
              const term=event.target.closest('[data-dictionary-term]');
              if(term){event.preventDefault();location.href='libzora-dictionary://lookup?'+encodeURIComponent(term.dataset.dictionaryTerm);return;}
              const anchor=event.target.closest('a[href^="#"]');
              if(anchor){
                const id=decodeURIComponent((anchor.getAttribute('href')||'').slice(1));
                if(id){
                  const target=document.getElementById(id)||document.getElementsByName(id)[0];
                  if(target){event.preventDefault();target.scrollIntoView({behavior:'smooth',block:'start'});return;}
                }
              }
              const button=event.target.closest('[data-dictionary-toggle]');if(!button)return;
              const action=button.dataset.dictionaryToggle;
              const target=action==='showAtLink'?button.nextElementSibling:action==='toggleImg'?button:button.parentElement.querySelector('.content');
              if(!target)return;
              if(action==='toggleImg'){target.style.maxHeight=target.style.maxHeight==='none'?'4em':'none';return;}
              target.style.display=getComputedStyle(target).display==='none'?'block':'none';
              const arrow=button.parentElement.querySelector('span.arrow');if(arrow)arrow.textContent=target.style.display==='none'?'►':'▼';
            });
            </script></body></html>
            """;
    }

    private static bool IsPresentationalAttribute(string name) =>
        name.Equals("style", StringComparison.OrdinalIgnoreCase)
        || name.Equals("color", StringComparison.OrdinalIgnoreCase)
        || name.Equals("bgcolor", StringComparison.OrdinalIgnoreCase)
        || name.Equals("face", StringComparison.OrdinalIgnoreCase)
        || name.Equals("size", StringComparison.OrdinalIgnoreCase)
        || name.Equals("align", StringComparison.OrdinalIgnoreCase);

    private static string BuildPlainEntryHtml(DictionaryEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append("<div class='libzora-plain-entry'>");
        if (!string.IsNullOrWhiteSpace(entry.Term))
            sb.Append("<div class='libzora-plain-headword'>").Append(WebUtility.HtmlEncode(entry.Term)).Append("</div>");
        var lines = entry.Definition
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sb.Append("<div class='libzora-plain-def'>");
        foreach (var line in lines)
            sb.Append("<p>").Append(WebUtility.HtmlEncode(line.Trim())).Append("</p>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    // Built-in theme-adaptive dictionary stylesheet. Applied when BuiltIn style mode
    // is active or when an MDX dictionary was imported without an external .css file.
    private const string BuiltInEntryCss = """
        a{color:var(--ink);text-decoration:underline;text-decoration-color:var(--line);text-underline-offset:2px}
        a:hover{text-decoration-color:var(--ink)}
        p{margin:0 0 6px}
        ul,ol{margin:4px 0 8px;padding-left:1.3em}
        li{margin:3px 0}
        hr{border:0;border-top:1px solid var(--line);margin:10px 0}
        .libzora-plain-headword{font-size:1.25em;font-weight:700;color:var(--ink);margin-bottom:8px;line-height:1.35}
        .libzora-plain-def p{margin:0 0 6px;color:var(--ink)}
        .chwd{display:block;font-size:12.5px;color:var(--muted);margin-bottom:8px;padding-bottom:6px;border-bottom:1px solid var(--line)}
        .entry{display:block;margin-bottom:14px}
        .entry+.entry{border-top:1px solid var(--line);padding-top:12px}
        .entryhead,.head{display:block;margin-bottom:8px;line-height:1.45}
        .entryhead>.hwd:has(+.hyphenation){display:none}
        .hwd,.hyphenation,.headword,.hw,.orth{font-size:1.28em;font-weight:700;color:var(--ink);margin-right:5px}
        .homnum{font-size:0.74em;vertical-align:super;color:var(--muted);margin-right:4px}
        .proncodes,.pron,.phon,.ipa,.pr{color:var(--muted);font-size:0.9em;margin-right:5px}
        .pos,.posg{display:inline-block;font-style:italic;font-weight:600;color:var(--muted);margin-right:6px}
        .freq,.level,.registerlab,.geo,.gram,.label{display:inline-block;font-size:0.74em;line-height:1.3;padding:1px 6px;border-radius:4px;background:var(--soft);border:1px solid var(--line);color:var(--muted);margin-right:4px;vertical-align:middle}
        .buttons{display:flex;flex-wrap:wrap;gap:6px;margin-top:8px}
        .popup-button{display:inline-block;font-size:12px;line-height:1.25;padding:3px 9px;border-radius:999px;border:1px solid var(--line);background:var(--soft);color:var(--ink);cursor:pointer}
        .popup-button:hover{border-color:var(--muted)}
        .at-link,.expandable>.content{display:none}
        .at-link{margin:6px 0 10px;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:var(--soft);font-size:0.92em}
        .popheader{display:block;font-size:11px;font-weight:700;letter-spacing:0.04em;text-transform:uppercase;color:var(--muted);margin-bottom:6px}
        .sense,.phrvbentry,.colloexa,.gramexa,.menuitem,.category{display:block;margin-top:9px}
        .sensenum,.sn{display:inline-block;min-width:1.35em;font-weight:700;color:var(--ink);margin-right:4px}
        .signpost,.lexunit{display:inline-block;font-weight:600;color:var(--ink);margin-right:6px}
        .def{color:var(--ink)}
        .defcn,.chn,.tran{display:block;color:var(--muted);margin-top:2px}
        .example,.exa,.ex,.x{display:block;margin:5px 0 5px 8px;padding-left:9px;border-left:2px solid var(--line);color:var(--muted);font-size:0.94em}
        .exacn{display:block;opacity:0.9;margin-top:1px}
        .expandable{display:block;margin-top:7px;cursor:pointer}
        .expandable>.heading,.expandable>.title{font-weight:600;color:var(--ink)}
        table{border-collapse:collapse;width:100%;margin:8px 0;font-size:0.92em}
        th,td{border:1px solid var(--line);padding:4px 7px;text-align:left}
        th{background:var(--soft);font-weight:600}
        """;
}
