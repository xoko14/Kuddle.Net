using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Kuddle.AST;
using Kuddle.Extensions;

namespace Kuddle.Serialization;

internal class ObjectDeserializer
{
    private const StringComparison NodeNameComparison = StringComparison.OrdinalIgnoreCase;

    private readonly KdlSerializerOptions _options;

    public ObjectDeserializer(KdlSerializerOptions? options = null)
    {
        _options = options ?? KdlSerializerOptions.Default;
    }

    internal static T DeserializeDocument<T>(KdlDocument doc, KdlSerializerOptions options)
        where T : new()
    {
        var worker = new ObjectDeserializer(options);

        var mapping = KdlTypeMapping.For<T>();
        var instance = new T();
        if (options.RootMapping == KdlRootMapping.AsNode)
        {
            if (doc.Nodes.Count != 1)
            {
                throw new KuddleSerializationException(
                    $"Expected exactly 1 root node, but found {doc.Nodes.Count}."
                );
            }
            worker.MapNodeToObject(doc.Nodes.First(), instance, mapping);
            return instance;
        }

        worker.MapDocumentToObject(doc.Nodes, instance, mapping);

        return instance;
    }

    private void MapDocumentToObject<T>(List<KdlNode> nodes, T instance, KdlTypeMapping mapping)
        where T : new()
    {
        if (mapping.IsDictionary)
        {
            var explicitNames = mapping
                .Children.Select(c => c.KdlName)
                .Concat(mapping.Properties.Select(p => p.KdlName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var dictionaryNodes = nodes.Where(n => !explicitNames.Contains(n.Name.Value));

            PopulateDictionary(
                (IDictionary)instance!,
                dictionaryNodes,
                mapping.DictionaryKeyProperty!.PropertyType,
                mapping.DictionaryValueProperty!.PropertyType
            );
        }
        var anonymousNodes = nodes.Where(n => n.Name.Value == "-").ToList();
        foreach (var map in mapping.Arguments)
        {
            if (map.ArgumentIndex < anonymousNodes.Count)
            {
                var kdlValue = anonymousNodes[map.ArgumentIndex].Arg(0);
                if (kdlValue != null)
                {
                    var val = KdlValueConverter.FromKdlOrThrow(
                        kdlValue,
                        map.Property.PropertyType,
                        map.KdlName,
                        map.TypeAnnotation
                    );
                    map.SetValue(instance!, val);
                }
            }
        }

        MapMembersFromNodes(nodes, instance, mapping.Properties.Concat(mapping.Children));
    }

    private void MapMembersFromNodes<T>(
        List<KdlNode> nodes,
        T instance,
        IEnumerable<KdlMemberMap> members
    )
        where T : new()
    {
        foreach (var map in members)
        {
            // This is essentially the logic from MapChildren
            List<KdlNode> matches = nodes
                .Where(n => n.Name.Value.Equals(map.KdlName, NodeNameComparison))
                .ToList();

            if (matches.Count == 0)
                continue;

            if (map.IsDictionary)
            {
                var dict = EnsureInstance(instance, map) as IDictionary;
                PopulateDictionary(
                    dict!,
                    map.IsFlattened ? matches : matches[^1].Children?.Nodes ?? [],
                    map.DictionaryKeyProperty!.PropertyType,
                    map.DictionaryValueProperty!.PropertyType
                );
            }
            else if (map.IsCollection)
            {
                PopulateCollection(
                    instance!,
                    map.IsFlattened ? matches : matches[^1].Children?.Nodes ?? [],
                    map
                );
            }
            else
            {
                var last = matches.Last();
                if (map.Property.PropertyType.IsKdlScalar)
                {
                    // Promoted property: value is in the first argument
                    var arg = last.Arg(0);
                    if (arg != null)
                    {
                        var val = KdlValueConverter.FromKdlOrThrow(
                            arg,
                            map.Property.PropertyType,
                            last.Name.Value,
                            map.TypeAnnotation
                        );
                        map.SetValue(instance!, val);
                    }
                }
                else
                {
                    map.SetValue(instance!, DeserializeObject(last, map.Property.PropertyType));
                }
            }
        }
    }

    internal static T DeserializeNode<T>(KdlNode node, KdlSerializerOptions options)
        where T : new()
    {
        var worker = new ObjectDeserializer(options);
        var metadata = KdlTypeMapping.For<T>();
        ValidateNodeName(node, metadata.NodeName);

        var instance = new T();
        worker.MapNodeToObject(node, instance, metadata);
        return instance;
    }

    /// <summary>
    /// Maps a KDL node's entries and children to an object instance.
    /// </summary>
    private void MapNodeToObject(KdlNode node, object instance, KdlTypeMapping mapping)
    {
        var consumedPropKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedChildNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Map Positional Arguments
        foreach (var map in mapping.Arguments)
        {
            if (map.IsCollection)
            {
                // RULE 8: Rest Arguments
                // Collect all arguments from map.ArgumentIndex to the end
                var argsList = node.Arguments.Skip(map.ArgumentIndex).ToList();
                var targetList = CreateList(map.ElementType!);

                foreach (var kdlVal in argsList)
                {
                    var val = KdlValueConverter.FromKdlOrThrow(
                        kdlVal,
                        map.ElementType!,
                        map.KdlName
                    );
                    targetList.Add(val);
                }
                map.SetValue(
                    instance,
                    ConvertCollection(targetList, map.Property.PropertyType, map.ElementType!)
                );
            }
            else
            {
                var kdlValue = node.Arg(map.ArgumentIndex);
                if (kdlValue != null)
                {
                    var val = KdlValueConverter.FromKdlOrThrow(
                        kdlValue,
                        map.Property.PropertyType,
                        map.KdlName,
                        map.TypeAnnotation
                    );
                    map.SetValue(instance, val);
                }
            }
        }

        foreach (var map in mapping.Properties.Where(m => !m.IsDictionary))
        {
            var kdlValue = node.Prop(map.KdlName);

            if (kdlValue == null && node.Children != null)
            {
                var childNode = node.Children.Nodes.FirstOrDefault(n =>
                    n.Name.Value.Equals(map.KdlName, NodeNameComparison)
                );

                if (childNode != null)
                {
                    kdlValue = childNode.Arg(0);
                    consumedChildNames.Add(map.KdlName);
                }
            }

            if (kdlValue != null)
            {
                var val = KdlValueConverter.FromKdlOrThrow(
                    kdlValue,
                    map.Property.PropertyType,
                    map.KdlName,
                    map.TypeAnnotation
                );
                map.SetValue(instance, val);

                consumedPropKeys.Add(map.KdlName);
            }
        }

        foreach (var map in mapping.Properties.Where(m => m.IsDictionary))
        {
            var dict = EnsureInstance(instance, map) as IDictionary;
            bool isNamespaced = !string.IsNullOrWhiteSpace(map.KdlName);
            string prefix = isNamespaced ? $"{map.KdlName}:" : "";

            foreach (var kdlProp in node.Properties)
            {
                string key = kdlProp.Key.Value;

                if (isNamespaced)
                {
                    if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string dictKey = key.Substring(prefix.Length);
                        dict![dictKey] = KdlValueConverter.FromKdlOrThrow(
                            kdlProp.Value,
                            map.Property.PropertyType.GetGenericArguments()[1],
                            key
                        );
                        consumedPropKeys.Add(key);
                    }
                }
                else
                {
                    if (consumedPropKeys.Contains(key) || key.Contains(':'))
                    {
                        continue;
                    }
                    var valueType = map.Property.PropertyType.GetGenericArguments()[1];
                    dict![key] = KdlValueConverter.FromKdlOrThrow(kdlProp.Value, valueType, key);
                    consumedPropKeys.Add(key);
                }
            }
        }

        if (node.Children != null)
        {
            if (mapping.IsDictionary)
            {
                var explicitChildNames = mapping
                    .Children.Select(c => c.KdlName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var dictionaryNodes = node.Children.Nodes.Where(n =>
                    !explicitChildNames.Contains(n.Name.Value)
                );

                PopulateDictionary(
                    (IDictionary)instance,
                    dictionaryNodes,
                    mapping.DictionaryKeyProperty!.PropertyType,
                    mapping.DictionaryValueProperty!.PropertyType
                );

                // In a root dictionary, all child nodes are considered consumed
                foreach (var n in node.Children.Nodes)
                    consumedChildNames.Add(n.Name.Value);
            }

            MapChildren(node.Children.Nodes, instance, mapping, consumedChildNames);
        }

        if (mapping.ExtensionDataProperty != null)
        {
            var extDict = EnsureInstance(instance, mapping.ExtensionDataProperty) as IDictionary;

            // Capture unmapped KDL properties
            foreach (var prop in node.Properties)
            {
                if (!consumedPropKeys.Contains(prop.Key.Value))
                {
                    // Get dictionary value type
                    var valueType = extDict!.GetType().GenericTypeArguments[1];
                    // Convert to native CLR type (string, double, bool) so Assertions pass
                    KdlValueConverter.TryFromKdl(prop.Value, valueType, out var nativeVal);
                    extDict![prop.Key.Value] = nativeVal;
                }
            }

            // Capture unmapped Child Nodes
            if (node.Children != null)
            {
                foreach (var childNode in node.Children.Nodes)
                {
                    if (!consumedChildNames.Contains(childNode.Name.Value))
                    {
                        // Store the actual KdlNode AST for complex unmapped structures
                        extDict![childNode.Name.Value] = childNode;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Maps child KDL nodes to properties marked with [KdlNode].
    /// </summary>
    private void MapChildren(
        List<KdlNode>? nodes,
        object instance,
        KdlTypeMapping mapping,
        HashSet<string> consumedNames
    )
    {
        if (nodes is null || nodes.Count == 0)
            return;

        foreach (var map in mapping.Children)
        {
            List<KdlNode> matches = nodes
                .Where(n => n.Name.Value.Equals(map.KdlName, NodeNameComparison))
                .ToList();

            if (matches.Count == 0)
                continue;

            consumedNames.Add(map.KdlName);

            List<KdlNode> nodesToProcess = map.IsFlattened
                ? matches
                : matches[^1].Children?.Nodes ?? [];

            if (map.IsDictionary)
            {
                var dict = EnsureInstance(instance, map) as IDictionary;
                PopulateDictionary(
                    dict!,
                    nodesToProcess,
                    map.DictionaryKeyProperty!.PropertyType,
                    map.DictionaryValueProperty!.PropertyType
                );
            }
            else if (map.IsCollection)
            {
                PopulateCollection(instance, nodesToProcess, map);
            }
            else
            {
                var last = matches.Last();
                object? value;

                if (map.Property.PropertyType.IsKdlScalar)
                {
                    var arg = last.Arg(0);
                    value =
                        arg != null
                            ? KdlValueConverter.FromKdlOrThrow(
                                arg,
                                map.Property.PropertyType,
                                last.Name.Value
                            )
                            : null;
                }
                else
                {
                    value = DeserializeObject(last, map.Property.PropertyType);
                }

                map.SetValue(instance, value);
            }
        }
    }

    private void PopulateCollection(
        object parentInstance,
        IEnumerable<KdlNode> nodes,
        KdlMemberMap map
    )
    {
        var list = CreateList(map.ElementType!);
        var elementMapping = KdlTypeMapping.For(map.ElementType!);

        foreach (var node in nodes)
        {
            // If the element name matches (or we are in a wrapped block), deserialize it
            object? item;
            if (map.ElementType!.IsKdlScalar)
            {
                var kdlVal = node.Arg(0);
                item =
                    kdlVal != null
                        ? KdlValueConverter.FromKdlOrThrow(
                            kdlVal,
                            map.ElementType!,
                            node.Name.Value
                        )
                        : null;
            }
            else
            {
                item = DeserializeObject(node, map.ElementType!);
            }

            if (item != null)
                list.Add(item);
        }

        map.SetValue(
            parentInstance,
            ConvertCollection(list, map.Property.PropertyType, map.ElementType!)
        );
    }

    private void PopulateDictionary(
        IDictionary dict,
        IEnumerable<KdlNode> nodes,
        Type keyType,
        Type valueType
    )
    {
        foreach (var node in nodes)
        {
            object key = Convert.ChangeType(node.Name.Value, keyType);
            object? value;

            if (valueType.IsKdlScalar)
            {
                var arg = node.Arg(0);
                value =
                    arg != null
                        ? KdlValueConverter.FromKdlOrThrow(arg, valueType, key.ToString()!)
                        : null;
            }
            else
            {
                value = DeserializeObject(node, valueType);
            }

            if (value != null)
                dict[key] = value;
        }
    }

    private object DeserializeObject(KdlNode node, Type type)
    {
        if (type.IsKdlScalar) { }
        var instance =
            Activator.CreateInstance(type)
            ?? throw new KuddleSerializationException(
                $"Failed to create instance of '{type.Name}'."
            );

        MapNodeToObject(node, instance, KdlTypeMapping.For(type));
        return instance;
    }

    private static void ValidateNodeName(KdlNode node, string nodeName)
    {
        if (!node.Name.Value.Equals(nodeName, NodeNameComparison))
        {
            throw new KuddleSerializationException(
                $"Expected node '{nodeName}', found '{node.Name.Value}'."
            );
        }
    }

    private object EnsureInstance<T>(T parent, KdlMemberMap map)
        where T : new()
    {
        var current = map.GetValue(parent!);
        if (current != null)
            return current;

        var newInstance = Activator.CreateInstance(map.Property.PropertyType)!;
        map.SetValue(parent!, newInstance);
        return newInstance;
    }

    private IList CreateList(Type elementType)
    {
        var listType = typeof(List<>).MakeGenericType(elementType);
        return (IList)Activator.CreateInstance(listType)!;
    }

    private object ConvertCollection(IList list, Type targetType, Type elementType)
    {
        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return array;
        }
        return list; // Assuming List<T> is compatible with target (IEnumerable/IReadOnlyList)
    }
}
