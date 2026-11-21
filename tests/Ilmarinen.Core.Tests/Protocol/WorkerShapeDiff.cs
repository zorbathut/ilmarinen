using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Core.Tests.Protocol;

/// <summary>
/// Whether a peer on an older worker protocol version can still talk to one on a newer, judged from the two versions' shapes (<see cref="WorkerShape"/>). It rests on how SignalR's System.Text.Json reads messages here: unknown members are skipped, a missing member reads as null or default unless it is <c>required</c>, which fails the call, enums travel as integers and an unknown value reads without complaint, and a reference type's nullability isn't enforced at all, only trusted by the code that reads the value (a null for a non-nullable value type does fail to read).
///
/// So what breaks depends on which way a type travels. Up (worker→server), an older worker writes and the newer server reads: a new member must be optional and nullable, so its absence reads as "an older worker"; dropping a member is fine, as the server skips it; a new enum member is fine, as the server knows every value an older worker sends. Down (server→worker), the newer server writes and an older worker reads: a new member is fine, as the worker skips it; dropping one isn't; a new enum member isn't. A type that travels both ways must satisfy both.
///
/// Being readable is not the same as being understood: a new down member an older worker skips is one it doesn't act on. That is the server's to handle, by checking the worker's version (CLAUDE.md), and no shape check can see it.
/// </summary>
public static class WorkerShapeDiff
{
    private sealed record Property(string Type, bool Required);

    private sealed class Shape
    {
        public readonly Dictionary<string, string> Methods = new();
        public readonly Dictionary<string, WorkerShapeDirection> Types = new();
        public readonly Dictionary<string, Dictionary<string, Property>> Properties = new();
        public readonly Dictionary<string, Dictionary<string, long>> Members = new();
    }

    /// <summary>The reasons a peer speaking <paramref name="older"/> can't talk to one speaking <paramref name="newer"/>; empty if it can.</summary>
    public static IReadOnlyList<string> Breaks(string older, string newer)
    {
        var before = Parse(older);
        var after = Parse(newer);
        var breaks = new List<string>();

        foreach (var (key, signature) in before.Methods)
        {
            if (!after.Methods.TryGetValue(key, out var now))
            {
                breaks.Add($"{key} was removed");
            }
            else if (now != signature)
            {
                breaks.Add($"{key} changed from \"{signature}\" to \"{now}\"");
            }
        }

        foreach (var (type, was) in before.Types)
        {
            var direction = was | after.Types.GetValueOrDefault(type);
            var up = direction.HasFlag(WorkerShapeDirection.Up);
            var down = direction.HasFlag(WorkerShapeDirection.Down);

            if (!after.Types.ContainsKey(type))
            {
                // No longer reachable: whatever referred to it changed, and is judged there.
                continue;
            }

            if (before.Members.TryGetValue(type, out var members))
            {
                var nowMembers = after.Members.GetValueOrDefault(type) ?? [];
                foreach (var (name, value) in members)
                {
                    if (!nowMembers.TryGetValue(name, out var nowValue))
                    {
                        breaks.Add($"enum member {type}.{name} was removed");
                    }
                    else if (nowValue != value)
                    {
                        breaks.Add($"enum member {type}.{name} changed value from {value} to {nowValue}");
                    }
                }
                foreach (var name in nowMembers.Keys.Except(members.Keys).Where(_ => down))
                {
                    breaks.Add($"enum member {type}.{name} was added to a type the server sends, which an older worker can't act on");
                }
                continue;
            }

            var properties = before.Properties.GetValueOrDefault(type) ?? [];
            var nowProperties = after.Properties.GetValueOrDefault(type) ?? [];
            foreach (var (name, property) in properties)
            {
                var member = $"{type}.{name}";
                if (!nowProperties.TryGetValue(name, out var now))
                {
                    if (down)
                    {
                        breaks.Add($"{member} was removed from a type the server sends");
                    }
                    continue;
                }

                if (Strip(now.Type) != Strip(property.Type))
                {
                    breaks.Add($"{member} changed type from {property.Type} to {now.Type}");
                }
                else if (up && IsNullable(property.Type) && !IsNullable(now.Type))
                {
                    breaks.Add($"{member} became non-nullable on a type workers send, which an older worker may leave null");
                }
                else if (down && !IsNullable(property.Type) && IsNullable(now.Type))
                {
                    breaks.Add($"{member} became nullable on a type the server sends, which an older worker trusts to be set");
                }

                if (up && now.Required && !property.Required)
                {
                    breaks.Add($"{member} became required on a type workers send");
                }
            }

            foreach (var (name, now) in nowProperties.Where(p => up && !properties.ContainsKey(p.Key)))
            {
                if (now.Required || !IsNullable(now.Type))
                {
                    breaks.Add($"{type}.{name} was added to a type workers send without being optional and nullable, so an older worker's message, which lacks it, can't be told apart or fails to read");
                }
            }
        }

        return breaks;
    }

    private static string Strip(string type)
    {
        return type.EndsWith('?') ? type[..^1] : type;
    }

    private static bool IsNullable(string type)
    {
        return type.EndsWith('?');
    }

    private static Shape Parse(string text)
    {
        var shape = new Shape();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var kind = line[..line.IndexOf(' ')];
            var rest = line[(kind.Length + 1)..];
            switch (kind)
            {
                case "method":
                {
                    // "hub Connect(...) -> ..." keyed by "hub Connect".
                    shape.Methods[rest[..rest.IndexOf('(')]] = rest;
                    break;
                }
                case "type":
                {
                    var (name, direction) = Split(rest);
                    shape.Types[name] = direction switch
                    {
                        "up" => WorkerShapeDirection.Up,
                        "down" => WorkerShapeDirection.Down,
                        "both" => WorkerShapeDirection.Up | WorkerShapeDirection.Down,
                        _ => throw new FormatException($"Unknown direction in shape line: {line}")
                    };
                    break;
                }
                case "property":
                {
                    var (member, type) = Split(rest);
                    var required = type.EndsWith(" required");
                    if (required)
                    {
                        type = type[..^" required".Length];
                    }
                    var (owner, name) = SplitMember(member);
                    Get(shape.Properties, owner)[name] = new Property(type, required);
                    break;
                }
                case "member":
                {
                    var (member, value) = Split(rest);
                    var (owner, name) = SplitMember(member);
                    Get(shape.Members, owner)[name] = long.Parse(value);
                    break;
                }
                default:
                {
                    throw new FormatException($"Unknown shape line: {line}");
                }
            }
        }
        return shape;
    }

    private static (string, string) Split(string text)
    {
        var space = text.IndexOf(' ');
        return (text[..space], text[(space + 1)..]);
    }

    private static (string Owner, string Name) SplitMember(string member)
    {
        var dot = member.LastIndexOf('.');
        return (member[..dot], member[(dot + 1)..]);
    }

    private static Dictionary<string, T> Get<T>(Dictionary<string, Dictionary<string, T>> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = [];
            map[key] = value;
        }
        return value;
    }
}
