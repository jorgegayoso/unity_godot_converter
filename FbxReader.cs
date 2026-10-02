#if TOOLS
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace UnityPrefabConverter;

// Reads just enough of a binary FBX to tell how Unity numbers a mesh's material slots.
// Unity orders them by the first polygon that uses each material; Godot keeps the order the materials are attached to the object, so the two can differ
public static class FbxReader {
// ----- VALUES -----
    private const string MAGIC = "Kaydara FBX Binary  ";
    private const int HEADER_SIZE = 27;
    private const int WIDE_HEADERS_VERSION = 7500;
    
    private sealed class FbxNode {
        public string Name;
        public readonly List<object> Properties = new();
        public readonly List<FbxNode> Children = new();
    }
    
// ----- PUBLIC CALLS -----
    // Material order
    // Mesh object name -> material names in Unity's slot order, or null when the file isn't a binary FBX
    public static Dictionary<string, List<string>> MaterialOrders(string path) {
        byte[] data;
        try {
            data = File.ReadAllBytes(path);
        } catch (IOException) {
            return null;
        }
        if (data.Length < HEADER_SIZE || Encoding.ASCII.GetString(data, 0, MAGIC.Length) != MAGIC) return null;
        try {
            return materialOrders(data);
        } catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException or InvalidCastException) {
            return null;
        }
    }
    
// ----- HELPERS -----
    // Material order
    private static Dictionary<string, List<string>> materialOrders(byte[] data) {
        var wide = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(23)) >= WIDE_HEADERS_VERSION;
        var top = new List<FbxNode>();
        var position = HEADER_SIZE;
        while (position < data.Length) {
            var node = readNode(data, ref position, wide, 0);
            if (node == null) break;
            top.Add(node);
        }
        var objects = top.FirstOrDefault(n => n.Name == "Objects");
        var connections = top.FirstOrDefault(n => n.Name == "Connections");
        var orders = new Dictionary<string, List<string>>();
        if (objects == null || connections == null) return orders;
        var names = new Dictionary<long, string>();
        var kinds = new Dictionary<long, string>();
        var polygonMaterials = new Dictionary<long, int[]>();
        foreach (var node in objects.Children) {
            if (node.Properties.Count < 2 || node.Properties[0] is not long id) continue;
            kinds[id] = node.Name;
            names[id] = objectName(node.Properties[1] as byte[]);
            if (node.Name != "Geometry") continue;
            var layer = node.Children.FirstOrDefault(c => c.Name == "LayerElementMaterial");
            if (layer?.Children.FirstOrDefault(c => c.Name == "Materials")?.Properties.FirstOrDefault() is int[] indices) polygonMaterials[id] = indices;
        }
        var attached = new Dictionary<long, List<string>>();
        var owners = new Dictionary<long, long>();
        foreach (var connection in connections.Children) {
            if (connection.Properties.Count < 3 || connection.Properties[1] is not long child || connection.Properties[2] is not long parent) continue;
            if (kinds.GetValueOrDefault(parent) != "Model") continue;
            if (kinds.GetValueOrDefault(child) == "Material") {
                if (!attached.TryGetValue(parent, out var list)) attached[parent] = list = new List<string>();
                list.Add(names[child]);
            } else if (kinds.GetValueOrDefault(child) == "Geometry") {
                owners[child] = parent;
            }
        }
        foreach (var (geometry, indices) in polygonMaterials) {
            if (!owners.TryGetValue(geometry, out var model) || !attached.TryGetValue(model, out var materials)) continue;
            var order = new List<string>();
            foreach (var index in indices) {
                if (index < 0 || index >= materials.Count || order.Contains(materials[index])) continue;
                order.Add(materials[index]);
                if (order.Count == materials.Count) break;
            }
            orders[names[model]] = order;
        }
        return orders;
    }
    // FBX stores names as "Name\0\1Class"
    private static string objectName(byte[] raw) {
        if (raw == null) return "";
        var end = Array.IndexOf(raw, (byte)0);
        return Encoding.UTF8.GetString(raw, 0, end < 0 ? raw.Length : end);
    }
    
    // Node records
    private static FbxNode readNode(byte[] data, ref int position, bool wide, int depth) {
        long end;
        long propertyCount;
        if (wide) {
            end = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position));
            propertyCount = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position + 8));
            position += 24;
        } else {
            end = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
            position += 12;
        }
        var nameLength = data[position++];
        if (end == 0) return null;
        var node = new FbxNode { Name = Encoding.ASCII.GetString(data, position, nameLength) };
        position += nameLength;
        // Only the per-polygon material list is unpacked; vertex data and animation keys are skipped
        var wanted = node.Name == "Materials";
        for (var i = 0; i < propertyCount; i++) node.Properties.Add(readProperty(data, ref position, wanted));
        var useful = depth == 0 ? node.Name == "Objects" || node.Name == "Connections" : depth == 1 ? node.Name == "Geometry" || node.Name == "C" : node.Name == "LayerElementMaterial" || node.Name == "Materials";
        while (useful && position < end) {
            var child = readNode(data, ref position, wide, depth + 1);
            if (child == null) break;
            node.Children.Add(child);
        }
        position = (int)end;
        return node;
    }
    private static object readProperty(byte[] data, ref int position, bool unpackArrays) {
        var type = (char)data[position++];
        switch (type) {
            case 'Y':
                position += 2;
                return null;
            case 'C':
                position += 1;
                return null;
            case 'I':
            case 'F':
                position += 4;
                return null;
            case 'D':
                position += 8;
                return null;
            case 'L':
                position += 8;
                return BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position - 8));
            case 'S':
            case 'R':
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
                position += 4 + length;
                return data.AsSpan(position - length, length).ToArray();
            case 'f':
            case 'd':
            case 'l':
            case 'i':
            case 'b':
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
                var compressed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4)) != 0;
                var stored = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 8));
                position += 12 + stored;
                if (!unpackArrays || type != 'i') return null;
                var bytes = data.AsSpan(position - stored, stored).ToArray();
                if (compressed) {
                    using var input = new ZLibStream(new MemoryStream(bytes), CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    input.CopyTo(output);
                    bytes = output.ToArray();
                }
                var values = new int[count];
                for (var i = 0; i < count; i++) values[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 4));
                return values;
            default:
                throw new InvalidDataException($"unknown FBX property type '{type}'");
        }
    }
}
#endif
