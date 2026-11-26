using System.Globalization;
using System.Text;

namespace DiskScan.Core;

/// <summary>What one search term is compared with.</summary>
public enum SearchTermField
{
    /// <summary>Plain text: file name, contents, or both, depending on the search mode.</summary>
    Default,
    Name,
    Content,
    Path,
    Extension,
    Size,
    Modified,
    Kind
}

/// <summary>One leaf of a parsed search expression.</summary>
public sealed class SearchTerm
{
    internal SearchTerm(SearchTermField field, string text)
    {
        Field = field;
        Text = text;
    }

    public SearchTermField Field { get; }

    /// <summary>Text for text fields (NFC-normalized), or the original condition value.</summary>
    public string Text { get; }

    /// <summary>True when the text contains * or ? and is compared with the whole file name.</summary>
    public bool IsWildcard { get; internal init; }

    internal string[] Extensions { get; init; } = [];
    internal long MinimumSize { get; init; } = long.MinValue;
    internal long MaximumSize { get; init; } = long.MaxValue;
    internal DateTime? FromLocal { get; init; }
    internal DateTime? BeforeLocal { get; init; }
    internal bool WantsFolder { get; init; }

    internal bool IsTextField => Field is SearchTermField.Default or SearchTermField.Name or SearchTermField.Content or SearchTermField.Path;
}

/// <summary>
/// A parsed search expression. Words separated by spaces must all match (AND);
/// OR (or |) offers alternatives, NOT (or a leading -) excludes, parentheses group,
/// and "quoted text" is matched literally. AND binds more tightly than OR.
/// Conditions use key:value, for example ext:exe, size:>10mb, date:7d, path:docs, type:folder.
/// </summary>
public sealed class QueryExpression
{
    public const int MaximumQueryLength = 4096;
    public const int MaximumTerms = 64;
    public const int MaximumNestingDepth = 32;

    private readonly Node _root;
    private readonly SearchTerm[] _terms;

    private QueryExpression(string query, Node root, SearchTerm[] terms)
    {
        Query = query;
        _root = root;
        _terms = terms;
        Terms = Array.AsReadOnly(terms);
    }

    public string Query { get; }

    public IReadOnlyList<SearchTerm> Terms { get; }

    /// <summary>
    /// Parses a query. AND, OR and NOT are case-insensitive keywords.
    /// Quoted text is always literal, including operator words.
    /// </summary>
    /// <exception cref="ArgumentException">The query syntax or size is invalid.</exception>
    public static QueryExpression Parse(string query) => Parse(query, null);

    /// <summary>Parses a query and extra conditions; both must match.</summary>
    public static QueryExpression Parse(string? query, string? conditions)
    {
        query ??= string.Empty;
        conditions ??= string.Empty;
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        bool hasConditions = !string.IsNullOrWhiteSpace(conditions);
        if (!hasQuery && !hasConditions)
            throw Invalid("검색어를 입력하세요.");
        if (query.Length > MaximumQueryLength || conditions.Length > MaximumQueryLength)
            throw Invalid($"검색어는 {MaximumQueryLength:N0}자 이하여야 합니다.");

        var terms = new List<SearchTerm>();
        Node? root = hasQuery ? new Parser(query, terms).Parse() : null;
        if (hasConditions)
        {
            Node extra = new Parser(conditions, terms).Parse();
            root = root is null ? extra : new AndNode([root, extra]);
        }

        return new QueryExpression(hasConditions ? $"{query} {conditions}".Trim() : query, root!, terms.ToArray());
    }

    /// <summary>Three-valued evaluation: 1 = match, -1 = no match, 0 = not known yet.</summary>
    internal sbyte Evaluate(ReadOnlySpan<sbyte> states) => _root.Evaluate(states);

    internal static ArgumentException Invalid(string message) => new(message, "query");

    private abstract class Node
    {
        public abstract sbyte Evaluate(ReadOnlySpan<sbyte> states);
    }

    private sealed class TermNode(int index) : Node
    {
        public override sbyte Evaluate(ReadOnlySpan<sbyte> states) => states[index];
    }

    private sealed class NotNode(Node child) : Node
    {
        public override sbyte Evaluate(ReadOnlySpan<sbyte> states) => (sbyte)-child.Evaluate(states);
    }

