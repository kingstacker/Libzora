# English (US) hyphenation data

The plain-text patterns and exceptions are copied without modification from
[tex-hyphen](https://github.com/hyphenation/tex-hyphen), revision
`5684c0f51c0b81133db2efbe60a408b4155a3ff5`:

- `hyph-utf8/tex/generic/hyph-utf8/patterns/txt/hyph-en-us.pat.txt`
- `hyph-utf8/tex/generic/hyph-utf8/patterns/txt/hyph-en-us.hyp.txt`

The copyright and permission notice from
[`hyph-en-us.tex`](https://github.com/hyphenation/tex-hyphen/blob/5684c0f51c0b81133db2efbe60a408b4155a3ff5/hyph-utf8/tex/generic/hyph-utf8/patterns/tex/hyph-en-us.tex)
is preserved in `Libzora.Infrastructure/ThirdParty/Notices/components/EnglishHyphenation/LICENSE.txt`
and embedded alongside the data. The data are parsed as text, never executed.

`EnglishHyphenator` applies the Liang pattern algorithm with the upstream
minimum of two characters before and three after a break. Original exception
spellings are honored, including words that must stay unbroken. The documented
upstream `democrat` issue is corrected by a local `dem-o-crat` exception in code;
the upstream files remain unchanged.
