#if TOOLS
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UnityPrefabConverter;

// Reads Unity's binary serialized assets (editor files keep their type trees) into the same maps the YAML parser produces
public static class UnityBinary {
// ----- VALUES -----
    private const int ALIGN_FLAG = 0x4000;
    private const int RAW_ARRAY_THRESHOLD = 256;
    private static readonly string[] COMMON_STRINGS = ("AABB|AnimationClip|AnimationCurve|AnimationState|Array|Base|BitField|bitset|bool|char|ColorRGBA|Component|data|deque|double|dynamic_array|FastPropertyName|first|float|Font|GameObject|Generic Mono|GradientNEW|GUID|GUIStyle|int|list|long long|map|Matrix4x4f|MdFour|MonoBehaviour|MonoScript|m_ByteSize|m_Curve|m_EditorClassIdentifier|m_EditorHideFlags|m_Enabled|m_ExtensionPtr|m_GameObject|m_Index|m_IsArray|m_IsStatic|m_MetaFlag|m_Name|m_ObjectHideFlags|m_PrefabInternal|m_PrefabParentObject|m_Script|m_StaticEditorFlags|m_Type|m_Version|Object|pair|PPtr<Component>|PPtr<GameObject>|PPtr<Material>|PPtr<MonoBehaviour>|PPtr<MonoScript>|PPtr<Object>|PPtr<Prefab>|PPtr<Sprite>|PPtr<TextAsset>|PPtr<Texture>|PPtr<Texture2D>|PPtr<Transform>|Prefab|Quaternionf|Rectf|RectInt|RectOffset|second|set|short|size|SInt16|SInt32|SInt64|SInt8|staticvector|string|TextAsset|TextMesh|Texture|Texture2D|Transform|TypelessData|UInt16|UInt32|UInt64|UInt8|unsigned int|unsigned long long|unsigned short|vector|Vector2f|Vector3f|Vector4f|m_ScriptingClassIdentifier|Gradient|Type*|int2_storage|int3_storage|BoundsInt|m_CorrespondingSourceObject|m_PrefabInstance|m_PrefabAsset|FileSize|Hash128|RenderingLayerMask|fixed_array|EntityId|LoadableObjectId|LoadableSceneId").Split('|');
    private static readonly Dictionary<uint, string> COMMON_BY_OFFSET = buildCommonTable();
    private static readonly Dictionary<string, int> PRIMITIVE_SIZES = new() {
        ["SInt8"] = 1, ["UInt8"] = 1, ["char"] = 1, ["bool"] = 1,
        ["short"] = 2, ["SInt16"] = 2, ["unsigned short"] = 2, ["UInt16"] = 2,
        ["int"] = 4, ["SInt32"] = 4, ["unsigned int"] = 4, ["UInt32"] = 4, ["Type*"] = 4, ["float"] = 4,
        ["long long"] = 8, ["SInt64"] = 8, ["unsigned long long"] = 8, ["UInt64"] = 8, ["FileSize"] = 8, ["double"] = 8
    };
    
    private sealed class TypeNode {
        public string Type;
        public string Name;
        public int Level;
        public int MetaFlag;
        public readonly List<TypeNode> Children = new();
        public bool Aligned => (MetaFlag & ALIGN_FLAG) != 0;
    }
    private sealed class SerializedFile {
        public byte[] Data;
        public bool BigEndian;
        public readonly List<(int ClassId, TypeNode Root)> Types = new();
        public readonly List<(long PathId, long Start, long Size, int TypeIndex, int ClassId)> Objects = new();
        public readonly List<string> ExternalGuids = new();
        public int Position;
        public string Stage = "the header";
        public string UnityVersion = "";
    }
    
// ----- PUBLIC CALLS -----
    // Detection
    public static bool IsSerializedFile(byte[] data) {
        if (data.Length < 20 || (data.Length >= 5 && Encoding.ASCII.GetString(data, 0, 5) == "%YAML")) return false;
        var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        return version >= 9 && version <= 50;
    }
    
