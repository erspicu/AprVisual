using System;
using System.Globalization;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  A tiny tokenizer + recursive reader for the MetalNES `.js` module / netlist
    //  format. It is JSON5-ish:
    //    - a `var <ident> = <value>` prefix (and an optional trailing `;`)
    //    - `//` line comments and `/* ... */` block comments
    //    - single- or double-quoted strings ('1A', "#pclp0")
    //    - bare-identifier object keys (vcc: 1) as well as quoted keys ("/oe": 4)
    //    - trailing commas / no whitespace required around `:` and `=`
    //  Commas are treated as whitespace — the structure (`{ } [ ] : =`) is
    //  unambiguous without them, which keeps the reader simple.
    //
    //  Mirrors ref/metalnes-main/source/metalnes/wire_defs.cpp (JsonTokenizer /
    //  JsonParser::ValueReader/ObjectReader/ArrayReader).
    // ───────────────────────────────────────────────────────────────────────────

    internal sealed class JsLexer
    {
        public enum Kind { LBrace, RBrace, LBracket, RBracket, Colon, Equals, String, Number, Ident, End }

        public readonly struct Token
        {
            public readonly Kind Kind;
            public readonly string Text;
            public readonly int Line;
            public Token(Kind kind, string text, int line) { Kind = kind; Text = text; Line = line; }
            public override string ToString() => $"{Kind}('{Text}')@{Line}";
        }

        private readonly string _s;
        private int _i;
        private int _line = 1;
        public string Path { get; }

        private bool _hasPeek;
        private Token _peek;

        public JsLexer(string source, string path) { _s = source; Path = path; }

        public Token Peek() { if (!_hasPeek) { _peek = Lex(); _hasPeek = true; } return _peek; }
        public Token Next() { if (_hasPeek) { _hasPeek = false; return _peek; } return Lex(); }

        public Exception Error(string msg) => new FormatException($"{Path}({_line}): {msg}");

        private void SkipTrivia()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '\n') { _line++; _i++; continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == ',') { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length)
                {
                    if (_s[_i + 1] == '/')        // line comment
                    {
                        _i += 2;
                        while (_i < _s.Length && _s[_i] != '\n') _i++;
                        continue;
                    }
                    if (_s[_i + 1] == '*')        // block comment
                    {
                        _i += 2;
                        while (_i + 1 < _s.Length && !(_s[_i] == '*' && _s[_i + 1] == '/'))
                        { if (_s[_i] == '\n') _line++; _i++; }
                        _i = Math.Min(_i + 2, _s.Length);
                        continue;
                    }
                }
                break;
            }
        }

        private Token Lex()
        {
            SkipTrivia();
            if (_i >= _s.Length) return new Token(Kind.End, "", _line);

            char c = _s[_i];
            switch (c)
            {
                case '{': _i++; return new Token(Kind.LBrace, "{", _line);
                case '}': _i++; return new Token(Kind.RBrace, "}", _line);
                case '[': _i++; return new Token(Kind.LBracket, "[", _line);
                case ']': _i++; return new Token(Kind.RBracket, "]", _line);
                case ':': _i++; return new Token(Kind.Colon, ":", _line);
                case '=': _i++; return new Token(Kind.Equals, "=", _line);
                case '\'': case '"': return LexString(c);
            }
            if (c == '-' || (c >= '0' && c <= '9')) return LexNumber();
            if (c == '_' || char.IsLetter(c) || c == '$') return LexIdent();
            throw Error($"unexpected character '{c}' (U+{(int)c:X4})");
        }

        private Token LexString(char quote)
        {
            int line = _line;
            _i++; // opening quote
            int start = _i;
            while (_i < _s.Length && _s[_i] != quote)
            {
                if (_s[_i] == '\n') _line++;     // unusual but be safe
                _i++;
            }
            if (_i >= _s.Length) throw Error("unterminated string literal");
            string text = _s.Substring(start, _i - start);
            _i++; // closing quote
            return new Token(Kind.String, text, line);
        }

        private Token LexNumber()
        {
            int line = _line;
            int start = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;   // '.' tolerated, never present
            return new Token(Kind.Number, _s.Substring(start, _i - start), line);
        }

        private Token LexIdent()
        {
            int line = _line;
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '$')) _i++;
            return new Token(Kind.Ident, _s.Substring(start, _i - start), line);
        }
    }

    internal sealed class JsReader
    {
        private readonly JsLexer _lex;
        public JsReader(JsLexer lex) { _lex = lex; }
        public JsReader(string source, string path) : this(new JsLexer(source, path)) { }

        public string Path => _lex.Path;
        public JsLexer.Kind PeekKind() => _lex.Peek().Kind;
        public string PeekText() => _lex.Peek().Text;

        /// <summary>Consume `var &lt;ident&gt; =` at the start of a file.</summary>
        public void ExpectVarHeader(out string varName)
        {
            var v = _lex.Next();
            if (v.Kind != JsLexer.Kind.Ident || v.Text != "var") throw _lex.Error($"expected 'var', got {v}");
            var n = _lex.Next();
            if (n.Kind != JsLexer.Kind.Ident && n.Kind != JsLexer.Kind.String) throw _lex.Error($"expected identifier after 'var', got {n}");
            varName = n.Text;
            var eq = _lex.Next();
            if (eq.Kind != JsLexer.Kind.Equals) throw _lex.Error($"expected '=', got {eq}");
        }

        /// <summary>Read `{ key: value ... }`, invoking <paramref name="onKey"/> after each `key:`.</summary>
        public void ReadObject(Action<string, JsReader> onKey)
        {
            Expect(JsLexer.Kind.LBrace);
            while (true)
            {
                var t = _lex.Peek();
                if (t.Kind == JsLexer.Kind.RBrace) { _lex.Next(); return; }
                if (t.Kind != JsLexer.Kind.Ident && t.Kind != JsLexer.Kind.String)
                    throw _lex.Error($"expected object key or '}}', got {t}");
                _lex.Next();
                Expect(JsLexer.Kind.Colon);
                onKey(t.Text, this);
            }
        }

        /// <summary>Read `[ value ... ]`, invoking <paramref name="onElement"/> for each element.</summary>
        public void ReadArray(Action<JsReader> onElement)
        {
            Expect(JsLexer.Kind.LBracket);
            while (true)
            {
                if (_lex.Peek().Kind == JsLexer.Kind.RBracket) { _lex.Next(); return; }
                onElement(this);
            }
        }

        // ── positional reads inside a fixed-shape array (transdef / segdef / pindef / ...) ──
        public void BeginArray() => Expect(JsLexer.Kind.LBracket);
        public bool AtArrayEnd() => _lex.Peek().Kind == JsLexer.Kind.RBracket;
        public void EndArray()
        {
            while (_lex.Peek().Kind != JsLexer.Kind.RBracket)
            {
                if (_lex.Peek().Kind == JsLexer.Kind.End) throw _lex.Error("unterminated array");
                SkipValue();
            }
            _lex.Next();
        }

        public string ReadString()
        {
            var t = _lex.Next();
            if (t.Kind is JsLexer.Kind.String or JsLexer.Kind.Ident) return t.Text;   // tolerate bare ident
            throw _lex.Error($"expected string, got {t}");
        }

        public int ReadInt()
        {
            var t = _lex.Next();
            if (t.Kind != JsLexer.Kind.Number) throw _lex.Error($"expected number, got {t}");
            // tolerate a stray trailing '.0' etc.
            int dot = t.Text.IndexOf('.');
            string digits = dot < 0 ? t.Text : t.Text.Substring(0, dot);
            if (!int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                throw _lex.Error($"bad integer '{t.Text}'");
            return v;
        }

        public bool ReadBool()
        {
            var t = _lex.Next();
            if (t.Kind == JsLexer.Kind.Ident && (t.Text == "true" || t.Text == "false")) return t.Text == "true";
            throw _lex.Error($"expected boolean, got {t}");
        }

        /// <summary>If the next token is `true`/`false`, consume it and return true.</summary>
        public bool TryReadBool(out bool value)
        {
            var t = _lex.Peek();
            if (t.Kind == JsLexer.Kind.Ident && (t.Text == "true" || t.Text == "false"))
            { _lex.Next(); value = t.Text == "true"; return true; }
            value = false;
            return false;
        }

        /// <summary>Recursively consume one value of any shape (used for unknown keys / skipped polygon data).</summary>
        public void SkipValue()
        {
            var t = _lex.Peek();
            switch (t.Kind)
            {
                case JsLexer.Kind.LBrace:   ReadObject(static (_, r) => r.SkipValue()); break;
                case JsLexer.Kind.LBracket: ReadArray(static r => r.SkipValue()); break;
                case JsLexer.Kind.String:
                case JsLexer.Kind.Number:
                case JsLexer.Kind.Ident:    _lex.Next(); break;
                default: throw _lex.Error($"unexpected token {t} (cannot skip)");
            }
        }

        private void Expect(JsLexer.Kind kind)
        {
            var t = _lex.Next();
            if (t.Kind != kind) throw _lex.Error($"expected {kind}, got {t}");
        }
    }
}
