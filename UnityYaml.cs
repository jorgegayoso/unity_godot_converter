#if TOOLS
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityPrefabConverter;

public readonly record struct UnityRef(long FileId, string Guid) {
    public bool IsNull => FileId == 0;
}

public sealed class UnityDocument {
    public int ClassId;
    public long FileId;
    public bool Stripped;
    public string TypeName;
    public Dictionary<string, object> Body;
}

// Minimal parser for the YAML subset Unity serializes (block maps, same-indent sequences, wrapped flow maps)
public static class UnityYaml {
// ----- VALUES -----
    private static readonly string[] INVALID_NODE_NAME_CHARACTERS = { ".", ":", "@", "/", "\"", "%" };
    private static readonly Regex HEADER_REGEX = new(@"^--- !u!(\d+) &(-?\d+)( stripped)?");
    
// ----- PUBLIC CALLS -----
    // Parsing
    public static List<UnityDocument> ParseFile(string path) {
        var data = File.ReadAllBytes(path);
        if (UnityBinary.IsSerializedFile(data)) return UnityBinary.Parse(data);
        return Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));
    }
    public static List<UnityDocument> Parse(string text) {
        var documents = new List<UnityDocument>();
        var body = new List<string>();
        UnityDocument current = null;
        foreach (var line in splitLines(text)) {
            if (line.StartsWith("---")) {
                finishDocument(current, body, documents);
                body = new List<string>();
                var match = HEADER_REGEX.Match(line);
                current = match.Success ? new UnityDocument {
                    ClassId = int.Parse(match.Groups[1].Value),
                    FileId = long.Parse(match.Groups[2].Value),
                    Stripped = match.Groups[3].Success
                } : null;
            } else if (current != null && !line.StartsWith("%")) {
                body.Add(line);
            }
        }
        finishDocument(current, body, documents);
        return documents;
    }
    public static Dictionary<string, object> ParseMapping(string text) {
        var lines = new List<string>();
        foreach (var line in splitLines(text)) {
            if (!line.StartsWith("%") && !line.StartsWith("---")) lines.Add(line);
        }
        var index = 0;
        return parseMapping(lines, ref index, 0);
    }
    
    // Accessors
    public static Dictionary<string, object> GetMap(Dictionary<string, object> map, string key) {
        return map != null && map.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;
    }
    public static List<object> GetList(Dictionary<string, object> map, string key) {
        return map != null && map.TryGetValue(key, out var value) ? value as List<object> : null;
    }
    public static string GetString(Dictionary<string, object> map, string key, string fallback = "") {
        return map != null && map.TryGetValue(key, out var value) && value is string text ? text : fallback;
    }
    public static double GetDouble(Dictionary<string, object> map, string key, double fallback = 0) {
        return ParseDouble(GetString(map, key, null), fallback);
    }
    // Parsed as integers: Unity file IDs exceed double precision, so a double round-trip merges neighbouring IDs
    public static long GetLong(Dictionary<string, object> map, string key, long fallback = 0) {
        var text = GetString(map, key, null)?.Trim();
        if (string.IsNullOrEmpty(text)) return fallback;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long)ParseDouble(text, fallback);
    }
    public static bool GetBool(Dictionary<string, object> map, string key, bool fallback = false) {
        return GetDouble(map, key, fallback ? 1 : 0) != 0;
    }
    public static UnityRef GetRef(Dictionary<string, object> map, string key) {
        return map != null && map.TryGetValue(key, out var value) ? ToRef(value) : default;
    }
    public static UnityRef ToRef(object value) {
        if (value is not Dictionary<string, object> map) return default;
        var guid = GetString(map, "guid", null);
        return new UnityRef(GetLong(map, "fileID"), guid?.ToLowerInvariant());
    }
    // Godot swaps characters a node name can't hold for underscores when it imports a model (Door.L becomes Door_L); C#'s ValidateNodeName drops them instead, so it isn't used
    public static string NodeName(string name) {
        foreach (var invalid in INVALID_NODE_NAME_CHARACTERS) name = name.Replace(invalid, "_");
        return name;
    }
    public static double ParseDouble(string text, double fallback) {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }
    