    // Reading
    public static List<UnityDocument> Parse(byte[] data) {
        var file = readFile(data);
        var documents = new List<UnityDocument>();
        foreach (var entry in file.Objects) {
            var root = entry.TypeIndex >= 0 && entry.TypeIndex < file.Types.Count ? file.Types[entry.TypeIndex].Root : null;
            if (root == null) throw new InvalidDataException("the file was saved without type information");
            file.Position = (int)entry.Start;
            var body = readValue(file, root) as Dictionary<string, object> ?? new Dictionary<string, object>();
            documents.Add(new UnityDocument { ClassId = entry.ClassId, FileId = entry.PathId, TypeName = root.Type, Body = body });
        }
        return documents;
    }
    public static List<string> ExternalGuids(byte[] data) {
        return readFile(data).ExternalGuids.Where(g => g != null).ToList();
    }
    
// ----- HELPERS -----
    // File layout
    // A failed read says how far it got, since the file itself usually can't be shared
    private static SerializedFile readFile(byte[] data) {
        var file = new SerializedFile { Data = data };
        try {
            readLayout(file);
        } catch (Exception exception) when (exception is ArgumentException or IndexOutOfRangeException or OverflowException) {
            var header = string.Join(" ", data.Take(320).Select(b => b.ToString("x2")));
            throw new InvalidDataException($"stopped in {file.Stage} at byte {file.Position} of {data.Length}, after {file.Types.Count} type(s) and {file.Objects.Count} object(s); Unity '{file.UnityVersion}'; header {header}");
        }
        return file;
    }
    private static void readLayout(SerializedFile file) {
        var data = file.Data;
        var version = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        long dataOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
        if (version < 9) throw new InvalidDataException($"serialized file version {version} is too old");
        file.Position = 16;
        file.BigEndian = data[file.Position] != 0;
        file.Position += 4;
        if (version >= 22) {
            file.Position += 4 + 8;
            dataOffset = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(file.Position));
            file.Position += 16;
        }
        file.UnityVersion = readCString(file);
        if (version >= 8) file.Position += 4;
        var typeTrees = version < 13 || data[file.Position++] != 0;
        if (version < 12 && version != 10) throw new InvalidDataException($"serialized file version {version} isn't supported");
        file.Stage = "the type list";
        var typeCount = readInt32(file);
        for (var i = 0; i < typeCount; i++) file.Types.Add(readType(file, version, typeTrees));
        file.Stage = "the object list";
        var bigIds = version >= 7 && version < 14 && readInt32(file) != 0;
        var objectCount = readInt32(file);
        for (var i = 0; i < objectCount; i++) {
            long pathId;
            if (bigIds) {
                pathId = readInt64(file);
            } else if (version < 14) {
                pathId = readInt32(file);
            } else {
                align(file);
                pathId = readInt64(file);
            }
            var start = (version >= 22 ? readInt64(file) : readUInt32(file)) + dataOffset;
            var size = readUInt32(file);
            var typeId = readInt32(file);
            int typeIndex;
            int classId;
            if (version < 16) {
                classId = readUInt16(file);
                typeIndex = file.Types.FindIndex(t => t.ClassId == typeId);
            } else {
                typeIndex = typeId;
                classId = typeIndex >= 0 && typeIndex < file.Types.Count ? file.Types[typeIndex].ClassId : 0;
            }
            if (version < 11) file.Position += 2;
            if (version >= 11 && version < 17) file.Position += 2;
            if (version == 15 || version == 16) file.Position += 1;
            file.Objects.Add((pathId, start, size, typeIndex, classId));
        }
        file.Stage = "the script list";
        if (version >= 11) {
            var scriptCount = readInt32(file);
            for (var i = 0; i < scriptCount; i++) {
                readInt32(file);
                if (version < 14) {
                    readInt32(file);
                } else {
                    align(file);
                    readInt64(file);
                }
            }
        }
        file.Stage = "the list of referenced files";
        var externalCount = readInt32(file);
        for (var i = 0; i < externalCount; i++) {
            if (version >= 6) readCString(file);
            string guid = null;
            if (version >= 5) {
                guid = guidText(data, file.Position);
                file.Position += 16 + 4;
            }
            readCString(file);
            file.ExternalGuids.Add(guid);
        }
    }
    private static (int ClassId, TypeNode Root) readType(SerializedFile file, int version, bool typeTrees) {
        var classId = readInt32(file);
        if (version >= 16) file.Position += 1;
        short scriptIndex = -1;
        if (version >= 17) scriptIndex = (short)readUInt16(file);
        if (version >= 13) {
            if ((version < 16 && classId < 0) || (version >= 16 && classId == 114)) file.Position += 16;
            file.Position += 16;
        }
        TypeNode root = null;
        if (typeTrees) {
            root = readTypeTree(file, version);
            if (version >= 21) {
                var dependencies = readInt32(file);
                file.Position += dependencies * 4;
            }
        }
        return (classId, root);
    }
    private static TypeNode readTypeTree(SerializedFile file, int version) {
        var nodeCount = readInt32(file);
        var stringSize = readInt32(file);
        var nodeSize = version >= 19 ? 32 : 24;
        var nodeStart = file.Position;
        var stringStart = nodeStart + nodeCount * nodeSize;
        var nodes = new List<TypeNode>();
        for (var i = 0; i < nodeCount; i++) {
            file.Position = nodeStart + i * nodeSize;
            readUInt16(file);
            var level = file.Data[file.Position];
            file.Position += 2;
            var typeOffset = readUInt32(file);
            var nameOffset = readUInt32(file);
            readInt32(file);
            readInt32(file);
            var metaFlag = readInt32(file);
            nodes.Add(new TypeNode {
                Level = level,
                Type = treeString(file.Data, stringStart, stringSize, typeOffset),
                Name = treeString(file.Data, stringStart, stringSize, nameOffset),
                MetaFlag = metaFlag
            });
        }
        file.Position = stringStart + stringSize;
        var stack = new List<TypeNode>();
        foreach (var node in nodes) {
            while (stack.Count > 0 && stack[^1].Level >= node.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0) stack[^1].Children.Add(node);
            stack.Add(node);
        }
        return nodes.Count > 0 ? nodes[0] : null;
    }
    
    // Values
    private static object readValue(SerializedFile file, TypeNode node) {
        object value;
        var aligned = node.Aligned;
        if (PRIMITIVE_SIZES.ContainsKey(node.Type)) {
            value = readPrimitive(file, node.Type);
        } else if (node.Type == "string") {
            var length = readInt32(file);
            value = Encoding.UTF8.GetString(file.Data, file.Position, length);
            file.Position += length;
            aligned = true;
        } else if (node.Type == "TypelessData") {
            var length = readInt32(file);
            value = file.Data.AsSpan(file.Position, length).ToArray();
            file.Position += length;
        } else if (node.Type == "pair" && node.Children.Count == 2) {
            var first = readValue(file, node.Children[0]);
            var second = readValue(file, node.Children[1]);
            value = first is string key ? new Dictionary<string, object> { [key] = second } : new Dictionary<string, object> { ["first"] = first, ["second"] = second };
        } else if (node.Children.Count > 0 && node.Children[0].Type == "Array") {
            var array = node.Children[0];
            if (array.Aligned) aligned = true;
            value = readArray(file, array.Children[^1], readInt32(file));
        } else if (node.Type.StartsWith("PPtr<")) {
            var fields = new Dictionary<string, object>();
            foreach (var child in node.Children) fields[child.Name] = readValue(file, child);
            value = pointer(file, fields);
        } else {
            var fields = new Dictionary<string, object>();
            foreach (var child in node.Children) fields[child.Name] = readValue(file, child);
            value = fields;
        }
        if (aligned) align(file);
        return value;
    }
    // Large arrays of plain numbers stay raw little-endian bytes, which is how the YAML side stores them as hex
    private static object readArray(SerializedFile file, TypeNode element, int count) {
        if (count < 0) throw new InvalidDataException("negative array length");
        if (PRIMITIVE_SIZES.TryGetValue(element.Type, out var size) && count > RAW_ARRAY_THRESHOLD && !element.Aligned) {
            var bytes = file.Data.AsSpan(file.Position, count * size).ToArray();
            file.Position += count * size;
            if (file.BigEndian && size > 1) {
                for (var i = 0; i < bytes.Length; i += size) Array.Reverse(bytes, i, size);
            }
            return bytes;
        }
        var list = new List<object>(Math.Min(count, 1 << 16));
        for (var i = 0; i < count; i++) list.Add(readValue(file, element));
        return list;
    }
    private static object readPrimitive(SerializedFile file, string type) {
        var span = file.Data.AsSpan(file.Position);
        var size = PRIMITIVE_SIZES[type];
        file.Position += size;
        var big = file.BigEndian;
        var invariant = CultureInfo.InvariantCulture;
        return type switch {
            "SInt8" => ((sbyte)span[0]).ToString(invariant),
            "UInt8" or "char" or "bool" => span[0].ToString(invariant),
            "short" or "SInt16" => (big ? BinaryPrimitives.ReadInt16BigEndian(span) : BinaryPrimitives.ReadInt16LittleEndian(span)).ToString(invariant),
            "unsigned short" or "UInt16" => (big ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)).ToString(invariant),
            "int" or "SInt32" => (big ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span)).ToString(invariant),
            "unsigned int" or "UInt32" or "Type*" => (big ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span)).ToString(invariant),
            "float" => (big ? BinaryPrimitives.ReadSingleBigEndian(span) : BinaryPrimitives.ReadSingleLittleEndian(span)).ToString("R", invariant),
            "double" => (big ? BinaryPrimitives.ReadDoubleBigEndian(span) : BinaryPrimitives.ReadDoubleLittleEndian(span)).ToString("R", invariant),
            "long long" or "SInt64" => (big ? BinaryPrimitives.ReadInt64BigEndian(span) : BinaryPrimitives.ReadInt64LittleEndian(span)).ToString(invariant),
            _ => (big ? BinaryPrimitives.ReadUInt64BigEndian(span) : BinaryPrimitives.ReadUInt64LittleEndian(span)).ToString(invariant)
        };
    }
    // Same shape as a YAML reference: {fileID, guid, type} for other files, {fileID} inside this one
    private static Dictionary<string, object> pointer(SerializedFile file, Dictionary<string, object> fields) {
        var fileIndex = (int)UnityYaml.GetLong(fields, "m_FileID");
        var pathId = UnityYaml.GetString(fields, "m_PathID", "0");
        var reference = new Dictionary<string, object> { ["fileID"] = pathId };
        if (fileIndex > 0 && fileIndex <= file.ExternalGuids.Count && file.ExternalGuids[fileIndex - 1] != null) {
            reference["guid"] = file.ExternalGuids[fileIndex - 1];
            reference["type"] = "2";
        }
        return reference;
    }
    
    // Primitives
    private static int readInt32(SerializedFile file) {
        var value = file.BigEndian ? BinaryPrimitives.ReadInt32BigEndian(file.Data.AsSpan(file.Position)) : BinaryPrimitives.ReadInt32LittleEndian(file.Data.AsSpan(file.Position));
        file.Position += 4;
        return value;
    }
    private static uint readUInt32(SerializedFile file) {
        var value = file.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(file.Data.AsSpan(file.Position)) : BinaryPrimitives.ReadUInt32LittleEndian(file.Data.AsSpan(file.Position));
        file.Position += 4;
        return value;
    }
    private static ushort readUInt16(SerializedFile file) {
        var value = file.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(file.Data.AsSpan(file.Position)) : BinaryPrimitives.ReadUInt16LittleEndian(file.Data.AsSpan(file.Position));
        file.Position += 2;
        return value;
    }
    private static long readInt64(SerializedFile file) {
        var value = file.BigEndian ? BinaryPrimitives.ReadInt64BigEndian(file.Data.AsSpan(file.Position)) : BinaryPrimitives.ReadInt64LittleEndian(file.Data.AsSpan(file.Position));
        file.Position += 8;
        return value;
    }
    private static string readCString(SerializedFile file) {
        var end = Array.IndexOf(file.Data, (byte)0, file.Position);
        if (end < 0) throw new InvalidDataException("unterminated string");
        var text = Encoding.UTF8.GetString(file.Data, file.Position, end - file.Position);
        file.Position = end + 1;
        return text;
    }
    private static void align(SerializedFile file) {
        file.Position = (file.Position + 3) & ~3;
    }
    
    // Strings
    private static Dictionary<uint, string> buildCommonTable() {
        var table = new Dictionary<uint, string>();
        uint offset = 0;
        foreach (var text in COMMON_STRINGS) {
            table[offset] = text;
            offset += (uint)text.Length + 1;
        }
        return table;
    }
    // Offsets with the top bit set point into Unity's built-in string table
    private static string treeString(byte[] data, int start, int size, uint offset) {
        if ((offset & 0x80000000) != 0) return COMMON_BY_OFFSET.TryGetValue(offset & 0x7FFFFFFF, out var common) ? common : (offset & 0x7FFFFFFF).ToString(CultureInfo.InvariantCulture);
        if (offset >= size) return "";
        var end = Array.IndexOf(data, (byte)0, start + (int)offset, size - (int)offset);
        return Encoding.UTF8.GetString(data, start + (int)offset, (end < 0 ? start + size : end) - start - (int)offset);
    }
    // Unity writes each GUID byte low nibble first in its text form
    private static string guidText(byte[] data, int position) {
        var builder = new StringBuilder(32);
        var empty = true;
        for (var i = 0; i < 16; i++) {
            var value = data[position + i];
            if (value != 0) empty = false;
            builder.Append("0123456789abcdef"[value & 0xF]).Append("0123456789abcdef"[value >> 4]);
        }
        return empty ? null : builder.ToString();
    }
}
#endif