    private sealed class AndNode(Node[] children) : Node
    {
        public override sbyte Evaluate(ReadOnlySpan<sbyte> states)
        {
            sbyte result = 1;
            foreach (Node child in children)
            {
                sbyte value = child.Evaluate(states);
                if (value < 0)
                    return -1;
                if (value == 0)
                    result = 0;
            }
            return result;
        }
    }

    private sealed class OrNode(Node[] children) : Node
    {
        public override sbyte Evaluate(ReadOnlySpan<sbyte> states)
        {
            sbyte result = -1;
            foreach (Node child in children)
            {
                sbyte value = child.Evaluate(states);
                if (value > 0)
                    return 1;
                if (value == 0)
                    result = 0;
            }
            return result;
        }
    }

    private sealed class Parser
    {
        private readonly string _query;
        private readonly List<SearchTerm> _terms;
        private int _position;
        private int _nestingDepth;
        private Token _current;

        public Parser(string query, List<SearchTerm> terms)
        {
            _query = query;
            _terms = terms;
            _current = ReadToken();
        }

        public Node Parse()
        {
            if (_current.Kind == TokenKind.End)
                throw Invalid("검색어를 입력하세요.");
            Node root = ParseOr();
            if (_current.Kind == TokenKind.RightParenthesis)
                throw Invalid("닫는 괄호 ')'에 대응하는 여는 괄호가 없습니다.");
            if (_current.Kind != TokenKind.End)
                throw Invalid("검색어를 이해하지 못했습니다.");
            return root;
        }

        private Node ParseOr()
        {
            var children = new List<Node> { ParseAnd() };
            while (_current.Kind == TokenKind.Or)
            {
                NextToken();
                EnsureOperand("OR");
                children.Add(ParseAnd());
            }
            return children.Count == 1 ? children[0] : new OrNode(children.ToArray());
        }

        private Node ParseAnd()
        {
            var children = new List<Node> { ParseUnary() };
            while (true)
            {
                if (_current.Kind == TokenKind.And)
                {
                    NextToken();
                    EnsureOperand("AND");
                    children.Add(ParseUnary());
                }
                else if (StartsOperand(_current.Kind))
                {
                    children.Add(ParseUnary());
                }
                else
                {
                    break;
                }
            }
            return children.Count == 1 ? children[0] : new AndNode(children.ToArray());
        }

        private Node ParseUnary()
        {
            if (_current.Kind == TokenKind.Not)
            {
                NextToken();
                EnsureOperand("NOT");
                return new NotNode(ParseUnary());
            }
            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            if (_current.Kind == TokenKind.Term)
            {
                int index = AddTerm(_current.Term!);
                NextToken();
                return new TermNode(index);
            }

            if (_current.Kind == TokenKind.LeftParenthesis)
            {
                if (_nestingDepth == MaximumNestingDepth)
                    throw Invalid($"괄호는 {MaximumNestingDepth:N0}단계까지만 중첩할 수 있습니다.");

                _nestingDepth++;
                NextToken();
                if (_current.Kind == TokenKind.RightParenthesis)
                    throw Invalid("빈 괄호에는 검색어를 넣어야 합니다.");
                if (_current.Kind == TokenKind.End)
                    throw Invalid("여는 괄호 '('를 닫아 주세요.");

                Node nested = ParseOr();
                if (_current.Kind != TokenKind.RightParenthesis)
                    throw Invalid("여는 괄호 '('를 닫아 주세요.");

                NextToken();
                _nestingDepth--;
                return nested;
            }

            if (_current.Kind == TokenKind.RightParenthesis)
                throw Invalid("닫는 괄호 ')' 앞에 검색어가 필요합니다.");
            if (_current.Kind is TokenKind.And or TokenKind.Or)
                throw Invalid($"{_current.Text} 연산자 앞에 검색어가 필요합니다.");

            throw Invalid("검색어가 필요합니다.");
        }

        private static bool StartsOperand(TokenKind kind) =>
            kind is TokenKind.Term or TokenKind.Not or TokenKind.LeftParenthesis;

        private int AddTerm(SearchTerm term)
        {
            if (_terms.Count == MaximumTerms)
                throw Invalid($"검색 항목은 {MaximumTerms:N0}개 이하여야 합니다.");

            _terms.Add(term);
            return _terms.Count - 1;
        }

        private void EnsureOperand(string operatorName)
        {
            if (!StartsOperand(_current.Kind))
                throw Invalid($"{operatorName} 연산자 뒤에 검색어가 필요합니다.");
        }

