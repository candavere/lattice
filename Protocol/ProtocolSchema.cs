using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lattice.Protocol;

/// <summary>
/// A per-type map from wire member name to the shape that member's value may
/// take, derived once by reflection over the <c>JsonPropertyName</c> attributes
/// of the wire records.
/// </summary>
/// <remarks>
/// This exists to make <c>unknown_field</c> a <em>decision</em> rather than a
/// guess. The serializer's
/// <see cref="JsonUnmappedMemberHandling.Disallow"/> is what actually enforces
/// the §8.3 rule and stays in force as the backstop, but the only way it can
/// report itself is a <see cref="JsonException"/>, and a reason code is
/// something the protocol has to be able to state exactly. Classifying it by
/// matching exception-message text would couple a wire contract to a runtime's
/// phrasing; walking the document against a member table derived from the same
/// attributes the serializer uses cannot drift from the schema, and it is
/// checked before deserialization, which is what §8.4's precedence requires.
/// </remarks>
internal sealed class ProtocolSchemaNode
{
    private ProtocolSchemaNode(IReadOnlyDictionary<string, ProtocolSchemaNode?> members) =>
        Members = members;

    /// <summary>
    /// The wire member names this object type accepts, each mapped to the node
    /// describing its value — <see langword="null"/> when the value is a scalar
    /// and therefore has no members to check.
    /// </summary>
    public IReadOnlyDictionary<string, ProtocolSchemaNode?> Members { get; }

    private static readonly ConcurrentDictionary<Type, ProtocolSchemaNode> Cache = new();

    /// <summary>The node for <paramref name="type"/>, built once per type.</summary>
    public static ProtocolSchemaNode For(Type type) => Cache.GetOrAdd(type, Build);

    private static ProtocolSchemaNode Build(Type type)
    {
        var members = new Dictionary<string, ProtocolSchemaNode?>(StringComparer.Ordinal);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            members[name] = NodeFor(property.PropertyType, depth: 0);
        }

        return new ProtocolSchemaNode(members);
    }

    private static ProtocolSchemaNode? NodeFor(Type type, int depth)
    {
        var element = ElementTypeOf(type);
        if (element is not null)
        {
            // Arrays and lists are transparent: the shape that matters is the
            // element's, and an empty array carries nothing to check.
            return IsComplex(element) ? For(element) : null;
        }

        return IsComplex(type) ? For(type) : null;
    }

    private static Type? ElementTypeOf(Type type) =>
        type.IsArray ? type.GetElementType() :
        type != typeof(string) && type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable
            ? enumerable.GetGenericArguments()[0]
            : null;

    /// <summary>
    /// A type is "complex" — and therefore contributes members to check —
    /// when it is a reference type other than <see cref="string"/>. The wire
    /// records are all classes; scalars, and the nullable value types the action
    /// and the limits use, are not.
    /// </summary>
    private static bool IsComplex(Type type) =>
        type.IsClass && type != typeof(string) && type != typeof(object) && !typeof(IEnumerable).IsAssignableFrom(type);
}

/// <summary>
/// Walks a parsed document against a <see cref="ProtocolSchemaNode"/> looking for
/// members the schema does not declare.
/// </summary>
internal static class ProtocolSchemaCheck
{
    /// <summary>
    /// The name of the first undeclared member found in <paramref name="value"/>,
    /// searching the object itself first and then its descendants, or
    /// <see langword="null"/> when every member is declared.
    /// </summary>
    /// <remarks>
    /// The document reaching this point has already passed the depth cap, so the
    /// recursion is bounded by <see cref="ProtocolLimits.MaxJsonDepth"/> and
    /// cannot overflow the stack.
    /// </remarks>
    public static string? FindUnknownMember(JsonElement value, ProtocolSchemaNode node)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (!node.Members.TryGetValue(property.Name, out var child))
                {
                    return property.Name;
                }

                if (child is not null)
                {
                    var nested = FindUnknownMember(property.Value, child);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindUnknownMember(item, node);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
