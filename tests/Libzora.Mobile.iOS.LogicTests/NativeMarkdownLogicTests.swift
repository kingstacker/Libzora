import Foundation

@main
struct NativeMarkdownLogicTests {
    private static var assertions = 0

    private static func check(_ condition: @autoclosure () -> Bool, _ message: String) {
        assertions += 1
        guard condition() else { fatalError("Regression failed: \(message)") }
    }

    private static func testBlockParsingAndDisplayMath() {
        let source = """
        # 核心结论

        > 引用说明：这里有一条公式：
        >
        > $$
        > E = mc^2
        > $$

        - 第一点
          ```swift
          let x = "$not_math$"
          ```
        - [x] 已完成任务

        | 列一 | 列二 |
        | :--- | ---: |
        | 左对齐 | 右对齐 |
        """
        let blocks = NativeMarkdownParser.parse(source)
        check(blocks.count == 4, "Expected heading, quote, list, table; got \(blocks.count)")

        if case let .heading(level, text) = blocks[0].kind {
            check(level == 1 && text == "核心结论", "Heading parsed cleanly")
        } else {
            fatalError("Expected heading at index 0")
        }

        if case let .quote(inner) = blocks[1].kind {
            check(inner.count == 2, "Quote contains paragraph and display math")
            if case let .math(latex) = inner[1].kind {
                check(latex == "E = mc^2", "Display math inside quote preserved")
            } else {
                fatalError("Expected display math inside quote")
            }
        } else {
            fatalError("Expected quote at index 1")
        }

        if case let .list(items) = blocks[2].kind {
            check(items.count == 2, "List has two items")
            check(items[0].blocks.count == 2, "First list item holds paragraph and nested code block")
            if case let .code(lang, code) = items[0].blocks[1].kind {
                check(lang == "swift" && code.contains("$not_math$"), "Nested code block inside list preserved")
            } else {
                fatalError("Expected nested code block inside list item")
            }
            check(items[1].checked == true, "Task checkbox parsed")
        } else {
            fatalError("Expected list at index 2")
        }

        if case let .table(table) = blocks[3].kind {
            check(table.headers == ["列一", "列二"], "Table headers parsed")
            check(table.alignments == [.leading, .trailing], "Table alignments parsed")
            check(table.rows == [["左对齐", "右对齐"]], "Table rows parsed")
        } else {
            fatalError("Expected table at index 3")
        }
    }

    private static func testCjkEmphasisAndSnakeCase() {
        let tokens = NativeMarkdownInlineParser.parse("**“概念”**的含义与 `snake_case_name` 及 foo_bar_baz。")
        check(tokens.contains { token in
            if case let .text(text, style, _) = token {
                return text == "“概念”" && style.contains(.bold)
            }
            return false
        }, "CJK punctuation adjacent bold delimiter works")

        check(tokens.contains { token in
            if case let .text(text, style, _) = token {
                return text == "snake_case_name" && style.contains(.code)
            }
            return false
        }, "Inline code preserved")

        check(tokens.contains { token in
            if case let .text(text, style, _) = token {
                return text.contains("foo_bar_baz") && !style.contains(.italic)
            }
            return false
        }, "Underscores inside ASCII identifiers do not trigger italics")
    }

    private static func testInlineMathAndCitationsAndSafeLinks() {
        let tokens = NativeMarkdownInlineParser.parse(
            "参考 [S1] 与公式 $x_1^2 + \\alpha$，价格 $5 和 $10 不是公式，链接 [安全](https://example.com/a) 与 [不安全](javascript:alert(1))。")

        check(tokens.contains { token in
            if case let .citation(id) = token { return id == "S1" }
            return false
        }, "Citation [S1] parsed as citation token")

        check(tokens.contains { token in
            if case let .math(latex, _) = token { return latex == "x_1^2 + \\alpha" }
            return false
        }, "Inline math with underscore protected from italics")

        check(!tokens.contains { token in
            if case let .math(latex, _) = token { return latex.contains("5 和 ") }
            return false
        }, "Currency amounts like $5 and $10 are not treated as inline math")

        check(tokens.contains { token in
            if case let .text(text, _, link) = token {
                return text == "安全" && link?.absoluteString == "https://example.com/a"
            }
            return false
        }, "HTTPS link preserved")

        check(tokens.contains { token in
            if case let .text(text, _, link) = token {
                return text.contains("不安全") && link == nil
            }
            return false
        }, "Non-HTTP(S) link stripped to plain text")
    }

    private static func testMathFormatter() {
        let runs = NativeMarkdownMathFormatter.format(#"\frac{\alpha + \beta}{2} \approx x_i^2"#)
        let plain = runs.map(\.text).joined()
        check(plain.contains("α + β"), "Greek symbols formatted")
        check(plain.contains("≈"), "Comparison symbols formatted")
        check(runs.contains { $0.script == .subscript && $0.text == "i" }, "Subscript formatted")
        check(runs.contains { $0.script == .superscript && $0.text == "2" }, "Superscript formatted")
    }

    private static func testStreamingUnclosedFence() {
        let blocks = NativeMarkdownParser.parse("说明：\n```python\nprint(1)")
        check(blocks.count == 2, "Unclosed streaming code fence renders as code block")
        if case let .code(lang, code) = blocks[1].kind {
            check(lang == "python" && code == "print(1)", "Unclosed code fence retains body")
        } else {
            fatalError("Expected code block for unclosed fence")
        }
    }

    private static func testStandaloneImageBlock() {
        let blocks = NativeMarkdownParser.parse("# 感悟\n\n![插图](data:image/png;base64,abc =120x80)\n\n尾段")
        check(blocks.count == 3, "Expected heading, standalone image block and paragraph; got \(blocks.count)")
        if case let .image(alt, url) = blocks[1].kind {
            check(alt == "插图" && url == "data:image/png;base64,abc", "Standalone image strips size hint and preserves data URI")
        } else {
            fatalError("Expected image block at index 1")
        }
    }

    static func main() {
        testBlockParsingAndDisplayMath()
        testCjkEmphasisAndSnakeCase()
        testInlineMathAndCitationsAndSafeLinks()
        testMathFormatter()
        testStreamingUnclosedFence()
        testStandaloneImageBlock()
        print("NativeMarkdownLogicTests: \(assertions) assertions passed")
    }
}