        private void NextToken() => _current = ReadToken();

        private Token ReadToken()
        {
            while (_position < _query.Length && char.IsWhiteSpace(_query[_position]))
                _position++;

            if (_position == _query.Length)
                return new Token(TokenKind.End);

            char value = _query[_position];
            switch (value)
            {
                case '(':
                    _position++;
                    return new Token(TokenKind.LeftParenthesis, "(");
                case ')':
                    _position++;
                    return new Token(TokenKind.RightParenthesis, ")");
                case '|':
                    _position++;
                    return new Token(TokenKind.Or, "OR");
                case '"':
                    return new Token(TokenKind.Term, Term: TextTerm(SearchTermField.Default, ReadQuoted(), quoted: true));
            }

            if (value is '-' or '!' && _position + 1 < _query.Length &&
                !char.IsWhiteSpace(_query[_position + 1]) && _query[_position + 1] is not (')' or '|' or '-' or '!'))
            {
                _position++;
                return new Token(TokenKind.Not, "NOT");
            }

            int start = _position;
            while (_position < _query.Length && !IsWordBoundary(_query[_position]))
            {
                if (_query[_position] == ':' && _position > start &&
                    TryField(_query[start.._position], out SearchTermField field))
                {
                    _position++;
                    string fieldValue = ReadFieldValue();
                    return new Token(TokenKind.Term, Term: CreateFieldTerm(field, fieldValue));
                }
                _position++;
            }

            string word = _query[start.._position];
            if (word.Equals("AND", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.And, "AND");
            if (word.Equals("OR", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Or, "OR");
            if (word.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                return new Token(TokenKind.Not, "NOT");

            return new Token(TokenKind.Term, Term: TextTerm(SearchTermField.Default, word, quoted: false));
        }

        private static bool IsWordBoundary(char value) =>
            char.IsWhiteSpace(value) || value is '(' or ')' or '"' or '|';

        private string ReadQuoted()
        {
            int quotePosition = _position++;
            int start = _position;
            while (_position < _query.Length && _query[_position] != '"')
                _position++;

            if (_position == _query.Length)
                throw Invalid($"{quotePosition + 1}번째 위치의 큰따옴표를 닫아 주세요.");

            string text = _query[start.._position];
            _position++;
            if (text.Length == 0)
                throw Invalid("큰따옴표 안에 검색어를 넣어야 합니다.");
            return text;
        }

        private string ReadFieldValue()
        {
            if (_position < _query.Length && _query[_position] == '"')
                return ReadQuoted();

            int start = _position;
            while (_position < _query.Length && !IsWordBoundary(_query[_position]))
                _position++;
            return _query[start.._position];
        }

        private static bool TryField(string key, out SearchTermField field)
        {
            field = key.ToLowerInvariant() switch
            {
                "name" or "이름" or "파일명" => SearchTermField.Name,
                "content" or "contents" or "text" or "내용" or "본문" => SearchTermField.Content,
                "path" or "경로" or "폴더" => SearchTermField.Path,
                "ext" or "확장자" => SearchTermField.Extension,
                "size" or "크기" or "용량" => SearchTermField.Size,
                "date" or "modified" or "dm" or "날짜" or "수정" or "수정일" => SearchTermField.Modified,
                "type" or "kind" or "종류" => SearchTermField.Kind,
                _ => SearchTermField.Default
            };
            return field != SearchTermField.Default;
        }

        private static SearchTerm TextTerm(SearchTermField field, string text, bool quoted)
        {
            if (text.Length == 0)
                throw Invalid("조건 값을 입력하세요.");
            string normalized = Normalize(text);
            bool wildcard = !quoted && field is SearchTermField.Default or SearchTermField.Name &&
                            normalized.AsSpan().IndexOfAny('*', '?') >= 0;
            return new SearchTerm(field, normalized) { IsWildcard = wildcard };
        }

        private static SearchTerm CreateFieldTerm(SearchTermField field, string value) => field switch
        {
            SearchTermField.Name or SearchTermField.Content or SearchTermField.Path =>
                TextTerm(field, value, quoted: field != SearchTermField.Name),
            SearchTermField.Extension => ExtensionTerm(value),
            SearchTermField.Size => SizeTerm(value),
            SearchTermField.Modified => DateTerm(value),
            SearchTermField.Kind => KindTerm(value),
            _ => throw Invalid("알 수 없는 조건입니다.")
        };

        private static SearchTerm ExtensionTerm(string value)
        {
            string[] extensions = value.Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.TrimStart('*').TrimStart('.'))
                .Where(item => item.Length > 0)
                .Select(item => "." + item.ToLowerInvariant())
                .Distinct()
                .ToArray();
            if (extensions.Length == 0)
                throw Invalid("확장자를 입력하세요. 예: ext:pdf 또는 ext:exe;dll");
            return new SearchTerm(SearchTermField.Extension, value) { Extensions = extensions };
        }

        private static SearchTerm KindTerm(string value) => value.ToLowerInvariant() switch
        {
            "file" or "files" or "f" or "파일" => new SearchTerm(SearchTermField.Kind, value) { WantsFolder = false },
            "folder" or "folders" or "dir" or "directory" or "d" or "폴더" => new SearchTerm(SearchTermField.Kind, value) { WantsFolder = true },
            _ => throw Invalid($"종류 조건은 type:file 또는 type:folder 입니다: {value}")
        };

        private static SearchTerm SizeTerm(string value)
        {
            string text = value.Trim();
            string error = $"크기 조건을 이해하지 못했습니다: {value} (예: size:>10mb, size:1mb..1gb)";
            int range = text.IndexOf("..", StringComparison.Ordinal);
            if (range < 0)
                range = text.IndexOf('~');
            if (range > 0)
            {
                int separatorLength = text[range] == '~' ? 1 : 2;
                if (!TryParseSize(text[..range], out long low) || !TryParseSize(text[(range + separatorLength)..], out long high) || high < low)
                    throw Invalid(error);
                return new SearchTerm(SearchTermField.Size, value) { MinimumSize = low, MaximumSize = high };
            }

            (string comparison, string rest) = SplitComparison(text);
            if (!TryParseSize(rest, out long size))
                throw Invalid(error);
            return comparison switch
            {
                ">" => new SearchTerm(SearchTermField.Size, value) { MinimumSize = size == long.MaxValue ? size : size + 1 },
                ">=" or "" => new SearchTerm(SearchTermField.Size, value) { MinimumSize = size },
                "<" => new SearchTerm(SearchTermField.Size, value) { MaximumSize = size - 1 },
                "<=" => new SearchTerm(SearchTermField.Size, value) { MaximumSize = size },
                "=" => new SearchTerm(SearchTermField.Size, value) { MinimumSize = size, MaximumSize = size },
                _ => throw Invalid(error)
            };
        }

        private static bool TryParseSize(string text, out long bytes)
        {
            bytes = 0;
            text = text.Trim().ToLowerInvariant();
            int split = 0;
            while (split < text.Length && (char.IsDigit(text[split]) || text[split] == '.'))
                split++;
            if (split == 0 || !double.TryParse(text[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return false;

            double multiplier = text[split..].Trim() switch
            {
                "" or "b" or "byte" or "bytes" => 1,
                "k" or "kb" or "kib" => 1024d,
                "m" or "mb" or "mib" => 1024d * 1024,
                "g" or "gb" or "gib" => 1024d * 1024 * 1024,
                "t" or "tb" or "tib" => 1024d * 1024 * 1024 * 1024,
                _ => double.NaN
            };
            double result = number * multiplier;
            if (double.IsNaN(result) || result < 0 || result > long.MaxValue)
                return false;
            bytes = (long)Math.Round(result);
            return true;
        }

        private static (string Comparison, string Remainder) SplitComparison(string text)
        {
            foreach (string comparison in new[] { ">=", "<=", ">", "<", "=" })
            {
                if (text.StartsWith(comparison, StringComparison.Ordinal))
                    return (comparison, text[comparison.Length..].Trim());
            }
            return ("", text);
        }

        private static SearchTerm DateTerm(string value)
        {
            string error = $"날짜 조건을 이해하지 못했습니다: {value} (예: date:7d, date:2026-11, date:>2026-11-01)";
            string text = value.Trim();
            DateTime now = DateTime.Now;

            int range = text.IndexOf("..", StringComparison.Ordinal);
            if (range < 0)
                range = text.IndexOf('~');
            if (range > 0)
            {
                int separatorLength = text[range] == '~' ? 1 : 2;
                if (!TryParsePeriod(text[..range], now, out DateTime from, out _) ||
                    !TryParsePeriod(text[(range + separatorLength)..], now, out _, out DateTime before) || before <= from)
                    throw Invalid(error);
                return new SearchTerm(SearchTermField.Modified, value) { FromLocal = from, BeforeLocal = before };
            }

            (string comparison, string rest) = SplitComparison(text);
            if (TryParseAge(rest, now, out DateTime since))
            {
                // Ages: 7d = within the last 7 days, >30d = older than 30 days.
                return comparison switch
                {
                    "" or "<" or "<=" => new SearchTerm(SearchTermField.Modified, value) { FromLocal = since },
                    ">" or ">=" => new SearchTerm(SearchTermField.Modified, value) { BeforeLocal = since },
                    _ => throw Invalid(error)
                };
            }

            if (!TryParsePeriod(rest, now, out DateTime start, out DateTime end))
                throw Invalid(error);
            return comparison switch
            {
                "" or "=" => new SearchTerm(SearchTermField.Modified, value) { FromLocal = start, BeforeLocal = end },
                ">" => new SearchTerm(SearchTermField.Modified, value) { FromLocal = end },
                ">=" => new SearchTerm(SearchTermField.Modified, value) { FromLocal = start },
                "<" => new SearchTerm(SearchTermField.Modified, value) { BeforeLocal = start },
                "<=" => new SearchTerm(SearchTermField.Modified, value) { BeforeLocal = end },
                _ => throw Invalid(error)
            };
        }

        private static bool TryParseAge(string text, DateTime now, out DateTime since)
        {
            since = default;
            text = text.Trim().ToLowerInvariant();
            int split = 0;
            while (split < text.Length && char.IsDigit(text[split]))
                split++;
            if (split == 0 || split == text.Length || !int.TryParse(text[..split], out int count) || count > 100_000)
                return false;

            try
            {
                since = text[split..].Trim() switch
                {
                    "h" or "시간" => now.AddHours(-count),
                    "d" or "일" => now.Date.AddDays(-(count - 1)),
                    "w" or "주" => now.Date.AddDays(-7 * count + 1),
                    "mo" or "개월" or "달" => now.Date.AddMonths(-count).AddDays(1),
                    "y" or "년" => now.Date.AddYears(-count).AddDays(1),
                    _ => default
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                since = DateTime.MinValue;
            }
            return since != default;
        }

        private static bool TryParsePeriod(string text, DateTime now, out DateTime start, out DateTime end)
        {
            text = text.Trim().ToLowerInvariant();
            DateTime today = now.Date;
            int weekOffset = ((int)today.DayOfWeek + 6) % 7;
            (start, end) = text switch
            {
                "today" or "오늘" => (today, today.AddDays(1)),
                "yesterday" or "어제" => (today.AddDays(-1), today),
                "thisweek" or "이번주" => (today.AddDays(-weekOffset), today.AddDays(7 - weekOffset)),
                "lastweek" or "지난주" => (today.AddDays(-weekOffset - 7), today.AddDays(-weekOffset)),
                "thismonth" or "이번달" => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1)),
                "lastmonth" or "지난달" => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1)),
                "thisyear" or "올해" => (new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1)),
                "lastyear" or "작년" => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1)),
                _ => (default, default)
            };
            if (start != default)
                return true;

            string[] parts = text.Split(['-', '.', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && parts[0].Length == 8 && parts[0].All(char.IsDigit))
                parts = [parts[0][..4], parts[0][4..6], parts[0][6..]];
            if (parts.Length is < 1 or > 3 || parts.Any(part => !part.All(char.IsDigit)) || parts[0].Length != 4)
                return false;

            try
            {
                int year = int.Parse(parts[0], CultureInfo.InvariantCulture);
                if (parts.Length == 1)
                {
                    start = new DateTime(year, 1, 1);
                    end = start.AddYears(1);
                }
                else if (parts.Length == 2)
                {
                    start = new DateTime(year, int.Parse(parts[1], CultureInfo.InvariantCulture), 1);
                    end = start.AddMonths(1);
                }
                else
                {
                    start = new DateTime(year, int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture));
                    end = start.AddDays(1);
                }
                return true;
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException or OverflowException)
            {
                return false;
            }
        }
    }

    internal static string Normalize(string text)
    {
        try
        {
            return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private readonly record struct Token(TokenKind Kind, string? Text = null, SearchTerm? Term = null);

    private enum TokenKind
    {
        End,
        Term,
        And,
        Or,
        Not,
        LeftParenthesis,
        RightParenthesis
    }
}
