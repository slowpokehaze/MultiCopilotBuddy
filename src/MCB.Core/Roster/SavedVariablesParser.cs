using System.Text;

namespace MCB.Core.Roster;

/// <summary>
/// Прагматичный парсер Lua-таблиц SavedVariables (WTF\...\SavedVariables\*.lua):
/// `Name = { ["key"] = value, ... }`. Поддержка: строки в двойных кавычках с
/// escape-последовательностями, [[длинные строки]], числа (в т.ч. экспоненты),
/// true/false/nil, вложенные таблицы, ключи в скобках и «голые» идентификаторы.
/// Синтаксический сахар Lua (`a.b.c`, функции, арифметика) в SavedVariables
/// не встречается — генерируется сериализацией клиента.
/// </summary>
public static class SavedVariablesParser
{
    /// <summary>Разобрать файл. Возвращает верхнеуровневую таблицу (пустую, если это не таблица).</summary>
    public static Dictionary<string, object> ParseFile(string path)
        => Parse(File.ReadAllText(path));

    public static Dictionary<string, object> Parse(string text)
    {
        var parser = new Parser(text);
        return parser.ParseDocument();
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public Dictionary<string, object> ParseDocument()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWs();
            // Формат: необязательные присваивания верхнего уровня; главный — таблица.
            while (!End)
            {
                var savepoint = _i;
                var name = TryReadBareIdentifier();
                if (name is null)
                {
                    break;
                }

                SkipWs();
                if (Peek == '=')
                {
                    _i++;
                    SkipWs();
                    result[name] = ParseValue();
                    SkipCommaOrSemicolon();
                    SkipWs();
                }
                else
                {
                    _i = savepoint; // не присваивание — дальше не парсим
                    break;
                }
            }

            return result;
        }

        private Dictionary<string, object> ParseTable()
        {
            var table = new Dictionary<string, object>(StringComparer.Ordinal);
            Expect('{');
            SkipWs();
            var arrayIndex = 1;
            while (!End && Peek != '}')
            {
                SkipWs();
                string key;
                if (Peek == '[')
                {
                    _i++;
                    SkipWs();
                    var keyToken = ParseValue();
                    key = KeyToString(keyToken);
                    SkipWs();
                    Expect(']');
                    SkipWs();
                    Expect('=');
                }
                else
                {
                    var savepoint = _i;
                    var bare = TryReadBareIdentifier();
                    SkipWs();
                    if (bare is not null && Peek == '=')
                    {
                        key = bare;
                        _i++;
                    }
                    else
                    {
                        _i = savepoint;
                        key = (arrayIndex++).ToString();
                    }
                }

                SkipWs();
                table[key] = ParseValue();
                SkipCommaOrSemicolon();
                SkipWs();
            }

            Expect('}');
            return table;
        }

        private object ParseValue()
        {
            SkipWs();
            if (End)
            {
                throw new FormatException($"Unexpected end of input at {_i}.");
            }

            return Peek switch
            {
                '{' => ParseTable(),
                '"' => ParseQuotedString('"'),
                '\'' => ParseQuotedString('\''),
                _ => ParseAtom(),
            };
        }

        private object ParseAtom()
        {
            var start = _i;
            while (!End && Peek != ',' && Peek != ';' && Peek != '}' && Peek != ']' && Peek != '\n' && Peek != '\r')
            {
                _i++;
            }

            var token = _s.Substring(start, _i - start).Trim();
            return token switch
            {
                "true" => true,
                "false" => false,
                "nil" => null!,
                _ => ParseNumber(token),
            };
        }

        private static object ParseNumber(string token)
        {
            // Lua-числа: 12, -3.5, 1e3, 0x1A.
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var hex))
            {
                return (double)hex;
            }

            if (double.TryParse(token, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            // Неизвестный атом — оставляем строкой (лучше, чем падать).
            return token;
        }

        private string ParseQuotedString(char quote)
        {
            Expect(quote);
            var sb = new StringBuilder();
            while (!End)
            {
                var c = _s[_i++];
                if (c == '\\' && !End)
                {
                    var next = _s[_i++];
                    sb.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        'a' => '\a',
                        'b' => '\b',
                        'f' => '\f',
                        'v' => '\v',
                        '"' => '"',
                        '\'' => '\'',
                        '\\' => '\\',
                        _ => next,
                    });
                    continue;
                }

                if (c == quote)
                {
                    return sb.ToString();
                }

                sb.Append(c);
            }

            throw new FormatException($"Unterminated string ending at {_i}.");
        }

        private void SkipCommaOrSemicolon()
        {
            SkipWs();
            if (!End && (Peek == ',' || Peek == ';'))
            {
                _i++;
            }
        }

        private string? TryReadBareIdentifier()
        {
            SkipWs();
            var start = _i;
            while (!End && (char.IsLetterOrDigit(Peek) || Peek == '_'))
            {
                _i++;
            }

            return _i > start ? _s.Substring(start, _i - start) : null;
        }

        private static string KeyToString(object? key)
            => key switch
            {
                double d => d == Math.Floor(d) && !double.IsInfinity(d)
                    ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : d.ToString(System.Globalization.CultureInfo.InvariantCulture)!,
                string s => s,
                bool b => b ? "true" : "false",
                null => "nil",
                _ => key.ToString() ?? "nil",
            };

        private void SkipWs()
        {
            while (!End)
            {
                var c = _s[_i];
                if (c == '-' && _i + 1 < _s.Length && _s[_i + 1] == '-')
                {
                    // Lua-комментарий до конца строки (--[[ ... ]] не встречается в SavedVariables).
                    while (!End && _s[_i] != '\n')
                    {
                        _i++;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    _i++;
                    continue;
                }

                break;
            }
        }

        private void Expect(char c)
        {
            SkipWs();
            if (End || Peek != c)
            {
                throw new FormatException($"Expected '{c}' at {_i}, got {(End ? "EOF" : Peek.ToString())}.");
            }

            _i++;
        }

        private char Peek => _s[_i];

        private bool End => _i >= _s.Length;
    }
}