// ----- HELPERS -----
    // Documents
    private static IEnumerable<string> splitLines(string text) {
        return text.Replace("\r\n", "\n").Split('\n');
    }
    private static void finishDocument(UnityDocument document, List<string> body, List<UnityDocument> documents) {
        if (document == null) return;
        var index = 0;
        var root = parseMapping(body, ref index, 0);
        foreach (var (key, value) in root) {
            document.TypeName = key;
            document.Body = value as Dictionary<string, object> ?? new Dictionary<string, object>();
            break;
        }
        document.Body ??= new Dictionary<string, object>();
        documents.Add(document);
    }
    
    // Block structure
    private static Dictionary<string, object> parseMapping(List<string> lines, ref int index, int indent) {
        var map = new Dictionary<string, object>();
        while (true) {
            index = skipBlank(lines, index);
            if (index >= lines.Count) break;
            var line = lines[index];
            var lineIndent = indentOf(line);
            if (lineIndent < indent || (lineIndent == indent && isSequenceItem(line, indent))) break;
            if (lineIndent > indent) {
                index++;
                continue;
            }
            var content = line.Substring(indent);
            var separator = findKeySeparator(content);
            index++;
            if (separator < 0) continue;
            var key = unquote(content.Substring(0, separator).Trim());
            var rest = content.Substring(separator + 1).Trim();
            map[key] = rest.Length == 0 ? parseChild(lines, ref index, indent, true) : parseInlineValue(lines, ref index, indent, rest);
        }
        return map;
    }
    private static List<object> parseSequence(List<string> lines, ref int index, int indent) {
        var list = new List<object>();
        while (true) {
            index = skipBlank(lines, index);
            if (index >= lines.Count) break;
            var line = lines[index];
            if (indentOf(line) != indent || !isSequenceItem(line, indent)) break;
            var contentStart = indent + 1;
            while (contentStart < line.Length && line[contentStart] == ' ') contentStart++;
            var rest = line.Substring(contentStart).Trim();
            if (rest.Length == 0) {
                index++;
                list.Add(parseChild(lines, ref index, indent, false));
            } else if (!startsFlowOrQuote(rest) && findKeySeparator(rest) >= 0) {
                lines[index] = new string(' ', contentStart) + line.Substring(contentStart);
                list.Add(parseMapping(lines, ref index, contentStart));
            } else {
                index++;
                list.Add(parseInlineValue(lines, ref index, indent, rest));
            }
        }
        return list;
    }
    private static object parseChild(List<string> lines, ref int index, int indent, bool allowSameIndentSequence) {
        var next = skipBlank(lines, index);
        if (next >= lines.Count) return null;
        var nextIndent = indentOf(lines[next]);
        if (nextIndent > indent) {
            index = next;
            return isSequenceItem(lines[next], nextIndent) ? parseSequence(lines, ref index, nextIndent) : parseMapping(lines, ref index, nextIndent);
        }
        if (allowSameIndentSequence && nextIndent == indent && isSequenceItem(lines[next], indent)) {
            index = next;
            return parseSequence(lines, ref index, indent);
        }
        return null;
    }
    private static object parseInlineValue(List<string> lines, ref int index, int indent, string rest) {
        var builder = new StringBuilder(rest);
        if (rest[0] == '{' || rest[0] == '[') {
            while (!isBalanced(builder.ToString()) && index < lines.Count) builder.Append(' ').Append(lines[index++].Trim());
            var position = 0;
            return parseFlow(builder.ToString(), ref position);
        }
        if (rest[0] == '"' || rest[0] == '\'') {
            while (!isQuoteClosed(builder.ToString()) && index < lines.Count) builder.Append(' ').Append(lines[index++].Trim());
            return unquote(builder.ToString());
        }
        if (rest[0] == '|' || rest[0] == '>') {
            var block = new List<string>();
            while (index < lines.Count && (lines[index].Trim().Length == 0 || indentOf(lines[index]) > indent)) block.Add(lines[index++].Trim());
            return string.Join("\n", block).TrimEnd();
        }
        while (index < lines.Count && lines[index].Trim().Length > 0 && indentOf(lines[index]) > indent) {
            builder.Append(' ').Append(lines[index++].Trim());
        }
        return builder.ToString();
    }
    
    // Flow structure
    private static object parseFlow(string text, ref int position) {
        skipSpaces(text, ref position);
        if (position >= text.Length) return "";
        if (text[position] == '{') {
            position++;
            var map = new Dictionary<string, object>();
            while (position < text.Length) {
                skipSpaces(text, ref position);
                if (position >= text.Length) break;
                if (text[position] == '}') {
                    position++;
                    break;
                }
                if (text[position] == ',') {
                    position++;
                    continue;
                }
                var keyStart = position;
                while (position < text.Length && text[position] != ':' && text[position] != '}') position++;
                var key = unquote(text.Substring(keyStart, position - keyStart).Trim());
                if (position < text.Length && text[position] == ':') position++;
                map[key] = parseFlow(text, ref position);
            }
            return map;
        }
        if (text[position] == '[') {
            position++;
            var list = new List<object>();
            while (position < text.Length) {
                skipSpaces(text, ref position);
                if (position >= text.Length) break;
                if (text[position] == ']') {
                    position++;
                    break;
                }
                if (text[position] == ',') {
                    position++;
                    continue;
                }
                list.Add(parseFlow(text, ref position));
            }
            return list;
        }
        var start = position;
        var quote = text[position] == '"' || text[position] == '\'' ? text[position] : '\0';
        if (quote != '\0') {
            position++;
            while (position < text.Length && !(text[position] == quote && text[position - 1] != '\\')) position++;
            position = System.Math.Min(position + 1, text.Length);
        }
        while (position < text.Length && text[position] != ',' && text[position] != '}' && text[position] != ']') position++;
        return unquote(text.Substring(start, position - start).Trim());
    }
    
    // Text utilities
    private static int skipBlank(List<string> lines, int index) {
        while (index < lines.Count && (lines[index].Trim().Length == 0 || lines[index].TrimStart().StartsWith("#"))) index++;
        return index;
    }
    private static void skipSpaces(string text, ref int position) {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }
    private static int indentOf(string line) {
        var count = 0;
        while (count < line.Length && line[count] == ' ') count++;
        return count;
    }
    private static bool isSequenceItem(string line, int indent) {
        return line.Length > indent && line[indent] == '-' && (line.Length == indent + 1 || line[indent + 1] == ' ');
    }
    private static bool startsFlowOrQuote(string text) {
        return text[0] == '{' || text[0] == '[' || text[0] == '"' || text[0] == '\'';
    }
    private static int findKeySeparator(string content) {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < content.Length; i++) {
            var c = content[i];
            if (quote != '\0') {
                if (c == quote) quote = '\0';
            } else if (c == '"' || c == '\'') {
                quote = c;
            } else if (c == '{' || c == '[') {
                depth++;
            } else if (c == '}' || c == ']') {
                depth--;
            } else if (c == ':' && depth == 0 && (i + 1 == content.Length || content[i + 1] == ' ')) {
                return i;
            }
        }
        return -1;
    }
    private static bool isBalanced(string text) {
        var depth = 0;
        var quote = '\0';
        foreach (var c in text) {
            if (quote != '\0') {
                if (c == quote) quote = '\0';
            } else if (c == '"' || c == '\'') {
                quote = c;
            } else if (c == '{' || c == '[') {
                depth++;
            } else if (c == '}' || c == ']') {
                depth--;
            }
        }
        return depth <= 0;
    }
    private static bool isQuoteClosed(string text) {
        var quote = text[0];
        for (var i = 1; i < text.Length; i++) {
            if (text[i] != quote) continue;
            if (quote == '"' && text[i - 1] == '\\') continue;
            if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'') {
                i++;
                continue;
            }
            return true;
        }
        return false;
    }
    private static string unquote(string text) {
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'') return text.Substring(1, text.Length - 2).Replace("''", "'");
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') {
            try {
                return Regex.Unescape(text.Substring(1, text.Length - 2));
            } catch {
                return text.Substring(1, text.Length - 2);
            }
        }
        return text;
    }
}
#endif
